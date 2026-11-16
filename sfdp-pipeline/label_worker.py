# -*- coding: utf-8 -*-
"""教师打标：用 SfdpTeacher.exe 把 tiles 目录批量转成 labels CSV。

并发 workers（默认 10）跑满 CPU；已存在 <dir>.csv 的目录自动跳过；
不加载 torch / cv2，避免和训练同时启动时触发 WinError 1455。
"""
import os
import subprocess
import threading
import time
from collections import deque

from common import load_config, log, sleep_until, stop_time

_print_lock = threading.Lock()


def say(msg):
    with _print_lock:
        log("teacher", msg)


def task_worker(cfg, queue, stats):
    teacher = cfg["teacher_exe"]
    tcfg = cfg["teacher"]
    tiles = cfg["tiles_primary"]
    labels = cfg["labels_dir"]

    while True:
        try:
            d = queue.popleft()
        except IndexError:
            return
        out = os.path.join(labels, d + ".csv")
        if os.path.exists(out) and os.path.getsize(out) > 0:
            stats["skip"] += 1
            continue
        try:
            proc = subprocess.run(
                [teacher, os.path.join(tiles, d), out,
                 "--mode", tcfg["mode"], "--crop", str(tcfg["crop"]),
                 "--per-pair", str(tcfg["per_pair"]), "--limit", "0"],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                timeout=tcfg["timeout_sec"])
            if proc.returncode == 0:
                stats["ok"] += 1
            else:
                stats["fail"] += 1
                say(f"FAIL {d} exit={proc.returncode}")
        except Exception as e:
            stats["fail"] += 1
            say(f"ERROR {d}: {e!r}")


def scan_todo(cfg):
    tiles = cfg["tiles_primary"]
    labels = cfg["labels_dir"]
    os.makedirs(labels, exist_ok=True)
    if not os.path.isdir(tiles):
        return []
    dirs = sorted(d for d in os.listdir(tiles) if os.path.isdir(os.path.join(tiles, d)))
    todo = [d for d in dirs if not (os.path.exists(os.path.join(labels, d + ".csv"))
                                    and os.path.getsize(os.path.join(labels, d + ".csv")) > 0)]
    say(f"SCAN dirs={len(dirs)} todo={len(todo)}")
    return todo


def run_round(cfg):
    todo = scan_todo(cfg)
    if not todo:
        return 0
    queue = deque(todo)
    stats = {"ok": 0, "skip": 0, "fail": 0}
    workers = max(1, int(cfg["teacher"]["workers"]))
    threads = [threading.Thread(target=task_worker, args=(cfg, queue, stats), daemon=True)
               for _ in range(workers)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    say(f"ROUND done ok={stats['ok']} skip={stats['skip']} fail={stats['fail']}")
    return stats["fail"]


def main():
    cfg = load_config()
    stop_ts = stop_time(cfg)
    interval = cfg["teacher"]["interval_sec"]
    while True:
        started = time.time()
        run_round(cfg)
        elapsed = time.time() - started
        wait = max(0, interval - elapsed)
        if not sleep_until(wait, stop_ts):
            say("到达停机时间，退出")
            return


if __name__ == "__main__":
    main()
