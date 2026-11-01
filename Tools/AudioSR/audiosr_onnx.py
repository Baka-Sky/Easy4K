#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
AudioSR ONNX 推理驱动（Easy4K 内置）

用 numpy + onnxruntime 复现 haoheliu/audiosr_basic 的官方推理流程：
    读音频 → 重采样 48k → 归一化 → 官方低通滤波（audiosr/lowpass.py，供低频回填用）
    → log-mel → VAE 条件编码 → DDIM(eta=1, CFG 3.5) → VAE 解码 → vocoder 重建
    → mel / 波形低频段回填（低音用低通后的原音频，保证不跑调）→ 写 48k WAV

后端：NVIDIA 显卡优先 CUDA(GPU)，其次 DirectML；其它显卡用 DirectML（A/N/I 通吃）；
逐级尝试，三级（CUDA / DirectML / CPU）都起不来才报错退出。

权重包（--models 指向的目录，各自扁平一层）：
    models-fp32/  官方 fp32（2.51 GiB），画质基准，CPU/GPU 都能跑
    models-fp16/  官方 fp16（1.26 GiB），仅 GPU，比 fp32 快约 9%，实测 59.4 dB SI-SDR
两者的 manifest.json 里 precision 字段分别是 fp32 / fp16，仅用于日志标注。

用法：
    python audiosr_onnx.py --input in.wav --output out.wav --models <权重包目录>
"""

import argparse
import json
import math
import os
import shutil
import sys
import time
import wave

import numpy as np

SR = 48000                       # AudioSR 固定工作采样率
LIB_N_FFT, LIB_HOP = 2048, 512   # librosa.stft/istft 默认参数（postprocessing 用）

EXIT_ALL_BACKENDS_FAILED = 3     # CUDA / DirectML / CPU 三级后端全部无法启动
EXIT_VRAM_OOM = 4                # 显存不足，且窗口已缩到最小值仍然失败


_LIVE = os.environ.get("AUDIOSR_LIVE_LOG")


def _emit(s):
    try:
        print(s, flush=True)
    except Exception:
        pass
    if _LIVE:
        # 逐行写、写完立刻关闭：外部（监控 CMD / 资源管理器 / 远程共享）随时可读，
        # 不会像常驻句柄那样把文件独占锁住
        try:
            with open(_LIVE, "a", encoding="utf-8") as f:
                f.write(s + "\n")
        except Exception:
            pass


def log(m):
    _emit(f"[AudioSR] {m}")


def warn(m):
    _emit(f"[AudioSR][警告] {m}")


class VramOOM(RuntimeError):
    """显存不足（与普通推理错误区分，便于自动缩小窗口重试）。"""


_OOM_HINTS = ("out of memory", "cuda failure 2", "cudamalloc", "cuda_error_out_of_memory",
              "failed to allocate", "memory allocation failure", "insufficient memory")


def is_oom(exc):
    s = str(exc).lower()
    return any(h in s for h in _OOM_HINTS)


# ============================ WAV I/O ============================

def read_wav(path):
    """读 PCM/float WAV → (float32 [C,N], sr)"""
    with open(path, "rb") as f:
        raw = f.read()
    if raw[:4] != b"RIFF" or raw[8:12] != b"WAVE":
        raise ValueError(f"不是 WAV 文件: {path}")
    fmt = data = None
    pos = 12
    while pos + 8 <= len(raw):
        cid, csz = raw[pos:pos + 4], int.from_bytes(raw[pos + 4:pos + 8], "little")
        body = raw[pos + 8:pos + 8 + csz]
        if cid == b"fmt ":
            fmt = body
        elif cid == b"data":
            data = body
        pos += 8 + csz + (csz & 1)
    if fmt is None or data is None:
        raise ValueError("WAV 缺少 fmt/data 块")

    tag = int.from_bytes(fmt[0:2], "little")
    channels = int.from_bytes(fmt[2:4], "little")
    sr = int.from_bytes(fmt[4:8], "little")
    bits = int.from_bytes(fmt[14:16], "little")
    if tag == 0xFFFE and len(fmt) >= 26:
        tag = int.from_bytes(fmt[24:26], "little")

    if tag == 3:
        x = np.frombuffer(data, "<f4" if bits == 32 else "<f8").astype(np.float32)
    elif tag == 1 and bits == 16:
        x = np.frombuffer(data, "<i2").astype(np.float32) / 32768.0
    elif tag == 1 and bits == 24:
        b = np.frombuffer(data, np.uint8).reshape(-1, 3).astype(np.int32)
        v = b[:, 0] | (b[:, 1] << 8) | (b[:, 2] << 16)
        x = np.where(v & 0x800000, v - 0x1000000, v).astype(np.float32) / 8388608.0
    elif tag == 1 and bits == 32:
        x = np.frombuffer(data, "<i4").astype(np.float32) / 2147483648.0
    else:
        raise ValueError(f"不支持的 WAV（tag={tag} bits={bits}）")

    n = len(x) // channels
    return x[:n * channels].reshape(n, channels).T.astype(np.float32), sr


def _info_field(v):
    """LIST/INFO 子块的值：UTF-8 + NUL 结尾，并按 RIFF 规则补齐到偶数长度。"""
    b = v.encode("utf-8") + b"\x00"
    return b + (b"\x00" if len(b) & 1 else b"")


def append_info_chunk(path, tags):
    """在 WAV 末尾追加 LIST/INFO 元数据块，并修正 RIFF 头里的文件长度。
    用于把"这段音频经过了 AudioSR"写进文件信息（MediaInfo / ffprobe / 资源管理器属性都能看到）。"""
    items = b"INFO"
    for k, v in tags.items():
        body = _info_field(v)
        items += k[:4].encode("ascii") + len(body).to_bytes(4, "little") + body
    chunk = b"LIST" + len(items).to_bytes(4, "little") + items

    with open(path, "r+b") as f:
        raw = f.read()
        pad = b"\x00" if (len(raw) - 8) & 1 else b""   # RIFF 要求块总长为偶数
        f.seek(4)
        f.write((len(raw) - 8 + len(pad) + len(chunk)).to_bytes(4, "little"))
        f.seek(0, os.SEEK_END)
        if pad:
            f.write(pad)
        f.write(chunk)


# 产物标记文案：AudioSR 处理过的音频必须带上这句，方便对外辨别音频处理来源。
# 必须用**纯 ASCII 英文**：WAV 的 LIST/INFO 块没有统一的编码约定，媒体工具（MediaInfo 等）
# 按本地代码页（中文系统 = GBK）解码，写 UTF-8 中文会显示成乱码。
# 与 C# 侧（ProcessingOrchestrator 的 AudioSrMarkUsed / EmbedAudio 的 -metadata comment）保持一致，不要随意改动。
EASY4K_AUDIO_MARK = ("This audio was super-resolved by the AudioSR module of Easy4K 2 Pro. "
                     "Please verify the audio processing source carefully!")


def write_wav(path, x, sr, bits=24):
    """float32 [C,N] → PCM WAV（并写入 AudioSR 处理来源标记）"""
    x = np.clip(x, -1.0, 1.0)
    ch = x.shape[0]
    flat = x.T.reshape(-1)
    if bits == 16:
        payload = (flat * 32767.0).astype("<i2").tobytes()
    elif bits == 24:
        v = np.round(flat * 8388607.0).astype(np.int32)
        b = np.empty((v.size, 3), np.uint8)
        b[:, 0], b[:, 1], b[:, 2] = v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF
        payload = b.tobytes()
    elif bits == 32:
        payload = (flat * 2147483647.0).astype("<i4").tobytes()
    else:
        raise ValueError("bits 只支持 16/24/32")

    with wave.open(path, "wb") as w:
        w.setnchannels(ch)
        w.setsampwidth(bits // 8)
        w.setframerate(sr)
        w.writeframes(payload)
    sec = len(payload) / (ch * bits // 8) / sr
    try:
        append_info_chunk(path, {
            "ISFT": "Easy4K 2 Pro AudioSR",
            "ICMT": EASY4K_AUDIO_MARK,
        })
    except Exception as ex:
        warn(f"写入处理来源标记失败（不影响音频本身）：{ex}")
    log(f"已写出 {path}（{sr} Hz / {ch} 声道 / {bits} bit / {sec:.1f} s）")


# ============================ 基础信号处理 ============================

def hann_periodic(n):
    return (0.5 - 0.5 * np.cos(2 * np.pi * np.arange(n) / n)).astype(np.float32)


def resample(x, sr_in, sr_out):
    if sr_in == sr_out:
        return x.astype(np.float32)
    from scipy.signal import resample_poly
    g = math.gcd(int(sr_in), int(sr_out))
    return resample_poly(x, sr_out // g, sr_in // g, axis=-1).astype(np.float32)


def find_cutoff_bin(energy, percentile):
    """官方 _find_cutoff：从高频往低频找第一个累积能量低于 threshold 的 bin。"""
    thr = energy[-1] * percentile
    idx = np.nonzero(energy < thr)[0]
    return int(idx[-1]) if idx.size else 0


# ---------- 官方 AudioSR 低通滤波（audiosr/lowpass.py 的 numpy 等价实现） ----------

def _lowpass_sos(highcut, fs, order, ftype):
    """按官方 ftype 选滤波器，返回二阶节。hz→归一化时夹到 0.99，避免 highcut=24000 时 scipy 报错。"""
    from scipy.signal import butter, cheby1, ellip, bessel
    nyq = 0.5 * fs
    hi = min(max(float(highcut) / nyq, 1e-4), 0.99)
    order = int(min(max(order, 2), 10))                 # 官方 limit(order, high=10, low=2)
    if ftype == "cheby1":
        return cheby1(order, 0.1, hi, btype="low", output="sos")
    if ftype == "ellip":
        return ellip(order, 0.1, 60, hi, btype="low", output="sos")
    if ftype == "bessel":
        return bessel(order, hi, btype="low", output="sos")
    return butter(order, hi, btype="low", output="sos")


def lowpass_official(data, highcut, fs, order=8, ftype="butter"):
    """官方 audiosr.lowpass.lowpass：零相位 SOS 滤波 → 硬降采样到 highcut 再升回原采样率 → 再滤一次。
    只接受 1 维输入（官方同样拒绝 (samples, 1)）。"""
    from scipy.signal import sosfiltfilt, resample_poly
    x = np.asarray(data, dtype=np.float64).ravel()
    sos = _lowpass_sos(highcut, fs, order, ftype)
    y = sosfiltfilt(sos, x)
    # 官方 stft_hard_lowpass：lowpass_ratio = highcut/nyq，fs_down = ratio * fs
    fs_down = int((min(highcut, fs * 0.5 - 1.0) / (0.5 * fs)) * fs)
    if 0 < fs_down < fs:
        y2 = resample_poly(resample_poly(y, fs_down, fs), fs, fs_down)
        y = y2[:len(x)] if len(y2) >= len(x) else np.pad(y2, (0, len(x) - len(y2)))
    y = sosfiltfilt(sos, y)
    if len(y) != len(x):
        y = y[:len(x)] if len(y) > len(x) else np.pad(y, (0, len(x) - len(y)))
    return y.astype(np.float32)


def audio_bandwidth_hz(stft, percentile=0.985, n_bins=1024):
    """官方 lowpass_filtering_prepare_inference 的截止频率：bin/1024*24000；低于 1kHz 视为空音频放行。"""
    magnitude = np.abs(stft)
    energy = np.cumsum(np.sum(magnitude, axis=1))       # stft 是 [freq, time]，先按时间求和再沿频率累积
    cutoff = find_cutoff_bin(energy, percentile) / float(n_bins) * 24000.0
    return 24000.0 if cutoff < 1000 else cutoff


class MelFront:
    """官方 mel_spectrogram_train 的 numpy 版：
    reflect pad 784 + hann(2048) + hop 480 + mel_basis + ln(clamp(x,1e-5))"""

    def __init__(self, models_dir, mf):
        st = mf.get("stft", {})
        self.n_fft = int(st.get("n_fft", 2048))
        self.hop = int(st.get("hop", 480))
        self.pad = int(st.get("pad_reflect", 784))
        self.clip = float(mf.get("mel", {}).get("log_clip_val", 1e-5))
        self.basis = np.load(os.path.join(models_dir, mf["mel"]["basis_file"])).astype(np.float32)
        self.n_mels = self.basis.shape[0]
        self.win = hann_periodic(self.n_fft)

    def n_frames(self, n):
        return 1 + (n + 2 * self.pad - self.n_fft) // self.hop

    def _blocks(self, y, chunk=512):
        yp = np.pad(y, (self.pad, self.pad), mode="reflect")
        n = self.n_frames(len(y))
        for s in range(0, n, chunk):
            idx = np.arange(s, min(s + chunk, n))
            seg = np.stack([yp[i * self.hop:i * self.hop + self.n_fft] for i in idx]) * self.win
            yield s, np.abs(np.fft.rfft(seg, axis=1)).astype(np.float32).T   # [1025, b]

    def log_mel(self, y):
        out = np.empty((self.n_frames(len(y)), self.n_mels), np.float32)
        for s, mag in self._blocks(y, 128):
            mel = self.basis @ mag
            out[s:s + mag.shape[1]] = np.log(np.maximum(mel, self.clip)).T
        return out


def librosa_stft(x):
    """librosa.stft 默认参数：n_fft=2048 hop=512 hann(periodic) center=True pad=0"""
    xp = np.pad(x, (LIB_N_FFT // 2, LIB_N_FFT // 2))
    frames = 1 + len(x) // LIB_HOP
    seg = np.stack([xp[i * LIB_HOP:i * LIB_HOP + LIB_N_FFT] for i in range(frames)])
    return np.fft.rfft(seg * hann_periodic(LIB_N_FFT), axis=1).T.astype(np.complex64)


def librosa_freq_energy(x, chunk=2048):
    """/librosa.stft/ 的 |.| 按帧求和后沿频率累积（省内存版）"""
    xp = np.pad(x, (LIB_N_FFT // 2, LIB_N_FFT // 2))
    frames = 1 + len(x) // LIB_HOP
    win = hann_periodic(LIB_N_FFT)
    acc = None
    for s in range(0, frames, chunk):
        idx = np.arange(s, min(s + chunk, frames))
        seg = np.stack([xp[i * LIB_HOP:i * LIB_HOP + LIB_N_FFT] for i in idx]) * win
        e = np.abs(np.fft.rfft(seg, axis=1)).sum(axis=0)
        acc = e if acc is None else acc + e
    return np.cumsum(acc)


def librosa_istft(S, length):
    win = hann_periodic(LIB_N_FFT).astype(np.float64)
    frames = S.shape[1]
    y = np.zeros(LIB_N_FFT + LIB_HOP * (frames - 1), np.float64)
    ifft = np.fft.irfft(S.T, n=LIB_N_FFT, axis=1) * win
    for i in range(frames):
        y[i * LIB_HOP:i * LIB_HOP + LIB_N_FFT] += ifft[i]
    wsq = (hann_periodic(LIB_N_FFT).astype(np.float64)) ** 2
    wss = np.zeros_like(y)
    for i in range(frames):
        wss[i * LIB_HOP:i * LIB_HOP + LIB_N_FFT] += wsq
    nz = wss > 1e-10
    y[nz] /= wss[nz]
    # 只去掉左侧居中填充；右端按请求长度裁剪，不能先丢掉最后半窗。
    y = y[LIB_N_FFT // 2:LIB_N_FFT // 2 + length]
    if len(y) < length:
        y = np.pad(y, (0, length - len(y)))
    return y[:length].astype(np.float32)


# ==================== 高频整形（Easy4K 自研后处理） ====================
# AudioSR 只在源带宽之外“生成”高频，生成物有两个通病：
#   1) 过量：谱能量甚至比中频还高（听感像“高频墙”/嘶声）；
#   2) 底噪：安静段也被填满宽带噪声。
# 这里不做硬砍，而是把生成的高频整形成“由源频谱自然外推”的包络：
#   · 只处理源带宽 edge 以上（带 0.35 倍频程过渡），edge 以下一个采样都不碰；
#   · 目标 = 本帧中频能量 × 源在 edge 处的 HF/中频比，再按源自身斜率继续滚降；
#   · 逐时频增益（时间 30ms / 频率 3 bin 平滑，快攻慢放），只衰减、不提升；
#   · 目标跟随中频动态 → 安静段自然归零，不会留下底噪。
HF_EDGE_TRANSITION_OCT = 0.10    # edge 以上过渡宽度（倍频程，约 1 kHz）
HF_SLOPE_MIN_DB_OCT = -6.0       # 源斜率下限（再缓就会过亮）
HF_SLOPE_MAX_DB_OCT = -30.0      # 源斜率上限（再陡高频会消失）


def shape_generated_hf(y, x_ref, edge_hz, sr=SR):
    """把模型生成的高频整形成源频谱自然外推的包络；只衰减不提升，edge 以下不动。
    返回 (整形后的波形, 统计信息)。"""
    from scipy.ndimage import uniform_filter1d
    n = min(y.shape[1], x_ref.shape[1])
    F = np.fft.rfftfreq(LIB_N_FFT, 1.0 / sr)
    i_edge = int(np.clip(edge_hz * LIB_N_FFT / sr, 1, len(F) - 2))
    i_ref = max(1, int(i_edge / 1.35))
    f_hi, f_lo = max(F[i_edge], 1.0), max(F[i_ref], 1.0)
    hf_mask = F >= edge_hz

    out = []
    stats = {"edge_hz": float(edge_hz), "slope": 0.0, "ratio_db": 0.0, "max_cut_db": 0.0}
    for c in range(y.shape[0]):
        yc = y[c, :n].astype(np.float64)
        xc = x_ref[c if c < x_ref.shape[0] else 0, :n].astype(np.float64)
        Y = librosa_stft(yc)
        X = librosa_stft(xc)
        A = np.abs(Y)

        # 源长时平均谱（1/6 倍频程平滑）→ edge 处的 HF/中频比 + 局部斜率
        ltas = uniform_filter1d(np.mean(np.abs(X), axis=1), size=9, mode="nearest")
        mid_src = float(np.mean(ltas[(F >= 2000) & (F < 8000)])) + 1e-12
        slope = 20.0 * np.log10((ltas[i_edge] + 1e-12) / (ltas[i_ref] + 1e-12)) / max(np.log2(f_hi / f_lo), 1e-6)
        slope = float(np.clip(slope, HF_SLOPE_MAX_DB_OCT, HF_SLOPE_MIN_DB_OCT))
        ratio_edge = float(ltas[i_edge]) / mid_src
        # 先按源自身斜率外推，再套一层「自然音乐高频上限」：
        # 16k 处不高于中频 -22 dB，之后每倍频程再回落 24 dB。
        # 只靠源斜率会把 mp3 通带内的平缓斜率一路推上去，导致 16k 以上过亮。
        ratio_db = 20.0 * np.log10(np.maximum(
            ratio_edge * (10.0 ** (slope * np.log2(np.maximum(F, 1.0) / f_hi) / 20.0)), 1e-12))
        cap_db = -22.0 - 24.0 * np.log2(np.maximum(F, 16000.0) / 16000.0)
        ratio = 10.0 ** (np.minimum(ratio_db, cap_db) / 20.0)

        # 目标幅度 = 逐帧中频能量 × 目标 HF/中频比 × 动态门控。
        # 门控只看这一帧音乐有多响（源是带限的，不能拿“源有没有 16k 以上内容”做门控，
        # 否则会把补出来的高频全掐掉）；安静段自然归零，等于把底噪删掉。
        mid_t = uniform_filter1d(np.mean(A[(F >= 2000) & (F < 8000)], axis=0), size=9, mode="nearest")
        ref = float(np.percentile(mid_t, 95)) + 1e-12
        dyn = np.clip((mid_t / ref) ** 0.5, 0.02, 1.0)
        target = ratio[:, None] * mid_t[None, :] * dyn[None, :]
        # 源本身在该频点就有的能量必须原样保留：整形只能压“多出来的”，不能把原有高频也压掉。
        # 这条保证 mp3 通带边缘（例如 16k~17k）的既有内容不被误伤。
        target = np.maximum(target, uniform_filter1d(np.abs(X), 9, axis=1, mode="nearest") * 1.05)

        # 逐 bin 只衰减增益；频率做 min 滤波防止窄带尖峰漏出，时间快攻慢放保住瞬态
        g = np.minimum(1.0, target / np.maximum(A, 1e-9))
        g = uniform_filter1d(g, 3, axis=0, mode="nearest")
        g = np.minimum(g, uniform_filter1d(g, 3, axis=0, mode="nearest"))
        g = uniform_filter1d(g, 5, axis=1, mode="nearest")
        g = np.minimum(g, uniform_filter1d(g, 3, axis=1, mode="nearest"))

        # edge 以下完全不动，edge 以上 0.35 倍频程内淡入
        w = np.zeros(len(F))
        i_end = int(np.clip(i_edge * (2.0 ** HF_EDGE_TRANSITION_OCT), i_edge + 1, len(F)))
        w[i_edge:i_end] = np.linspace(0.0, 1.0, i_end - i_edge)
        w[i_end:] = 1.0
        gain = 1.0 - w[:, None] * (1.0 - g)

        if hf_mask.any():
            stats["max_cut_db"] = max(stats["max_cut_db"],
                                      float(-20 * np.log10(max(float(g[hf_mask].min()), 1e-9))))
        stats["slope"], stats["ratio_db"] = slope, float(20 * np.log10(max(ratio_edge, 1e-12)))
        out.append(librosa_istft(Y * gain, n))

    return np.stack(out).astype(np.float32), stats


def restore_output_level(y, reference, reference_peak):
    """按 0–8kHz 的 RMS 对齐源音频（保住低频/中频响度，不被高频尖峰拖低），再软限幅。"""
    F = np.fft.rfftfreq(LIB_N_FFT, 1.0 / SR)
    body = F < 8000

    def rms_body(z):
        Z = np.abs(librosa_stft(z))
        return float(np.sqrt(np.mean(Z[body] ** 2))) + 1e-12

    out = []
    for c in range(y.shape[0]):
        yc = y[c].astype(np.float64)
        xc = reference[c if c < reference.shape[0] else 0, :len(yc)].astype(np.float64)
        r = float(np.clip(rms_body(xc) / rms_body(yc), 0.5, 2.0))
        out.append(yc * r)
    y2 = np.stack(out)
    # 软限幅：只压 0.9 以上的峰，不再整体降电平（否则新增高频一顶到峰值，低频就被拖低 2 dB）
    lim = 0.9
    over = np.abs(y2) > lim
    if over.any():
        y2 = np.where(over,
                      np.sign(y2) * (lim + 0.08 * np.tanh((np.abs(y2) - lim) / 0.08)),
                      y2)
    return y2.astype(np.float32)


# ============================ ONNX ============================

GRAPHS = {"cond": "vae_feature_extract.onnx", "ddpm": "ddpm.onnx",
          "decoder": "vae_decoder.onnx", "vocoder": "vocoder.onnx"}


def read_precision(models_dir):
    """读 manifest 的 precision 字段（fp16 / fp32），缺失按 fp32 处理（最保守）。"""
    try:
        with open(os.path.join(models_dir, "manifest.json"), "r", encoding="utf-8") as f:
            return str(json.load(f).get("precision", "fp32")).lower()
    except Exception:
        return "fp32"


ORT_PKG_ENV = "EASY4K_ORT_PKG"   # 强制指定 ORT 包的开关（切换后端重启时用）
ORT_CUDA_DIRNAME = "ort-cuda"    # CUDA 版 ORT 包所在目录（与本脚本同级的子目录）


def has_nvidia():
    """本机是否有 NVIDIA 显卡。nvidia-smi 随驱动一起安装，是最省事可靠的判据。"""
    if shutil.which("nvidia-smi"):
        return True
    root = os.environ.get("SystemRoot", r"C:\Windows")
    return os.path.exists(os.path.join(root, "System32", "nvidia-smi.exe"))


def prepare_ort():
    """按显卡选择 ORT 包，返回包名。
    CUDA 版（ort-cuda 目录）与 DirectML 版（便携 Python 自带）都叫 onnxruntime，无法共存，
    只能在 import 之前用 sys.path 决定加载哪一个。"""
    pkg = os.environ.get(ORT_PKG_ENV) or ("cuda" if has_nvidia() else "dml")
    if pkg == "cuda":
        root = os.path.join(os.path.dirname(os.path.abspath(__file__)), ORT_CUDA_DIRNAME)
        capi = os.path.join(root, "onnxruntime", "capi")
        # 包不完整时（例如传到一半）直接退回 DirectML：import 半截包会直接崩进程
        if not (os.path.isfile(os.path.join(capi, "onnxruntime_providers_cuda.dll"))
                and os.path.isfile(os.path.join(capi, "cudnn64_9.dll"))):
            warn(f"CUDA 推理包不完整（{ORT_CUDA_DIRNAME}），改用 DirectML 后端")
            return "dml"
        if hasattr(os, "add_dll_directory") and os.path.isdir(capi):
            os.add_dll_directory(capi)
        # cuDNN 9 的前端在运行时还会自己 LoadLibrary 后端库（cudnn_graph64_9 等），
        # 这一步不走 add_dll_directory，必须让 capi 出现在 PATH 里才找得到，否则会崩进程
        os.environ["PATH"] = capi + os.pathsep + os.environ.get("PATH", "")
        sys.path.insert(0, root)
        return "cuda"
    return "dml"


def open_sessions(models_dir, device):
    """→ (sessions, backend)。
    后端逐级尝试：NVIDIA 上 CUDA → DirectML → CPU；其它显卡上 DirectML → CPU。
    权重包（fp16/fp32）由调用方按用户选择决定，后端降级不改变精度。
    三级后端都起不来时才报错退出。"""
    pkg = prepare_ort()
    import onnxruntime as ort

    def build(providers, want_ep=None):
        so = ort.SessionOptions()
        so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        so.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        if providers[0][0] == "DmlExecutionProvider":
            so.enable_mem_pattern = False
        if providers[0][0] == "CUDAExecutionProvider":
            # 6GB 级显卡（P106 / GTX1060）上，CUDA EP 默认的 kNextPowerOfTwo 显存扩张策略
            # 会把 arena 成倍放大，叠加 EXHAUSTIVE 卷积搜索的 workspace，很容易直接爆显存。
            # 改成按需分配 + 启发式搜索，并关掉 CPU arena / 内存复用，换来峰值显存的可控。
            so.enable_cpu_mem_arena = False
            so.enable_mem_pattern = False
        sess = {k: ort.InferenceSession(os.path.join(models_dir, v), so, providers=providers)
                for k, v in GRAPHS.items()}
        if want_ep is not None:
            # ORT 在 EP 初始化失败时不会报错，而是静默改跑 CPU，必须核对真实生效的 provider
            used = sess["ddpm"].get_providers()
            if want_ep not in used:
                raise RuntimeError(f"{want_ep} 未生效（实际使用 {'、'.join(used)}）")
        return sess, providers[0][0]

    def probe(sess, backend):
        """解码器 + UNet 各跑一次最小推理：权重包不匹配时输出会是非有限值。"""
        dec = sess["decoder"].run(None, {"z": np.zeros((1, 16, 8, 32), np.float32)})[0]
        if not np.isfinite(dec).all():
            raise RuntimeError(f"{backend} 上解码器输出异常：权重包不完整或与图不匹配")
        v = sess["ddpm"].run(None, {"x": np.zeros((1, 32, 8, 32), np.float32),
                                    "timesteps": np.array([1], np.int64)})[0]
        if not np.isfinite(v).all():
            raise RuntimeError("UNet(ddpm) 输出全为非有限值：图与权重 .data 不匹配"
                               "（常见于 fp16 图配了 fp32 数据，请检查权重包）")

    avail = ort.get_available_providers()
    log(f"onnxruntime {ort.__version__}（{pkg} 包）可用后端：{', '.join(avail)}")

    order = []
    if device in ("auto", "cuda"):
        order.append(("CUDAExecutionProvider", "CUDA(GPU)"))
    if device in ("auto", "dml"):
        order.append(("DmlExecutionProvider", "DirectML(GPU)"))

    for ep, tag in order:
        if ep not in avail:
            continue
        try:
            pv = {ep: ({"device_id": 0,
                        "arena_extend_strategy": "kSameAsRequested",
                        "cudnn_conv_algo_search": "HEURISTIC"}
                       if ep == "CUDAExecutionProvider" else {"device_id": 0})}
            sess, _ = build([(ep, pv[ep]), "CPUExecutionProvider"], want_ep=ep)
            probe(sess, tag)
            return sess, tag
        except Exception as e:
            if is_oom(e):
                warn(f"{tag} 显存不足（{e}）")
            else:
                warn(f"{tag} 不可用（{type(e).__name__}: {e}）")
            # CUDA 包内没有 DirectML 后端，要退到 DX12 只能换成另一个包重跑一次
            if ep == "CUDAExecutionProvider" and os.environ.get(ORT_PKG_ENV) != "dml":
                log("改用 DirectML 后端重新运行（进程重启一次）")
                os.environ[ORT_PKG_ENV] = "dml"
                sys.stdout.flush()
                sys.stderr.flush()
                os.execv(sys.executable, [sys.executable] + sys.argv)

    if device in ("cuda", "dml") and not any(ep in avail for ep, _ in order):
        warn(f"onnxruntime 没有 {device} 对应的 ExecutionProvider")

    warn("回退 CPU 推理（速度明显慢于显卡，仅作兜底）")
    try:
        sess, _ = build(["CPUExecutionProvider"], want_ep="CPUExecutionProvider")
        probe(sess, "CPU")
    except Exception as e:
        warn(f"CPU 后端也无法启动（{type(e).__name__}: {e}）")
        warn("CUDA / DirectML / CPU 三级后端均不可用，音频超分无法进行")
        sys.exit(EXIT_ALL_BACKENDS_FAILED)
    return sess, "CPU"


class AudioSR:
    def __init__(self, models_dir, device, steps, guidance, seed, overlap):
        with open(os.path.join(models_dir, "manifest.json"), "r", encoding="utf-8") as f:
            self.mf = json.load(f)
        self.window_seconds = float(self.mf.get("window_seconds", 5.12))
        self.scale_factor = float(self.mf.get("scale_factor", 1.0))
        self.uncond_value = float(self.mf.get("cfg", {}).get("unconditional_value", -11.4981))
        self.n_latent = int(self.mf.get("latent", {}).get("channels", 16))
        self.f_size = int(self.mf.get("latent", {}).get("f_size", 32))
        self.vae_down = int(self.mf.get("latent", {}).get("vae_downsample", 8))
        self.front = MelFront(models_dir, self.mf)
        sc = self.mf.get("scheduler", {})
        self.a_all = np.load(os.path.join(models_dir, sc["alphas_cumprod_file"])).astype(np.float64)

        self.steps = int(steps)
        self.guidance = float(guidance)
        self.overlap = float(overlap)
        self.rng = np.random.default_rng(seed)
        self.sess, self.backend = open_sessions(models_dir, device)

        # 官方 make_ddim_timesteps("uniform") + make_ddim_sampling_parameters(eta)
        eta = float(sc.get("eta", 1.0))
        num = int(sc.get("num_train_timesteps", 1000))
        c = num // self.steps
        ts = np.asarray(list(range(0, num, c))) + 1          # +1：让首个 alpha 对应真实起点
        a = self.a_all[ts]
        a_prev = np.asarray([self.a_all[0]] + self.a_all[ts[:-1]].tolist())
        self.ddim_t = ts
        self.ddim_alphas_prev = a_prev
        self.ddim_sigmas = eta * np.sqrt((1 - a_prev) / (1 - a) * (1 - a / a_prev))

    def _v(self, x, cond, step):
        """单样本一次 UNet 前向。实测 batch=1 每样本最快（B1 199ms / B2 446ms / B4 351ms），所以不并批。"""
        xin = np.concatenate([x, cond * self.scale_factor], axis=1).astype(np.float32)
        t = np.array([step], dtype=np.int64)
        return self.sess["ddpm"].run(["v_pred"], {"x": xin, "timesteps": t})[0]

    def ddim(self, cond, uncond, lat_t, on_step=None):
        shape = (1, self.n_latent, lat_t, self.f_size)
        x = self.rng.standard_normal(shape).astype(np.float32)
        n = len(self.ddim_t)
        for i, step in enumerate(np.flip(self.ddim_t)):
            idx = n - i - 1
            a_t = float(self.a_all[step])
            a_prev = float(self.ddim_alphas_prev[idx])
            sigma = float(self.ddim_sigmas[idx])
            v_t = self._v(x, cond, int(step))
            v_u = self._v(x, uncond, int(step))
            v = v_u + self.guidance * (v_t - v_u)
            e_t = math.sqrt(a_t) * v + math.sqrt(1 - a_t) * x
            pred_x0 = math.sqrt(a_t) * x - math.sqrt(1 - a_t) * v
            dir_xt = math.sqrt(max(0.0, 1 - a_prev - sigma ** 2)) * e_t
            x = (math.sqrt(a_prev) * pred_x0 + dir_xt
                 + sigma * self.rng.standard_normal(shape).astype(np.float32))
            # 步级回调：单个窗口要跑几十秒，靠它把进度条喂动（窗口级上报太粗）
            if on_step is not None:
                on_step(i + 1)
        return x

    def run_window(self, cond_mel, fill_mel, gt_wave, mel_cutoff, wave_cutoff, on_step=None,
                   regen_all=False):
        """单声道一个窗口：cond_mel [1,frames,256] 原始全带宽 log-mel（条件输入）；
        fill_mel 官方低通版 log-mel（mel 域低频回填）；gt_wave [1,N] 官方低通版波形（波形域低频回填）"""
        frames = cond_mel.shape[1]
        lat_t = frames // self.vae_down
        noise = self.rng.standard_normal((1, self.n_latent, lat_t, self.f_size)).astype(np.float32)
        cond = self.sess["cond"].run(["cond"], {"mel": cond_mel[:, None].astype(np.float32),
                                                "noise": noise})[0]
        uncond = np.full_like(cond, self.uncond_value)

        z = self.ddim(cond, uncond, lat_t, on_step)

        mel = self.sess["decoder"].run(["mel"], {"z": z.astype(np.float32)})[0]      # [1,1,frames,256]
        if not regen_all:
            # 官方做法：mel 低频段回填源音频（防跑调）。全频重建模式下整段交给模型。
            mel[:, :, :, :mel_cutoff] = fill_mel[:, None, :, :mel_cutoff]
        voc_in = mel[0, 0].T.astype(np.float32)[None]                                # [1,256,frames]
        wav = self.sess["vocoder"].run(["wav"], {"mel": voc_in})[0][0, 0]            # [N]

        if regen_all:
            return wav.astype(np.float32)          # 全频重建：不做波形域低频回填
        o, g = wav.astype(np.float64), gt_wave[0].astype(np.float64)                 # 低频回填（波形域）
        n = min(len(o), len(g))
        S_out, S_gt = librosa_stft(o[:n]), librosa_stft(g[:n])
        k = min(wave_cutoff, S_out.shape[0])
        S_out[:k] = S_gt[:k]          # 低频段直接等于源，避免逐窗能量比缩放造成电平抽动
        r = librosa_istft(S_out, len(o))
        return (np.pad(r, (0, len(o) - len(r))) if len(r) < len(o) else r[:len(o)]).astype(np.float32)


# ============================ 主流程 ============================

def process_file(input_path, output_path, models_dir=None, steps=50, guidance=3.5, seed=42,
                 overlap=0.5, channels="auto", device="auto",
                 bits=24, window=None):
    """可调用的完整流程：读音频 → 分窗推理 → 低频回填 → 写 48kHz WAV。
    （命令行入口 main() 只是解析参数后调用本函数，两条路径行为完全一致。）
    """
    models_dir = models_dir or os.path.dirname(os.path.abspath(__file__))
    # 参数打包成命名空间，保持与命令行版本逐字一致，避免两处逻辑漂移
    a = argparse.Namespace(
        input=input_path, output=output_path, models=models_dir,
        steps=steps, guidance=guidance, seed=seed, overlap=overlap,
        channels=channels, device=device, bits=bits, window=window)

    t0 = time.time()
    precision = read_precision(a.models)
    eng = AudioSR(a.models, a.device, a.steps, a.guidance, a.seed, a.overlap)
    if a.window:
        eng.window_seconds = float(a.window)
    elif os.environ.get("EASY4K_SR_WINDOW"):     # 显存不足自动缩窗后重启传下来的窗口
        eng.window_seconds = float(os.environ["EASY4K_SR_WINDOW"])
    log(f"权重包 {precision} / 后端 {eng.backend} / DDIM {eng.steps} 步 / CFG {eng.guidance} / "
        f"窗口 {eng.window_seconds}s / 重叠 {a.overlap:.0%} / 种子 {a.seed}")
    regen_all = os.environ.get("EASY4K_SR_REGEN_ALL", "0") == "1"
    log("全频段重建：已启用（低/中/高频都由模型重新生成，不做官方低频回填）" if regen_all
        else "全频段重建：已关闭（官方行为：只补高频，低频回填源音频）")
    log("高频整形：已启用（生成的高频按源频谱自然外推的包络整形，安静段不产生底噪）")

    x, sr = read_wav(a.input)
    log(f"读入 {os.path.basename(a.input)}：{sr} Hz / {x.shape[0]} 声道 / {x.shape[1] / sr:.1f} s")
    if a.channels == "mono" and x.shape[0] > 1:
        x = x.mean(axis=0, keepdims=True)
    x = resample(x, sr, SR)
    source_peak = float(np.max(np.abs(x)))
    if source_peak <= 1e-8:
        raise ValueError("输入音频没有有效信号")

    # 模型内部仍使用官方 0.5 峰值；source_peak 用于最终恢复原始响度。
    x = (x - x.mean(axis=-1, keepdims=True)).astype(np.float32)
    x = (x / (float(np.max(np.abs(x))) + 1e-8) * 0.5).astype(np.float32)

    # 官方 AudioSR 低通滤波（audiosr/lowpass.py → lowpass_filtering_prepare_inference）：
    # 用输入能量 0.985 分位求截止频率，再按官方随机滤波器族做 8 阶零相位低通，并硬降采样再升回 48k。
    # 低通结果 x_lp 专门用于低频回填（mel 域 + 波形域），条件输入仍用原始全带宽 mel —— 与官方一致。
    lp_cut_hz = audio_bandwidth_hz(librosa_stft(x.mean(axis=0)))
    lp_type = str(np.random.default_rng(int(a.seed)).choice(["butter", "cheby1", "ellip", "bessel"]))
    x_lp = np.stack([lowpass_official(x[c], lp_cut_hz, SR, order=8, ftype=lp_type)
                     for c in range(x.shape[0])]).astype(np.float32)
    log(f"官方低通滤波：截止 {lp_cut_hz:.0f} Hz / 8 阶 {lp_type}（零相位 + 硬降采样再升采样）")

    # 分窗：窗口 = win_frames 个 mel 帧（帧数必须能被 VAE 下采样 8 整除）
    win_frames = int(round(eng.window_seconds * 100))            # 100 mel 帧/秒
    dur = x.shape[1] / SR
    pad_dur = math.ceil(dur / eng.window_seconds) * eng.window_seconds
    total_frames = int(round(pad_dur * 100))
    n_pad = int(SR * pad_dur)
    # 末尾必须用「边缘延拓」而不是补零：补零会让最后一个窗口看到一段静音，而静音的 log-mel
    # 会被 clamp 到 log(1e-5) = -11.5129，与 manifest 的无条件值 uncond_value = -11.4981 只差 0.015，
    # 于是 CFG 的 v = v_u + 3.5*(v_t - v_u) 退化成纯无条件采样 —— 模型在没有任何音频线索的情况下
    # 自由发挥，吐出结构化的高频噪声；且每个声道独立抽噪声（self.rng），左右声道互不相关，
    # 听感就是"结尾/某一段突然炸出一片高频"。边缘延拓让该窗口始终看到延续的真实音频，条件不退化。
    n_tail = max(0, n_pad - x.shape[1])
    if n_tail:
        x = np.pad(x, ((0, 0), (0, n_tail)), mode="edge")
        x_lp = np.pad(x_lp, ((0, 0), (0, n_tail)), mode="edge")

    def _mel_stack(sig):
        """每声道分别取 log-mel（对齐官方 wav_feature_extraction + pad_spec）"""
        out = []
        for c in range(sig.shape[0]):
            m = eng.front.log_mel(sig[c])
            if m.shape[0] < total_frames:
                m = np.pad(m, ((0, total_frames - m.shape[0]), (0, 0)))
            out.append(m[:total_frames])
        return np.stack(out)                                    # [C, frames, 256]

    in_mel = _mel_stack(x)                                      # 条件输入：原始全带宽
    lp_mel = _mel_stack(x_lp)                                   # 低频回填：官方低通版（waveform_lowpass）
    log("模型输入特征提取完成")

    # 低频回填截止（mel 域：官方 mel_replace_ops 用 lowpass_mel 算；
    # 波形域：官方 postprocessing 用 waveform_lowpass 算）
    mel_cutoff = find_cutoff_bin(
        np.cumsum(np.exp(lp_mel.astype(np.float64)).sum(axis=1).mean(axis=0)), 0.985)
    wave_cutoff = find_cutoff_bin(librosa_freq_energy(x_lp.mean(axis=0)), 0.985)
    log(f"低频回填：mel bin {mel_cutoff}/256、STFT bin {wave_cutoff}/1024"
        f"（≈{wave_cutoff * SR / 2048.0:.0f} Hz）")

    hop_frames = max(1, int(round(win_frames * (1 - a.overlap))))
    starts = list(range(0, max(1, total_frames - win_frames + 1), hop_frames))
    if starts[-1] != total_frames - win_frames:
        starts.append(total_frames - win_frames)
    n_win = len(starts)
    log(f"共 {n_win} 个窗口（每窗 {eng.window_seconds}s）")

    win_len = win_frames * 480
    acc = np.zeros((x.shape[0], x.shape[1]), np.float64)
    wsum = np.zeros(x.shape[1], np.float64)
    ramp = np.maximum(hann_periodic(win_len).astype(np.float64), 1e-3)   # 交叉淡入淡出权重

    _shape_logged = [False]

    def finalize():
        """交叉淡入淡出归一化 → 高频整形 → 去均值 → 按中频响度对齐源。"""
        y = (acc / np.where(wsum < 1e-6, 1.0, wsum))[:, :int(round(dur * SR))]
        y, st = shape_generated_hf(y, x[:, :y.shape[1]], wave_cutoff * SR / 2048.0)
        if not _shape_logged[0]:
            _shape_logged[0] = True
            log(f"高频整形：边缘 {st['edge_hz']:.0f} Hz / 源斜率 {st['slope']:.1f} dB/oct / "
                f"边缘处 HF 比中频 {st['ratio_db']:.1f} dB / 最大衰减 {st['max_cut_db']:.1f} dB")
        y = y - y.mean(axis=-1, keepdims=True)
        # x 在进模型前被归一化到 0.5 峰值（官方做法），这里换算回原始电平再对齐响度，
        # 否则整曲会比源低 20*log10(0.5/源峰值) dB —— 表现为“低频/中频被压了”。
        k = 2.0 * float(source_peak)
        return restore_output_level(y * k, (x * k)[:, :y.shape[1]], source_peak)

    # 步级进度：把「第几窗 × 第几个声道 × 第几步」折算成整体百分比。
    # 单个窗口就要跑几十秒，只按窗口上报的话进度条会长时间钉在 0%；
    # 节流到「至少 1.5s 且整数百分比有变化」才打印，避免刷屏。
    n_ch = x.shape[0]
    total_units = max(1, n_win * n_ch)
    n_steps = max(1, len(eng.ddim_t))
    _last_pct = [-1]
    _last_t = [0.0]

    def step_cb(win_idx, ch_idx):
        def cb(done):
            frac = (win_idx * n_ch + ch_idx + done / n_steps) / total_units
            pct = int(min(1.0, frac) * 100)
            now = time.time()
            if pct != _last_pct[0] and now - _last_t[0] >= 1.5:
                _last_pct[0] = pct
                _last_t[0] = now
                log(f"进度 {pct}%  窗口 {win_idx + 1}/{n_win}  声道 {ch_idx + 1}/{n_ch}  步 {done}/{n_steps}")
        return cb

    try:
        for n, f0 in enumerate(starts):
            ts = time.time()
            s0 = f0 * 480
            gt = x_lp[:, s0:s0 + win_len]
            if gt.shape[1] < win_len:
                gt = np.pad(gt, ((0, 0), (0, win_len - gt.shape[1])))
            try:
                outs = [eng.run_window(in_mel[c, f0:f0 + win_frames][None].astype(np.float32),
                                       lp_mel[c, f0:f0 + win_frames][None].astype(np.float32),
                                       gt[c:c + 1], mel_cutoff, wave_cutoff, step_cb(n, c),
                                       regen_all=regen_all)
                        for c in range(x.shape[0])]
            except Exception as e:
                if is_oom(e):
                    raise VramOOM(str(e)) from e
                raise
            m = min(win_len, outs[0].shape[0], acc.shape[1] - s0)
            w = ramp[:m]
            for c, o in enumerate(outs):
                acc[c, s0:s0 + m] += o[:m] * w
            wsum[s0:s0 + m] += w
            el = time.time() - t0
            # 进度行：Easy4K 用正则抓 "进度 N%"，格式不要随意改
            log(f"进度 {int((n + 1) / n_win * 100)}%  窗口 {n + 1}/{n_win}  本窗 {time.time() - ts:.1f}s  "
                f"累计 {el:.0f}s  预计剩余 {el / (n + 1) * (n_win - n - 1):.0f}s")
            if (n + 1) % 20 == 0:                      # 长音频保护：每 20 窗落一次盘，中断不至于全丢
                try:
                    write_wav(a.output + ".partial.wav", finalize().astype(np.float32), SR, a.bits)
                except Exception as e:
                    warn(f"中途落盘失败（不影响继续）：{e}")
    except VramOOM as e:
        # 6GB 级显卡（P106 / GTX1060）在默认 5.12s 窗口下会被 DDIM 的 UNet 激活顶到上限。
        # 窗口减半只增加窗口数量与耗时，不改变低频回填/响度恢复的口径，比直接失败好得多。
        cur = float(eng.window_seconds)
        nxt = max(2.0, cur / 2.0)
        if nxt < cur - 0.01 and os.environ.get("EASY4K_SR_SHRINK") != "1":
            warn(f"显存不足（{e}），窗口 {cur:.2f}s → {nxt:.2f}s 后自动重跑")
            os.environ["EASY4K_SR_WINDOW"] = f"{nxt:.2f}"
            os.environ["EASY4K_SR_SHRINK"] = "1"
            sys.stdout.flush()
            sys.stderr.flush()
            os.execv(sys.executable, [sys.executable] + sys.argv)
        warn(f"显存不足，且窗口已缩到 {cur:.2f}s：{e}")
        warn("请关闭占用显存的其它程序（或改用 fp16 权重包）后重试")
        sys.exit(EXIT_VRAM_OOM)

    write_wav(a.output, finalize().astype(np.float32), SR, a.bits)
    for p in (a.output + ".partial.wav",):
        try:
            if os.path.exists(p):
                os.remove(p)
        except OSError:
            pass
    log(f"全部完成，用时 {time.time() - t0:.1f}s")


def main():
    ap = argparse.ArgumentParser(description="AudioSR ONNX 推理（DirectML 优先；fp16 权重包不允许 CPU）")
    ap.add_argument("--input", required=True, help="输入 WAV（其它格式先用 ffmpeg 抽成 WAV）")
    ap.add_argument("--output", required=True, help="输出 48kHz WAV")
    ap.add_argument("--models", default=os.path.dirname(os.path.abspath(__file__)),
                    help="权重包目录（含 manifest.json 与 4 个 ONNX）")
    ap.add_argument("--steps", type=int, default=50, help="DDIM 步数（官方 50）")
    ap.add_argument("--guidance", type=float, default=3.5, help="CFG 强度（官方 3.5）")
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--overlap", type=float, default=0.5, help="相邻窗口重叠 0~0.75")
    ap.add_argument("--channels", default="auto", choices=["auto", "mono"])
    ap.add_argument("--device", default="auto", choices=["auto", "cuda", "dml", "cpu"])
    ap.add_argument("--bits", type=int, default=24, choices=[16, 24, 32])
    ap.add_argument("--window", type=float, default=None, help="窗口秒数，默认取 manifest")
    a = ap.parse_args()
    process_file(a.input, a.output, a.models, a.steps, a.guidance, a.seed, a.overlap,
                 a.channels, a.device, a.bits, a.window)


if __name__ == "__main__":
    main()
