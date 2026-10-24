# Easy4K Bug 修复清单

口径（本次所有改动都围绕它）：**拆帧 → 全部超分 → 分开（挑保留帧）→ 补帧（只补保留帧）→ 全部合并（回填）。**
即：超分对全部源帧跑；去重只省补帧的算力；合并时回填成完整帧数，时长/帧率不变。

---

## BUG-01 补帧一开始就崩溃（rife-ncnn-vulkan exit=-1073741819）

- **现象**：补帧刚起跑就 `[exit=-1073741819]`，stderr 反复刷
  `decode image .../if_input_kept/_subset_ok.json failed`。GPU 本身正常（正常打印设备信息后崩）。
- **原因**：补帧输入子集的"完成标记"文件 `_subset_ok.json` 被写在了**帧目录里面**。
  RIFE 会把 `-i` 输入目录里的**每一个条目**都当图片去解码，多一个 json 直接崩。
- **修复**：标记移到帧目录**外面**（`if_input_kept.done.json`，与目录同级）；
  清理子集时改为清**全部文件**（不再只清 `*.png`），历史版本残留的 json 一并清掉。
- **位置**：`Services/ProcessingOrchestrator.cs` → `KeptSubsetMarkerPath` / `TryBuildKeptSubsetAsync`
- **验证**：T1 实测补帧正常跑完 1200 帧；子集目录内已无非 png 文件。

## BUG-02 重启后 `Access to the path '...\if_input_kept\00000001.png' is denied`，整个任务失败

- **现象**：补帧崩溃后再次启动，日志
  `帧去重：构建补帧输入子集失败：Access to the path 'D:\SR\DEMO\if_input_kept\00000001.png' is denied.`
  然后 `帧去重：构建补帧输入子集失败（保留帧与超分产物对不上号）`，任务直接失败。
- **原因**：清理旧子集用的是 `Directory.Delete(dir, true)`，只要目录里**有一个**文件被残留进程 /
  杀毒实时扫描占用，整个删除就抛异常，任务整体判失败。
- **修复**：逐个文件删除 + 退避重试（`TryDeleteWithRetry`）；复制同样带重试（`CopyWithRetry`）；
  真清不掉就自动换**备用目录** `if_input_kept_b` 重试，绝不因为个别被占用的文件让任务失败。
- **位置**：`Services/ProcessingOrchestrator.cs` → `EnsureKeptSubsetAsync` / `TryBuildKeptSubsetAsync`
- **验证**：T1/T3 实测无 Access denied；备用目录分支逻辑已就位。

## BUG-03 重启后把已经拷好的子集删了重拷，还报"对不上号"

- **现象**：补帧已经做完，重启后又把 `if_input_kept` 删掉重拷一遍，拷完仍报错对不上号。
- **原因**：没有"这活儿已经干完了"的凭据，每次启动都无条件重建子集。
- **修复**：加完成标记 `if_input_kept.done.json`，记录**帧数 / 字节数 / 源目录 / 保留帧列表指纹**；
  四项全部一致就直接复用，不再删、不再拷。指纹不一致（比如这次勾了超分、上次没勾）才重建。
- **位置**：`Services/ProcessingOrchestrator.cs` → `KeptSubsetMarker` / `KeptSubsetUsable` / `EnsureKeptSubsetAsync`
- **验证**：T3 模拟"补帧崩溃后重启"，日志：
  `帧去重：补帧输入子集已存在且校验一致（240 帧），直接复用`；
  `if_input_kept` 内文件时间戳未变（确实没删没重拷），随后补帧直接跑完。

## BUG-04 删帧文件时窗口"未响应"（崩溃后重启尤其明显）

- **现象**：崩溃后重启，删几千个残留帧时界面卡死、转圈"未响应"。
- **原因**：`RunAsync` 是在 UI 线程上跑的，删帧用的是同步 `Directory.GetFiles` +
  `File.Delete` 循环，几千个文件把主线程占死；同类同步删除还有涡轮初始化里的整目录递归删除。
- **修复**：新增 `CleanPartialOutputAsync`：枚举 + 删除全部放进后台线程，串行路径与涡轮路径统一改用它。
- **位置**：`Services/ProcessingOrchestrator.cs` → `CleanPartialOutputAsync`，替换拆帧/超分/补帧各降级重试的清理点
- **说明**：没有改删除逻辑，只是把同一个删除动作挪到后台线程并顺带上报进度。

## BUG-05 挑帧（搬移）阶段没有任何进度、提示和日志

- **现象**：挑几千个保留帧复制/清理时，进度条不动、状态文字不更新、日志也没有一行，看起来像卡死。
- **原因**：该阶段只做文件复制，没有 `ProgressChanged`、也没有 `_logger` 输出。
- **修复**：清理旧子集、复制保留帧两个循环都每 200 个文件上报一次：
  进度条（`Current/Total` + 百分比）+ 状态明细 + 写日志，例如
  `帧去重：准备补帧输入（清理上次残留）x/y`、`帧去重：准备补帧输入（保留帧）x/y`；
  各阶段清理残留也上报 `${stageText}（清理上次残留）x/y`。
- **位置**：`Services/ProcessingOrchestrator.cs` → `TryBuildKeptSubsetAsync` / `CleanPartialOutputAsync`

## BUG-06 显存溢出后"自动降级重试"其实无效

- **现象**：`vkAllocateMemory failed -2` 后降级重试仍然失败/崩溃。
- **原因**：降级只把线程降到 `1:1:1`，没打开省显存模式；显存不够时降线程往往不够。
- **修复**：补帧降级重试同时打开 `-u`（UHD 省显存）：`useUhdMode: true`。
- **位置**：`Services/ProcessingOrchestrator.cs` → 补帧 NCNN 分支的 `_fatalGpuError` 降级；`RifeCommandBuilder.Build`

## BUG-07 检测不到盘符时启动崩溃

- **现象**：`appsettings.json` 里 `TempRoot` 写成绝对路径（如 `H:\SR`），目标机没有 H: 盘，
  启动即 `DirectoryNotFoundException`，软件打不开。
- **修复**：`EnsureWritableDir`：目录创建失败时改用默认目录（`<可写根>/Temp`、`<可写根>/Output`）并打警告，不再崩溃。
- **位置**：`ViewModels/MainViewModel.cs`

## BUG-08 强制清理临时目录没清补帧子集

- **现象**：点"强制清理临时目录"，`if_input_kept` / 其完成标记仍残留。
- **修复**：`CleanTempIn` 与 `CleanTempInAsync` 的白名单都补上
  `if_input_kept`、`if_input_kept_b`、`if_input_kept.done.json`、`if_input_kept_b.done.json`。
- **位置**：`ViewModels/MainViewModel.cs`

## BUG-09 Release/Debug 产物缺 ffmpeg / ffprobe

- **现象**：打包产物运行时报"视频信息检测失败"。
- **原因**：`csproj` 里 robocopy 用了 `/XD bin`，把 `Tools\FFmpeg-Lei\bin`、
  `officalrealsr\...\torch\bin` 一起排除了。
- **修复**：从 robocopy 排除列表里去掉 `bin`。
- **位置**：`Easy4K.csproj`

## BUG-10 启动崩溃 0xC000027B（STATUS_STOWED_EXCEPTION）

- **现象**：无参数正常启动也崩，退出码 `0xC000027B`。
- **原因**：增量构建留下的陈旧 XAML 资源（`.pri` / `.xbf`）。
- **修复**：删掉 `bin/obj` 下的 Debug 产物干净重建。
- **备注**：非代码问题，属构建环境。

## BUG-11 高级设置里的小字与插图延迟显示

- **现象**：进「高级模式」，卡片右下角的两张插图要等一下才冒出来，旁边的小字也跟着"跳"一下 / 看起来是延迟出现。
- **原因**（两个叠加）：
  1. 插图原图是 **3840×2160 的 4K PNG**（每张约 190KB），页面上只显示 **150px 宽**。
     代码在 `Loaded` 之后才用 `SetSourceAsync` 按**原始尺寸**解码，8.29 M 像素 / 几十 MB 内存，
     而且两张是**串行** `await`（第二张要等第一张解完）。解码期间图是 `Collapsed`。
  2. 插图所在的 Grid 列是 `Width="Auto"`、图初始 `Collapsed` —— 图一出现，列宽从 0 变 150，
     左侧那列的小字（`FontSize=10`，`TextWrapping=Wrap`）被挤着**重新折行**，看起来就是"小字也延迟出现"。
- **修复**：
  - 解码时就用 `DecodePixelWidth = 300`（按逻辑像素）让解码器**直接缩到显示尺寸**，
    像素量降到约 1/166；解码结果按文件路径**静态缓存**，反复进页面/切主题不再重解。
  - 两张插图**并行**加载，不再串行。
  - 在**构造函数**里就发起加载（与页面构建、布局重叠），不等 `Loaded`。
  - XAML 里给两张图写死 `Width="150" Height="84"`（与 16:9 原图一致），图片出现时不再挤动小字。
- **位置**：`AdvancedPage.xaml`（两张 Image）、`AdvancedPage.xaml.cs`（`ApplyCardArt` / `LoadThemeArtAsync`）

## BUG-12 Offical（PyTorch）补帧在 RTX 显卡上仍走 CPU，无法用 CUDA

- **现象**：机器是 RTX 系列，Offical 补帧引擎照样按 CPU 跑，日志出现
  `Offical 引擎未检测到可用 GPU（CUDA），本次补帧降级为 CPU 推理`。
- **原因**：**依赖层面，不是软件代码的问题。** 捆绑的便携 Python 里装的是
  **CPU-only 版 PyTorch**。实测 `Tools\officalrife\python\python.exe -c "import torch; ..."`：
  ```
  torch 2.13.0+cpu      ← "+cpu" = CPU-only 构建
  cuda_ver None
  is_available False
  ```
  该 python 目录总共只有 0.54 GB，`torch\lib` 下没有任何 `cudart / cublas / cudnn` 动态库
  （CUDA 版光 lib 就有 2~3 GB）。
  而 `Tools\officalrife\run.py` 的设备选择是
  `torch.device("cuda" if torch.cuda.is_available() else "cpu")`（仅 `-cpu` 时强制 CPU），
  CPU-only 构建下这个判断恒为 False，所以插任何 RTX 卡都不会走 CUDA。
  C# 侧逻辑正确：只有勾选「使用CPU处理所有模型」才会传 `-cpu`。
- **最终处置（用户决定）**：**整条 Offical 引擎砍掉**。理由是：要用上 CUDA 必须把 PyTorch 换成
  CUDA 版（下载 2.4 GB、解包 3.5 GB，发布 ZIP 从 5.25 GB 涨到 8~9 GB），而 NCNN（Vulkan）
  在 N / A / I 卡上本来都能跑，不值得为一个只能靠 CPU 落地的引擎背这个体积。
  - **删除的资产**：`Tools\officalrife\models`（466.7 MB 的 pkl 模型）、便携 python 里的
    `torch` / `torchgen` / `functorch` 及其专属依赖 `sympy / mpmath / networkx / filelock /
    fsspec / jinja2 / markupsafe`（约 456 MB）、`run.py` 与 `model\` 下的 IFNet 定义。
    `Tools\officalrife` 目录整体消失，**`Tools` 从 6482.3 MB 降到 5556.8 MB（省 925.5 MB）**。
  - **保留的解释器**：那个便携 Python（`python.exe` + numpy / Pillow / onnxruntime）被
    **AudioSR 音频超分复用**（`AudioSRCommandBuilder` 的驱动脚本靠它运行），因此
    挪到 `Tools\python\` 并保留（实测挪动后 numpy / Pillow 正常，`python314._pth` 全是相对路径）。
  - **代码/界面一并摘除**：删除 `OfficalRifeCommandBuilder`；`ProcessingOptions.IfEngine`
    与 `AppSettings.DefaultIfEngine` 移除；补帧分支只剩 NCNN（原 if/else 拉平）；
    `TurboSupported` 不再排除 Offical；`RunStageWithDirectoryPolling` 去掉 `forcedCpu`
    与 `[IF_DEVICE]` 处理；主界面「补帧设置」删掉"种类"下拉与说明弹窗（只留"模型"）；
    超分/去重/自动测试/关于/向导/高级页四语文案里的 Offical 全部清掉；
    `ToolPaths.OfficalPythonExe` → `PortablePythonExe`，`OfficalRifeDir` 等字段删除。
- **位置**：`Tools\`（资产）、`ProcessingOrchestrator.cs`、`ToolPaths.cs`、`ToolPathResolver.cs`、
  `ProcessingOptions.cs`、`AppSettings.cs`、`ToolPaths.cs`、`MainViewModel*.cs`、
  `MainPage.xaml(.cs)`、`AdvancedPage.xaml`、`MainWindow.xaml.cs`、`WelcomeWindow.xaml`、
  `Loc\strings.json`、`Easy4K.csproj`
- **验证**：编译 0 错误；删除后跑通两轮 NCNN 端到端（无超分/带超分各一次，见下 T6/T7）。

## BUG-13 AudioSR 完全没有跑在 GPU 上

- **现象**：勾选音频超分后，任务能跑完，但推理实际走的是 CPU，速度慢且看不出原因。
- **原因**：**随包便携 Python 里根本没有 onnxruntime**。实测它 import 到的是开发机用户目录里的
  `%APPDATA%\Python\Python314\site-packages\onnxruntime`（这份恰好是 `onnxruntime_directml`，所以
  开发机上"看起来能用"）。换一台干净机器：轻则 import 失败 → 整个音频超分静默回退原音轨，
  重则捡到一份 **CPU 版 onnxruntime**（没有 `DmlExecutionProvider`）→ 永远走 CPU，
  日志里只有一句"回退 CPU 推理"。
- **修复**：
  1. 把 `onnxruntime`（**DirectML** 版）及其运行期依赖（`flatbuffers` / `google`(protobuf) / `packaging`）
     装进随包便携 Python 的 `Lib\site-packages`，不再依赖用户机器上的环境；
     `Tools\python` 从 94 MB 增到 169 MB（`DirectML.dll` 17.7 MB + `onnxruntime.dll` 20.1 MB 在内）。
  2. 实测不需要 `sympy`（onnxruntime 的 METADATA 里列了，但推理路径不 import），故不打包，省 74 MB。
  3. 日志把后端显式写出来：`音频超分推理后端：DirectML（GPU）` / `…：CPU（…）`，
     避免"到底跑没跑显卡"只能靠猜。
- **位置**：`Tools\python\Lib\site-packages`（依赖）、`Services/ProcessingOrchestrator.cs`（后端提示）
- **验证**：便携 python 里 `ort.__file__` 指向随包目录、`get_available_providers()` 含
  `DmlExecutionProvider`；用 `models-fp16` 的真实 `vae_decoder.onnx` / `ddpm.onnx` 在
  DirectML 上各跑一次最小推理，输出均为有限值；整条 `--selftest` 跑通并打印
  `音频超分推理后端：DirectML（GPU）`（FP16 档，48kHz WAV 正常产出）。

## BUG-14 低主频 CPU 下有概率 UI 死锁（日志过长）

- **现象**：跑一段时间后界面卡死（点不动、进度不走），低主频 CPU 上更容易出现。
- **原因**：日志链路是**逐行**切 UI 线程的，而且每次都要把整段文本重排一遍：
  `Logger.Add` → `DispatcherQueue.TryEnqueue`（1 次）→ 界面 `OnLogEntryAdded` 里再
  `TryEnqueue`（第 2 次）→ 字符串拼接 + `Split(200 行)` + `Join` + 整个 `TextBox.Text` 重新赋值 →
  再 `TryEnqueue` 滚到底（第 3 次）。ffmpeg / 外部工具的 stderr 是**突发**的（一次几十上百行），
  低主频 CPU 上 UI 线程被这些回调彻底占满，消息队列只进不出，表现就是"死锁"。
- **修复**：
  - `Logger`：写日志只进内存缓冲，UI 侧由 `Timer` **每 100 ms 批量刷一次**
    （事件从 `EntryAdded` 改为批量的 `EntriesAdded`），UI 线程工作量与日志速率解耦；
    定时器回调整体兜异常（定时器线程里抛出未捕获异常会直接崩进程）。
  - `ProgressPage`：维护 200 行**行缓冲**，一批日志只重建一次文本、只滚一次底；
    单行超过 1200 字符先截断再展示（完整内容仍在 Logger 的完整日志里，导出/报告不受影响）。
- **位置**：`Services/Logger.cs`、`ProgressPage.xaml.cs`
- **验证**：多轮自测（含 stderr 突发的 ffmpeg 阶段）日志完整、任务正常收尾；日志区不再逐行重排。

## BUG-15 Turbo 模式警告弹窗只出现一次（应每次启动提醒一次）

- **现象**：涡轮模式 + 机械盘的警告只在"手动切换涡轮"那一次弹过，之后重启软件（配置里涡轮仍是开的）
  再也不提醒；块大小越界的警告也不是"每次启动一次"。
- **原因**：机械盘检查只挂在 `OnTurboToggled`（用户手动切换）上，启动时从不检查；
  块大小警告用的是页面实例字段，重进页面会重新计数。
- **修复**：`AdvancedPage` 增加 `_turboDiskWarnedThisLaunch`（static）：页面加载时若
  `Vm.TurboMode` 已开启且临时目录在机械盘，就按同一套弹窗提醒一次，并把涡轮关掉；
  块大小警告的 `_turboBlockWarned` 也改成 static（语义 = 每次启动最多提醒一次）。
- **位置**：`AdvancedPage.xaml.cs`
- **验证**：编译通过；逻辑上"每次启动一次、每次启动最多一次"。

## BUG-16 FFmpeg 合并时未把已判定的重复帧恢复（成品少帧）

- **现象**：开了帧去重后合并，重复帧没有回填回来，成品比原片短。
- **原因**：回填（`TryExpandDedupFramesAsync`）失败时**静默继续**：
  `if (await TryExpand...) framesForMerge = expandedDir;` 没有 else 分支，
  于是 FFmpeg 直接拿"去重后的少帧序列"去合并；另外回填内部逐帧 `File.Copy` 的
  异常是 `catch { }` 跳过的，个别帧复制失败也会导致展开帧数不足而没人发现。
- **修复**：
  1. 回填内部增加**严格数量校验**：展开帧数必须等于 `槽位数 × 倍率`，不符就返回失败并给出数量对比。
  2. 调用方改为**失败即停**：`帧去重回填失败：为避免产出一支少帧的成品，本次已停止…`，
     宁可停下来让人清理临时目录重跑，也不出少帧成品。
- **位置**：`Services/ProcessingOrchestrator.cs`（回填方法 + 合并阶段调用处）
- **验证**：`帧去重回填：60 帧 → 展开为 300 帧` 正常；数量校验通过。

## BUG-17 SFDP 判定过松（真实运动会漏判成重复帧）

- **现象**：SFDP 把一些"其实在动"的相邻帧判成重复帧删掉。
- **原因**：第 1 阶的局部判据只有"块内**平均差** ≥ 阈值"这一条。
  细块约 1/32 边长（1080p 下 33×33 ≈ 1089 像素），若块内只有几十个像素发生了真实运动
  （幅度十几个灰阶），平均到整块会被稀释到 τ_block = 2.0 以下 —— 平均差看不见、
  dHash（9×8）更看不见，于是被判成重复。默认档又是"性能档"（只跑前两阶），没有光流兜底。
- **修复**（只做加法，方向上只会更保守：少删、不误删）：
  1. 新增**块内"变化像素占比"判据**（`d > 10 灰阶` 的像素占块内比例）：细块 ≥ 2%、粗块组 ≥ 0.5%
     → 判为不同。补的正是"小面积/低幅度局部运动"这个盲区。
  2. 性能档 dHash 阈值 **5 → 4**（与完美档同水平）：64bit 哈希允许 5 位不同偏宽，
     只差几笔的相邻帧也会被一并判成重复。
- **位置**：`Services/FrameDedupService.cs`（`Options` / `Judge` / `FromMode`）
- **验证**：测试素材是"每组 5 帧完全相同"，剔除数仍为 48/60（说明收紧没有影响真正的重复帧），
  整条链路（去重→子集→补帧→回填→合并）照常跑通。
- **说明**：这两个数值（2% / 0.5% / dHash 4）是**保守取值**。若你的素材上仍然偏松，
  给我一段最能复现的片段，我可以按实测分布重新标定（旧值是上一轮用 1080p 动画素材标定的）。

## 作者 Fix：AudioSR 介入后改 PCM，无法判断重采样是否介入

- **现象**：音频链路一旦交给 AudioSR，音轨最后统一以 PCM 嵌入，日志里只有
  "音频超分直出，未重采样"，看不出中间到底有没有重采样过。
- **原因**：AudioSR 前会先把音轨转成 48 kHz WAV —— **源不是 48k 时这一步本身就是一次重采样**，
  但这一信息此前既没采集（ffprobe 只读了 codec_name）也没输出。
- **修复**：
  1. `VideoInfoDetector` 的音频探测改为同时读 `codec_name` 与 `sample_rate`
     （`VideoInfo.AudioSampleRate`）。
  2. 合并/嵌入音频时输出**完整链路**，两种分支都写清楚：
     - AudioSR 生效：`音频链路：源 44.1kHz → 转 48kHz（重采样） → AudioSR 升带宽 → 嵌入 PCM 2.0 24bit 48kHz（不再重采样）`
     - 未开/未生效：`音频链路：源 44.1kHz → ffmpeg 重采样 → 嵌入 PCM 2.0 24bit 96kHz`
- **位置**：`Models/VideoInfo.cs`、`Services/VideoInfoDetector.cs`、`Services/ProcessingOrchestrator.cs`
- **验证**：用 44.1kHz 音轨的 `test_video.mp4` 实跑，两种分支的链路行都正确打印。

## Feat：预计完成时间

- **做法**：进度条本身是"本阶段"的进度，所以按**本阶段已用时间 ÷ 本阶段进度**线性外推，
  阶段切换时重新计时；文案同时给剩余时长与预计完成时刻：
  `本阶段预计剩余 2 分 10 秒（约 18:42 完成）`。
  刚起步（< 3 秒）或进度过小（≤ 2%）时估算抖动太大，暂不显示。
- **位置**：`ViewModels/MainViewModel.cs`（`EtaText` + `BuildEtaText`）、`ProgressPage.xaml`
- **说明**：这是**阶段级** ETA（软件没有全局进度模型），所以文案写"本阶段"。
  要"整片预计完成时间"的话需要先做全局进度（各阶段按帧数加权），再说一声我加。

## BUG-18 SFDP 回填：重复段"段尾"被插入了过渡帧（幽灵帧）

- **现象**：开去重 + 补帧后，**每个重复段的最后一帧位置会出现一帧"混了下一段画面"的过渡帧**
  （定格段本该是纯定格，结果末尾冒出一帧渐变）——即"重复帧的位置被插入了不该在那的过渡帧"。
- **原因（算清楚了）**：RIFE 的 `-n`（目标总帧数）是**沿整条子序列时间轴均匀摊分**的，
  **不是"每个输入帧固定占 mult 帧"**。实测（12 个保留帧、`-n 60`）纯色段起点落在
  `out1,6,11,16,21,26,30,36,41,46,51,56` → 每段分别是 **5,5,5,5,5,5,4,6,5,5,5,5 帧**。
  而"相邻两个保留帧之间"这一段在成片里横跨 **L 个源槽位**（L = 该重复段的长度），
  RIFE 给这一段的 mult 帧是按整段摊的（≈ 每个源槽位 1 帧）。
  旧代码却把这 mult 帧当成"段尾那一帧的 mult 帧过渡"来用（`holdBlock` 只在"下一个槽位同段"时为真，
  段尾恒为假）→ 于是把"本段后面若干源槽位的画面（= 朝下一段渐变的混合帧）"搬到了段尾。
- **实测证据**（12 组纯色、每组 5 帧相同）：
  | 位置 | 修复前 | 修复后 |
  | --- | --- | --- |
  | 第 2 组段尾（第 50 帧，应为纯绿） | `(1,142,113)` 绿蓝混合 | `(1,254,0)` 纯绿 ✓ |
  | 第 11 组段尾（第 275 帧，应为纯青绿） | `(96,31,125)` 青紫混合 | `(1,126,127)` 纯青绿 ✓ |
- **修复**：回填时先按 `expandPos` 预扫出每个槽位所属**重复段长度**，规则改成：
  - 段长 > 1（该帧被判为与上一帧重复）→ **整段（含段尾）一律定格**，取该段首帧的产物；
  - 段长 == 1（该源帧没有重复、下一帧就是另一个源帧）→ 才用 RIFE 的过渡块
    （此时"相邻两个保留帧之间"正好等于该槽位的时长，取到的就是正确的运动插值）。
- **位置**：`Services/ProcessingOrchestrator.cs` → `TryExpandDedupFramesAsync`
- **验证**：
  - 12 组纯色素材：每段 25 帧全为纯色、段间硬切，段尾不再有混合帧 ✓
  - 混合素材（10 帧互不相同 + 10 帧完全相同 + 10 帧互不相同）：定格段 25 帧全纯色；
    真实运动段逐子帧渐变（源帧每帧 +23 灰阶 → 每子帧 ≈ +4.6）✓ 说明补帧没有被"一刀切"掉。

## BUG-19 无法判断 AudioSR 到底有没有介入（音频链路上全是 ffmpeg）

- **现象**：音频这一段前后都是 ffmpeg（提取音轨 → 转 48kHz WAV → 嵌入 PCM），
  日志里全是 `ffmpeg ...` 命令，看不出 AudioSR 是真跑了还是被兜底跳过了；
  产物的文件信息里也没有任何痕迹可以事后核对。
- **修复（三件事，互为佐证）**：
  1. **唯一的结论行**：音频阶段开头输出一条 `【音频处理来源】…`：
     - `AudioSR 已介入（FP16 低精度性能模式 / 后端 DirectML（GPU））`
     - `AudioSR 未介入（未勾选音频超分 / 驱动脚本缺失 / 权重包不完整 / FP16 与 CPU 互斥 /
       转 48k 失败 / 推理失败（退出码 N）/ 异常（…））`
     判定口径 = **是否真的拿到 AudioSR 的 48kHz 产物**（不是"勾了没有"）。
  2. **产物文件信息里写标记**（事后也能核对）：
     - 成品视频：`ffmpeg … -metadata comment="…"`（只覆盖 comment 字段，Title/日期等仍继承）
     - AudioSR 输出的 WAV：追加 `LIST/INFO` 块（`ISFT` + `ICMT`）
     - AudioSR 介入时用：`本音频超分辨率使用Easy4K 2 Pro软件中的AudioSR模块处理 请仔细辨别音频处理来源!`
     - 未介入时用：`本音频未使用Easy4K 2 Pro软件中的AudioSR模块（原音轨 + ffmpeg 重采样） 请仔细辨别音频处理来源!`
       （文案在 `Tools\AudioSR\audiosr_onnx.py` 与 `ProcessingOrchestrator` 两处，需保持一致）
  3. **成品实测**：产出后用 ffprobe 读音轨规格 + comment 标记并写进日志：
     `成品音频实测（ffprobe）：codec_name=pcm_s24le / sample_rate=48000 / channels=2 / TAG:COMMENT=…`
     （介入 → 48kHz；未介入 → 96kHz）
- **位置**：`Services/ProcessingOrchestrator.cs`（`AudioSrMark*` 常量、`RunAudioSrAsync`、
  音频阶段结论行、`ProbeAudioSummary`）、`Services/CommandBuilders/FFmpegCommandBuilder.cs`（`EmbedAudio` 的 comment 形参）、
  `Tools/AudioSR/audiosr_onnx.py`（`append_info_chunk` / `EASY4K_AUDIO_MARK`）
- **验证**：
  - AudioSR 介入（`test_video.mp4`，FP16）：日志 `【音频处理来源】AudioSR 已介入（…DirectML（GPU））`；
    成品实测 `pcm_s24le / 48000 / 2ch / TAG:COMMENT=本音频超分辨率使用Easy4K 2 Pro软件中的AudioSR模块处理 …`；
    AudioSR 输出的 WAV 也带 `TAG:encoder=Easy4K 2 Pro AudioSR` + 同一条 comment。
  - AudioSR 未介入（未勾选）：日志 `【音频处理来源】AudioSR 未介入（未勾选音频超分）`；
    成品实测 `pcm_s24le / 96000 / 2ch / TAG:COMMENT=本音频未使用Easy4K 2 Pro软件中的AudioSR模块（原音轨 + ffmpeg 重采样） …`。

---

## BUG-20 AudioSR 成品有刺耳的高频噪音（窄带尖峰）

- **现象**：部分歌曲经 AudioSR 后，成品某一段"刺耳/有高频噪音"，PotPlayer 看波形像某一声道"炸了"。
- **排查（全部用实测数据定位，不靠猜）**：
  1. **先排除低通**：4 种滤波器（butter/cheby1/ellip/bessel）× 8 阶 × 5 个截止频率（15~23kHz），
     在全频带信号上实测——峰值从不放大（0.5 → 最高 0.491）、无 NaN、长度正确。低通不是元凶。
     另：`LP_TYPE` 由 `np.random.seed(42)` 决定，固定选中 `ellip`，不存在"随机抽到烂滤波器"。
  2. **带宽检测也没坏**：原始 MP3 检测出 `真实带宽 ≈ 13312 Hz`（该 MP3 在 16–18kHz 只剩 -51.5 dB，
     是编码断崖），断崖之上的编码残留**已被低通切掉**。
  3. **病灶在成品的 15–16kHz**：一簇离散窄带尖峰（15586 / 15609 / 15680 / 15703 Hz），
     高出本地频谱趋势 **4.58 倍（+13.2 dB）**，而两侧是干净的单调衰减——自然音乐不可能这样。
     原始 MP3 该频段 -17.6 dB，成品 -6.9 dB（被抬高 10.7 dB）。
     → 属于扩散模型在**生成频段内的窄带幻觉**（CFG 3.5 偏高时的典型产物）；
       叠加"每个声道独立抽噪声"，听感就是"某个声道炸出一片高频"。
  4. **另有一个结构性缺陷**：时长向上取整到窗口整数倍时**末尾补零**。静音的 log-mel 被 clamp 到
     `log(1e-5) = -11.5129`，与 manifest 的无条件值 `uncond_value = -11.4981` 只差 0.015 →
     `v = v_u + 3.5*(v_t - v_u)` 退化成**纯无条件采样**，最后一个窗口在没有任何音频线索的情况下
     自由发挥；且该窗口只被自身覆盖的区域权重会被交叉淡化相互抵消（`acc/wsum`），噪声全额保留。
- **修复**（`Tools/AudioSR/audiosr_onnx.py`）：
  1. **末尾改「边缘延拓」**（`mode="edge"`，不再补零），让最后一个窗口始终看到延续的真实音频。
  2. **新增「高频护栏」后处理** `suppress_hf_artifacts()`，只作用于 **8kHz 以上**、只改幅度不动相位：
     - **窄带尖峰钳制**：每频点取跨帧 90 分位得到幅度谱 `M[f]`，与 ±1.13kHz 滑动平均趋势相比，
       超出 `HF_GUARD_MAX_EXCESS = 1.4`（+2.9dB）的按倍数压回趋势附近（下限 -20dB）；
     - **静音门控**：源信号低于峰值 -50dB 的时刻，保护带整体衰减（那里本就不该有声音）。
     - 阈值由**实测标定**：伪影尖峰 = 4.68 倍，正常频段最高仅 1.66 倍，分离度良好；
       增益不做跨频平滑（实测平滑会把单 bin 的 -12dB 修正稀释成 -6dB），只在 8kHz 起点做 6 bin 渐变。
  3. 保留 `--no-hf-guard` 开关，便于 A/B 对比（默认开启）。
- **验证**：
  - 对同一产物施加护栏：窄带尖峰最大衰减 **12.1 dB**，15–16kHz 频段 -4.04 dB；
    8kHz 以下总能量变化 **-0.0000 dB**（证明规则完全不碰低频）；其余频段 ≤1.15 dB。
  - STFT→ISTFT 往返本身透明（RMS 偏差 -90 dB），不会顺手劣化音频。
  - 真实歌曲端到端 A/B（110.7s 立体声，用时 58 分钟）：
    - 修复前：`15609Hz = 4.58 倍（+13.2 dB）`
    - 修复后：`15609Hz = 2.06 倍（+6.3 dB）`，15–16kHz 频段 **-3.52 dB**
    - 其余频段变化 ≤0.62 dB（8–13kHz +0.4~0.5dB、16–24kHz +0.07~0.43dB、13–14kHz -0.50dB、19–20kHz -0.62dB）
    - 产出对照文件：`Output\Glichery - Sea Of Problems (Explicit)_AudioSR_48k_已修复高频.wav`
      （与 `…_AudioSR_48k.wav` 直接对比试听）

---

## 验证状态

| 用例 | 内容 | 结果 |
| --- | --- | --- |
| T1 | 1200 帧 / 串行 / 去重开 / 补帧×5 / 超分关 / 合并 | 通过：拆帧 1200 → 剔除 960 保留 240 → 子集 240 → 补帧 1200 → 回填 6000 帧 @150fps，成品 6000 帧 |
| T2 | 60 帧 / 带超分（超分全算） | 通过：超分 60 帧（全部源帧）→ 子集 12 → 补帧 60 → 回填 300 帧 @150fps；**本体帧（j=0）60/60 全部等于该源帧自身的超分产物** |
| T3 | 模拟补帧崩溃后重启（不清理临时目录） | 通过：子集"直接复用"，文件未被删/未重拷，补帧→回填→合并全部跑完 |
| T4 | 强制复现"上次残留"现场（子集目录里塞 30 个无标记垃圾文件） | 通过：日志出现 `帧去重：准备补帧输入（清理上次残留）30/30`、`（保留帧）12/12`；30 个垃圾全清掉，无 Access denied、任务未失败，子集目录内无非 png 残留 |
| T5 | 涡轮模式（块 32，2 块）回归，带超分 | 通过：块级流水跑完，超分 60 / 补帧 60 → 回填 300 帧；本体帧 60/60 一致，子集目录无非 png 残留 |
| T6 | 砍掉 Offical 后回归：60 帧 / 去重关 / 补帧×2 / 合并 | 通过：拆帧 60 → 补帧 120 → 合并，全程 0 错误（输出 60fps） |
| T7 | 砍掉 Offical 后回归：60 帧 / 去重开 / 补帧×5 / 合并 | 通过：剔除 48 保留 12 → 子集 12（进度日志在）→ 补帧 60 → 回填 300 帧 @150fps |
| T8 | 日志节流 + 去重收紧 + 回填严格校验：60 帧 / 去重开 / 超分×2 / 补帧×5 / 合并 | 通过：剔除仍为 48/60（收紧未误伤真重复帧）→ 回填 300 帧 |
| T9 | 音频链路（未开 AudioSR）：`test_video.mp4`（44.1kHz 音轨）/ 拆帧+去重+合并+音频 | 通过：`音频链路：源 44.1kHz → ffmpeg 重采样 → 嵌入 PCM 2.0 24bit 96kHz` |
| T10 | AudioSR 跑 GPU 端到端：同上素材 + FP16 档 | 通过：`音频超分推理后端：DirectML（GPU）`，48kHz WAV 产出，链路行明确标出"转 48kHz（重采样）" |
| T11 | Logger 加异常兜底后回归：60 帧 / 去重开 / 补帧×5 / 合并 | 通过：剔除 48 → 回填 300 帧 → 合并完成 |
| T12 | RIFE `-n` 语义实测（4 张纯色帧 + `-n 8` / `-n 7` / `-n 16`） | 结论：`-n` = 目标总帧数，沿子序列时间轴**均匀摊分**，**不是每输入帧固定占 mult 帧** |
| T13 | 内容级审计（12 组纯色 × 5 帧相同，去重+补帧×5+合并） | 修复前：段尾出现混合帧（幽灵帧）；修复后：每段 25 帧全纯色、段间硬切 ✓ |
| T14 | 内容级审计（10 帧互不相同 + 10 帧相同 + 10 帧互不相同） | 定格段全纯色；真实运动段逐子帧渐变（每子帧 ≈ +4.6 灰阶，源帧步长 +23）→ 插值未被改死 ✓ |
| T15 | AudioSR 介入 + 产物标记（FP16 / DirectML） | 通过：`【音频处理来源】AudioSR 已介入（…DirectML（GPU））`；成品 `pcm_s24le / 48000 / 2ch` + 指定 comment；AudioSR 输出 WAV 带 `ISFT`+`ICMT` 标记 |
| T16 | AudioSR 未介入 + 产物标记（未勾选） | 通过：`【音频处理来源】AudioSR 未介入（未勾选音频超分）`；成品 `pcm_s24le / 96000 / 2ch` + "未使用"comment |

### 测试用素材提示
自测素材是"每 5 帧一组、组内 4 帧重复"，所以每组刚好过 `MinRunLength=3` 的时序平滑，剔除率 80%。
（早前用"每组 2 帧重复"的素材时，因低于 `MinRunLength=3` 被撤销，剔除 0 帧——那不是 bug，是时序平滑的保护。）
