# -*- coding: utf-8 -*-
"""固态滚动缓存管理（C:\\sfdp-hot）。

规则（与原流水线一致）：
· 每轮间隔 interval_sec（默认 600s）；
· 清理：热盘里 mtime 超过 idle_minutes（默认 90 分钟）且主盘已有同名目录的旧目录；
· 迁入：按主盘目录 mtime 从新到旧，把尚未缓存的最新目录复制到热盘，每轮最多 max_dirs_per_round（默认 300）；
· 热盘剩余空间低于 min_free_gb（默认 15GB）时停止迁入。
"""
import os
import shutil
import time

from common import load_config, log, sleep_until, stop_time


def free_gb(path):
    try:
        usage = shutil.disk_usage(path)
        return usage.free / (1024 ** 3)
    except Exception:
        return 0.0


def list_dirs(path):
    try:
        return [d for d in os.listdir(path) if os.path.isdir(os.path.join(path, d))]
    except FileNotFoundError:
        return []


def copy_dir(src, dst):
    """先复制到临时目录再改名，避免训练读到半份数据。"""
    tmp = dst + ".copying"
    if os.path.exists(tmp):
        shutil.rmtree(tmp, ignore_errors=True)
    shutil.copytree(src, tmp)
    if os.path.exists(dst):
        shutil.rmtree(dst, ignore_errors=True)
    os.rename(tmp, dst)


def round_once(cfg):
    hc = cfg["hot_cache"]
    primary = cfg["tiles_primary"]
    hot = cfg["tiles_hot"]
    os.makedirs(hot, exist_ok=True)

    primary_dirs = set(list_dirs(primary))
    hot_dirs = list_dirs(hot)
    now = time.time()
    idle_sec = hc["idle_minutes"] * 60

    # 1) 清理长时间未访问且主盘有备份的热盘目录
    removed = 0
    for d in hot_dirs:
        p = os.path.join(hot, d)
        try:
            if now - os.path.getmtime(p) > idle_sec and d in primary_dirs:
                shutil.rmtree(p, ignore_errors=True)
                removed += 1
        except FileNotFoundError:
            continue
    hot_dirs = set(list_dirs(hot))

    # 2) 从主盘迁入最新的未缓存目录
    candidates = []
    for d in primary_dirs - hot_dirs:
        p = os.path.join(primary, d)
        try:
            candidates.append((os.path.getmtime(p), d))
        except FileNotFoundError:
            continue
    candidates.sort(reverse=True)

    copied = 0
    for _, d in candidates:
        if copied >= hc["max_dirs_per_round"]:
            break
        if free_gb(hot) < hc["min_free_gb"]:
            log("hot_cache", f"剩余空间不足 {hc['min_free_gb']}GB，停止迁入")
            break
        try:
            copy_dir(os.path.join(primary, d), os.path.join(hot, d))
            copied += 1
        except Exception as e:
            log("hot_cache", f"COPY_FAIL {d}: {e!r}")

    log("hot_cache", f"round done: removed={removed} copied={copied} "
                     f"hot={len(list_dirs(hot))} free={free_gb(hot):.1f}GB")


def main():
    cfg = load_config()
    hc = cfg["hot_cache"]
    if not hc.get("enabled", True):
        log("hot_cache", "已禁用（config.hot_cache.enabled=false）")
        return
    stop_ts = stop_time(cfg)
    while True:
        try:
            round_once(cfg)
        except Exception as e:
            log("hot_cache", f"ROUND_ERROR {e!r}")
        if not sleep_until(hc["interval_sec"], stop_ts):
            log("hot_cache", "到达停机时间，退出")
            return


if __name__ == "__main__":
    main()
