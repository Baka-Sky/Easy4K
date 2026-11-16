# -*- coding: utf-8 -*-
"""SFDP 训练流水线公共工具：配置加载、日志、子进程执行。"""
import json
import os
import subprocess
import sys
import time
from datetime import datetime

BASE = os.path.dirname(os.path.abspath(__file__))


def load_config(path=None):
    path = path or os.path.join(BASE, "config.json")
    with open(path, "r", encoding="utf-8") as f:
        cfg = json.load(f)
    cfg.setdefault("log_dir", "logs")
    if not os.path.isabs(cfg["log_dir"]):
        cfg["log_dir"] = os.path.join(BASE, cfg["log_dir"])
    os.makedirs(cfg["log_dir"], exist_ok=True)
    return cfg


def log(channel, msg):
    line = f"[{datetime.now():%Y-%m-%d %H:%M:%S}] [{channel}] {msg}"
    print(line, flush=True)


def log_file(cfg, channel):
    path = os.path.join(cfg["log_dir"], f"{channel}.log")
    return open(path, "a", encoding="utf-8")


def run(cfg, channel, args, timeout=None, cwd=None):
    """同步执行一个子进程并把输出写进对应通道日志；返回退出码。"""
    log(channel, "RUN " + " ".join(args))
    started = time.time()
    try:
        with log_file(cfg, channel) as out:
            proc = subprocess.run(args, cwd=cwd, stdout=out, stderr=subprocess.STDOUT,
                                  timeout=timeout)
        code = proc.returncode
    except subprocess.TimeoutExpired:
        log(channel, f"TIMEOUT after {timeout}s")
        return -1
    except Exception as e:
        log(channel, f"ERROR {e!r}")
        return -2
    log(channel, f"EXIT {code} in {time.time() - started:.1f}s")
    return code


def stop_time(cfg):
    """返回停止时间戳（秒）；空字符串表示不自动停机。"""
    text = (cfg.get("stop_at") or "").strip()
    if not text:
        return None
    return datetime.strptime(text, "%Y-%m-%d %H:%M").timestamp()


def sleep_until(seconds, stop_ts=None):
    """可被停止时间打断的 sleep。返回 False 表示已到停机时间。"""
    end = time.time() + seconds
    while time.time() < end:
        if stop_ts is not None and time.time() >= stop_ts:
            return False
        time.sleep(min(5, end - time.time()))
    return stop_ts is None or time.time() < stop_ts


if __name__ == "__main__":
    cfg = load_config()
    log("common", f"配置加载成功：{json.dumps(cfg, ensure_ascii=False)[:200]}…")
    sys.exit(0)
