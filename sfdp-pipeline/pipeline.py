# -*- coding: utf-8 -*-
"""SFDP 四链路训练流水线（重写版）。

四条链路并行、互不阻塞：
  1) 下载    B 站采集（siai_all.py rank）
  2) 切片    raw → tiles（siai_all.py tiles）
  3) 打标    SfdpTeacher 并发 10（label_worker.py）
  4) 训练    train（每轮结束导出 FP32/FP16 ONNX）
另外可选第五条：固态滚动缓存（hot_cache.py）。

用法：python pipeline.py [--config config.json]
停止：Ctrl+C；或在 config.json 里设置 stop_at（如 "2026-12-01 15:00"）。
"""
import argparse
import os
import subprocess
import sys
import threading
import time

from common import BASE, load_config, log, log_file, sleep_until, stop_time

_stop = threading.Event()


def link_loop(cfg, name, build_args, interval, pre_run=None):
    """一条链路：执行 → 等待 interval → 再执行；异常后 60s 重试。"""
    while not _stop.is_set():
        try:
            if pre_run:
                pre_run()
            args = build_args()
            if args:
                log(name, "RUN " + " ".join(args))
                with log_file(cfg, name) as out:
                    code = subprocess.run(args, stdout=out, stderr=subprocess.STDOUT).returncode
                log(name, f"EXIT {code}")
            else:
                log(name, "本链路未配置命令，跳过")
        except Exception as e:
            log(name, f"ERROR {e!r}")
        wait = interval if interval > 0 else 5
        if not _sleep(wait):
            return


def _sleep(seconds):
    end = time.time() + seconds
    while time.time() < end:
        if _stop.is_set():
            return False
        time.sleep(min(2, end - time.time()))
    return True


def build(cfg):
    py = cfg["python"]
    script = cfg["siai_script"]

    def download():
        return [py, script] + list(cfg["download"]["extra_args"])

    def tiles():
        return [py, script] + list(cfg["tiles"]["extra_args"])

    def teacher():
        return [py, os.path.join(BASE, "label_worker.py")]

    def train():
        return [py, script] + list(cfg["train"]["extra_args"])

    def hot_cache():
        return [py, os.path.join(BASE, "hot_cache.py")]

    return download, tiles, teacher, train, hot_cache


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", default=None)
    args = ap.parse_args()
    cfg = load_config(args.config)

    stop_ts = stop_time(cfg)
    if stop_ts:
        log("pipeline", f"自动停机时间：{cfg['stop_at']}")

    download, tiles, teacher, train, hot_cache = build(cfg)
    env_tiles = os.environ.get("SFDP_TILES") or cfg["tiles_hot"]
    os.environ["SFDP_TILES"] = env_tiles
    log("pipeline", f"SFDP_TILES={env_tiles}")

    threads = [
        threading.Thread(target=link_loop, args=(cfg, "download", download, cfg["download"]["interval_sec"]), daemon=True),
        threading.Thread(target=link_loop, args=(cfg, "tiles", tiles, cfg["tiles"]["interval_sec"]), daemon=True),
        threading.Thread(target=link_loop, args=(cfg, "teacher", teacher, cfg["teacher"]["interval_sec"]), daemon=True),
    ]
    if cfg["hot_cache"].get("enabled", True):
        threads.append(threading.Thread(target=link_loop, args=(cfg, "hot_cache", hot_cache,
                                                               cfg["hot_cache"]["interval_sec"]), daemon=True))
    train_thread = threading.Thread(target=link_loop, args=(cfg, "train", train,
                                                            cfg["train"]["interval_sec"]), daemon=True)
    threads.append(train_thread)

    for t in threads:
        t.start()

    try:
        while any(t.is_alive() for t in threads):
            if stop_ts is not None and time.time() >= stop_ts:
                log("pipeline", "到达停机时间，正在退出")
                _stop.set()
                break
            time.sleep(5)
    except KeyboardInterrupt:
        log("pipeline", "收到 Ctrl+C，正在退出")
        _stop.set()

    for t in threads:
        t.join(timeout=10)
    log("pipeline", "已退出")


if __name__ == "__main__":
    sys.exit(main())
