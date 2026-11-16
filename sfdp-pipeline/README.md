# SFDP 训练流水线（重写版）

原先跑在服务器上的「B 站下载 → 裁剪/切片 → 教师打标 → 训练 → ONNX 导出」脚本丢失后按原设计重写。
训练/切片/打标的**核心实现**仍然复用 `dist/easy4k-sfdp-siamese-net/code/siai_all.py`（未丢），
这里补齐的是当时丢失的**编排层**：四链路并行、固态滚动缓存、监控与自动停机。

## 文件

| 文件 | 作用 |
| --- | --- |
| `config.json` | 所有路径、间隔、并发、缓存策略、停机时间 |
| `pipeline.py` | 主入口：四条链路并行（下载 / 切片 / 打标 / 训练）+ 可选热缓存链路 |
| `hot_cache.py` | 固态滚动缓存：每 10 分钟一轮，清理 90 分钟未用的旧目录，从机械盘迁入最新目录（每轮最多 300 个，固态至少留 15GB） |
| `label_worker.py` | 教师打标并发调度（默认 10 进程），已打标目录自动跳过 |
| `monitor.py` | 每分钟输出 tiles/labels 数量、热盘/主盘剩余空间、GPU 利用率，写 `logs/status.json` |
| `run_pipeline.ps1` | Windows 启动器：后台运行、崩溃 5 秒后自动重启、日志落盘 |
| `common.py` | 配置加载、日志与子进程封装 |

## 快速开始

1. 改 `config.json`：
   - `python`：本机 Python 路径
   - `siai_script` / `teacher_exe`：训练脚本与教师程序的真实路径
   - `tiles_primary`（机械盘 tiles 主目录）、`tiles_hot`（固态缓存目录）、`labels_dir`
   - `stop_at`：如 `"2026-12-01 15:00"`，到点四条链路自动停；留空表示一直跑
2. 启动：
   ```powershell
   powershell -ExecutionPolicy Bypass -File run_pipeline.ps1
   ```
3. 看状态：
   - `logs/pipeline.out.log`：总日志
   - `logs/download.log` / `tiles.log` / `teacher.log` / `train.log`
   - `logs/status.json`：每分钟刷新

## 与原流水线的对应关系

- 四链路并行：`pipeline.py` 里的 download / tiles / teacher / train 四个线程
- 打标 10 并发：`label_worker.py` 的 `teacher.workers`
- 固态滚动缓存：`hot_cache.py`，参数对应「10 分钟一轮 / 90 分钟闲置清理 / 每轮最多 300 个 / 至少保留 15GB」
- 训练时优先读热盘：`pipeline.py` 会设置 `SFDP_TILES=<tiles_hot>`，`siai_all.py` 找不到时自动回退主盘目录
- 每轮训练导出 FP32 + FP16 ONNX：由 `siai_all.py train` 完成，产物用 `siai_all.py pack` 可同步到 `Tools\SFDP`

## 注意

- 训练与打标同时跑时，打标进程**不要 import torch/cv2**（`label_worker.py` 已遵守），否则容易触发 WinError 1455。
- 热缓存复制采用「先复制到 `*.copying` 再改名」，训练不会读到半份数据。
- 删除文件后不要往同一块盘大量写入，避免覆盖可恢复数据。
