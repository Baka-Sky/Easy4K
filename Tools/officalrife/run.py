# -*- coding: utf-8 -*-
"""Easy4K Offical RIFE 帧插值脚本（官方 PyTorch pkl 模型，支持 v2.3 / v4.6 ~ v4.26 / RPR v7）。
用法: python run.py -i <输入帧目录> -o <输出帧目录> -m <模型目录> [-mult <倍率>]
模型目录须含 flownet.pkl；模型名形如 official_4.6 / official_2.3 / official_4.26_heavy / rpr_v7_2.3。
v4 系列定义来自 HolyWu/vs-rife（MIT）；v2.3 / RPR v7 定义来自 SVFI（MIT），见随附 model/ 目录。
"""
import argparse
import glob
import importlib
import math
import os
import sys
import time

import numpy as np

try:
    from PIL import Image
except ImportError:
    print("ERROR: 缺少 Pillow，请先执行 pip install Pillow")
    sys.exit(1)

import torch
import torch.nn as nn
from torch.nn import functional as F

_HERE = os.path.dirname(os.path.abspath(__file__))
_MODEL_DIR = os.path.join(_HERE, "model")
if _MODEL_DIR not in sys.path:
    sys.path.insert(0, _MODEL_DIR)

# v4 系列版本 -> (模块名, Head来源, encode通道, 对齐模数)
# Head来源: None=无encode; "seq16"=动态Sequential(16); "seq32"=动态Sequential(32); "file"=模块内Head类
VERSIONS = {
    "4.6":        ("IFNet_HDv3_v4_6", None, 0, 32),
    "4.7":        ("IFNet_HDv3_v4_7", "seq16", 4, 32),
    "4.8":        ("IFNet_HDv3_v4_8", "seq16", 4, 32),
    "4.9":        ("IFNet_HDv3_v4_9", "seq16", 4, 32),
    "4.10":       ("IFNet_HDv3_v4_10", "seq32", 8, 32),
    "4.11":       ("IFNet_HDv3_v4_11", "seq32", 8, 32),
    "4.12":       ("IFNet_HDv3_v4_12", "seq32", 8, 32),
    "4.13":       ("IFNet_HDv3_v4_13", "seq32", 8, 32),
    "4.14":       ("IFNet_HDv3_v4_14", "seq32", 8, 32),
    "4.15":       ("IFNet_HDv3_v4_15", "file", 8, 32),
    "4.16.lite":  ("IFNet_HDv3_v4_16_lite", "file", 4, 32),
    "4.17":       ("IFNet_HDv3_v4_17", "file", 8, 32),
    "4.18":       ("IFNet_HDv3_v4_18", "file", 8, 32),
    "4.19":       ("IFNet_HDv3_v4_19", "file", 8, 32),
    "4.20":       ("IFNet_HDv3_v4_20", "file", 8, 32),
    "4.21":       ("IFNet_HDv3_v4_21", "file", 8, 32),
    "4.22":       ("IFNet_HDv3_v4_22", "file", 8, 32),
    "4.23":       ("IFNet_HDv3_v4_23", "file", 8, 32),
    "4.24":       ("IFNet_HDv3_v4_24", "file", 8, 32),
    "4.25":       ("IFNet_HDv3_v4_25", "file", 4, 64),
    "4.25.heavy": ("IFNet_HDv3_v4_25_heavy", "file", 4, 64),
    "4.26":       ("IFNet_HDv3_v4_26", "file", 4, 64),
    "4.26.heavy": ("IFNet_HDv3_v4_26_heavy", "file", 16, 64),
}

# 特殊老架构：版本 -> 引擎标识
# "hdv2"  = v2.3 纯光流 IFNet（无 timestep，插帧用 warp 平均 + 二分）
# "rpr_v7"= RPR v7（带 timestep，block_tea/contextnet/unet）
SPECIAL = {
    "2.3":       "hdv2",
    "rpr_v7_2.3": "rpr_v7",
}


def log(msg):
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


def parse_version(model_dir):
    name = os.path.basename(os.path.normpath(model_dir))
    v = name.replace("official_", "", 1)
    v = v.replace("_heavy", ".heavy").replace("_lite", ".lite")
    return v


def make_head(kind):
    if kind == "seq16":
        return nn.Sequential(nn.Conv2d(3, 16, 3, 2, 1), nn.ConvTranspose2d(16, 4, 4, 2, 1))
    if kind == "seq32":
        return nn.Sequential(
            nn.Conv2d(3, 32, 3, 2, 1),
            nn.LeakyReLU(0.2, True),
            nn.Conv2d(32, 32, 3, 1, 1),
            nn.LeakyReLU(0.2, True),
            nn.Conv2d(32, 32, 3, 1, 1),
            nn.LeakyReLU(0.2, True),
            nn.ConvTranspose2d(32, 8, 4, 2, 1),
        )
    return None


def load_model(model_dir, version, device):
    if version in SPECIAL:
        engine = SPECIAL[version]
        if engine == "hdv2":
            from IFNet_HDv2 import IFNet
            from warplayer_hd import warp
        else:
            from IFNet_v7_multi import IFNet_m as IFNet
            from warplayer_hd import warp
        state = torch.load(os.path.join(model_dir, "flownet.pkl"), map_location="cpu", weights_only=False)
        state = {k.replace("module.", ""): v for k, v in state.items() if "module." in k}
        flownet = IFNet()
        flownet.load_state_dict(state, strict=True)
        flownet.eval().to(device)
        return flownet, None, engine, warp

    mod_name, head_kind, _enc, _mod = VERSIONS[version]
    mod = importlib.import_module(mod_name)
    IFNetCls = mod.IFNet

    state = torch.load(os.path.join(model_dir, "flownet.pkl"), map_location="cpu", weights_only=False)
    state = {k.replace("module.", ""): v for k, v in state.items() if "module." in k}

    flownet = IFNetCls(1.0, False)
    flownet.load_state_dict(state, strict=False)
    flownet.eval().to(device)

    encode = None
    if head_kind == "file":
        encode = mod.Head()
    elif head_kind is not None:
        encode = make_head(head_kind)
    if encode is not None:
        esd = {k.replace("encode.", ""): v for k, v in state.items() if "encode." in k}
        encode.load_state_dict(esd, strict=True)
        encode.eval().to(device)
    return flownet, encode, "v4", None


def load_frame(path, device, half=False):
    img = Image.open(path).convert("RGB")
    t = torch.from_numpy(np.asarray(img, dtype=np.float32)).permute(2, 0, 1).unsqueeze(0) / 255.0
    t = t.to(device)
    return t.half() if half else t


def save_frame(t, path, h, w):
    # 统一转 float 再保存：half（-u 模式）tensor 直接转 numpy 可能失败
    t = t[0, :, :h, :w].detach().float().clamp(0, 1).permute(1, 2, 0).cpu().numpy()
    Image.fromarray((t * 255).round().astype(np.uint8)).save(path)


def interpolate_v4(flownet, encode, a, b, t, ph, pw, ten_flow_div, grid, device, half=False):
    dt = torch.float16 if half else torch.float
    timestep = torch.full([1, 1, ph, pw], t, dtype=dt, device=device)
    if encode is not None:
        f0 = encode(a)
        f1 = encode(b)
        return flownet(a, b, timestep, ten_flow_div, grid, f0, f1)
    return flownet(a, b, timestep, ten_flow_div, grid)


def interpolate_hdv2(flownet, warp, a, b):
    x = torch.cat((a, b), 1)
    flow, _ = flownet(x, 1.0)
    wa = warp(a, flow[:, :2])
    wb = warp(b, flow[:, 2:4])
    return (wa + wb) / 2.0


def interpolate_rpr(flownet, warp, a, b, t, half=False):
    x = torch.cat((a, b), 1)
    dt = torch.float16 if half else torch.float
    ts = torch.tensor([t], dtype=dt, device=x.device)
    flow_list, mask_list, merged, *_ = flownet(x, timestep=ts)
    return merged[2]


def gen_chain_hdv2(fn, a, b, depth):
    """v2.3 二分插帧：把区间 [a,b] 递归二分 depth 次，返回中间帧列表"""
    if depth <= 0:
        return []
    m = fn(a, b)
    return gen_chain_hdv2(fn, a, m, depth - 1) + [m] + gen_chain_hdv2(fn, m, b, depth - 1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-i", required=True, help="输入帧目录")
    ap.add_argument("-o", required=True, help="输出帧目录")
    ap.add_argument("-m", required=True, help="模型目录（含 flownet.pkl）")
    ap.add_argument("-mult", type=int, default=2, help="补帧倍率（默认2）")
    ap.add_argument("-threads", type=int, default=0, help="PyTorch 推理线程数（0=自动）")
    ap.add_argument("-u", action="store_true", help="降低画质以降低内存占用（FP16 推理）")
    ap.add_argument("-cpu", action="store_true", help="强制使用 CPU 推理（绕过 CUDA 检测，速度慢）")
    args = ap.parse_args()

    pkl = os.path.join(args.m, "flownet.pkl")
    if not os.path.exists(pkl):
        print(f"ERROR: {args.m} 下找不到 flownet.pkl")
        sys.exit(1)

    version = parse_version(args.m)
    if version not in VERSIONS and version not in SPECIAL:
        print(f"ERROR: 不支持的模型版本 {version}（支持: {', '.join(sorted(VERSIONS))} / {', '.join(sorted(SPECIAL))}）")
        sys.exit(1)

    mult = max(2, args.mult)
    # -cpu 强制 CPU（绕过 CUDA 检测）；否则按可用性自动选择
    if args.cpu:
        device = torch.device("cpu")
    else:
        device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    # 显式标记推理设备：软件据此在"进行中"页标注是否降级为 CPU（cuda=GPU / cpu=CPU 降级）
    print(f"[IF_DEVICE] {device.type}", flush=True)

    # 线程滑块：控制 PyTorch 推理线程数（0=torch 自动）
    if args.threads > 0:
        try:
            torch.set_num_threads(args.threads)
            try:
                torch.set_num_interop_threads(args.threads)
            except RuntimeError:
                pass
            log(f"推理线程数: {args.threads}")
        except Exception as ex:
            log(f"设置线程数失败: {ex}")

    log(f"加载模型: {args.m}（版本 {version}，device={device}）")
    torch.set_grad_enabled(False)

    flownet, encode, engine, warp = load_model(args.m, version, device)
    log(f"模型加载成功（引擎 {engine}）")

    # 降低画质（-u）：FP16 推理，模型参数与中间张量内存占用减半
    use_half = args.u
    if use_half:
        flownet = flownet.half()
        if encode is not None:
            encode = encode.half()
        log("已启用降低画质模式（FP16 推理，降低内存占用）")

    files = sorted(glob.glob(os.path.join(args.i, "*.png")),
                   key=lambda p: int(os.path.splitext(os.path.basename(p))[0]))
    if not files:
        print(f"ERROR: 输入目录 {args.i} 没有 PNG 帧")
        sys.exit(1)
    n = len(files)
    out_total = (n - 1) * mult + 1
    log(f"共 {n} 帧，倍率 x{mult}，输出 ~{out_total} 帧")

    # v2.3 纯光流引擎只能二分（2 的幂倍率）
    if engine == "hdv2" and mult not in (2, 4, 8, 16):
        mult = 2 ** max(1, round(math.log2(mult)))
        out_total = (n - 1) * mult + 1
        log(f"v2.3 仅支持 2 的幂倍率，已调整为 x{mult}（输出 ~{out_total} 帧）")
    depth = max(0, round(math.log2(mult))) if engine == "hdv2" else 0

    os.makedirs(args.o, exist_ok=True)
    f0 = load_frame(files[0], device, use_half)
    _, _, h, w = f0.shape

    if engine == "v4":
        dt = torch.float16 if use_half else torch.float
        modulo = VERSIONS[version][3]
        ph = math.ceil(h / modulo) * modulo
        pw = math.ceil(w / modulo) * modulo
        pad = (0, pw - w, 0, ph - h)
        f0 = F.pad(f0, pad)
        ten_flow_div = torch.tensor([(pw - 1.0) / 2.0, (ph - 1.0) / 2.0], dtype=dt, device=device)
        ten_h = torch.linspace(-1.0, 1.0, pw, dtype=dt, device=device).view(1, 1, 1, pw).expand(-1, -1, ph, -1)
        ten_v = torch.linspace(-1.0, 1.0, ph, dtype=dt, device=device).view(1, 1, ph, 1).expand(-1, -1, -1, pw)
        grid = torch.cat([ten_h, ten_v], 1)
    elif engine == "rpr_v7":
        # IFNet_v7_multi 内部把输入对齐到 16 的倍数但返回未裁剪（360→368 报错），
        # 手动 pad 到 16 对齐、输出经 save_frame 裁回原尺寸，避免张量尺寸不匹配
        modulo = 16
        ph = math.ceil(h / modulo) * modulo
        pw = math.ceil(w / modulo) * modulo
        pad = (0, pw - w, 0, ph - h)
        f0 = F.pad(f0, pad)
        ten_flow_div, grid = None, None
    else:
        pad = (0, 0, 0, 0)
        ph, pw = h, w
        ten_flow_div, grid = None, None

    save_frame(f0, os.path.join(args.o, "%08d.png" % 1), h, w)
    out_idx = 2
    try:
        with torch.no_grad():
            for pair in range(n - 1):
                f1 = load_frame(files[pair + 1], device, use_half)
                if engine in ("v4", "rpr_v7"):
                    f1 = F.pad(f1, pad)
                if engine == "v4":
                    mids = [
                        interpolate_v4(flownet, encode, f0, f1, (k * 1.0) / mult, ph, pw, ten_flow_div, grid, device, use_half)
                        for k in range(1, mult)
                    ]
                elif engine == "hdv2":
                    mids = gen_chain_hdv2(lambda a, b: interpolate_hdv2(flownet, warp, a, b), f0, f1, depth)
                else:  # rpr_v7
                    mids = [interpolate_rpr(flownet, warp, f0, f1, (k * 1.0) / mult, use_half) for k in range(1, mult)]
                for mid in mids:
                    save_frame(mid, os.path.join(args.o, "%08d.png" % out_idx), h, w)
                    out_idx += 1
                save_frame(f1, os.path.join(args.o, "%08d.png" % out_idx), h, w)
                out_idx += 1
                f0 = f1
                if (pair + 1) % 100 == 0:
                    log(f"已处理 {pair + 1}/{n - 1} 组")
    except Exception as ex:
        # 安全帧率：设备/内存类错误打印 FATAL_GPU_ERROR 标记，由软件识别后停止或降级重试
        msg = f"{type(ex).__name__}: {ex}"
        low = msg.lower()
        if any(k in low for k in ("out of memory", "oom", "cuda error", "device-side assert",
                                  "bad_alloc", "device mismatch", "device busy")):
            print(f"[FATAL_GPU_ERROR] 设备错误或内存不足: {msg}", flush=True)
            sys.exit(2)
        print(f"ERROR: Offical 补帧失败: {msg}", flush=True)
        sys.exit(1)

    log(f"Offical RIFE 补帧完成，共 {out_total} 帧")
    sys.exit(0)


if __name__ == "__main__":
    main()
