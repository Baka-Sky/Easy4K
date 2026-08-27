using System.IO;
using System.Text.RegularExpressions;
using Easy4K.Models;
using Easy4K.Services.CommandBuilders;

namespace Easy4K.Services;

/// <summary>处理上下文：把所有运行所需信息打包传给 Orchestrator</summary>
public sealed class ProcessingContext
{
    public string InputVideo { get; set; } = "";
    public string TempRoot { get; set; } = "";
    public string OutputRoot { get; set; } = "";
    /// <summary>手动导入的外部帧文件夹（非空时复制到 Temp/input_frames 并跳过拆帧）</summary>
    public string ExternalFramesDir { get; set; } = "";
    public VideoInfo Video { get; set; } = new();
    public ProcessingOptions Options { get; set; } = new();
    public string SrModel { get; set; } = "";
    public int SrScale { get; set; } = 2;
    public string IfModel { get; set; } = "";
    public int IfMultiplier { get; set; } = 2;
    public int HdrSaturation { get; set; } = 200;
    public int HdrContrast { get; set; } = 200;
    public GpuInfo Gpu { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public ToolPaths Tools { get; set; } = new();
}

/// <summary>处理流水线编排：拆帧 → 超分 → 补帧 → 合并 → 嵌入音频 → HDR 转换。
/// 实现 BUG-03/04/06/07/08 等运行时修复，进度解析。</summary>
public sealed class ProcessingOrchestrator
{
    private readonly ProcessRunner _runner;
    private readonly Logger _logger;

    public ProcessingOrchestrator(ProcessRunner runner, Logger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    /// <summary>当前阶段进度推送</summary>
    public event Action<ProcessProgress>? ProgressChanged;

    /// <summary>运行整个流水线。返回最终输出文件路径，失败返回 null。</summary>
    public async Task<string?> RunAsync(ProcessingContext ctx, CancellationToken ct)
    {
        var tempRoot = ctx.TempRoot;
        var inputFrames = Path.Combine(tempRoot, "input_frames").Replace('\\', '/');
        var srFrames = Path.Combine(tempRoot, "4k_frames").Replace('\\', '/');
        var ifFrames = Path.Combine(tempRoot, "output_frames").Replace('\\', '/');
        var tempVideo = Path.Combine(tempRoot, "temp_video.mkv").Replace('\\', '/');

        var totalFrames = ctx.Video.TotalFrames;
        if (totalFrames <= 0)
        {
            _logger.Error("无法获取视频总帧数，无法继续");
            return null;
        }

        // BUG-09：NCNN 的 v2/v3 老模型不支持 -n 自定义帧数，仅支持 2 倍补帧；Offical 引擎不受限
        var oldNcnnModel = ctx.Options.IfEngine != "Offical"
            && ctx.IfModel.StartsWith("rife-v", StringComparison.OrdinalIgnoreCase)
            && !ctx.IfModel.StartsWith("rife-v4", StringComparison.OrdinalIgnoreCase);
        var ifMult = oldNcnnModel ? 2 : ctx.IfMultiplier;
        if (oldNcnnModel && ctx.IfMultiplier != 2)
            _logger.Warn($"模型 {ctx.IfModel} 仅支持 2 倍补帧，本次已按 x2 处理");
        var targetFrames = ctx.Options.Interpolation ? totalFrames * ifMult : totalFrames;
        var outFps = ctx.Options.Interpolation ? ctx.Video.FrameRate * ifMult : ctx.Video.FrameRate;
        var outWidth = ctx.Video.Width * (ctx.Options.SuperResolution ? ctx.SrScale : 1);
        var outHeight = ctx.Video.Height * (ctx.Options.SuperResolution ? ctx.SrScale : 1);
        // CPU 处理模式：超分/补帧模型全部用 CPU 推理（NCNN -g -1 / Offical 强制 CPU）
        var cpu = ctx.Settings.UseCpuProcessing;
        if (cpu)
            _logger.Warn("已开启 CPU 处理模式：超分/补帧模型将全部使用 CPU 推理，速度会大幅下降");

        // 高负载稳定性：预估磁盘占用，剩余空间过少时提前警告（几万帧 PNG 可能占用数十 GB）
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(tempRoot)!);
            if (drive.IsReady)
            {
                var peakMB = totalFrames * (ctx.Options.SuperResolution || ctx.Options.Interpolation ? 6 : 2);
                var freeGB = drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
                var needGB = peakMB / 1024.0;
                if (freeGB < needGB * 0.5)
                {
                    _logger.Warn($"磁盘剩余空间可能不足：当前 {freeGB:0.0} GB，此任务峰值预估约需 {needGB:0.0} GB，处理中可能因空间不足崩溃");
                }
            }
        }
        catch { }

        // 手动导入外部帧文件夹时，复制到 Temp/input_frames，后续统一用 Temp 目录
        var useExternalFrames = !string.IsNullOrEmpty(ctx.ExternalFramesDir) && Directory.Exists(ctx.ExternalFramesDir);

        // 输入帧目录
        var inputDirForSr = ctx.Options.SuperResolution ? srFrames : inputFrames;
        var inputDirForIf = srFrames; // 补帧基于超分结果，无超分则基于原帧
        if (!ctx.Options.SuperResolution) inputDirForIf = inputFrames;

        // ============ 阶段1：拆帧 ============
        // 手动导入外部帧时始终复制到临时目录（与拆分帧勾选解耦），补帧/超分/合并可单独运行
        if (useExternalFrames)
        {
            // 复制外部帧到临时目录（统一中间产物都在 Temp，清理更干净）
            _logger.Info($"复制外部帧到临时目录: {ctx.ExternalFramesDir} → {inputFrames}");
            await CopyFramesAsync(ctx.ExternalFramesDir, inputFrames, ct);
            _logger.Success($"外部帧已复制到临时目录（{CountFrames(inputFrames)} 帧）");
        }
        else if (ctx.Options.SplitFrames)
        {
            var existing = CountFrames(inputFrames);
            if (existing >= totalFrames)
            {
                // 上次已拆好帧，跳过拆帧（崩溃重跑不从头）
                _logger.Info($"当前[拆帧]已完成 自动跳过此步骤");
            }
            else
            {
                // 上次拆帧中途结束（意外关闭/被停止）：本次重新开始时提示并清理残留
                if (existing > 0)
                {
                    _logger.Warn("此次拆帧遇到了因意外导致关闭，已自动清理并重新启动");
                    CleanPartialOutput(inputFrames);
                }
                _logger.Info($"开始拆帧: {Path.GetFileName(ctx.InputVideo)}");
                // 拆帧不受安全帧率限制（安全帧率只负责 Vulkan 设备丢失/显存溢出自动停止），始终多线程全速
                // 勾选「使FFmpeg尝试使用GPU加速」时用 -hwaccel auto（GPU 解码），失败自动回退 CPU 拆帧
                var useGpu = ctx.Settings.UseGpuAcceleration;
                var (args, _) = FFmpegCommandBuilder.SplitFrames(ctx.InputVideo, inputFrames, useGpu);
                _logger.Command($"ffmpeg {args}");
                var exit = await RunStageWithProgress(ProcessStage.Splitting, "拆帧中", totalFrames,
                    ctx.Tools.FFmpegExe, args, ParseFfmpegFrame, inputFrames, ct);
                // -hwaccel auto 在硬件解码器不可用时会静默回退软件解码（进程仍正常退出），
                // 通过 stderr 检测硬件加速失败标志，明确告知用户实际在用 CPU
                if (exit == 0 && useGpu && ContainsHwAccelFailure(_lastStderr))
                    _logger.Warn("GPU 解码未生效（未检测到可用硬件解码器），本次拆帧实际使用 CPU");
                if (exit != 0)
                {
                    ct.ThrowIfCancellationRequested();
                    // GPU 解码失败 → 回退 CPU 拆帧重试一次
                    if (useGpu)
                    {
                        _logger.Warn("GPU 加速拆帧失败，自动回退 CPU 拆帧");
                        (args, _) = FFmpegCommandBuilder.SplitFrames(ctx.InputVideo, inputFrames, false);
                        _logger.Command($"ffmpeg {args}");
                        exit = await RunStageWithProgress(ProcessStage.Splitting, "拆帧中(已回退)", totalFrames,
                            ctx.Tools.FFmpegExe, args, ParseFfmpegFrame, inputFrames, ct);
                    }
                    if (exit != 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        return Fail("拆帧失败");
                    }
                }
                _logger.Success($"拆帧完成: {CountFrames(inputFrames)} 帧");
            }
        }

        // ============ 阶段2：超分 ============
        if (ctx.Options.SuperResolution)
        {
            var existingSr = CountFrames(srFrames);
            if (existingSr >= totalFrames)
            {
                // 上次已超分完成，跳过（崩溃重跑不从头）
                _logger.Info($"当前[超分]已完成 自动跳过此步骤");
            }
            else
            {
                // 上次超分中途结束（意外关闭/被停止）：本次重新开始时提示并清理残留
                if (existingSr > 0)
                {
                    _logger.Warn("此次超分遇到了因意外导致关闭，已自动清理并重新启动");
                    CleanPartialOutput(srFrames);
                }
                var safe = ctx.Settings.UseSafeFrameRate;
                // 线程由滑块控制（1:1:1 ~ 1:32:32）；安全帧率只负责"致命 GPU 错误自动停止"，不再强制单线程
                var jThreads = $"1:{Math.Clamp(ctx.Settings.ThreadCount, 1, 32)}:{Math.Clamp(ctx.Settings.ThreadCount, 1, 32)}";
                _fatalGpuError = false;
                _logger.Info(cpu
                    ? $"超分开始(CPU): 模型 {ctx.SrModel} ×{ctx.SrScale}（线程 {jThreads}）"
                    : $"超分开始: 模型 {ctx.SrModel} ×{ctx.SrScale}（线程 {jThreads}）");
                var args = RealEsrganCommandBuilder.Build(inputFrames, srFrames, ctx.SrModel, ctx.SrScale, jThreads, useCpu: cpu);
                _logger.Command($"realesrgan-ncnn-vulkan {args}");
                var exit = await RunStageWithDirectoryPolling(ProcessStage.SuperRes, "超分中", totalFrames,
                    ctx.Tools.RealEsrganExe, args, srFrames, ct);
                // 捆绑的 realesrgan-ncnn-vulkan 构建不支持 CPU（-g -1 报 invalid gpu device）：
                // CPU 模式下超分失败时自动升级 GPU 处理，避免整个任务卡死（补帧阶段仍会正常走 CPU）
                if (exit != 0 && cpu && _lastStderr.Contains("invalid gpu device", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn("超分不支持 CPU 推理（realesrgan-ncnn 无 CPU 后端），已升级为 GPU 处理");
                    CleanPartialOutput(srFrames);
                    var cpuFallbackArgs = RealEsrganCommandBuilder.Build(inputFrames, srFrames, ctx.SrModel, ctx.SrScale, jThreads, useCpu: false);
                    _logger.Command($"realesrgan-ncnn-vulkan {cpuFallbackArgs}（CPU 升级 GPU）");
                    exit = await RunStageWithDirectoryPolling(ProcessStage.SuperRes, "超分中(升级GPU)", totalFrames,
                        ctx.Tools.RealEsrganExe, cpuFallbackArgs, srFrames, ct);
                }
                if (exit != 0)
                {
                    // 用户停止 → 抛取消异常由上层显示"处理已停止"；工具异常 → 本次停止，下次启动清理重跑
                    ct.ThrowIfCancellationRequested();
                    // 安全帧率开启：检测到 Vulkan 设备丢失/显存溢出 → 自动停止
                    if (safe && _fatalGpuError)
                    {
                        return Fail("已按安全帧率自动停止：检测到 Vulkan 设备丢失或显存溢出");
                    }
                    // 安全帧率未开启：显卡错误不停止，自动降级为单线程重试一次
                    if (_fatalGpuError)
                    {
                        _logger.Warn("检测到显卡错误（Vulkan 设备丢失/显存溢出），自动降级为单线程重试本次超分");
                        CleanPartialOutput(srFrames);
                        var retryArgs = RealEsrganCommandBuilder.Build(inputFrames, srFrames, ctx.SrModel, ctx.SrScale, "1:1:1", useCpu: cpu);
                        _logger.Command($"realesrgan-ncnn-vulkan {retryArgs}（降级单线程重试）");
                        exit = await RunStageWithDirectoryPolling(ProcessStage.SuperRes, "超分中(降级)", totalFrames,
                            ctx.Tools.RealEsrganExe, retryArgs, srFrames, ct);
                        if (exit != 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            return Fail("超分失败");
                        }
                    }
                    else
                    {
                        return Fail("超分失败");
                    }
                }
                _logger.Success($"超分完成: {CountFrames(srFrames)} 帧");
            }
        }

        // ============ 阶段3：补帧 ============
        if (ctx.Options.Interpolation)
        {
            var existingIf = CountFrames(ifFrames);
            if (existingIf >= targetFrames)
            {
                // 上次已补帧完成，跳过（崩溃重跑不从头）
                _logger.Info($"当前[补帧]已完成 自动跳过此步骤");
            }
            else
            {
                // 上次补帧中途结束（意外关闭/被停止）：本次重新开始时提示并清理残留
                if (existingIf > 0)
                {
                    _logger.Warn("此次补帧遇到了因意外导致关闭，已自动清理并重新启动");
                    CleanPartialOutput(ifFrames);
                }

                // ===== Offical 引擎（PyTorch pkl 模型）=====
                if (ctx.Options.IfEngine == "Offical")
                {
                    var modelDir = Path.Combine(ctx.Tools.OfficalRifeModelsRoot, ctx.IfModel);
                    if (!Directory.Exists(modelDir) || !File.Exists(Path.Combine(modelDir, "flownet.pkl")))
                        return Fail($"Offical 模型不存在: {ctx.IfModel}");
                    // Python 优先用便携版（Tools\officalrife\python\python.exe），否则回退系统 python
                    var py = File.Exists(ctx.Tools.OfficalPythonExe) ? ctx.Tools.OfficalPythonExe : "python";
                    var safe = ctx.Settings.UseSafeFrameRate;
                    // 线程滑块对 Offical 同样生效（torch 推理线程）；安全帧率负责致命设备/内存错误自动停止
                    var threads = Math.Clamp(ctx.Settings.ThreadCount, 1, 32);
                    _fatalGpuError = false;
                    _logger.Info($"补帧开始(Offical): 模型 {ctx.IfModel} ×{ctx.IfMultiplier} ({ctx.Video.FrameRate:0.##}→{outFps:0.##}fps)（线程 {threads}）");
                    var oArgs = OfficalRifeCommandBuilder.Build(ctx.Tools.OfficalRifeRunPy, inputDirForIf, ifFrames, modelDir,
                        ctx.IfMultiplier, threads, ctx.Settings.LowerQualityForVram, ctx.Settings.UseCpuProcessing);
                    _logger.Command($"python {oArgs}");
                    var oExit = await RunStageWithDirectoryPolling(ProcessStage.Interpolating, "补帧中(Offical)", targetFrames,
                        py, oArgs, ifFrames, ct, forcedCpu: cpu);
                    if (oExit != 0)
                    {
                        // 用户停止 → 抛取消异常由上层显示"处理已停止"；工具异常 → 本次停止，下次启动清理重跑
                        ct.ThrowIfCancellationRequested();
                        // 安全帧率开启：检测到设备/内存错误 → 自动停止
                        if (safe && _fatalGpuError)
                        {
                            return Fail("已按安全帧率自动停止：检测到设备错误或内存溢出");
                        }
                        // 安全帧率未开启：设备/内存错误不停止，自动降级为单线程重试一次
                        if (_fatalGpuError)
                        {
                            _logger.Warn("检测到设备错误/内存溢出，自动降级为单线程重试本次补帧(Offical)");
                            CleanPartialOutput(ifFrames);
                            var retryArgs = OfficalRifeCommandBuilder.Build(ctx.Tools.OfficalRifeRunPy, inputDirForIf, ifFrames, modelDir,
                                ctx.IfMultiplier, 1, ctx.Settings.LowerQualityForVram, ctx.Settings.UseCpuProcessing);
                            _logger.Command($"python {retryArgs}（降级单线程重试）");
                            oExit = await RunStageWithDirectoryPolling(ProcessStage.Interpolating, "补帧中(Offical降级)", targetFrames,
                                py, retryArgs, ifFrames, ct, forcedCpu: cpu);
                            if (oExit != 0)
                            {
                                ct.ThrowIfCancellationRequested();
                                return Fail("Offical 补帧失败");
                            }
                        }
                        else
                        {
                            return Fail("Offical 补帧失败（请确认已安装 Python 与 PyTorch，模型为官方 pkl 格式）");
                        }
                    }
                    _logger.Success($"补帧完成(Offical): {CountFrames(ifFrames)} 帧");
                }
                else
                {
                    // ===== NCNN 引擎（rife-ncnn-vulkan）=====
                    var safe = ctx.Settings.UseSafeFrameRate;
                    // 线程由滑块控制（1:1:1 ~ 1:32:32）；安全帧率只负责"致命 GPU 错误自动停止"，不再强制单线程
                    var jThreads = $"1:{Math.Clamp(ctx.Settings.ThreadCount, 1, 32)}:{Math.Clamp(ctx.Settings.ThreadCount, 1, 32)}";
                    _fatalGpuError = false;
                    _logger.Info($"补帧开始: 模型 {ctx.IfModel} ×{ifMult} ({ctx.Video.FrameRate:0.##}→{outFps:0.##}fps)（线程 {jThreads}）");
                    var args = RifeCommandBuilder.Build(inputDirForIf, ifFrames, ctx.IfModel, ctx.IfMultiplier, targetFrames, jThreads, useCpu: cpu);
                    _logger.Command($"rife-ncnn-vulkan {args}");
                    var exit = await RunStageWithDirectoryPolling(ProcessStage.Interpolating, "补帧中", targetFrames,
                        ctx.Tools.RifeExe, args, ifFrames, ct);

                    // BUG-04：rife-v4.25/v4.26 命令行版会报 "layer MemoryData not exists" → 回退 v4.6
                    if (exit != 0 && _lastStderr.Contains("MemoryData not exists", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Warn($"模型 {ctx.IfModel} 不被命令行版支持（BUG-04），自动切换为 rife-v4.6");
                        CleanPartialOutput(ifFrames);
                        var args2 = RifeCommandBuilder.Build(inputDirForIf, ifFrames, "rife-v4.6", ctx.IfMultiplier, targetFrames, jThreads, useCpu: cpu);
                        _logger.Command($"rife-ncnn-vulkan {args2}");
                        exit = await RunStageWithDirectoryPolling(ProcessStage.Interpolating, "补帧中(回退v4.6)", targetFrames,
                            ctx.Tools.RifeExe, args2, ifFrames, ct);
                    }
                    if (exit != 0)
                    {
                        // 用户停止 → 抛取消异常由上层显示"处理已停止"；工具异常 → 本次停止，下次启动清理重跑
                        ct.ThrowIfCancellationRequested();
                        // 安全帧率开启：检测到 Vulkan 设备丢失/显存溢出 → 自动停止
                        if (safe && _fatalGpuError)
                        {
                            return Fail("已按安全帧率自动停止：检测到 Vulkan 设备丢失或显存溢出");
                        }
                        // 安全帧率未开启：显卡错误不停止，自动降级为单线程重试一次
                        if (_fatalGpuError)
                        {
                            _logger.Warn("检测到显卡错误（Vulkan 设备丢失/显存溢出），自动降级为单线程重试本次补帧");
                            CleanPartialOutput(ifFrames);
                            var retryArgs = RifeCommandBuilder.Build(inputDirForIf, ifFrames, ctx.IfModel, ctx.IfMultiplier, targetFrames, "1:1:1", ctx.Settings.LowerQualityForVram, useCpu: cpu);
                            _logger.Command($"rife-ncnn-vulkan {retryArgs}（降级单线程重试）");
                            exit = await RunStageWithDirectoryPolling(ProcessStage.Interpolating, "补帧中(降级)", targetFrames,
                                ctx.Tools.RifeExe, retryArgs, ifFrames, ct);
                            if (exit != 0)
                            {
                                ct.ThrowIfCancellationRequested();
                                return Fail("补帧失败");
                            }
                        }
                        else
                        {
                            return Fail("补帧失败");
                        }
                    }
                    _logger.Success($"补帧完成: {CountFrames(ifFrames)} 帧");
                }
            }
        }

        // ============ 阶段4：合并视频 ============
        string currentVideo = "";
        if (ctx.Options.MergeVideo)
        {
            var framesForMerge = ctx.Options.Interpolation ? ifFrames : (ctx.Options.SuperResolution ? srFrames : inputFrames);
            var fpsForMerge = ctx.Options.Interpolation ? outFps : ctx.Video.FrameRate;
            var total = ctx.Options.Interpolation ? targetFrames : totalFrames;
            // 勾选「使FFmpeg尝试使用GPU加速」→ GPU 编码优先（NVIDIA NVENC → AMD AMF → Intel QSV），失败自动回退 CPU libx265
            var hwEnc = ctx.Settings.UseGpuAcceleration ? DetectHardwareEncoder(ctx.Tools.FFmpegExe, ctx.Gpu) : null;
            if (ctx.Settings.UseGpuAcceleration && hwEnc is null)
                _logger.Warn("未检测到可用的 GPU 编码器（NVENC/AMF/QSV），本次合并视频使用 CPU 编码（libx265）");
            else
                _logger.Info(hwEnc is null
                    ? "开始合并视频（编码：CPU libx265）"
                    : $"开始合并视频（编码：GPU {hwEnc.Split(' ')[0]}）");
            var args = FFmpegCommandBuilder.MergeFrames(framesForMerge, tempVideo, fpsForMerge,
                hwEnc ?? CpuEncodeArgs(ctx.Settings.EncodePreset));
            _logger.Command($"ffmpeg {args}");
            var exit = await RunStageWithProgress(ProcessStage.Merging, "合并中", total,
                ctx.Tools.FFmpegExe, args, ParseFfmpegFrame, null, ct);
            if (exit != 0 && hwEnc is not null)
            {
                // 硬件编码失败 → 自动回退 CPU 编码重试（NVIDIA/AMD/Intel 都可能有编码器不可用的情况）
                _logger.Warn($"硬件编码 {hwEnc.Split(' ')[0]} 失败，自动回退到 CPU 编码（libx265）");
                args = FFmpegCommandBuilder.MergeFrames(framesForMerge, tempVideo, fpsForMerge,
                    CpuEncodeArgs(ctx.Settings.EncodePreset));
                _logger.Command($"ffmpeg {args}");
                exit = await RunStageWithProgress(ProcessStage.Merging, "合并中(已回退)", total,
                    ctx.Tools.FFmpegExe, args, ParseFfmpegFrame, null, ct);
            }
            if (exit != 0) return Fail("合并视频失败");
            _logger.Success("合并视频完成");
            currentVideo = tempVideo;
        }
        else
        {
            // 不合并就无最终视频可继续；规格书主链路必合并，这里兜底
            currentVideo = tempVideo;
        }

        // ============ 阶段5：SDR→HDR 转换（仅开启 HDR 时，先渲染 HDR 再合并音频） ============
        // hdrDone：是否实际完成 HDR 转换（fallback SDR 时文件名不带 HDR 标记）
        var hdrDone = false;
        if (ctx.Options.SdrToHdr)
        {
            if (!ctx.Gpu.SupportsHdr)
            {
                _logger.Error("SDR→HDR 转换需要 NVIDIA RTX 显卡，当前显卡不支持，跳过 HDR 步骤（输出 SDR）");
            }
            else if (!ctx.Tools.NvEncExists)
            {
                _logger.Error("NVEncC64 未安装，跳过 HDR 转换（输出 SDR）");
            }
            else
            {
                // HDR 先于音频合并：NVEncC 只处理视频流，输出中间文件，音频随后嵌入
                var hdrVideo = Path.Combine(tempRoot, "hdr_video.mkv").Replace('\\', '/');
                _logger.Info($"HDR 转换开始: saturation={ctx.HdrSaturation}, contrast={ctx.HdrContrast}");
                var args = NvEncCommandBuilder.Build(currentVideo, hdrVideo, ctx.HdrSaturation, ctx.HdrContrast);
                _logger.Command($"NVEncC64 {args}");
                var exit = await RunStageWithProgress(ProcessStage.HdrConverting, "HDR转换中", 100,
                    ctx.Tools.NvEncExe, args, ParseNvencProgress, null, ct, percentDisplay: true);
                if (exit != 0) return Fail("HDR 转换失败");
                _logger.Success("HDR 转换完成");
                currentVideo = hdrVideo; // 后续合并音频基于 HDR 视频流
                hdrDone = true;
            }
        }

        // ============ 阶段6：合并原音频 ============
        // 流程：从原视频提取音频 → 合并到当前视频（PCM 2.0 24bit 96kHz）
        if (ctx.Options.MergeAudio && ctx.Video.HasAudio)
        {
            var audioPath = Path.Combine(tempRoot, "audio.flac").Replace('\\', '/');
            // 若未提前拆分音频，则现在提取
            if (!File.Exists(audioPath))
            {
                _logger.Info("从输入视频提取音频...");
                var exArgs = FFmpegCommandBuilder.ExtractAudio(ctx.InputVideo, audioPath);
                _logger.Command($"ffmpeg {exArgs}");
                var exExit = await RunStageAsync(ProcessStage.AddingAudio, "提取音频",
                    ctx.Tools.FFmpegExe, exArgs, ct);
                if (exExit != 0 || !File.Exists(audioPath))
                {
                    _logger.Warn("音频提取失败，跳过合并原音频");
                }
            }

            if (File.Exists(audioPath))
            {
                var audioEmbedded = Path.Combine(tempRoot, "audio_embedded.mkv").Replace('\\', '/');
                _logger.Info("合并原音频到新视频 (PCM 2.0 24bit 96kHz)");
                var useGpu = ctx.Settings.UseGpuAcceleration;
                var args = FFmpegCommandBuilder.EmbedAudio(currentVideo, audioPath, audioEmbedded, useGpu);
                _logger.Command($"ffmpeg {args}");
                var exit = await RunStageAsync(ProcessStage.AddingAudio, "合并原音频",
                    ctx.Tools.FFmpegExe, args, ct);
                // -hwaccel auto 静默回退：硬件加速未生效时明确提示
                if (exit == 0 && useGpu && ContainsHwAccelFailure(_lastStderr))
                    _logger.Warn("GPU 加速未生效，本次合并原音频实际使用 CPU 处理");
                if (exit != 0)
                {
                    // GPU 尝试失败 → 回退无 -hwaccel 重试一次
                    if (useGpu)
                    {
                        _logger.Warn("GPU 加速合并原音频失败，自动回退重试");
                        args = FFmpegCommandBuilder.EmbedAudio(currentVideo, audioPath, audioEmbedded, false);
                        _logger.Command($"ffmpeg {args}");
                        exit = await RunStageAsync(ProcessStage.AddingAudio, "合并原音频(已回退)",
                            ctx.Tools.FFmpegExe, args, ct);
                    }
                    if (exit != 0) return Fail("合并原音频失败");
                }
                _logger.Success("合并原音频完成");
                currentVideo = audioEmbedded;
            }
        }
        else if (ctx.Options.MergeAudio && !ctx.Video.HasAudio)
        {
            _logger.Warn("输入视频无音频流，跳过合并原音频");
        }

        // 未勾选合并视频时无最终视频，跳过输出步骤（中间帧保留在 Temp）
        if (!ctx.Options.MergeVideo)
        {
            _logger.Info("未勾选合并视频，跳过最终视频输出（中间帧保留在临时目录）");
            ProgressChanged?.Invoke(new ProcessProgress { Stage = ProcessStage.Done, StageText = "完成" });
            return tempVideo;
        }

        // ============ 阶段7：输出到最终路径 ============
        // 帧文件夹模式无输入视频文件，用帧文件夹名作为输出文件基础名
        var nameBase = string.IsNullOrEmpty(ctx.InputVideo) ? ctx.ExternalFramesDir : ctx.InputVideo;
        var finalName = OutputNamer.Build(nameBase, ctx.Options.SuperResolution, ctx.Options.Interpolation,
            hdrDone, outWidth, outHeight, outFps, ctx.Options.MergeAudio);
        var finalPath = Path.Combine(ctx.OutputRoot, finalName).Replace('\\', '/');
        Directory.CreateDirectory(ctx.OutputRoot);
        File.Copy(currentVideo, finalPath, overwrite: true);

        ProgressChanged?.Invoke(new ProcessProgress { Stage = ProcessStage.Done, StageText = "完成" });
        _logger.Success($"全部完成！输出: {finalPath}");
        return finalPath;
    }

    // ---- 进度解析器 ----

    private static readonly Regex FfmpegFrameRe = new(@"frame=\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex NvencPercentRe = new(@"(\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);

    private static long? ParseFfmpegFrame(string line)
    {
        var m = FfmpegFrameRe.Match(line);
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    private static long? ParseNvencProgress(string line)
    {
        // NVEncC 输出既有 "Frames: 2450/3929" 也有 "23.45%"。HDR 阶段按百分比显示（Total=100），
        // 必须优先解析百分比：否则像 "12000/100" 这类帧号/分母行会被误当比例，算出 12000% 的离奇进度。
        var p = NvencPercentRe.Match(line);
        if (p.Success && double.TryParse(p.Groups[1].Value, out var pct))
            return (long)Math.Clamp(Math.Round(pct), 0, 100);
        // 帧/帧 格式仅在比例合理时采用（分母 2~100000 且分子不超过分母）
        var m = Regex.Match(line, @"(\d+)/(\d+)");
        if (m.Success && long.TryParse(m.Groups[1].Value, out var c) && long.TryParse(m.Groups[2].Value, out var t)
            && t >= 2 && t <= 100000 && c <= t)
            return (long)(c * 100.0 / t);
        return null;
    }

    // ---- 工具方法 ----

    private string _lastStderr = "";
    /// <summary>当前阶段是否发生 Vulkan 设备丢失/显存溢出（安全帧率开启时据此自动停止）</summary>
    private bool _fatalGpuError;

    /// <summary>ffmpeg -hwaccel 硬件加速失败的常见 stderr 标志（此时 ffmpeg 会静默回退软件解码/编码，进程仍正常退出）</summary>
    private static readonly string[] HwAccelFailMarkers =
    {
        "hwaccel initialisation returned error",
        "Failed setup for format",
        "Hardware acceleration failed",
        "Could not initialise hardware"
    };

    private static bool ContainsHwAccelFailure(string stderr)
        => Array.Exists(HwAccelFailMarkers, m => stderr.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>判断 stderr 行是否为致命 GPU 错误（设备丢失 / 显存溢出 / Offical 的 FATAL_GPU_ERROR 标记）</summary>
    private static bool IsFatalGpuLine(string line)
    {
        return line.Contains("vkQueueSubmit failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("VK_ERROR_DEVICE_LOST", StringComparison.OrdinalIgnoreCase)
            || line.Contains("vkAllocateMemory failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("VK_ERROR_OUT_OF_DEVICE_MEMORY", StringComparison.OrdinalIgnoreCase)
            || line.Contains("out of device memory", StringComparison.OrdinalIgnoreCase)
            || line.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
            || line.Contains("FATAL_GPU_ERROR", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> RunStageWithProgress(ProcessStage stage, string stageText, long total,
        string exe, string args, Func<string, long?> frameParser, string? previewDir, CancellationToken ct,
        bool percentDisplay = false)
    {
        var lastLog = DateTime.MinValue;
        return await RunStageWithProgressEx(stage, stageText, total, exe, args, line =>
        {
            var v = frameParser(line);
            if (v.HasValue)
            {
                ProgressChanged?.Invoke(new ProcessProgress
                {
                    Stage = stage,
                    StageText = stageText,
                    Current = v.Value,
                    Total = total,
                    PercentDisplay = percentDisplay
                });
                // 节流：每秒记一条实时进度，让命令输出区有内容（百分比阶段显示百分比）
                var now = DateTime.Now;
                if ((now - lastLog).TotalSeconds >= 1)
                {
                    lastLog = now;
                    _logger.Info(percentDisplay
                        ? $"{stageText} {v.Value}%"
                        : $"{stageText} 已处理 {v.Value}/{total}");
                }
            }
        }, previewDir, ct);
    }

    private async Task<int> RunStageWithProgressEx(ProcessStage stage, string stageText, long total,
        string exe, string args, Action<string> onLine, string? previewDir, CancellationToken ct)
    {
        var captured = new System.Text.StringBuilder();
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? pollTask = null;

        // 拆帧阶段：目录轮询最新帧，供预览图实时刷新（FFmpeg frame= 是覆盖式输出，逐行解析不到中间进度）
        if (!string.IsNullOrEmpty(previewDir))
        {
            pollTask = Task.Run(async () =>
            {
                while (!pollCts.IsCancellationRequested)
                {
                    var latest = FindLatestFrame(previewDir);
                    if (!string.IsNullOrEmpty(latest))
                    {
                        ProgressChanged?.Invoke(new ProcessProgress
                        {
                            Stage = stage,
                            StageText = stageText,
                            LatestFramePath = latest
                        });
                    }
                    try { await Task.Delay(200, pollCts.Token); } catch (OperationCanceledException) { break; }
                }
            }, ct);
        }

        var exit = await _runner.RunAsync(exe, args,
            onLine: line => { onLine(line); },
            onStderr: line => { lock (captured) captured.AppendLine(line); },
            ct: ct);

        pollCts.Cancel();
        if (pollTask is not null) { try { await pollTask; } catch { } }
        lock (captured) _lastStderr = captured.ToString();
        return exit;
    }

    /// <summary>运行一个只往目录写帧的工具（Real-ESRGAN / RIFE），
    /// 通过轮询输出目录帧数 + 解析 stderr 百分比来估算进度。
    /// Real-ESRGAN 输出 0.00%~98.33% 的帧内 tile 进度；RIFE 无进度输出，仅靠帧数。
    /// forcedCpu：CPU 处理模式开启时传入 true，Offical 的 [IF_DEVICE] cpu 提示改为"主动强制"而非"自动降级"。</summary>
    private async Task<int> RunStageWithDirectoryPolling(ProcessStage stage, string stageText, long total,
        string exe, string args, string outputDir, CancellationToken ct, bool forcedCpu = false)
    {
        var captured = new System.Text.StringBuilder();
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 当前帧内的 tile 进度（0-100），由 stderr 百分比更新
        double innerPercent = 0;
        var innerLock = new object();
        // Vulkan 设备丢失/显存溢出后只快速失败一次（避免多次触发反复 Kill）
        var fatalKilled = false;
        // Offical run.py 设备标记：CUDA 不可用时降级 CPU，阶段文本标注（降级）
        var cpuDegraded = false;
        var displayText = stageText;

        var pollTask = Task.Run(async () =>
        {
            var lastLog = DateTime.MinValue;
            while (!pollCts.IsCancellationRequested)
            {
                try
                {
                    var count = CountFrames(outputDir);
                    double inner;
                    lock (innerLock) inner = innerPercent;
                    // 进度 = 已完成帧数 + 当前帧内百分比/100，上限 cap 到 total
                    var current = Math.Min(count + inner / 100.0, total);
                    if (current > 0)
                    {
                        ProgressChanged?.Invoke(new ProcessProgress
                        {
                            Stage = stage,
                            StageText = displayText,
                            Current = (long)Math.Floor(current),
                            Total = total,
                            LatestFramePath = FindLatestFrame(outputDir) ?? ""
                        });
                        // 节流实时日志：每 1 秒记录一次帧数，避免 Real-ESRGAN stderr 缓冲导致的"假死"
                        var now = DateTime.Now;
                        if ((now - lastLog).TotalSeconds >= 1)
                        {
                            lastLog = now;
                            _logger.Info($"{displayText} 已处理 {count}/{total} 帧");
                        }
                    }
                }
                catch { }
                try { await Task.Delay(200, pollCts.Token); } catch (OperationCanceledException) { break; }
            }
        }, ct);

        var exit = await _runner.RunAsync(exe, args,
            onLine: line =>
            {
                // Offical run.py 打印 [IF_DEVICE] cpu/cuda：未指定 -cpu 时 CUDA 不可用则自动降级 CPU；
                // 指定 -cpu（CPU 处理模式）时属用户主动选择，提示语区分开，避免误导
                if (!cpuDegraded && line.Contains("[IF_DEVICE] cpu", StringComparison.OrdinalIgnoreCase))
                {
                    cpuDegraded = true;
                    if (forcedCpu)
                    {
                        _logger.Info("Offical 引擎已按 CPU 处理模式强制使用 CPU 推理");
                    }
                    else
                    {
                        displayText = stageText + "（降级）";
                        _logger.Warn("Offical 引擎未检测到可用 GPU（CUDA），本次补帧降级为 CPU 推理");
                    }
                }
                // 检测 Vulkan 设备丢失/显存溢出（线程过高压垮 GPU 或显存不足）。
                // 一旦出现立即终止进程快速失败，避免工具反复刷错浪费时间
                if (IsFatalGpuLine(line))
                {
                    if (!fatalKilled)
                    {
                        fatalKilled = true;
                        _fatalGpuError = true;
                        _logger.Warn($"检测到 Vulkan 设备丢失/显存溢出：{line.Trim()}，终止当前进程");
                        _runner.KillCurrent();
                    }
                    return;
                }
                // 解析 stderr 的百分比（Real-ESRGAN: 0.00% ~ 98.33%）
                var m = NvencPercentRe.Match(line);
                if (m.Success && double.TryParse(m.Groups[1].Value, out var pct))
                {
                    lock (innerLock) innerPercent = pct;
                }
            },
            onStderr: line => { lock (captured) captured.AppendLine(line); },
            ct: ct);

        pollCts.Cancel();
        try { await pollTask; } catch { }
        lock (captured) _lastStderr = captured.ToString();
        return exit;
    }

    private async Task<int> RunStageAsync(ProcessStage stage, string stageText,
        string exe, string args, CancellationToken ct)
    {
        var captured = new System.Text.StringBuilder();
        var exit = await _runner.RunAsync(exe, args,
            onLine: null,
            onStderr: line => { lock (captured) captured.AppendLine(line); },
            ct: ct);
        lock (captured) _lastStderr = captured.ToString();
        return exit;
    }

    private static int CountFrames(string dir)
        => Directory.Exists(dir) ? Directory.GetFiles(dir, "*.png").Length : 0;

    /// <summary>把外部帧文件夹复制到临时目录（增量：目标已存在同名帧则跳过），带进度推送。
    /// 放在后台线程执行，避免大量文件复制阻塞 UI。</summary>
    private async Task CopyFramesAsync(string srcDir, string dstDir, CancellationToken ct)
    {
        Directory.CreateDirectory(dstDir);
        var files = Directory.GetFiles(srcDir, "*.png");
        var total = files.Length;
        var copied = 0;
        await Task.Run(() =>
        {
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                var dst = Path.Combine(dstDir, Path.GetFileName(f));
                if (!File.Exists(dst)) File.Copy(f, dst, overwrite: false);
                copied++;
                if (copied % 100 == 0 || copied == total)
                {
                    ProgressChanged?.Invoke(new ProcessProgress
                    {
                        Stage = ProcessStage.Splitting,
                        StageText = "复制外部帧",
                        Current = copied,
                        Total = total
                    });
                }
            }
        }, ct);
    }

    /// <summary>返回目录下最后写入的 PNG 帧路径（预览用，按最后写入时间判断，无则 null）</summary>
    private static string? FindLatestFrame(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        try
        {
            string? latest = null;
            var latestTicks = 0L;
            foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
            {
                var t = File.GetLastWriteTimeUtc(f).Ticks;
                if (t > latestTicks) { latestTicks = t; latest = f; }
            }
            return latest;
        }
        catch { return null; }
    }

    private static string CleanPartialOutput(string dir)
    {
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.GetFiles(dir, "*.png")) File.Delete(f);
        }
        return dir;
    }

    private string? Fail(string msg)
    {
        ProgressChanged?.Invoke(new ProcessProgress { Stage = ProcessStage.Failed, StageText = msg });
        _logger.Error(msg);
        return null;
    }

    // ---- GPU 编码探测（合并阶段） ----

    /// <summary>CPU 编码参数（通用回退方案）</summary>
    private static string CpuEncodeArgs(string preset) => $"libx265 -preset {preset} -crf 18";

    /// <summary>候选 GPU 硬件编码器（按 NVIDIA → AMD → Intel 顺序），失败时上层会自动回退 CPU。</summary>
    private static readonly string[] HwEncoderCandidates =
    {
        "hevc_nvenc -preset p7 -tune hq -rc vbr -cq 18",
        "hevc_amf -quality quality -rc cqp -qp_i 18 -qp_p 18",
        "hevc_qsv -preset medium -global_quality 18"
    };
    private static string? _detectedHwEncoder;

    /// <summary>探测 FFmpeg 可用的 GPU 硬件编码器（NVENC → AMF → QSV，覆盖 NVIDIA/AMD/Intel）。
    /// 探测结果静态缓存，仅首次运行 ffmpeg -encoders 查询一次；探测不到返回 null（调用方回退 CPU）。
    /// 注意：编码器"编译进 FFmpeg"不代表硬件可用，实际失败时上层会自动回退 CPU 重试。</summary>
    private static string? DetectHardwareEncoder(string ffmpegExe, GpuInfo gpu)
    {
        if (_detectedHwEncoder is not null)
            return string.IsNullOrEmpty(_detectedHwEncoder) ? null : _detectedHwEncoder;
        try
        {
            using var p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = "-hide_banner -encoders",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            p.Start();
            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            foreach (var cand in HwEncoderCandidates)
            {
                var name = cand.Split(" ")[0];
                // 非 NVIDIA 显卡直接跳过 NVENC（否则每次合并都会白尝试失败再回退）
                if (!gpu.IsNvidia && name.StartsWith("hevc_nvenc", StringComparison.OrdinalIgnoreCase)) continue;
                if (text.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    _detectedHwEncoder = cand;
                    return cand;
                }
            }
        }
        catch { }
        _detectedHwEncoder = "";
        return null;
    }
}
