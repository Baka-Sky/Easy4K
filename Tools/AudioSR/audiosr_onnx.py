#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
AudioSR ONNX 推理驱动（Easy4K 内置）

用 numpy + onnxruntime 复现 haoheliu/audiosr_basic 的官方推理流程：
    读音频 → 重采样 48k → 归一化 → 官方算法低通（去掉伪高频，也是模型的低频条件）
    → log-mel → VAE 条件编码 → DDIM(eta=1, CFG 3.5) → VAE 解码 → vocoder 重建
    → mel / 波形低频段回填（低音用回原音频，保证不跑调）→ 写 48k WAV

后端：DirectML(GPU，A/N/I 显卡通吃) 优先；fp32 权重包失败时自动回退 CPU，
fp16 权重包**禁止**回退 CPU（CPU 的 fp16 算子又少又慢，官方明确不建议）。

权重包（--models 指向的目录，各自扁平一层）：
    models-fp32/  官方 fp32（2.51 GiB），画质基准，CPU/GPU 都能跑
    models-fp16/  官方 fp16（1.26 GiB），仅 GPU，比 fp32 快约 9%，实测 59.4 dB SI-SDR
两者的 manifest.json 里 precision 字段分别是 fp32 / fp16，本脚本据此决定是否允许 CPU。

用法：
    python audiosr_onnx.py --input in.wav --output out.wav --models <权重包目录>
"""

import argparse
import json
import math
import os
import sys
import time
import wave

import numpy as np

SR = 48000                       # AudioSR 固定工作采样率
LP_ORDER = 8                     # 官方 lowpass order
LP_TYPE = "butter"               # 官方按种子随机选：butter/cheby1/ellip/bessel
LIB_N_FFT, LIB_HOP = 2048, 512   # librosa.stft/istft 默认参数（postprocessing 用）

EXIT_NO_GPU_FOR_FP16 = 3         # fp16 权重包 + 只有 CPU → 拒绝运行


def log(m):
    print(f"[AudioSR] {m}", flush=True)


def warn(m):
    print(f"[AudioSR][警告] {m}", flush=True)


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


def write_wav(path, x, sr, bits=24):
    """float32 [C,N] → PCM WAV"""
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

    def freq_energy(self, y):
        """沿频率累积的 |STFT| 能量（对帧求和）→ 判真实带宽"""
        energy = None
        for _, mag in self._blocks(y):
            e = mag.sum(axis=1)
            energy = e if energy is None else energy + e
        return np.cumsum(energy)


def lowpass_official(x, highcut, fs):
    """官方 lowpass_filter：sosfiltfilt → 硬重采样带限 → sosfiltfilt"""
    from scipy.signal import butter, cheby1, ellip, bessel, sosfiltfilt, resample_poly
    hi = highcut / (0.5 * fs)
    if LP_TYPE == "butter":
        sos = butter(LP_ORDER, hi, btype="low", output="sos")
    elif LP_TYPE == "cheby1":
        sos = cheby1(LP_ORDER, 0.1, hi, btype="low", output="sos")
    elif LP_TYPE == "ellip":
        sos = ellip(LP_ORDER, 0.1, 60, hi, btype="low", output="sos")
    else:
        sos = bessel(LP_ORDER, hi, btype="low", output="sos")

    def fit(y, n):
        return np.pad(y, (0, n - len(y))) if len(y) < n else y[:n]

    y = fit(sosfiltfilt(sos, x).astype(np.float32), len(x))
    fs_down = int(hi * fs)                     # stft_hard_lowpass：降采样再升回来
    if 1000 < fs_down < fs:
        g = math.gcd(fs_down, fs)
        y = resample_poly(y, fs_down // g, fs // g).astype(np.float32)
        y = resample_poly(y, fs // g, fs_down // g).astype(np.float32)
        y = fit(y, len(x))
    y = fit(sosfiltfilt(sos, y).astype(np.float32), len(x))
    return y


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
    y = y[LIB_N_FFT // 2:len(y) - LIB_N_FFT // 2]
    if len(y) < length:
        y = np.pad(y, (0, length - len(y)))
    return y[:length].astype(np.float32)


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


def open_sessions(models_dir, device, precision):
    """→ (sessions, backend)。DirectML 优先。
    fp32：DirectML 失败自动回退 CPU；fp16：不回退 CPU，直接报错退出（CPU 的 fp16 算子又少又慢）。"""
    import onnxruntime as ort

    def build(providers):
        so = ort.SessionOptions()
        so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        so.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        if providers[0][0] == "DmlExecutionProvider":
            so.enable_mem_pattern = False
        return {k: ort.InferenceSession(os.path.join(models_dir, v), so, providers=providers)
                for k, v in GRAPHS.items()}, providers[0][0]

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

    has_dml = "DmlExecutionProvider" in ort.get_available_providers()
    if device in ("auto", "dml") and has_dml:
        try:
            sess, tag = build([("DmlExecutionProvider", {"device_id": 0}), "CPUExecutionProvider"])
            probe(sess, "DirectML")
            return sess, "DirectML(GPU)"
        except Exception as e:
            warn(f"DirectML 不可用（{type(e).__name__}: {e}）")
    elif device == "dml":
        warn("onnxruntime 没有 DmlExecutionProvider")

    if precision == "fp16":
        # CPU 的 fp16 内核覆盖极差（官方原话：far fewer fp16 kernels, often slower），拒绝运行
        warn("fp16 权重包禁止在 CPU 上运行（CPU 的 fp16 算子又少又慢），请改用 FP32 模式或检查显卡驱动")
        sys.exit(EXIT_NO_GPU_FOR_FP16)

    warn("回退 CPU 推理（fp32 权重包，速度明显慢于 GPU，仅作兜底）")
    sess, tag = build(["CPUExecutionProvider"])
    probe(sess, "CPU")
    return sess, "CPU"


class AudioSR:
    def __init__(self, models_dir, device, steps, guidance, seed, overlap, precision):
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
        self.precision = precision
        self.sess, self.backend = open_sessions(models_dir, device, precision)

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

    def run_window(self, cond_mel, gt_wave, mel_cutoff, wave_cutoff, on_step=None):
        """单声道一个窗口：cond_mel [1,frames,256]（低通音频的 log-mel）；gt_wave [1,N]（低通音频）"""
        frames = cond_mel.shape[1]
        lat_t = frames // self.vae_down
        noise = self.rng.standard_normal((1, self.n_latent, lat_t, self.f_size)).astype(np.float32)
        cond = self.sess["cond"].run(["cond"], {"mel": cond_mel[:, None].astype(np.float32),
                                                "noise": noise})[0]
        uncond = np.full_like(cond, self.uncond_value)

        z = self.ddim(cond, uncond, lat_t, on_step)

        mel = self.sess["decoder"].run(["mel"], {"z": z.astype(np.float32)})[0]      # [1,1,frames,256]
        mel[:, :, :, :mel_cutoff] = cond_mel[:, None, :, :mel_cutoff]                # 低频回填（mel 域）
        voc_in = mel[0, 0].T.astype(np.float32)[None]                                # [1,256,frames]
        wav = self.sess["vocoder"].run(["wav"], {"mel": voc_in})[0][0, 0]            # [N]

        o, g = wav.astype(np.float64), gt_wave[0].astype(np.float64)                 # 低频回填（波形域）
        n = min(len(o), len(g))
        S_out, S_gt = librosa_stft(o[:n]), librosa_stft(g[:n])
        k = min(wave_cutoff, S_out.shape[0])
        ratio = np.mean(np.sum(np.abs(S_gt[:k])) / (np.sum(np.abs(S_out[:k])) + 1e-12))
        S_out[:k] = S_gt[:k] / min(max(float(ratio), 0.8), 1.2)
        r = librosa_istft(S_out, len(o))
        return (np.pad(r, (0, len(o) - len(r))) if len(r) < len(o) else r[:len(o)]).astype(np.float32)


# ============================ 主流程 ============================

def process_file(input_path, output_path, models_dir=None, steps=50, guidance=3.5, seed=42,
                 overlap=0.5, lowpass="auto", channels="auto", device="auto",
                 bits=24, window=None):
    """可调用的完整流程：读音频 → 低通 → 分窗推理 → 写 48kHz WAV。
    （命令行入口 main() 只是解析参数后调用本函数，两条路径行为完全一致。）
    """
    global LP_TYPE, LP_ORDER
    models_dir = models_dir or os.path.dirname(os.path.abspath(__file__))
    # 参数打包成命名空间，保持与命令行版本逐字一致，避免两处逻辑漂移
    a = argparse.Namespace(
        input=input_path, output=output_path, models=models_dir,
        steps=steps, guidance=guidance, seed=seed, overlap=overlap, lowpass=lowpass,
        channels=channels, device=device, bits=bits, window=window)

    t0 = time.time()
    precision = read_precision(a.models)
    eng = AudioSR(a.models, a.device, a.steps, a.guidance, a.seed, a.overlap, precision)
    if a.window:
        eng.window_seconds = float(a.window)
    log(f"权重包 {precision} / 后端 {eng.backend} / DDIM {eng.steps} 步 / CFG {eng.guidance} / "
        f"窗口 {eng.window_seconds}s / 重叠 {a.overlap:.0%} / 种子 {a.seed}")

    x, sr = read_wav(a.input)
    log(f"读入 {os.path.basename(a.input)}：{sr} Hz / {x.shape[0]} 声道 / {x.shape[1] / sr:.1f} s")
    if a.channels == "mono" and x.shape[0] > 1:
        x = x.mean(axis=0, keepdims=True)
    x = resample(x, sr, SR)

    # 官方 read_wav_file：去均值 → 峰值归一化到 0.5
    x = x - x.mean(axis=-1, keepdims=True)
    x = (x / (float(np.max(np.abs(x))) + 1e-8) * 0.5).astype(np.float32)

    # 低通（官方 _locate_cutoff_freq + np.random.choice；cutoff<1kHz 视为空音频不滤）
    bin_idx = find_cutoff_bin(eng.front.freq_energy(x.mean(axis=0)), 0.985)
    cutoff_hz = bin_idx * SR / eng.front.n_fft
    need_lp = True
    if a.lowpass == "off":
        need_lp, cutoff_hz = False, 24000.0
    elif a.lowpass != "auto":
        cutoff_hz = float(a.lowpass)
    elif cutoff_hz < 1000:
        need_lp, cutoff_hz = False, 24000.0
    log(f"频谱判定真实带宽 ≈ {bin_idx * SR / eng.front.n_fft:.0f} Hz → "
        + (f"低通到 {cutoff_hz:.0f} Hz" if need_lp else "不做低通（按 24 kHz 处理）"))

    np.random.seed(a.seed)                        # 官方：choice 是 seed 后第一次取随机数
    LP_TYPE = str(np.random.choice(["butter", "cheby1", "ellip", "bessel"]))
    LP_ORDER = int(eng.mf.get("lowpass", {}).get("order", 8))

    if need_lp:
        log(f"低通中（{LP_TYPE} order={LP_ORDER}，{x.shape[1] / SR:.0f}s × {x.shape[0]} 声道）…")
        lp = np.stack([lowpass_official(c, cutoff_hz, SR) for c in x])
    else:
        lp = x.copy()
    log(f"低通完成 {time.time() - t0:.1f}s")

    # 分窗：窗口 = win_frames 个 mel 帧（帧数必须能被 VAE 下采样 8 整除）
    win_frames = int(round(eng.window_seconds * 100))            # 100 mel 帧/秒
    dur = x.shape[1] / SR
    pad_dur = math.ceil(dur / eng.window_seconds) * eng.window_seconds
    total_frames = int(round(pad_dur * 100))
    n_pad = int(SR * pad_dur)
    x = np.pad(x, ((0, 0), (0, max(0, n_pad - x.shape[1]))))
    lp = np.pad(lp, ((0, 0), (0, max(0, n_pad - lp.shape[1]))))

    # 每声道分别取低通后的 log-mel（对齐官方 wav_feature_extraction + pad_spec）
    lp_mel = []
    for c in range(lp.shape[0]):
        m = eng.front.log_mel(lp[c])
        if m.shape[0] < total_frames:
            m = np.pad(m, ((0, total_frames - m.shape[0]), (0, 0)))
        lp_mel.append(m[:total_frames])
    lp_mel = np.stack(lp_mel)                                    # [C, frames, 256]
    log("低通音频特征提取完成")

    # 低频回填截止（mel 域：官方 mel_replace_ops；波形域：官方 _get_cutoff_index_np）
    lin = np.exp(lp_mel.astype(np.float64))
    mel_cutoff = find_cutoff_bin(np.cumsum(lin.sum(axis=1).mean(axis=0)), 0.985)
    wave_cutoff = find_cutoff_bin(librosa_freq_energy(lp.mean(axis=0)), 0.985)
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

    def finalize():
        """交叉淡入淡出归一化 + 去均值 + 峰值归一到 0.5（与官方 generate_batch 一致）"""
        y = (acc / np.where(wsum < 1e-6, 1.0, wsum))[:, :int(round(dur * SR))]
        y = y - y.mean(axis=-1, keepdims=True)
        m = float(np.max(np.abs(y)))
        return (y / m * 0.5) if m > 0 else y

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

    for n, f0 in enumerate(starts):
        ts = time.time()
        s0 = f0 * 480
        gt = lp[:, s0:s0 + win_len]
        if gt.shape[1] < win_len:
            gt = np.pad(gt, ((0, 0), (0, win_len - gt.shape[1])))
        outs = [eng.run_window(lp_mel[c, f0:f0 + win_frames][None].astype(np.float32),
                               gt[c:c + 1], mel_cutoff, wave_cutoff, step_cb(n, c))
                for c in range(lp.shape[0])]
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
    ap.add_argument("--lowpass", default="auto", help="auto / off / 截止Hz")
    ap.add_argument("--channels", default="auto", choices=["auto", "mono"])
    ap.add_argument("--device", default="auto", choices=["auto", "dml", "cpu"])
    ap.add_argument("--bits", type=int, default=24, choices=[16, 24, 32])
    ap.add_argument("--window", type=float, default=None, help="窗口秒数，默认取 manifest")
    a = ap.parse_args()
    process_file(a.input, a.output, a.models, a.steps, a.guidance, a.seed, a.overlap,
                 a.lowpass, a.channels, a.device, a.bits, a.window)


if __name__ == "__main__":
    main()
