# -*- coding: utf-8 -*-
"""流水线状态监控：每分钟输出一次磁盘、队列、GPU 利用率，并写 logs/status.json。"""
import json
import os
import shutil
import subprocess
import time
from datetime import datetime

from common import BASE, load_config, log

INTERVAL = 60


def count_dirs(path):
    try:
        return len([d for d in os.listdir(path) if os.path.isdir(os.path.join(path, d))])
    except FileNotFoundError:
        return 0


def count_files(path, suffix=""):
    try:
        return len([f for f in os.listdir(path) if f.endswith(suffix)])
    except FileNotFoundError:
        return 0


def free_gb(path):
    try:
        return round(shutil.disk_usage(path).free / (1024 ** 3), 1)
    except Exception:
        return -1


def gpu_usage():
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=utilization.gpu,memory.used,memory.total",
                              "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=8).stdout.strip()
        return out.splitlines()[0] if out else "n/a"
    except Exception:
        return "n/a"


def main():
    cfg = load_config()
    os.makedirs(cfg["log_dir"], exist_ok=True)
    while True:
        status = {
            "time": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
            "tiles_primary": count_dirs(cfg["tiles_primary"]),
            "tiles_hot": count_dirs(cfg["tiles_hot"]),
            "labels": count_files(cfg["labels_dir"], ".csv"),
            "free_hot_gb": free_gb(cfg["tiles_hot"]),
            "free_primary_gb": free_gb(cfg["tiles_primary"]),
            "gpu": gpu_usage(),
        }
        log("monitor", json.dumps(status, ensure_ascii=False))
        try:
            with open(os.path.join(cfg["log_dir"], "status.json"), "w", encoding="utf-8") as f:
                json.dump(status, f, ensure_ascii=False, indent=2)
        except Exception as e:
            log("monitor", f"WRITE_STATUS_FAIL {e!r}")
        time.sleep(INTERVAL)


if __name__ == "__main__":
    main()
