using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Easy4K.Models;
using Easy4K.Services.CommandBuilders;
using Easy4K.SFDP;

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
    /// <summary>补帧模型的 vfi_driver.py --model 取值（见 VfiCatalog）；
    /// 空串 = 走 ncnn 轨（rife-ncnn-vulkan），非空 = 走 PyTorch 轨（仅 NVIDIA）。</summary>
    public string IfDriverKey { get; set; } = "";
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

    // ---- 音频处理来源（AudioSR 有没有真正介入）----
    // 音频链路上前后都是 ffmpeg（提取 → 转 48k → 嵌入），日志里全是 ffmpeg 命令，
    // 所以这里额外维护"是否介入 + 原因 + 后端"，最后输出唯一结论行，并把标记写进产物文件信息。
    /// <summary>AudioSR 未介入的原因（null = 已介入或未请求）</summary>
    private string? _audioSrSkipReason;
    /// <summary>AudioSR 推理后端（由驱动脚本输出解析：DirectML（GPU）/ CPU）</summary>
    private string? _audioSrBackend;
    /// <summary>AudioSR 精度档文案（FP16 低精度性能模式 / FP32 高精度完美模式）</summary>
    private string _audioSrPrecisionText = "";

    /// <summary>写进产物文件信息的"音频处理来源"标记（成品视频的 comment / AudioSR WAV 的 ICMT）。
    /// 必须是**纯 ASCII 英文**：WAV 的 LIST/INFO 块没有统一编码约定，媒体工具（MediaInfo）按本地代码页
    /// 解码，写 UTF-8 中文会显示乱码（实测中文显示成 "鏈煶棰?）。
    /// 与 Tools\AudioSR\audiosr_onnx.py 里的 EASY4K_AUDIO_MARK 保持一致，不要随意改动。</summary>
    private const string AudioSrMarkUsed =
        "This audio was super-resolved by the AudioSR module of Easy4K 2 Pro. Please verify the audio processing source carefully!";
    private const string AudioSrMarkNotUsed =
        "AudioSR was NOT used for this audio (original track + ffmpeg resample). Please verify the audio processing source carefully!";

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

        // 只勾了拆帧时不产出成品：以前这种情况会在几秒内"完成"并弹完成窗，看起来像"跑了 0 秒"。
        // 这里提前用警告说明，避免误判成失败/无响应。
        if (!ctx.Options.SuperResolution && !ctx.Options.Interpolation && !ctx.Options.MergeVideo)
        {
            _logger.Warn("当前只勾选了「拆帧」：不会执行超分/补帧/合并，也不会产出成品视频；如需成品请勾选「合并视频」。");
        }

        // 续跑兜底：未勾选补帧时去重会把重复帧移出 input_frames（只剩保留帧），拆帧的"够不够"要按保留数比；
        // 勾选补帧时重复帧留在原地（超分要对全部源帧跑），input_frames 始终是全量。
        var cachedDedupKept = ctx.Settings.DedupEnabled && !ctx.Options.Interpolation ? TryReadDedupKept(tempRoot) : 0;

        // BUG-09：NCNN 的 v2/v3 老模型不支持 -n 自定义帧数，仅支持 2 倍补帧
        var oldNcnnModel = ctx.IfModel.StartsWith("rife-v", StringComparison.OrdinalIgnoreCase)
            && !ctx.IfModel.StartsWith("rife-v4", StringComparison.OrdinalIgnoreCase);

        // 补帧目标：优先"目标帧率"（成片 fps 精确等于用户填的值，避免 23.976×2=47.952 这种小数），
        // 否则按整数倍率。ifRatio 允许是小数（如 120 ÷ 23.976 = 5.005）：
        //   · 总输出帧数 = round(源帧数 × ifRatio)
        //   · RIFE 的 -n / 回填取块仍按整数粒度 ifMult = round(ifRatio)（保留帧区块的"每帧几帧"）
        var ifRatio = ctx.Options.TargetFps > 0 && ctx.Video.FrameRate > 0
            ? ctx.Options.TargetFps / ctx.Video.FrameRate
            : (double)ctx.IfMultiplier;
        if (oldNcnnModel)
        {
            ifRatio = 2;
            if (ctx.Options.TargetFps > 0)
                _logger.Warn($"模型 {ctx.IfModel} 不支持自定义帧数（-n），目标帧率 {ctx.Options.TargetFps:0.##}fps 本次已忽略，按 x2 处理");
            else if (ctx.IfMultiplier != 2)
                _logger.Warn($"模型 {ctx.IfModel} 仅支持 2 倍补帧，本次已按 x2 处理");
        }
        if (ifRatio < 1) ifRatio = 1;
        var ifMult = Math.Max(1, (int)Math.Round(ifRatio));
        var targetFrames = ctx.Options.Interpolation ? (long)Math.Round(totalFrames * ifRatio) : totalFrames;
        var outFps = ctx.Options.Interpolation
            ? (ctx.Options.TargetFps > 0 && !oldNcnnModel ? ctx.Options.TargetFps : ctx.Video.FrameRate * ifMult)
            : ctx.Video.FrameRate;
        if (ctx.Options.Interpolation && ctx.Options.TargetFps > 0 && !oldNcnnModel)
            _logger.Info($"补帧目标帧率 {ctx.Options.TargetFps:0.##} fps：源 {ctx.Video.FrameRate:0.##} fps → "
                + $"比例 ≈{ifRatio:0.###}×，输出 {targetFrames} 帧（原按倍率 ×{ctx.IfMultiplier} 会是 {ctx.Video.FrameRate * ctx.IfMultiplier:0.##} fps）");
        var outWidth = ctx.Video.Width * (ctx.Options.SuperResolution ? ctx.SrScale : 1);
        var outHeight = ctx.Video.Height * (ctx.Options.SuperResolution ? ctx.SrScale : 1);
        // CPU 处理模式：超分/补帧模型全部用 CPU 推理（NCNN -g -1）
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
            if (existing >= (cachedDedupKept > 0 ? cachedDedupKept : totalFrames))
            {
                // 上次已拆好帧，跳过拆帧（崩溃重跑不从头）
                _logger.Info($"当前[拆帧]已完成 自动跳过此步骤");
            }
            else
            {
                // 上次拆帧未完整结束（中断/被停止）：本次自动清理残留并从该步骤重新开始，属正常恢复
                if (existing > 0)
                {
                    _logger.Warn("检测到上次拆帧未完整结束留下的中间文件，已自动清理，本次从拆帧重新开始");
                    await CleanPartialOutputAsync(inputFrames, ProcessStage.Splitting, "拆帧中", ct);
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

        // ============ 涡轮模式：块级流水（CPU 侧按块判决/准备 与 GPU 侧超分/补帧 同时进行） ============
        // 走通后 srFrames / ifFrames 已按全局连续编号写满，后续阶段会凭"帧数够"自动跳过，直接进入合并，
        // 合并与收尾逻辑无需任何改动；任一步失败都返回 false，本次自动回退逐阶段串行，不会把任务做废。
        var turboDedupKept = 0;
        if (ctx.Settings.TurboMode)
        {
            if (TurboSupported(ctx))
            {
                var (turboOk, dedupKeptInTurbo) = await RunTurboBlocksAsync(ctx, tempRoot, inputFrames, srFrames, ifFrames, totalFrames, ifMult, cpu, ct);
                if (turboOk)
                {
                    turboDedupKept = dedupKeptInTurbo;
                    _logger.Success("[涡轮] 块级流水已产出全部帧，后续阶段将自动跳过并进入合并");
                }
                else
                {
                    _logger.Warn("[涡轮] 块级流水未完成，本次回退为逐阶段串行处理");
                }
            }
            else
            {
                _logger.Warn("[涡轮] 当前组合暂不支持块级流水（需勾选超分或补帧、补帧使用 NCNN 引擎），本次按逐阶段串行处理");
            }
        }

        // ============ 阶段1.5：帧去重（高级模式开关） ============
        // 剔除与上一帧重复的画面 → 超分/补帧只算保留帧；合并前按 map 回填，时长与帧率不变。
        // 涡轮模式下判决已经并入流水线（与 GPU 重叠跑完），这里不再重复判一遍。
        var dedupKept = turboDedupKept; // 0 = 未启用/未生效
        if (ctx.Settings.DedupEnabled && dedupKept == 0)
            dedupKept = await RunDedupAsync(ctx, tempRoot, inputFrames, totalFrames, ct);

        // 去重生效后：勾选补帧时超分仍处理全部源帧（去重只省补帧的算力），未勾选补帧时超分只算保留帧；
        // 补帧始终只算保留帧（= 回填前的数量）。
        var srTarget = dedupKept > 0 && !ctx.Options.Interpolation ? dedupKept : totalFrames;
        var ifTarget = dedupKept > 0 ? dedupKept * ifMult : targetFrames;

        // ============ 阶段2：超分 ============
        if (ctx.Options.SuperResolution)
        {
            var existingSr = CountFrames(srFrames);
            if (existingSr >= srTarget)
            {
                // 上次已超分完成，跳过（崩溃重跑不从头）
                _logger.Info($"当前[超分]已完成 自动跳过此步骤");
            }
            else
            {
                // 上次超分未完整结束（中断/被停止）：本次自动清理残留并从该步骤重新开始，属正常恢复
                if (existingSr > 0)
                {
                    _logger.Warn("检测到上次超分未完整结束留下的中间文件，已自动清理，本次从超分重新开始");
                    await CleanPartialOutputAsync(srFrames, ProcessStage.SuperRes, "超分中", ct);
                }
                var safe = ctx.Settings.UseSafeFrameRate;
                // 线程由滑块控制（1:1:1 ~ 1:32:32）；安全帧率只负责"致命 GPU 错误自动停止"，不再强制单线程
                var jThreads = $"1:{Math.Clamp(ctx.Settings.ThreadCount, 1, 32)}:{Math.Clamp(ctx.Settings.ThreadCount, 1, 32)}";
                _fatalGpuError = false;
                // 模型属于哪个超分算法，就用那个算法的引擎（Real-ESRGAN / Real-CUGAN / RealSR / SRMD / waifu2x）
                var srExe = SrCommandBuilder.ExeFor(ctx.Tools, ctx.SrModel);
                if (string.IsNullOrEmpty(srExe) || !File.Exists(srExe))
                {
                    return Fail($"超分模型 {ctx.SrModel} 对应的引擎没有部署（{Path.GetFileName(srExe)}）");
                }
                _logger.Info(cpu
                    ? $"超分开始(CPU): 模型 {ctx.SrModel} ×{ctx.SrScale}（线程 {jThreads}）"
                    : $"超分开始: 模型 {ctx.SrModel} ×{ctx.SrScale}（线程 {jThreads}）");
                var args = SrCommandBuilder.Build(ctx.SrModel, ctx.SrScale, inputFrames, srFrames, jThreads, ctx.Settings.LowerQualityForVram, useCpu: cpu);
                _logger.Command($"{Path.GetFileName(srExe)} {args}");
                var exit = await RunStageWithDirectoryPolling(ProcessStage.SuperRes, "超分中", totalFrames,
                    srExe, args, srFrames, ct);
                // 捆绑的 realesrgan-ncnn-vulkan 构建不支持 CPU（-g -1 报 invalid gpu device）：
                // CPU 模式下超分失败时自动升级 GPU 处理，避免整个任务卡死（补帧阶段仍会正常走 CPU）
                if (exit != 0 && cpu && _lastStderr.Contains("invalid gpu device", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn("超分不支持 CPU 推理（realesrgan-ncnn 无 CPU 后端），已升级为 GPU 处理");
                    await CleanPartialOutputAsync(srFrames, ProcessStage.SuperRes, "超分中", ct);
                    var cpuFallbackArgs = SrCommandBuilder.Build(ctx.SrModel, ctx.SrScale, inputFrames, srFrames, jThreads, ctx.Settings.LowerQualityForVram, useCpu: false);
                    _logger.Command($"{Path.GetFileName(srExe)} {cpuFallbackArgs}（CPU 升级 GPU）");
                    exit = await RunStageWithDirectoryPolling(ProcessStage.SuperRes, "超分中(升级GPU)", totalFrames,
                        srExe, cpuFallbackArgs, srFrames, ct);
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
                        await CleanPartialOutputAsync(srFrames, ProcessStage.SuperRes, "超分中(降级)", ct);
                        var retryArgs = SrCommandBuilder.Build(ctx.SrModel, ctx.SrScale, inputFrames, srFrames, "1:1:1", ctx.Settings.LowerQualityForVram, useCpu: cpu);
                        _logger.Command($"{Path.GetFileName(srExe)} {retryArgs}（降级单线程重试）");
                        exit = await RunStageWithDirectoryPolling(ProcessStage.SuperRes, "超分中(降级)", totalFrames,
                            srExe, retryArgs, srFrames, ct);
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
            if (existingIf >= ifTarget)
            {
                // 上次已补帧完成，跳过（崩溃重跑不从头）
                _logger.Info($"当前[补帧]已完成 自动跳过此步骤");
            }
            else
            {
                // 上次补帧未完整结束（中断/被停止）：本次自动清理残留并从该步骤重新开始，属正常恢复
                if (existingIf > 0)
                {
                    _logger.Warn("检测到上次补帧未完整结束留下的中间文件，已自动清理，本次从补帧重新开始");
                    await CleanPartialOutputAsync(ifFrames, ProcessStage.Interpolating, "补帧中", ct);
                }

                // 去重生效（勾选补帧）时：超分产物是全部源帧，补帧只能喂"保留帧"子集 ——
                // 按地图里的保留帧序号挑帧并重新连续编号，RIFE 的输入/输出比例才与实际输入帧数一致。
                if (dedupKept > 0)
                {
                    var srcForIf = ctx.Options.SuperResolution ? srFrames : inputFrames;
                    var keptDir = await EnsureKeptSubsetAsync(tempRoot, srcForIf,
                        Path.Combine(tempRoot, "if_input_kept"), ct);
                    if (keptDir is null)
                        return Fail("帧去重：准备补帧输入子集失败（保留帧与超分产物对不上号，或目录被占用）");
                    inputDirForIf = keptDir;
                }

                // ===== 补帧：rife-ncnn-vulkan（ncnn + Vulkan）=====
                var safe = ctx.Settings.UseSafeFrameRate;
                // 线程由滑块控制（1:1:1 ~ 1:32:32）；安全帧率只负责"致命 GPU 错误自动停止"，不再强制单线程
                var jThreads = $"1:{Math.Clamp(ctx.Settings.ThreadCount, 1, 32)}:{Math.Clamp(ctx.Settings.ThreadCount, 1, 32)}";
                _fatalGpuError = false;
                _logger.Info($"补帧开始: 模型 {ctx.IfModel} ×{ifRatio:0.###} ({ctx.Video.FrameRate:0.##}→{outFps:0.##}fps)（线程 {jThreads}）");
                // -n 是"输出总帧数"：去重生效时输入目录只剩保留帧，必须按 保留帧×倍率 传，
                // 否则 RIFE 会把比例放大到 原帧数×倍率/保留帧数，且回填时按倍率取块会全部错位。
                // ifTarget 恰好就是这个值（未去重时等于 原帧数×倍率）。
                // ncnn + PyTorch 双轨：RIFE / DRBA / GMFSS / GIMM-VFI 上游只有 PyTorch 实现，没有 ncnn 版本，
                // 走 python-torch + vfi_driver.py；其余模型仍走 rife-ncnn-vulkan（Vulkan，A/N/I 通吃）。
                var useTorch = ctx.IfDriverKey.Length > 0;
                string vfiExe, args;
                if (useTorch)
                {
                    // CPU 处理模式在这条轨上物理上做不到（上游只有 CUDA 实现，DRBA/GMFSS 还依赖 cupy），
                    // 明确告知而不是静默忽略，避免用户以为"勾了 CPU 却在用显卡磨时间"。
                    if (cpu)
                        _logger.Warn($"CPU 处理模式对 PyTorch 轨补帧族不生效（{ctx.IfModel} 上游只有 CUDA 实现），本次仍走 GPU");
                    vfiExe = ctx.Tools.TorchPythonExe;
                    // 高级性能选项同样要落到这条轨上：处理线程 → torch/OpenCV 线程数，降低画质 → FP16 推理。
                    // 另外两点必须讲清楚（原先把「安全帧率」错当成驱动的 --scdet 开关）：
                    // · 「安全帧率」在这条轨上没有对应语义：PyTorch 驱动本来就是一出错就非零退出、不会降级重试，
                    //   所以它既不该控制场景检测，也没有额外行为，这里只在开启时提示一句。
                    // · 场景切换检测（切镜处不插帧）命中时驱动会**写入源帧本身**（物理帧）。阈值偏低时快速运动会被
                    //   误判成切镜，于是在运动段插入物理帧 → 观感就是顿挫/鬼影。所以这里默认**不开**，
                    //   需要时由后续单独的开关 + 实测标定阈值再启用。
                    if (ctx.Settings.UseSafeFrameRate && ctx.Settings.UseCpuProcessing == false)
                        _logger.Info("PyTorch 轨补帧：安全帧率无额外作用（该轨出错即停，不做降级重试）；场景切换检测默认关闭以免运动中插物理帧");
                    args = VfiTorchCommandBuilder.Build(ctx.Tools.VfiDriverScript, ctx.IfDriverKey, inputDirForIf, ifFrames,
                        ifTarget, VfiTorchCommandBuilder.ScaleFor(ctx.Video.Width), sceneDetect: false,
                        threads: Math.Clamp(ctx.Settings.ThreadCount, 1, 32), fp16: ctx.Settings.LowerQualityForVram);
                    _logger.Command($"python-torch {args}");
                }
                else
                {
                    vfiExe = ctx.Tools.RifeExe;
                    args = RifeCommandBuilder.Build(inputDirForIf, ifFrames, ctx.IfModel, ctx.IfMultiplier, ifTarget, jThreads, ctx.Settings.LowerQualityForVram, useCpu: cpu);
                    _logger.Command($"rife-ncnn-vulkan {args}");
                }
                var exit = await RunStageWithDirectoryPolling(ProcessStage.Interpolating, "补帧中", ifTarget,
                    vfiExe, args, ifFrames, ct);

                // BUG-04：rife-v4.25/v4.26 命令行版会报 "layer MemoryData not exists" → 回退 v4.6
                if (!useTorch && exit != 0 && _lastStderr.Contains("MemoryData not exists", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn($"模型 {ctx.IfModel} 不被命令行版支持，已因硬件原因回退至 rife-v4.6");
                    await CleanPartialOutputAsync(ifFrames, ProcessStage.Interpolating, "补帧中", ct);
                    var args2 = RifeCommandBuilder.Build(inputDirForIf, ifFrames, "rife-v4.6", ctx.IfMultiplier, ifTarget, jThreads, ctx.Settings.LowerQualityForVram, useCpu: cpu);
                    _logger.Command($"rife-ncnn-vulkan {args2}（因硬件原因回退至v4.6）");
                    exit = await RunStageWithDirectoryPolling(ProcessStage.Interpolating, "补帧中(因硬件原因回退至v4.6)", ifTarget,
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
                    // 安全帧率未开启：显卡错误不停止，自动降级为单线程 + 省显存(UHD)模式重试一次。
                    // 实测显存不足（vkAllocateMemory failed -2）时，光降线程往往还不够，
                    // 必须同时打开 -u 省显存模式，重试才有意义。
                    if (!useTorch && _fatalGpuError)
                    {
                        _logger.Warn("检测到显卡错误（Vulkan 设备丢失/显存溢出），自动降级为单线程 + 省显存模式重试本次补帧");
                        await CleanPartialOutputAsync(ifFrames, ProcessStage.Interpolating, "补帧中(降级)", ct);
                        var retryArgs = RifeCommandBuilder.Build(inputDirForIf, ifFrames, ctx.IfModel, ctx.IfMultiplier, ifTarget, "1:1:1", useUhdMode: true, useCpu: cpu);
                        _logger.Command($"rife-ncnn-vulkan {retryArgs}（降级单线程+省显存重试）");
                        exit = await RunStageWithDirectoryPolling(ProcessStage.Interpolating, "补帧中(降级)", ifTarget,
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

        // ============ 阶段4：合并视频 ============
        string currentVideo = "";
        if (ctx.Options.MergeVideo)
        {
            var framesForMerge = ctx.Options.Interpolation ? ifFrames : (ctx.Options.SuperResolution ? srFrames : inputFrames);

            // 帧去重回填：把保留帧按索引表展开成与原始帧数一致的完整序列，时长/帧率/音频保持不变。
            // 勾选补帧时超分对全部源帧都跑了，因此每个槽位的"本体帧"用该源帧自己真实的超分产物；
            // 未勾选补帧时超分只算了保留帧，本体帧只能沿用保留帧的结果。
            if (dedupKept > 0)
            {
                var expandedDir = Path.Combine(tempRoot, "expanded_frames").Replace('\\', '/');
                // 补帧时每个保留帧在补帧结果里占 ifMult 帧，回填按"输出帧的时间位置"取源
                // （ratio 可以是小数：目标帧率 ÷ 源帧率）；不补帧时 ratio = 1、粒度 = 1
                var expandRatio = ctx.Options.Interpolation ? ifRatio : 1.0;
                var expandBlock = ctx.Options.Interpolation ? ifMult : 1;
                var selfFramesDir = ctx.Options.Interpolation
                    ? (ctx.Options.SuperResolution ? srFrames : inputFrames)
                    : null;
                // 回填失败绝不静默继续：那样 FFmpeg 会拿"去重后的少帧序列"去合并，成品直接变短，
                // 而用户只会以为"去重把重复帧弄丢了"。这里明确停下，给出可执行的处置。
                if (!await TryExpandDedupFramesAsync(tempRoot, framesForMerge, expandedDir, expandRatio, expandBlock, ct, selfFramesDir))
                    return Fail("帧去重回填失败：为避免产出一支少帧的成品，本次已停止。"
                        + "（dedup_map.json 缺失/不可读，或个别帧复制失败；可清理临时文件后重跑）");
                framesForMerge = expandedDir;
            }

            var fpsForMerge = ctx.Options.Interpolation ? outFps : ctx.Video.FrameRate;
            var total = ctx.Options.Interpolation ? targetFrames : totalFrames;
            // 勾选「使FFmpeg尝试使用GPU加速」→ 自动挑硬件编码器（N 卡 NVENC / A 卡 AMF / Intel QSV），失败回退 CPU libx265。
            // CPU 处理模式下一律走 CPU 编码：CPU 模式意味着"不要碰显卡"，再挑硬件编码器自相矛盾。
            // CQ/QP 与 CPU CRF 都来自高级页。
            var cpuArgs = CpuEncodeArgs(ctx.Settings.EncodePreset, ctx.Settings.Crf);
            var wantHwEnc = ctx.Settings.UseGpuAcceleration && !ctx.Settings.UseCpuProcessing;
            var hwEnc = wantHwEnc ? DetectHardwareEncoder(ctx.Tools.FFmpegExe, ctx.Gpu) : null;
            var hwArgs = hwEnc is null ? null : HwEncodeArgs(hwEnc, ctx.Settings.HwQualityMode, ctx.Settings.HwQuality);
            if (wantHwEnc && hwEnc is null)
                _logger.Warn("未检测到可用的 GPU 编码器（NVENC/AMF/QSV），本次合并视频使用 CPU 编码（libx265）");
            else
                _logger.Info(hwEnc is null
                    ? $"开始合并视频（编码：CPU libx265，CRF {Math.Clamp(ctx.Settings.Crf, 0, 51)}）"
                    : $"开始合并视频（编码：GPU {hwEnc}，质量模式 {QualityModeText(ctx.Settings.HwQualityMode)} {Math.Clamp(ctx.Settings.HwQuality, 0, 51)}）");
            var args = FFmpegCommandBuilder.MergeFrames(framesForMerge, tempVideo, fpsForMerge,
                hwArgs ?? cpuArgs, SubtitleSource(ctx));
            _logger.Command($"ffmpeg {args}");
            var exit = await RunStageWithProgress(ProcessStage.Merging, "合并中", total,
                ctx.Tools.FFmpegExe, args, ParseFfmpegFrame, null, ct);
            if (exit != 0 && hwEnc is not null)
            {
                // 硬件编码失败 → 自动回退 CPU 编码重试（NVIDIA/AMD/Intel 都可能有编码器不可用的情况）
                _logger.Warn($"硬件编码 {hwEnc} 失败，自动回退到 CPU 编码（libx265）");
                args = FFmpegCommandBuilder.MergeFrames(framesForMerge, tempVideo, fpsForMerge,
                    cpuArgs, SubtitleSource(ctx));
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
                // CPU 处理模式管不到这里：NVEncC 的 --vpp-ngx-truehdr 只能跑在 NVIDIA GPU 上
                if (ctx.Settings.UseCpuProcessing)
                    _logger.Warn("SDR→HDR 由 NVEncC 的 GPU 滤镜完成，没有 CPU 路径：CPU 处理模式对该步骤不生效");
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

        // ============ 阶段6：音频（音频超分 / 合并原音频） ============
        // 提取原音轨 →（可选）音频超分升到 48kHz → 按「合并原音频」决定去向：
        //   勾选：嵌入当前视频，用 AudioSR 的 48kHz 直出（未开超分则 ffmpeg 重采样到 96kHz）
        //   未勾选：不碰视频，超分结果作为单独的 48kHz WAV 落到输出目录
        var nameBaseAudio = string.IsNullOrEmpty(ctx.InputVideo)
            ? Path.GetFileName(ctx.ExternalFramesDir.TrimEnd('/', '\\'))
            : Path.GetFileNameWithoutExtension(ctx.InputVideo);
        // 未勾选「合并进视频」时音频超分的独立 WAV 产物（未勾选合并视频时它就是本次的成品）
        string? standaloneAudioWav = null;

        if ((ctx.Options.MergeAudio || ctx.Options.AudioSr) && ctx.Video.HasAudio)
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
                    _logger.Warn(ctx.Options.MergeAudio ? "音频提取失败，跳过合并原音频" : "音频提取失败，跳过音频超分");
                }
            }

            if (File.Exists(audioPath))
            {
                // 音频超分（勾选时）：把音轨升到 48kHz 高带宽
                var srWav = ctx.Options.AudioSr ? await RunAudioSrAsync(ctx, audioPath, tempRoot, ct) : null;

                // 音频处理来源：以"是否真的拿到 AudioSR 的 48kHz 产物"为准（未勾选 / 条件不满足 / 推理失败都算未介入）。
                // 这条唯一的结论行 + 写进产物的文件信息标记，才是判断"AudioSR 到底有没有介入"的依据 ——
                // 光看 ffmpeg 命令看不出来（提取/转 48k/嵌入都是 ffmpeg）。
                var audioSrEngaged = srWav is not null;
                var audioMark = audioSrEngaged ? AudioSrMarkUsed : AudioSrMarkNotUsed;
                _logger.Info(audioSrEngaged
                    ? $"【音频处理来源】AudioSR 已介入（{_audioSrPrecisionText} / 后端 {_audioSrBackend ?? "未知"}），"
                      + "该标记已写入产物文件信息"
                    : $"【音频处理来源】AudioSR 未介入（{(ctx.Options.AudioSr ? _audioSrSkipReason ?? "未知原因" : "未勾选音频超分")}），"
                      + "该标记已写入产物文件信息");

                if (!ctx.Options.MergeAudio)
                {
                    // 未勾选「将原音频合并进新视频」：只把超分结果作为独立 WAV 输出，不动视频
                    if (srWav is not null)
                    {
                        Directory.CreateDirectory(ctx.OutputRoot);
                        var wavOut = Path.Combine(ctx.OutputRoot, $"{nameBaseAudio}_AudioSR_48kHz.wav").Replace('\\', '/');
                        File.Copy(srWav, wavOut, overwrite: true);
                        standaloneAudioWav = wavOut;
                        _logger.Success($"音频超分结果已单独输出（未勾选合并进视频）: {wavOut}");
                    }
                }
                else
                {
                    var audioToEmbed = srWav ?? audioPath;
                    var embedRate = srWav is not null ? 48000 : 96000;
                    var audioEmbedded = Path.Combine(tempRoot, "audio_embedded.mkv").Replace('\\', '/');
                    // 音频链路显式记一条：AudioSR 一旦介入，音轨会先被转成 48kHz WAV
                    // （源不是 48k 时，这一步本身就是一次重采样），之后又统一以 PCM 嵌入。
                    // 光看输出参数（PCM 24bit）根本判断不出中间到底有没有重采样过，所以把链路写清楚。
                    var srcRate = ctx.Video.AudioSampleRate;
                    var srcText = srcRate > 0 ? $"{srcRate / 1000.0:0.##}kHz" : "未知采样率";
                    var preResample = srcRate > 0 && srcRate != 48000;
                    _logger.Info(audioSrEngaged
                        ? $"音频链路：源 {srcText} → 转 48kHz{(preResample ? "（重采样）" : "（无需重采样）")}"
                          + $" → AudioSR 升带宽 → 嵌入 PCM 2.0 24bit 48kHz（不再重采样）"
                        : $"音频链路：源 {srcText} → ffmpeg 重采样 → 嵌入 PCM 2.0 24bit {embedRate / 1000}kHz");
                    var useGpu = ctx.Settings.UseGpuAcceleration;
                    var args = FFmpegCommandBuilder.EmbedAudio(currentVideo, audioToEmbed, audioEmbedded, useGpu, embedRate, audioMark);
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
                            args = FFmpegCommandBuilder.EmbedAudio(currentVideo, audioToEmbed, audioEmbedded, false, embedRate, audioMark);
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
        }
        else if (ctx.Options.MergeAudio && !ctx.Video.HasAudio)
        {
            _logger.Warn("输入视频无音频流，跳过合并原音频");
        }
        else if (ctx.Options.AudioSr && !ctx.Video.HasAudio)
        {
            _logger.Warn("输入视频无音频流，跳过音频超分");
        }

        // 未勾选合并视频时无最终视频，跳过输出步骤（中间帧保留在 Temp）
        if (!ctx.Options.MergeVideo)
        {
            if (standaloneAudioWav is not null)
            {
                // 只跑音频超分（不合并视频）：本次成品就是那个 WAV，作为结果返回给完成窗/报告
                // 顺手实测一遍：确认文件信息里确实带上了 AudioSR 的处理来源标记
                var wavProbe = ProbeAudioSummary(ctx.Tools.FFprobeExe, standaloneAudioWav);
                if (wavProbe is not null) _logger.Info($"成品音频实测（ffprobe）：{wavProbe}");
                _logger.Warn("未勾选合并视频：本次不产出成品视频，成品为音频超分结果");
                ProgressChanged?.Invoke(new ProcessProgress { Stage = ProcessStage.Done, StageText = "完成（仅音频超分）" });
                return standaloneAudioWav;
            }
            _logger.Warn("未勾选合并视频：本次不产出成品视频（中间帧保留在临时目录），处理到此结束");
            ProgressChanged?.Invoke(new ProcessProgress { Stage = ProcessStage.Done, StageText = "完成（未勾选合并，无成品输出）" });
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

        // 成品实测（最直接的判据）：音轨编码/采样率 + 文件信息里的处理来源标记。
        // AudioSR 介入 → pcm_s24le / 48000 Hz + AudioSR 标记；未介入 → 重采样到 96000 Hz + "未使用"标记。
        var probe = ProbeAudioSummary(ctx.Tools.FFprobeExe, finalPath);
        if (probe is not null) _logger.Info($"成品音频实测（ffprobe）：{probe}");

        ProgressChanged?.Invoke(new ProcessProgress { Stage = ProcessStage.Done, StageText = "完成" });
        _logger.Success($"全部完成！输出: {finalPath}");
        return finalPath;
    }

    // ---- 进度解析器 ----

    private static readonly Regex FfmpegFrameRe = new(@"frame=\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex NvencPercentRe = new(@"(\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);
    /// <summary>AudioSR 驱动的进度行形如 "[AudioSR] 进度 42%  窗口 3/7 ..."</summary>
    private static readonly Regex AudioSrPercentRe = new(@"进度\s*(\d+)\s*%", RegexOptions.Compiled);

    private static long? ParseAudioSrPercent(string line)
    {
        var m = AudioSrPercentRe.Match(line);
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? Math.Clamp(v, 0, 100) : null;
    }

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

    /// <summary>判断 stderr 行是否为致命 GPU 错误（设备丢失 / 显存溢出）</summary>
    private static bool IsFatalGpuLine(string line)
    {
        return line.Contains("vkQueueSubmit failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("VK_ERROR_DEVICE_LOST", StringComparison.OrdinalIgnoreCase)
            || line.Contains("vkAllocateMemory failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("VK_ERROR_OUT_OF_DEVICE_MEMORY", StringComparison.OrdinalIgnoreCase)
            || line.Contains("out of device memory", StringComparison.OrdinalIgnoreCase)
            || line.Contains("out of memory", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> RunStageWithProgress(ProcessStage stage, string stageText, long total,
        string exe, string args, Func<string, long?> frameParser, string? previewDir, CancellationToken ct,
        bool percentDisplay = false, Action<string>? rawLine = null, Func<string>? stageLabel = null)
    {
        // stageLabel 非空时以它为准：AudioSR 的后端要等脚本启动后才确定，阶段文字得跟着变
        Func<string> label = stageLabel ?? (() => stageText);
        var lastLog = DateTime.MinValue;
        return await RunStageWithProgressEx(stage, label, total, exe, args, line =>
        {
            rawLine?.Invoke(line);
            var v = frameParser(line);
            if (v.HasValue)
            {
                var text = label();
                ProgressChanged?.Invoke(new ProcessProgress
                {
                    Stage = stage,
                    StageText = text,
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
                        ? $"{text} {v.Value}%"
                        : $"{text} 已处理 {v.Value}/{total}");
                }
            }
        }, previewDir, ct);
    }

    private async Task<int> RunStageWithProgressEx(ProcessStage stage, Func<string> stageLabel, long total,
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
                        // 帧文件名就是序号（00000123.png）→ 顺带报出真实进度。
                        // 否则这些轮询事件只能带 Total=0，Percent 恒为 0，
                        // 转发到界面会把进度条每 200ms 归零一次（表现为拆帧时进度条抽搐）。
                        long? frameNo = null;
                        if (total > 0 && long.TryParse(Path.GetFileNameWithoutExtension(latest), out var n))
                            frameNo = n;

                        ProgressChanged?.Invoke(new ProcessProgress
                        {
                            Stage = stage,
                            StageText = stageLabel(),
                            LatestFramePath = latest,
                            Current = frameNo ?? 0,
                            Total = frameNo.HasValue ? total : 0
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
    /// Real-ESRGAN 输出 0.00%~98.33% 的帧内 tile 进度；RIFE 无进度输出，仅靠帧数。</summary>
    private async Task<int> RunStageWithDirectoryPolling(ProcessStage stage, string stageText, long total,
        string exe, string args, string outputDir, CancellationToken ct,
        Action<ProcessProgress>? report = null)
    {
        var captured = new System.Text.StringBuilder();
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 当前帧内的 tile 进度（0-100），由 stderr 百分比更新
        double innerPercent = 0;
        var innerLock = new object();
        // Vulkan 设备丢失/显存溢出后只快速失败一次（避免多次触发反复 Kill）
        var fatalKilled = false;

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
                        if (report is not null)
                        {
                            // 涡轮模式：进度交给涡轮进度中枢汇总（避免与另一条流水互相覆盖）
                            report(new ProcessProgress
                            {
                                Stage = stage,
                                StageText = stageText,
                                Current = (long)Math.Floor(current),
                                Total = total,
                                LatestFramePath = FindLatestFrame(outputDir) ?? ""
                            });
                        }
                        else
                        {
                            ProgressChanged?.Invoke(new ProcessProgress
                            {
                                Stage = stage,
                                StageText = stageText,
                                Current = (long)Math.Floor(current),
                                Total = total,
                                LatestFramePath = FindLatestFrame(outputDir) ?? ""
                            });
                        }
                        // 节流实时日志：每 1 秒记录一次帧数，避免 Real-ESRGAN stderr 缓冲导致的"假死"
                        var now = DateTime.Now;
                        if ((now - lastLog).TotalSeconds >= 1)
                        {
                            lastLog = now;
                            _logger.Info($"{stageText} 已处理 {count}/{total} 帧");
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

    // ===================== 音频超分（阶段5.5） =====================

    /// <summary>音频超分（AudioSR）：把已提取的音轨转成 48k WAV → 跑 audiosr_onnx.py → 返回升采样后的 48kHz WAV。
    /// 任一前置条件不满足（脚本/权重包缺失、fp16 撞 CPU）或推理失败时返回 null，
    /// 由调用方回退到原音轨 + ffmpeg 重采样，保证主链路不因音频超分而失败。</summary>
    private async Task<string?> RunAudioSrAsync(ProcessingContext ctx, string inputAudio, string tempRoot, CancellationToken ct)
    {
        var precision = string.Equals(ctx.Options.AudioSrPrecision, "fp16", StringComparison.OrdinalIgnoreCase) ? "fp16" : "fp32";
        var packText = precision == "fp16" ? "FP16 低精度性能模式" : "FP32 高精度完美模式";
        // 进度文字带模式标注，避免运行中看不出跑的是哪一档
        var stageLabel = precision == "fp16" ? "音频超分中(FP16)" : "音频超分中(FP32)";
        _audioSrSkipReason = null;
        _audioSrBackend = null;
        _audioSrPrecisionText = packText;
        // 后端要等脚本打印"后端 XXX"才知道；确定后补进阶段文字，如「音频超分中(FP16) · CUDA（GPU）」
        Func<string> stageLabelNow = () =>
            _audioSrBackend is null ? stageLabel : $"{stageLabel} · {_audioSrBackend}";

        if (!ctx.Tools.AudioSrScriptExists)
        {
            _audioSrSkipReason = "驱动脚本缺失";
            _logger.Warn($"音频超分跳过：未找到驱动脚本 {ctx.Tools.AudioSrScript}");
            return null;
        }
        if (!ctx.Tools.AudioSrPackExists(precision))
        {
            _audioSrSkipReason = $"{packText} 的权重包不完整";
            _logger.Warn($"音频超分跳过：{packText} 的权重包不完整（{ctx.Tools.AudioSrModelsFor(precision)}）");
            return null;
        }

        try
        {
            var modelsDir = ctx.Tools.AudioSrModelsFor(precision);
            var inWav = Path.Combine(tempRoot, "audiosr_in_48k.wav").Replace('\\', '/');
            var outWav = Path.Combine(tempRoot, "audiosr_out_48k.wav").Replace('\\', '/');

            _logger.Info($"音频超分开始（{packText}）：AudioSR 升采样到 48kHz");
            // 先宣告阶段：AudioSR 要跑完第一个窗口才吐进度（单个窗口可能几十秒），
            // 不先发一条的话进度条上方会一直停在上一阶段的文案（如"合并中"）上
            ProgressChanged?.Invoke(new ProcessProgress
            {
                Stage = ProcessStage.AudioSr,
                StageText = stageLabel,
                Current = 0,
                Total = 100,
                PercentDisplay = true
            });
            var convArgs = FFmpegCommandBuilder.ToWav48k(inputAudio, inWav);
            _logger.Command($"ffmpeg {convArgs}");
            var convExit = await RunStageAsync(ProcessStage.AudioSr, stageLabel, ctx.Tools.FFmpegExe, convArgs, ct);
            if (convExit != 0 || !File.Exists(inWav))
            {
                _logger.Warn("音频超分跳过：音轨转 48k WAV 失败");
                return null;
            }

            var py = File.Exists(ctx.Tools.PortablePythonExe) ? ctx.Tools.PortablePythonExe : "python";
            var args = AudioSrCommandBuilder.Build(ctx.Tools.AudioSrScript, inWav, outWav, modelsDir, ctx.Settings.UseCpuProcessing);
            _logger.Command($"python {args}");
            var exit = await RunStageWithProgress(ProcessStage.AudioSr, stageLabel, 100,
                py, args, ParseAudioSrPercent, null, ct, percentDisplay: true, stageLabel: stageLabelNow, rawLine: line =>
                {
                    // 驱动脚本开头会打印 "权重包 fp16 / 后端 DirectML(GPU) / DDIM 50 步 ..."。
                    // 把后端摘出来单独提示：否则用户只能看到进度百分比，无法确认音频超分到底跑没跑在显卡上。
                    if (line.Contains("后端 CUDA", StringComparison.OrdinalIgnoreCase))
                    {
                        _audioSrBackend = "CUDA（GPU）";
                        _logger.Success("音频超分推理后端：CUDA（GPU）");
                    }
                    else if (line.Contains("后端 DirectML", StringComparison.OrdinalIgnoreCase))
                    {
                        _audioSrBackend = "DirectML（GPU）";
                        _logger.Success("音频超分推理后端：DirectML（GPU）");
                    }
                    else if (line.Contains("后端 CPU", StringComparison.OrdinalIgnoreCase))
                    {
                        _audioSrBackend = "CPU";
                        _logger.Warn("音频超分推理后端：CPU（显卡后端不可用或权重包只允许 CPU），速度会明显慢于显卡");
                    }
                });
            if (exit != 0 || !File.Exists(outWav))
            {
                _audioSrSkipReason = exit == 0 ? "未产出 48kHz WAV" : $"推理失败（退出码 {exit}）";
                _logger.Warn(exit == 0
                    ? "音频超分失败：未产出 48kHz WAV，本次回退原音轨"
                    : $"音频超分失败（退出码 {exit}），本次回退原音轨");
                return null;
            }

            var sizeMb = new FileInfo(outWav).Length / 1024.0 / 1024.0;
            _logger.Success($"音频超分完成（{packText}）：48kHz WAV {sizeMb:F1} MB");
            return outWav;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _audioSrSkipReason = $"异常（{ex.Message}）";
            _logger.Warn($"音频超分异常，本次回退原音轨：{ex.Message}");
            return null;
        }
    }

    // ===================== 帧去重（阶段1.5）与回填 =====================

    /// <summary>去重结果映射文件（放临时目录，供回填与续跑复用）</summary>
    private static string DedupMapPath(string tempRoot) => Path.Combine(tempRoot, "dedup_map.json");

    /// <summary>读取已存在的去重保留帧数（无文件返回 0）。用于拆帧阶段的续跑判断。</summary>
    private static int TryReadDedupKept(string tempRoot)
    {
        try
        {
            var p = DedupMapPath(tempRoot);
            if (!File.Exists(p)) return 0;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
            return doc.RootElement.TryGetProperty("keptCount", out var v) ? v.GetInt32() : 0;
        }
        catch { return 0; }
    }

    /// <summary>读取地图里的"保留帧源序号"（1 基）。超分对全部源帧跑完后，补帧输入按它挑帧。</summary>
    private static List<int> TryReadDedupKeptIndices(string tempRoot)
    {
        var list = new List<int>();
        try
        {
            var p = DedupMapPath(tempRoot);
            if (!File.Exists(p)) return list;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
            if (!doc.RootElement.TryGetProperty("keptIndices", out var arr)) return list;
            foreach (var item in arr.EnumerateArray()) list.Add(item.GetInt32());
        }
        catch { }
        return list;
    }

    /// <summary>补帧输入子集的完成标记：帧数/字节数/源目录/保留列表指纹。
    /// 续跑复用前逐项校验，避免误用上一次任务遗留下来的子集。</summary>
    private sealed record KeptSubsetMarker(int Count, long Bytes, string SrcDir, string KeptHash);

    /// <summary>完成标记必须放在帧目录【外面】。RIFE 会把 -i 输入目录里的每一个条目都当图片解码，
    /// 目录内多一个 .json 会让 rife-ncnn-vulkan 直接崩溃（实测 exit=-1073741819，
    /// stderr 全是 "decode image .../_subset_ok.json failed"）。</summary>
    private static string KeptSubsetMarkerPath(string dstDir) => dstDir + ".done.json";

    private static string FingerprintOf(List<int> keptIndices)
        => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join(",", keptIndices))));

    /// <summary>子集是否已完整可用：完成标记存在，且帧数/字节数/源目录/保留列表指纹四项全对得上。</summary>
    private static bool KeptSubsetUsable(string dstDir, string srcDir, List<int> keptIndices)
    {
        try
        {
            var p = KeptSubsetMarkerPath(dstDir);
            if (!File.Exists(p)) return false;
            var m = System.Text.Json.JsonSerializer.Deserialize<KeptSubsetMarker>(File.ReadAllText(p));
            if (m is null) return false;
            if (m.Count != keptIndices.Count) return false;
            if (!string.Equals(m.SrcDir, srcDir, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(m.KeptHash, FingerprintOf(keptIndices), StringComparison.Ordinal)) return false;
            var files = Directory.GetFiles(dstDir, "*.png");
            if (files.Length != m.Count) return false;
            long bytes = 0;
            foreach (var f in files) bytes += new FileInfo(f).Length;
            return bytes == m.Bytes;
        }
        catch { return false; }
    }

    /// <summary>准备补帧输入子集：按 dedup_map.json 的保留帧序号，从 srcDir（超分产物等）里挑出保留帧，
    /// 复制到 dstDir 并重新连续编号（1 基）。补帧输入必须缩成"保留帧序列"，否则 RIFE 的输入/输出
    /// 比例会按全部帧数放大，回填按倍率取块会整体错位。
    ///
    /// 三处稳健性（实测踩过：重启后 Directory.Delete 因残留占用抛 Access denied，整个任务直接失败）：
    /// ① 续跑时子集若已完整（带校验标记）直接复用，不做"删几千个文件再拷一遍"；
    /// ② 清理与复制全程在后台线程，并上报进度（否则几千个文件静默几十秒，看起来像卡死）；
    /// ③ 逐个文件带退避重试，真清不掉就换备用目录，绝不因为一个被占用的文件让任务失败。
    /// 返回可用的子集目录；失败返回 null。</summary>
    private async Task<string?> EnsureKeptSubsetAsync(string tempRoot, string srcDir, string dstDir, CancellationToken ct)
    {
        var keptIndices = TryReadDedupKeptIndices(tempRoot);
        if (keptIndices.Count == 0)
        {
            _logger.Warn("帧去重：地图里没有保留帧序号，无法准备补帧输入子集");
            return null;
        }

        if (KeptSubsetUsable(dstDir, srcDir, keptIndices))
        {
            _logger.Info($"帧去重：补帧输入子集已存在且校验一致（{keptIndices.Count} 帧），直接复用");
            return dstDir;
        }

        if (await TryBuildKeptSubsetAsync(srcDir, dstDir, keptIndices, ct)) return dstDir;

        var altDir = dstDir + "_b";
        _logger.Warn($"帧去重：{Path.GetFileName(dstDir)} 准备失败（可能被残留进程或杀毒软件占用），" +
                     $"改用备用目录 {Path.GetFileName(altDir)} 重试");
        if (await TryBuildKeptSubsetAsync(srcDir, altDir, keptIndices, ct)) return altDir;
        return null;
    }

    private async Task<bool> TryBuildKeptSubsetAsync(string srcDir, string dstDir, List<int> keptIndices, CancellationToken ct)
    {
        try
        {
            // 清理旧内容：逐个删（带退避重试，并上报进度）。整体 Directory.Delete 只要有一个文件被占用
            // 就全盘失败，这正是"重启后 Access to the path ... is denied"的直接原因。
            // 注意清【全部】文件而不只是 *.png：目录里任何非图片残留都会让 RIFE 解码失败而崩溃。
            var stale = Directory.Exists(dstDir)
                ? Directory.GetFiles(dstDir)
                : Array.Empty<string>();
            if (stale.Length > 0)
            {
                await Task.Run(() =>
                {
                    for (var i = 0; i < stale.Length; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        TryDeleteWithRetry(stale[i]);
                        if ((i + 1) % 200 == 0 || i + 1 == stale.Length)
                        {
                            var text = $"帧去重：准备补帧输入（清理上次残留）{i + 1}/{stale.Length}";
                            _logger.Info(text);
                            ProgressChanged?.Invoke(new ProcessProgress
                            {
                                Stage = ProcessStage.Splitting,
                                StageText = "帧去重：准备补帧输入",
                                Current = i + 1,
                                Total = stale.Length,
                                PercentDisplay = true,
                                CustomDetail = text
                            });
                        }
                    }
                }, ct);
            }
            else if (!Directory.Exists(dstDir))
            {
                Directory.CreateDirectory(dstDir);
            }
            try { File.Delete(KeptSubsetMarkerPath(dstDir)); } catch { }

            var total = keptIndices.Count;
            var written = 0;
            long bytes = 0;
            await Task.Run(() =>
            {
                for (var k = 0; k < total; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    var src = Path.Combine(srcDir, $"{keptIndices[k]:D8}.png");
                    if (!File.Exists(src)) throw new FileNotFoundException($"源帧不存在：{src}");
                    var dst = Path.Combine(dstDir, $"{k + 1:D8}.png");
                    CopyWithRetry(src, dst);
                    bytes += new FileInfo(dst).Length;
                    written++;
                    if (written % 200 == 0 || written == total)
                    {
                        var text = $"帧去重：准备补帧输入（保留帧）{written}/{total}";
                        _logger.Info(text);
                        ProgressChanged?.Invoke(new ProcessProgress
                        {
                            Stage = ProcessStage.Splitting,
                            StageText = "帧去重：准备补帧输入",
                            Current = written,
                            Total = total,
                            PercentDisplay = true,
                            CustomDetail = text
                        });
                    }
                }
            }, ct);

            var onDisk = Directory.GetFiles(dstDir, "*.png").Length;
            if (onDisk != total)
            {
                _logger.Warn($"帧去重：补帧输入子集数量不符（{onDisk}/{total}，源目录 {srcDir}）");
                return false;
            }
            File.WriteAllText(KeptSubsetMarkerPath(dstDir), System.Text.Json.JsonSerializer.Serialize(
                new KeptSubsetMarker(total, bytes, srcDir, FingerprintOf(keptIndices))));
            _logger.Info($"帧去重：补帧输入已缩为保留帧子集 {total} 帧（源目录 {srcDir}）");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warn($"帧去重：准备补帧输入子集失败（{dstDir}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>带退避重试的复制：预览解码、杀毒实时扫描、上一次残留的进程都可能短暂占用目标文件。
    /// 目标若是只读（拷贝会继承属性）先去掉只读位再覆盖。</summary>
    private static void CopyWithRetry(string src, string dst, int attempts = 5)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (File.Exists(dst))
                {
                    var a = File.GetAttributes(dst);
                    if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(dst, a & ~FileAttributes.ReadOnly);
                }
                File.Copy(src, dst, overwrite: true);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Thread.Sleep(150 * (i + 1));
        }
        throw new IOException($"复制失败（目标被占用或不可写）：{dst}");
    }

    /// <summary>带退避重试的删除；始终失败只告警返回（留着的内容会让数量校验失败，由调用方换备用目录）。</summary>
    private static void TryDeleteWithRetry(string path, int attempts = 4)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (!File.Exists(path)) return;
                var a = File.GetAttributes(path);
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
                File.Delete(path);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Thread.Sleep(120 * (i + 1));
        }
    }

    /// <summary>阶段1.5：相邻帧去重。判为重复的帧移到 input_frames_dedup_removed（不真删），
    /// 写 dedup_map.json（keptCount + 回填位置表）。返回保留帧数；未生效返回 0。</summary>
    private async Task<int> RunDedupAsync(ProcessingContext ctx, string tempRoot, string inputFrames, long totalFrames, CancellationToken ct)
    {
        try
        {
            var kept = CountFrames(inputFrames);

            // 续跑：map 已存在且与实际帧数对得上 → 直接复用，不重复分析。
            // 勾选补帧时重复帧留在 input_frames 里（比全量），未勾选时已被移出（比保留数）。
            var cached = TryReadDedupKept(tempRoot);
            if (cached > 0 && (ctx.Options.Interpolation ? kept == totalFrames : cached == kept))
            {
                _logger.Info($"帧去重：复用已有结果（保留 {cached} / {totalFrames} 帧）");
                return cached;
            }

            // 拆帧结果不完整时不做（避免把不完整序列当成完整序列去重）
            if (kept < totalFrames)
            {
                _logger.Warn($"帧去重跳过：拆帧结果不完整（{kept}/{totalFrames} 帧）");
                return 0;
            }

            var modeText = ctx.Settings.DedupMode == "uhd" ? "UHD Mode 完美模式" : "Performance Mode 性能模式";
            _logger.Info($"帧去重开始（{modeText}）：共 {kept} 帧");
            var modeShort = ctx.Settings.DedupMode == "uhd" ? "完美" : "性能";

            var opt = FrameDedupService.Options.FromMode(ctx.Settings.DedupMode);
            // 勾选补帧时：超分对全部源帧跑，去重只省补帧的算力 —— 重复帧必须留在 input_frames 里供超分使用。
            // 未勾选补帧时：去重省的是超分，把重复帧移出 input_frames（保留在 removed 目录，便于排查/回退）。
            var dedupOnlyForIf = ctx.Options.Interpolation;
            var removedDir = Path.Combine(tempRoot, "input_frames_dedup_removed");
            if (!dedupOnlyForIf) Directory.CreateDirectory(removedDir);

            // 进度文本形如：帧去重(性能) 判决阶段(dHash) 已判重365帧 第1234帧/共7187帧
            // （"已判重"=累计判为重复的帧数；"第 N 帧/共 M 帧"=判决进度；百分比单独显示在右侧）
            string DedupText(string phase, int dup) => $"帧去重({modeShort}) {phase} 已判重{dup}帧";

            var result = await FrameDedupService.AnalyzeAsync(inputFrames, opt, pr =>
                ProgressChanged?.Invoke(new ProcessProgress
                {
                    Stage = ProcessStage.Splitting,
                    StageText = DedupText(pr.Phase, pr.DuplicateCount),
                    Current = pr.Done,
                    Total = pr.Total,
                    PercentDisplay = false,   // 用"第 N 帧/共 M 帧"表达判决进度，避免与"已判重 N 帧"挨在一起被误读
                    FrameCompareMode = true,
                    // 三联预览：右「筛选帧」= 正在判决的帧；中「对比帧」= 与它作比较的上一帧；
                    // 左「判决帧」= 最近被判为重复的那张（还没判出重复时留空）
                    LatestFramePath = pr.CurrentFramePath,
                    CompareFramePath = pr.CompareFramePath,
                    CompareFrameIndex = pr.CompareIndex,
                    VerdictFramePath = pr.VerdictFramePath,
                    VerdictFrameIndex = pr.VerdictIndex
                }), ct);

            var decisions = result.Decisions.ToList();
            var moveFailed = 0;
            if (!dedupOnlyForIf)
            {
                // 重复帧移出 input_frames（保留在 removed 目录，便于排查/回退）
                ProgressChanged?.Invoke(new ProcessProgress
                {
                    Stage = ProcessStage.Splitting,
                    StageText = DedupText("搬移重复帧", result.DuplicateCount),
                    Current = result.Total,
                    Total = result.Total,
                    PercentDisplay = true,
                    FrameCompareMode = true
                });
                // 逐个搬移并重试：预览图解码、杀毒软件实时扫描、资源管理器缩略图都可能短暂占用 PNG。
                // 极个别帧始终搬不动时，把它改判为"保留"——必须保证判决表与磁盘上的实际帧数一致，
                // 否则回填表会与文件对不上（成品帧数、时长都会缩水）。
                // 搬移是几千次文件操作（含失败重试），必须放后台线程，否则会阻塞 UI
                await Task.Run(() =>
                {
                    for (var i = 0; i < decisions.Count; i++)
                    {
                        var d = decisions[i];
                        if (!d.Duplicate) continue;
                        var src = Path.Combine(inputFrames, $"{d.Index:D8}.png");
                        if (!File.Exists(src)) continue;
                        var dst = Path.Combine(removedDir, $"{d.Index:D8}.png");
                        if (!TryMoveFrame(src, dst))
                        {
                            decisions[i] = d with { Duplicate = false };
                            moveFailed++;
                        }
                    }
                }, ct);
                if (moveFailed > 0)
                {
                    _logger.Warn($"帧去重：{moveFailed} 帧因文件被占用无法移出，已改判为保留（成品帧数与时长不受影响）；" +
                                 "若频繁出现，可关闭图片预览或把临时目录加入杀毒软件实时扫描白名单");
                    ProgressChanged?.Invoke(new ProcessProgress
                    {
                        Stage = ProcessStage.Splitting,
                        StageText = DedupText("搬移重复帧", decisions.Count(x => x.Duplicate)),
                        Current = decisions.Count,
                        Total = decisions.Count,
                        PercentDisplay = true,
                        FrameCompareMode = true
                    });
                }
            }

            // 回填位置表：第 k 个输出槽位应取"保留序列"里的第几帧（1 基）
            var expandPos = new List<int>(decisions.Count);
            // 保留帧的源序号（1 基）：补帧输入子集按它从超分产物里挑帧（超分对全部源帧跑了）
            var keptIndices = new List<int>();
            var seenKept = 0;
            for (var i = 0; i < decisions.Count; i++)
            {
                if (!decisions[i].Duplicate)
                {
                    seenKept++;
                    keptIndices.Add(i + 1);
                }
                expandPos.Add(Math.Max(1, seenKept));
            }

            var keptNow = dedupOnlyForIf ? keptIndices.Count : CountFrames(inputFrames);
            var movedCount = decisions.Count(x => x.Duplicate);   // 真实剔除量
            var dedupRate = decisions.Count > 0 ? (double)movedCount / decisions.Count : 0;
            File.WriteAllText(DedupMapPath(tempRoot), System.Text.Json.JsonSerializer.Serialize(new
            {
                mode = ctx.Settings.DedupMode,
                total = result.Total,
                keptCount = keptNow,
                duplicateCount = movedCount,
                expandPos,
                keptIndices
            }));

            _logger.Success(dedupOnlyForIf
                ? $"帧去重完成：剔除 {movedCount} 帧；超分仍处理全部 {decisions.Count} 帧，补帧只处理 {keptNow} 帧（省 {dedupRate:P1}）"
                : $"帧去重完成：剔除 {movedCount} 帧，实际处理 {keptNow} 帧（省 {dedupRate:P1}），重复帧保留在 {removedDir}");

            // 各阶淘汰量（论文四阶级联的可观测性：出问题时能一眼看出卡在哪一阶）
            var byPixel = result.Decisions.Count(d => d.Decided == FrameDedupService.Stage.PixelDiff);
            var byHash = result.Decisions.Count(d => d.Decided == FrameDedupService.Stage.Hash);
            var byQr = result.Decisions.Count(d => d.Decided == FrameDedupService.Stage.Qr);
            var byFlow = result.Decisions.Count(d => d.Decided == FrameDedupService.Stage.Flow);
            _logger.Info($"各阶判决：像素差淘汰 {byPixel}，dHash 淘汰 {byHash}，QR 淘汰 {byQr}，" +
                         $"光流淘汰 {byFlow}，四阶全过判重 {result.DuplicateCount}");

            var qrSample = result.Decisions.Where(d => d.Decided >= FrameDedupService.Stage.Qr).Select(d => d.Qr).ToList();
            if (qrSample.Count > 0)
                _logger.Info($"QR 距离 D_norm（论文归一化）：最小 {qrSample.Min():0.###e-0}，最大 {qrSample.Max():0.###e-0}，" +
                             $"阈值 {opt.QrThreshold} → 共淘汰 {byQr} 帧");
            var flowSample = result.Decisions.Where(d => d.Decided >= FrameDedupService.Stage.Flow).Select(d => d.MeanFlow).ToList();
            if (flowSample.Count > 0)
                _logger.Info($"光流 MeanFlow：最小 {flowSample.Min():0.###}，最大 {flowSample.Max():0.###} 像素，" +
                             $"阈值 {opt.FlowThreshold} → 共淘汰 {byFlow} 帧");
            return keptNow;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warn($"帧去重失败，本次按不去重继续：{ex.Message}");
            return 0;
        }
    }

    /// <summary>合并前回填：按 dedup_map.json 的位置表，把保留帧展开成完整序列。
    /// ratio = 每个源帧在成片里占的帧数（补帧倍率，或"目标帧率 ÷ 源帧率"得到的小数；不补帧时 = 1），
    /// blockMult = RIFE 输出里"每个保留帧占几帧"的整数粒度（= round(ratio)），用于在保留帧区块里取插值帧。
    ///
    /// 映射方式：**按输出帧的时间位置**逐帧取源（而不是"每个槽位固定 ratio 帧"）——
    /// ratio 是小数时每个槽位占的帧数会交替取整（如 5.005 → 多数槽位 5 帧、少数 6 帧），
    /// 只有按时间取帧才能让输出帧数 = round(源帧数×ratio) 且时间轴不错位。
    ///
    /// 内容规则（BUG-18 修正）：
    /// - 重复段（该帧被判为与上一帧重复，段长 &gt; 1）→ **整段（含段尾）定格**，取该源帧自身的产物；
    ///   段尾不能放过渡帧：RIFE 只被要求给"相邻两个保留帧之间" blockMult 帧，而这一段在成片里
    ///   横跨 L 个源槽位，那 blockMult 帧是按整段均匀摊的 —— 拿来当段尾的过渡帧会把下一段画面混进来（幽灵帧）。
    /// - 段长为 1 的源帧（没有重复，下一帧就是另一个源帧）→ 才用 RIFE 的过渡块（该间隔正好等于该槽位时长）。
    /// selfFramesDir 非空时（勾选补帧：超分对全部源帧跑了），定格帧取该源帧**自身**的产物。
    /// 返回 true 表示已生成可用的展开目录（随后合并改读该目录）。</summary>
    private async Task<bool> TryExpandDedupFramesAsync(string tempRoot, string framesDir, string expandedDir,
        double ratio, int blockMult, CancellationToken ct, string? selfFramesDir = null)
    {
        try
        {
            var mapPath = DedupMapPath(tempRoot);
            if (!File.Exists(mapPath)) return false;

            List<int>? expandPos = null;
            using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mapPath)))
            {
                if (doc.RootElement.TryGetProperty("expandPos", out var arr))
                {
                    expandPos = new List<int>();
                    foreach (var item in arr.EnumerateArray()) expandPos.Add(item.GetInt32());
                }
            }
            if (expandPos is null || expandPos.Count == 0) return false;
            if (ratio < 1e-6) ratio = 1;
            if (blockMult < 1) blockMult = 1;

            // 保留帧按文件名排序后的列表：位置表里的序号指向"第几个保留帧"
            var keptFiles = Directory.GetFiles(framesDir, "*.png");
            if (keptFiles.Length == 0) return false;
            Array.Sort(keptFiles, StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(expandedDir))
            {
                // 上一次中断留下的展开目录可能有几万帧：同步删除会卡死 UI，表现为进度文字不动
                var cleanText = "回填：清理上次展开文件…";
                ProgressChanged?.Invoke(new ProcessProgress
                {
                    Stage = ProcessStage.Merging,
                    StageText = cleanText,
                    CustomDetail = cleanText
                });
                await Task.Run(() => Directory.Delete(expandedDir, true), ct);
            }
            Directory.CreateDirectory(expandedDir);

            // 预扫：每个槽位所属"重复段"的长度。expandPos 非递减，同值连续区间 = 一个重复段
            // （长度 1 = 该源帧没有重复，后面紧跟的是另一个源帧）。
            var runLens = new int[expandPos.Count];
            for (var i = 0; i < expandPos.Count; )
            {
                var j = i;
                while (j + 1 < expandPos.Count && expandPos[j + 1] == expandPos[i]) j++;
                for (var t = i; t <= j; t++) runLens[t] = j - i + 1;
                i = j + 1;
            }

            // 输出总帧数 = round(源帧数 × ratio)：ratio 为整数时等于原来的 源帧数×倍率
            var totalOut = (long)Math.Round(expandPos.Count * ratio);
            if (totalOut <= 0) return false;

            await Task.Run(() =>
            {
                for (var q = 1; q <= totalOut; q++)
                {
                    ct.ThrowIfCancellationRequested();
                    // 第 q 个输出帧对应的源时间（0 基，单位 = 源帧）→ 落在哪个源槽位、槽位内子位置
                    var pos = (q - 1) / ratio;
                    var slot = (int)Math.Floor(pos);
                    if (slot >= expandPos.Count) slot = expandPos.Count - 1;
                    if (slot < 0) slot = 0;
                    var f = pos - slot;

                    var selfFile = selfFramesDir is null ? null : Path.Combine(selfFramesDir, $"{slot + 1:D8}.png");
                    var held = runLens[slot] > 1;   // 重复段（含段尾）整段定格
                    string? srcPath = null;

                    // 定格段：取该源帧自身的产物（没有自身产物时退回保留帧区块首帧）
                    if (selfFile is not null && File.Exists(selfFile) && (held || f <= 1e-3))
                        srcPath = selfFile;

                    if (srcPath is null)
                    {
                        // 插值帧：取该槽位对应保留帧区块里、最接近子位置 f 的那一帧
                        var k = expandPos[slot];
                        var j = held ? 0 : (int)Math.Round(f * blockMult);
                        if (j < 0) j = 0;
                        if (j >= blockMult) j = blockMult - 1;
                        var srcIdx = (k - 1) * blockMult + j;
                        if (srcIdx >= 0 && srcIdx < keptFiles.Length) srcPath = keptFiles[srcIdx];
                        else if (selfFile is not null && File.Exists(selfFile)) srcPath = selfFile;
                    }
                    if (srcPath is null) continue;

                    var dst = Path.Combine(expandedDir, $"{q:D8}.png");
                    try { File.Copy(srcPath, dst, overwrite: true); }
                    catch { /* 个别帧复制失败时跳过，不中断整个流程 */ }
                }
            }, ct);

            // 回填必须"一个不多一个不少"：展开后的帧数应与 源帧数×比例 严格相等。
            // 单个 File.Copy 失败上面是 catch 跳过，若最终数量对不上，说明成品会少帧，必须报出来。
            var n = CountFrames(expandedDir);
            if (n != totalOut)
            {
                _logger.Warn($"帧去重回填不完整：展开 {n} 帧，应为 {totalOut} 帧（个别帧复制失败或源帧缺失）");
                return false;
            }
            _logger.Info($"帧去重回填：{expandPos.Count} 源帧 → 展开为 {n} 帧（{ratio:0.###}×）");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 这里返回 false 后调用方会直接 Fail 整个任务（见合并阶段），不是"继续跑"，
            // 日志措辞必须跟行为一致，否则排查时会被误导成"少帧成品已经产出"。
            _logger.Warn($"帧去重回填失败：{ex.GetType().Name}: {ex.Message}（本次按失败处理，不会产出少帧成品）");
            return false;
        }
    }

    /// <summary>涡轮模式适用性判定：需要勾选超分或补帧（纯粹有外部工具可并行）。
    /// 帧去重已支持（按块流式判决，判决与 GPU 侧重叠）。</summary>
    private static bool TurboSupported(ProcessingContext ctx)
        => ctx.Options.SuperResolution || ctx.Options.Interpolation;

    /// <summary>待 GPU 处理的块：块内帧清单 + 各阶段输出的全局起始编号（块内连续编号需要整体平移）。
    /// Frames = 该块送超分的全部源帧；KeptOffsets = 去重后保留帧在 Frames 里的
    /// 下标（0 基，升序），补帧只喂这些帧；为 null 表示未去重，补帧用全部帧。</summary>
    private sealed record TurboBlock(int Index, List<string> Frames, int SrStartNumber, int IfStartNumber)
    {
        public List<int>? KeptOffsets { get; init; }

        /// <summary>该块补帧实际处理的帧数（去重时 = 保留帧数，否则 = 块内全部帧）。</summary>
        public int IfFrameCount => KeptOffsets?.Count ?? Frames.Count;
    }

    /// <summary>涡轮模式进度中枢：把 CPU 去重判决与两条 GPU 流水（超分 / 补帧）的进度汇总成**一条单调不倒退的总进度**，
    /// 并维护每条流水各自的最新产出帧（预览区一格一条流水，互不覆盖）。
    ///
    /// 背景：涡轮模式下有三处会同时上报进度 —— 去重按"帧"、块提交按"块"、两条流水各按"块内帧"，
    /// 口径互不相同又交替到达，百分比在几个口径之间来回跳，这就是"进度条抽抽"的根源。
    /// 这里统一成「源帧」：每个阶段都要经手全部 N 个源帧（被去重跳过的帧对该阶段而言也算已处理完），
    /// 总进度 = 各阶段已完成源帧之和 / 各阶段总量之和，且用 _lastPercent 钳成只增不减。
    /// 这样总量在开跑前就完全确定，不会出现"去重先跑完就把进度条顶到 100%"或中途倒退。</summary>
    private sealed class TurboProgressHub
    {
        private readonly Action<ProcessProgress> _emit;
        private readonly object _lock = new();
        private readonly List<Slot> _slots = new();
        private readonly Dictionary<string, Slot> _byKey = new();
        /// <summary>单调钳制用的上次百分比：任何情况下都不让进度条倒退</summary>
        private double _lastPercent;

        private sealed class Slot
        {
            public string Key = "";
            public string StageName = "";
            /// <summary>该阶段要经手的源帧总数（各阶段相同，开跑前即确定）</summary>
            public long Total;
            /// <summary>已完成整块的源帧数（含被去重跳过、对该阶段无需处理的帧）</summary>
            public long Committed;
            /// <summary>当前块内已完成的源帧数</summary>
            public long Live;
            /// <summary>最新产出帧路径与帧号（预览用）</summary>
            public string Path = "";
            public long FrameIndex;
        }

        public TurboProgressHub(Action<ProcessProgress> emit) => _emit = emit;

        /// <summary>注册一条流水线，编号按注册顺序（预览标签「流水线 N」即由此而来）。</summary>
        public void AddPipeline(string key, string stageName, long total)
        {
            lock (_lock)
            {
                var slot = new Slot { Key = key, StageName = stageName, Total = Math.Max(0, total) };
                _slots.Add(slot);
                _byKey[key] = slot;
            }
        }

        /// <summary>汇报某阶段当前块的处理进度：done = 本块内已完成的源帧数。</summary>
        public void Advance(string key, long done, string? path = null, long frameIndex = 0)
        {
            lock (_lock)
            {
                if (!_byKey.TryGetValue(key, out var s)) return;
                s.Live = Math.Max(0, done);
                if (!string.IsNullOrEmpty(path)) { s.Path = path; s.FrameIndex = frameIndex; }
                EmitLocked();
            }
        }

        /// <summary>某阶段处理完一整块：本块源帧数计入已完成，块内进度清零。</summary>
        public void CommitBlock(string key, long sourceFrames)
        {
            lock (_lock)
            {
                if (!_byKey.TryGetValue(key, out var s)) return;
                s.Committed += Math.Max(0, sourceFrames);
                s.Live = 0;
                EmitLocked();
            }
        }

        /// <summary>被去重剔除的源帧：它们不会再进 GPU，对这些阶段视作已处理完（否则进度条永远到不了 100%）。</summary>
        public void Credit(string key, long sourceFrames)
        {
            lock (_lock)
            {
                if (!_byKey.TryGetValue(key, out var s) || sourceFrames <= 0) return;
                s.Committed += sourceFrames;
            }
        }

        /// <summary>立刻按当前状态推一次进度（用于开跑时先把预览区各流水格子摆出来）。</summary>
        public void Emit()
        {
            lock (_lock) EmitLocked();
        }

        private void EmitLocked()
        {
            long done = 0, total = 0;
            var parts = new List<string>(_slots.Count);
            var pipelines = new List<PipelineFramePreview>(_slots.Count);
            for (var i = 0; i < _slots.Count; i++)
            {
                var s = _slots[i];
                var live = Math.Min(s.Committed + s.Live, s.Total);
                done += live;
                total += s.Total;
                parts.Add($"{s.StageName} {live}/{s.Total}");
                pipelines.Add(new PipelineFramePreview
                {
                    Key = s.Key,
                    Label = $"流水线 {i + 1}（{s.StageName}）",
                    FramePath = s.Path,
                    FrameIndex = s.FrameIndex
                });
            }

            var percent = total > 0 ? Math.Min(100.0, done * 100.0 / total) : 0;
            if (percent < _lastPercent) percent = _lastPercent;   // 单调钳制：绝不倒退
            else _lastPercent = percent;

            var summary = parts.Count > 0 ? string.Join(" · ", parts) : "准备中";
            _emit(new ProcessProgress
            {
                Stage = ProcessStage.SuperRes,
                StageText = $"[涡轮] {summary}（源帧）",
                Current = (long)Math.Round(percent),
                Total = 100,
                PercentDisplay = true,
                CustomDetail = $"[涡轮] 并行推进 {percent:0.#}%（各流水已完成源帧 {summary}）",
                Pipelines = pipelines
            });
        }
    }

    /// <summary>涡轮模式核心：把帧序列切成块，CPU 侧按块准备输入（开了帧去重时判决也在这里做），
    /// 两条 GPU 流水用有界队列串起来——超分 worker 处理块 n+1 的同时补帧 worker 正在处理块 n，
    /// 两个外部进程真正同时跑；队列容量 2 形成背压，避免帧文件与显存双爆。
    /// 产物按全局连续编号写入 srFrames / ifFrames，与逐阶段串行的目录结构完全一致，
    /// 因此后续阶段会凭"帧数够"自动跳过，合并阶段不需要任何改动。
    /// 返回 (Ok, DedupKept)：Ok=false 表示未完成（调用方回退逐阶段串行）；DedupKept 是流水线内
    /// 做完去重后的保留帧数（未启用去重时为 0，调用方据此跳过串行去重）。</summary>
    private async Task<(bool Ok, int DedupKept)> RunTurboBlocksAsync(ProcessingContext ctx, string tempRoot, string inputFrames,
        string srFrames, string ifFrames, long totalFrames, int ifMult, bool cpu, CancellationToken ct)
    {
        var blockFrames = Math.Clamp(ctx.Settings.TurboBlockFrames, 32, 4000);
        var turboRoot = Path.Combine(tempRoot, "turbo_blocks");

        // 线程：两条流水都按用户设定的线程数跑，不对半分。
        // 早先试过「两条流水各分一半」（8 → 每条 4），实测反而慢一大截：单个进程用满线程比两个进程各半
        // 高效得多（模型只加载一次、显存与调度冲突少）。296 帧 360p 实测：串行 1:8:8 = 58.5s，
        // 涡轮两条各 1:8:8 = 59.6s，涡轮两条各 1:4:4 = 72.7s。线程数交给用户自己权衡。
        var budget = Math.Clamp(ctx.Settings.ThreadCount, 1, 32);
        var jThreads = $"1:{budget}:{budget}";

        var files = Directory.GetFiles(inputFrames, "*.png");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        if (files.Length == 0)
        {
            _logger.Warn("[涡轮] 输入帧目录为空，无法进行块级流水");
            return (false, 0);
        }

        try
        {
            if (Directory.Exists(turboRoot))
            {
                // 上次中断留下的块目录可能有上万个文件：同步递归删除会卡住 UI 线程，
                // 表现就是进度文字一直停在"准备中"却还在跑。这里先给文案，再丢后台线程删。
                var cleanText = "准备中：清理上次涡轮临时文件…";
                ProgressChanged?.Invoke(new ProcessProgress
                {
                    Stage = ProcessStage.SuperRes,
                    StageText = cleanText,
                    CustomDetail = cleanText
                });
                await Task.Run(() => Directory.Delete(turboRoot, true), ct);
            }
            Directory.CreateDirectory(turboRoot);
            await CleanPartialOutputAsync(srFrames, ProcessStage.SuperRes, "超分中", ct);
            if (ctx.Options.Interpolation)
                await CleanPartialOutputAsync(ifFrames, ProcessStage.Interpolating, "补帧中", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warn($"[涡轮] 初始化临时目录失败：{ex.Message}");
            return (false, 0);
        }

        // 帧去重判决也并进流水线：判决是纯 CPU 运算（dHash / QR / 光流），正好与 GPU 侧重叠。
        // 拆帧结果不完整时不做（与串行路径同一条规则），本次交给后面的串行阶段处理。
        var streamDedup = ctx.Settings.DedupEnabled && files.Length >= totalFrames;
        if (ctx.Settings.DedupEnabled && !streamDedup)
            _logger.Warn($"[涡轮] 拆帧结果不完整（{files.Length}/{totalFrames} 帧），本次不做去重判决");
        var dedupOpt = streamDedup ? FrameDedupService.Options.FromMode(ctx.Settings.DedupMode) : null;
        var dedupModeShort = ctx.Settings.DedupMode == "uhd" ? "完美" : "性能";
        // 全局原始判决（未做时序平滑）：下标 = 原始帧号 - 1
        var raw = streamDedup ? new List<FrameDedupService.FrameDecision>(files.Length) : null;
        // 定稿判决（下标 = 原始帧号 - 1）：发布一块时写入，之后不再改动。
        // 末尾不再对 raw 统一跑一次时序平滑 —— 发布时每块都已掌握全局长度，结论就是最终结论；
        // 末尾再平滑一次只会在"搬不动改判保留"改变过序列之后产生级联差异（实测差 6 帧、被判据对不上拦下）。
        var finalDup = streamDedup ? new bool[files.Length] : null;
        var finalized = 0;   // 已写入定稿表的帧数
        var removedDir = Path.Combine(tempRoot, "input_frames_dedup_removed");
        var dedupMoveFailed = 0;
        // 勾选补帧时超分对整块全部帧跑，重复帧不能移出（超分要用它们）；
        // 只有"去重省超分"的路径（未勾选补帧）才需要搬移目录。
        var dedupMovesFrames = streamDedup && !ctx.Options.Interpolation;
        if (dedupMovesFrames) Directory.CreateDirectory(removedDir);   // 搬移重复帧前必须先建好目标目录

        var blockCount = (int)Math.Ceiling(files.Length / (double)blockFrames);
        _logger.Info($"[涡轮] 块级流水开始：{files.Length} 帧 → {blockCount} 块 × {blockFrames} 帧，" +
                     (streamDedup
                         ? $"CPU 侧去重判决（{dedupModeShort}模式）与 GPU 侧超分/补帧同时进行"
                         : "CPU 侧准备与 GPU 侧超分/补帧同时进行"));

        // 涡轮统一进度中枢：三处并发上报（去重按帧、块提交按块、两条流水各按块内帧）在这里汇总成一条单调总进度，
        // 同时按流水保存各自的最新产出帧（预览区一格一条流水）。不走涡轮的串行路径完全不经过这里，行为不变。
        // 计量统一成「源帧」：每个阶段都要经手全部 files.Length 个源帧，被去重跳过的帧由 Credit 记为已完成。
        var hub = new TurboProgressHub(p => ProgressChanged?.Invoke(p));
        if (streamDedup) hub.AddPipeline("dedup", "帧去重", files.Length);
        if (ctx.Options.SuperResolution) hub.AddPipeline("sr", "超分", files.Length);
        if (ctx.Options.Interpolation) hub.AddPipeline("if", "补帧", files.Length);
        hub.Emit();   // 先把各流水的格子摆出来，预览区立刻能看出当前在跑几条流水

        // 两条 GPU 流水串起来：超分 worker 处理块 n+1 的同时，补帧 worker 正在处理块 n，
        // 两个外部进程各吃一份显存、真正同时跑；CPU 侧负责把下一块的帧喂进来。
        // 队列容量 2：手里最多压两块，再往上就让上游等 —— 这就是背压，避免帧文件与显存双爆。
        var srQueue = Channel.CreateBounded<TurboBlock>(new BoundedChannelOptions(2)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var ifQueue = Channel.CreateBounded<TurboBlock>(new BoundedChannelOptions(2)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        // 故障联动：任一 worker 抛异常时必须取消整条流水，否则另一侧会因为输出队列满（容量 2 + Wait）
        // 永久阻塞在 WriteAsync 上 —— 表现是"处理卡住不动"，而不是报错退出，用户完全不知道该重试还是清理。
        // turboToken 用于本流水内部的所有读写与子进程调用；用户按停止走的是外层 ct，两者区分开，
        // 这样"用户主动停止"与"worker 报错"能在最后分辨，前者不掩盖、后者不漏报。
        using var turboCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var turboToken = turboCts.Token;

        var srWorker = Task.Run(async () =>
        {
            try
            {
                await foreach (var block in srQueue.Reader.ReadAllAsync(turboToken))
                {
                    if (!await SuperResBlockAsync(ctx, block, turboRoot, srFrames, jThreads, cpu, turboToken,
                            p => hub.Advance("sr", p.Current, p.LatestFramePath,
                                             block.SrStartNumber + Math.Max(0, p.Current - 1))))
                        throw new InvalidOperationException($"第 {block.Index} 块超分失败");
                    hub.CommitBlock("sr", block.Frames.Count);
                    await ifQueue.Writer.WriteAsync(block, turboToken);
                }
                ifQueue.Writer.Complete();
            }
            catch (Exception ex)
            {
                ifQueue.Writer.TryComplete(ex);
                turboCts.Cancel();   // 联动：让补帧侧与生产侧都不要再等
                throw;
            }
        }, turboToken);

        // 补帧流水的进度上报：进度中枢按「源帧」计量，所以把"已产出的输出帧数"换算回源帧数；
        // 借了下一块的首帧会多算一份插值，这里按本块补帧实际处理的帧数（去重时 = 保留帧数）封顶。
        Action<ProcessProgress> ReportIf(TurboBlock block)
        {
            var sourceFrames = (long)block.IfFrameCount;
            return p =>
            {
                var done = Math.Min(p.Current / ifMult, sourceFrames);
                hub.Advance("if", done, p.LatestFramePath, block.IfStartNumber + Math.Max(0, p.Current - 1));
            };
        }

        var ifWorker = Task.Run(async () =>
        {
            // 补帧侧原来没有 try/catch：它一旦抛异常就没人再读 ifQueue，超分侧会永久阻塞在 WriteAsync 上。
            // 现在失败时同时把上游队列关掉 + 取消整条流水，故障表现为明确报错而不是卡死。
            try
            {
            // 押后一块再补帧：要知道"下一帧"是谁才能把块的末帧插值对。
            // RIFE 对没有后继的末帧只能原地保持，而整段处理时它是与下一帧的插值结果 ——
            // 押后一块就能把下一块的首帧借来当"下一帧"，块边界与整段处理结果完全一致。
            TurboBlock? pending = null;
            await foreach (var block in ifQueue.Reader.ReadAllAsync(turboToken))
            {
                if (pending is not null)
                {
                    if (!await InterpBlockAsync(ctx, pending, turboRoot, ifFrames, ifMult, jThreads, cpu, LookaheadOf(block), turboToken, ReportIf(pending)))
                        throw new InvalidOperationException($"第 {pending.Index} 块补帧失败");
                    hub.CommitBlock("if", pending.IfFrameCount);
                }
                pending = block;
            }
            // 最后一块没有"下一块"，它的末帧本来就是原地保持（整段处理时也一样）
            if (pending is not null)
            {
                if (!await InterpBlockAsync(ctx, pending, turboRoot, ifFrames, ifMult, jThreads, cpu, null, turboToken, ReportIf(pending)))
                    throw new InvalidOperationException($"第 {pending.Index} 块补帧失败");
                hub.CommitBlock("if", pending.IfFrameCount);
            }
            }
            catch (Exception ex)
            {
                srQueue.Writer.TryComplete(ex);
                turboCts.Cancel();     // 联动：让超分侧与生产侧都不要再等
                throw;
            }
        }, turboToken);

        /// <summary>下一块里"第一个保留帧"的产物路径：补帧拿它当"下一帧"，让本块末帧的插值与整段一致。
        /// 去重时该块超分产物是全帧，所以必须按保留帧下标取，不能直接取 00000001。</summary>
        string LookaheadOf(TurboBlock next)
        {
            var dir = Path.Combine(turboRoot, $"block_{next.Index:D4}",
                ctx.Options.SuperResolution ? "sr_out" : "sr_in");
            var off = next.KeptOffsets is { Count: > 0 } ko ? ko[0] : 0;
            return Path.Combine(dir, $"{off + 1:D8}.png");
        }

        var srNumber = 1;
        var ifNumber = 1;

        // ---- 开帧去重时 CPU 侧的额外工作（判决与 GPU 侧重叠）----
        // 判决按块流水：块末尾的重复段要看到下一块才知道真实长度（时序平滑会把孤立的短重复段撤销），
        // 所以「判决」跑在前面、「定稿交付给 GPU」晚一块 —— GPU 一直在吃已经定稿的块，不会等整段判完。

        async Task DecideDedupBlockAsync(int b)
        {
            var s = b * blockFrames;
            var e = Math.Min((b + 1) * blockFrames, files.Length);
            var carry = s > 0;   // 首块没有可比对象
            var batch = new List<string>(e - s + 1);
            // 把上一块的末帧放在清单最前面当"对比帧"：判决只看相邻两帧，这样块内判决与整段一次跑完全一致，
            // 也不用为每块复制一份帧文件。
            if (carry) batch.Add(files[s - 1]);
            for (var i = s; i < e; i++) batch.Add(files[i]);

            var result = await FrameDedupService.AnalyzeAsync(batch, dedupOpt!, pr =>
            {
                // 进度中枢按"块内已完成帧数"累加，所以这里传本块内的判决数（含对比帧的要扣掉）；
                // 预览标签用全局帧号，单独算。涡轮下一格一条流水，不给三联（FrameCompareMode=false）。
                var off = carry ? 1 : 0;
                var local = Math.Max(0, pr.Done - off);
                hub.Advance("dedup", local, pr.CurrentFramePath, s + local);
            }, ct, applySmoothing: false);

            // 丢掉对比帧那一条（它属于上一块），其余按全局帧号补上索引
            var skip = carry ? 1 : 0;
            for (var k = skip; k < result.Decisions.Count; k++)
                raw!.Add(result.Decisions[k] with { Index = s + (k - skip) + 1 });

            hub.CommitBlock("dedup", e - s);   // 本块判决完毕
        }

        // 定稿一块：此时下一块已判完，本块末尾重复段的真实长度确定，可以做时序平滑；
        // 随后把整块交给超分流水（勾选补帧时含全部帧，否则只含保留帧）。
        async Task ReleaseDedupBlockAsync(int b)
        {
            var s = b * blockFrames;
            var e = Math.Min((b + 1) * blockFrames, files.Length);
            // 已知范围 = 本块 + 下一块。再往后的重复段光下一块就已经攒够 blockFrames 帧（≥32），
            // 必定超过 MinRunLength（3），撤不撤销的结论不会再变，所以不必再等。
            var horizon = Math.Min(Math.Min((b + 2) * blockFrames, files.Length), raw!.Count);
            var smoothed = raw.GetRange(0, horizon);
            FrameDedupService.SmoothIsolatedRuns(smoothed, dedupOpt!.MinRunLength);

            // 勾选补帧时：重复帧留在原地给超分用（超分全算），只记下"保留帧在整块里的下标"，补帧按它取子集；
            // 未勾选补帧时：重复帧移出 input_frames（保留在 removed 目录，便于排查/回退），
            // 搬不动就改判为保留 —— 判决表、磁盘、超分产物三者必须一致，否则成品帧数会缩水。
            var allFrames = new List<string>(e - s);
            var keptOffsets = ctx.Options.Interpolation ? new List<int>() : null;
            for (var i = s; i < e; i++)
            {
                var dup = smoothed[i].Duplicate;
                if (dup && dedupMovesFrames && !TryMoveFrame(files[i], Path.Combine(removedDir, $"{i + 1:D8}.png")))
                {
                    dup = false;
                    dedupMoveFailed++;
                }
                finalDup![i] = dup;
                if (!dup)
                {
                    keptOffsets?.Add(allFrames.Count);   // 保留帧在整块里的下标
                    allFrames.Add(files[i]);
                }
                else if (keptOffsets is not null)
                {
                    allFrames.Add(files[i]);             // 勾选补帧：重复帧也送超分
                }
            }
            finalized = e;

            var block = new TurboBlock(b + 1, allFrames, srNumber, ifNumber)
            {
                KeptOffsets = keptOffsets
            };
            srNumber += allFrames.Count;
            ifNumber += ctx.Options.Interpolation ? keptOffsets!.Count * ifMult : allFrames.Count;

            // CPU 侧准备：把块帧拷进独立目录（外部工具只吃目录），这一步与 GPU 的处理同时进行
            await PrepareBlockAsync(block, turboRoot, ct);
            await srQueue.Writer.WriteAsync(block, turboToken);   // 队列满 → 在这里等 GPU 追上来（worker 报错时靠联动取消解开）

            // 被剔除的源帧不会再进补帧：对它视作已处理完。超分全算时超分不需要记账，否则超分也要记。
            var dropped = (e - s) - (keptOffsets?.Count ?? allFrames.Count);
            if (ctx.Options.Interpolation) hub.Credit("if", dropped);
            else { hub.Credit("sr", dropped); hub.Credit("if", dropped); }
        }

        try
        {
            for (var b = 0; b < blockCount; b++)
            {
                ct.ThrowIfCancellationRequested();

                if (streamDedup)
                {
                    await DecideDedupBlockAsync(b);
                    if (b > 0) await ReleaseDedupBlockAsync(b - 1);   // 上一块的末尾重复段长度已确定
                    continue;
                }

                var frames = new List<string>();
                for (var i = b * blockFrames; i < Math.Min((b + 1) * blockFrames, files.Length); i++)
                    frames.Add(files[i]);

                var block = new TurboBlock(b + 1, frames, srNumber, ifNumber);
                srNumber += frames.Count;
                ifNumber += ctx.Options.Interpolation ? frames.Count * ifMult : frames.Count;

                // CPU 侧准备：把块帧拷进独立目录（外部工具只吃目录），这一步与 GPU 的处理同时进行
                await PrepareBlockAsync(block, turboRoot, ct);

                await srQueue.Writer.WriteAsync(block, turboToken);   // 队列满 → 在这里等 GPU 追上来（worker 报错时靠联动取消解开）
            }

            if (streamDedup) await ReleaseDedupBlockAsync(blockCount - 1);

            srQueue.Writer.Complete();
            // 优先抛出 worker 里的真实异常：一侧失败会联动取消另一侧，另一侧抛的是 OperationCanceledException，
            // 直接 await Task.WhenAll 可能把"被取消"当成结论，用户就只看到"已取消"而不知道真实原因。
            var workers = Task.WhenAll(srWorker, ifWorker);
            try { await workers; }
            catch
            {
                var real = workers.Exception?.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException);
                if (real is not null) throw real;
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            srQueue.Writer.TryComplete();
            ifQueue.Writer.TryComplete();
            // 联动取消（不是用户按停止）时，把 worker 里的真实异常捞出来抛：
            // 否则用户看到的是"处理已取消"，完全不知道真实原因（也分不清该重试还是该清临时目录）。
            if (!ct.IsCancellationRequested && turboCts.IsCancellationRequested)
            {
                var done = Task.WhenAll(srWorker, ifWorker);
                try { await done; } catch { /* 只为取出真实异常 */ }
                var real = done.Exception?.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException);
                if (real is not null) throw real;
            }
            throw;
        }
        catch (Exception ex)
        {
            srQueue.Writer.TryComplete();
            ifQueue.Writer.TryComplete();
            try { await Task.WhenAll(srWorker, ifWorker); } catch { }
            await RestoreDedupRemovedSafeAsync(streamDedup, inputFrames, removedDir, files.Length);
            _logger.Warn($"[涡轮] {ex.Message}，回退逐阶段串行");
            return (false, 0);
        }

        // 去重后 SR 只喂了保留帧，所以核对基准是"定稿后的保留帧数"而不是原始帧数
        var expectedKept = files.Length;
        if (streamDedup)
        {
            if (finalized != files.Length)
            {
                await RestoreDedupRemovedSafeAsync(true, inputFrames, removedDir, files.Length);
                _logger.Warn($"[涡轮] 去重判决只定稿了 {finalized}/{files.Length} 帧，回退逐阶段串行");
                return (false, 0);
            }
            expectedKept = 0;
            foreach (var dup in finalDup!) if (!dup) expectedKept++;
        }

        // 勾选补帧时超分对全部源帧跑（去重只省补帧），未勾选补帧时超分只算保留帧
        var expectedSr = ctx.Options.Interpolation ? files.Length : expectedKept;
        var srDone = !ctx.Options.SuperResolution || CountFrames(srFrames) >= expectedSr;
        var ifDone = !ctx.Options.Interpolation || CountFrames(ifFrames) >= expectedKept * ifMult;
        if (!srDone || !ifDone)
        {
            // 判决表、超分产物、补帧产物对不上号：一个都不要用，整段回退串行重跑
            _logger.Warn($"[涡轮] 产物数量对不上（超分 {CountFrames(srFrames)}/{expectedSr} 帧、补帧 {CountFrames(ifFrames)} 帧，" +
                         $"应有 {expectedKept} × {ifMult}），回退逐阶段串行");
            await RestoreDedupRemovedSafeAsync(streamDedup, inputFrames, removedDir, files.Length);
            return (false, 0);
        }

        var dedupKept = 0;
        if (streamDedup)
        {
            dedupKept = expectedKept;
            // 回填位置表：第 k 个输出槽位应取"保留序列"里的第几帧（1 基）
            var expandPos = new List<int>(finalDup!.Length);
            // 保留帧的源序号（1 基）：补帧输入按它挑帧
            var keptIndices = new List<int>(dedupKept);
            var seenKept = 0;
            for (var i = 0; i < finalDup!.Length; i++)
            {
                if (!finalDup[i])
                {
                    seenKept++;
                    keptIndices.Add(i + 1);
                }
                expandPos.Add(Math.Max(1, seenKept));
            }
            var dropped = finalDup!.Length - dedupKept;
            File.WriteAllText(DedupMapPath(tempRoot), System.Text.Json.JsonSerializer.Serialize(new
            {
                mode = ctx.Settings.DedupMode,
                total = finalDup!.Length,
                keptCount = dedupKept,
                duplicateCount = dropped,
                expandPos,
                keptIndices
            }));

            _logger.Success(ctx.Options.Interpolation
                ? $"[涡轮] 帧去重（{dedupModeShort}模式）判决已并入流水线：剔除 {dropped} 帧；" +
                  $"超分仍处理全部 {finalDup!.Length} 帧，补帧只处理 {dedupKept} 帧（省 {(double)dropped / finalDup!.Length:P1}）"
                : $"[涡轮] 帧去重（{dedupModeShort}模式）判决已并入流水线：剔除 {dropped} 帧，" +
                  $"实际处理 {dedupKept} 帧（省 {(double)dropped / finalDup!.Length:P1}），重复帧保留在 {removedDir}");
            if (dedupMoveFailed > 0)
                _logger.Warn($"帧去重：{dedupMoveFailed} 帧因文件被占用无法移出，已改判为保留（成品帧数与时长不受影响）");
            // 各阶淘汰量（论文四阶级联的可观测性：出问题时能一眼看出卡在哪一阶）
            _logger.Info($"[涡轮] 各阶判决：像素差淘汰 {raw!.Where(d => d.Decided == FrameDedupService.Stage.PixelDiff).Count()}，" +
                         $"dHash 淘汰 {raw!.Where(d => d.Decided == FrameDedupService.Stage.Hash).Count()}，" +
                         $"QR 淘汰 {raw!.Where(d => d.Decided == FrameDedupService.Stage.Qr).Count()}，" +
                         $"光流淘汰 {raw!.Where(d => d.Decided == FrameDedupService.Stage.Flow).Count()}，" +
                         $"四阶全过判重 {raw!.Where(d => d.Decided == FrameDedupService.Stage.Duplicate).Count()}");
        }

        _logger.Success($"[涡轮] 块级流水完成：{blockCount} 块全部处理完毕，超分 {CountFrames(srFrames)} 帧、补帧 {CountFrames(ifFrames)} 帧");
        return (true, dedupKept);
    }

    /// <summary>涡轮回退前把已搬走的重复帧搬回 input_frames。串行路径是用「帧数够不够」判断拆帧是否完整的，
    /// 少了几十帧会被判成「拆帧不完整」从而跳过去重，接着超分只吃到保留帧、补帧却仍按原始帧数要结果，
    /// 成品会错（实测过：235 帧喂进去、要求出 592 帧，RIFE 硬凑出来的后段全是废帧）。
    /// 搬回去（含复制回退）之后串行路径才能干净重跑。这里不吃取消令牌 —— 收尾动作必须做完。</summary>
    private async Task RestoreDedupRemovedSafeAsync(bool needed, string inputFrames, string removedDir, int expectedFrames)
    {
        if (!needed || !Directory.Exists(removedDir)) return;
        try
        {
            await Task.Run(() =>
            {
                foreach (var f in Directory.GetFiles(removedDir, "*.png"))
                    TryMoveFrame(f, Path.Combine(inputFrames, Path.GetFileName(f)));
            });
        }
        catch (Exception ex)
        {
            _logger.Warn($"[涡轮] 恢复被移出的重复帧失败：{ex.Message}");
        }

        var n = CountFrames(inputFrames);
        if (n < expectedFrames)
            _logger.Warn($"[涡轮] 被移出的重复帧只回到 {n}/{expectedFrames} 帧；" +
                         "串行路径检测到拆帧不完整会自动重新拆帧后再处理");
        else
            _logger.Info($"[涡轮] 已把移出的重复帧放回 input_frames（{n} 帧），本次按逐阶段串行重跑");
    }

    /// <summary>CPU 侧准备：把块内帧拷进该块的输入目录（外部工具只吃目录，无法只处理指定的几帧），
    /// 这一步在 CPU 上做，与 GPU 正在处理的上一块重叠。</summary>
    private static async Task PrepareBlockAsync(TurboBlock block, string turboRoot, CancellationToken ct)
    {
        var srIn = Path.Combine(turboRoot, $"block_{block.Index:D4}", "sr_in");
        await Task.Run(() =>
        {
            if (Directory.Exists(srIn)) Directory.Delete(srIn, true);
            Directory.CreateDirectory(srIn);

            var idx = 0;
            foreach (var frame in block.Frames)
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(frame, Path.Combine(srIn, $"{++idx:D8}.png"), overwrite: true);
            }
        }, ct);
    }

    /// <summary>超分流水：处理一块，结果按全局连续编号并入 srFrames（未勾选超分时块帧直接并入）。
    /// report：非空时（涡轮）把块内进度交给进度中枢汇总，不直接推给 UI。</summary>
    private async Task<bool> SuperResBlockAsync(ProcessingContext ctx, TurboBlock block, string turboRoot,
        string srFrames, string jThreads, bool cpu, CancellationToken ct, Action<ProcessProgress>? report = null)
    {
        var blockDir = Path.Combine(turboRoot, $"block_{block.Index:D4}");
        var srIn = Path.Combine(blockDir, "sr_in");
        var srOut = Path.Combine(blockDir, "sr_out");

        try
        {
            if (!ctx.Options.SuperResolution)
            {
                await MergeBlockFramesAsync(srIn, srFrames, block.SrStartNumber, ct);
                return true;
            }

            // 涡轮分块同样按模型所属算法分派引擎
            var srExeBlock = SrCommandBuilder.ExeFor(ctx.Tools, ctx.SrModel);
            if (string.IsNullOrEmpty(srExeBlock) || !File.Exists(srExeBlock))
            {
                _logger.Warn($"[涡轮] 超分块 {block.Index}：模型 {ctx.SrModel} 对应的引擎没有部署");
                return false;
            }
            var args = SrCommandBuilder.Build(ctx.SrModel, ctx.SrScale, srIn, srOut, jThreads,
                ctx.Settings.LowerQualityForVram, useCpu: cpu);
            var exit = await RunStageWithDirectoryPolling(ProcessStage.SuperRes,
                $"[涡轮] 超分块 {block.Index}", block.Frames.Count, srExeBlock, args, srOut, ct,
                report: report);
            if (exit != 0)
            {
                ct.ThrowIfCancellationRequested();
                _logger.Warn($"[涡轮] 超分块 {block.Index} 失败（exit={exit}）");
                return false;
            }
            await MergeBlockFramesAsync(srOut, srFrames, block.SrStartNumber, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[涡轮] 超分块 {block.Index} 异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>补帧流水：处理一块，结果按全局连续编号并入 ifFrames（未勾选补帧时跳过）；块目录用完即删省空间。
    /// lookaheadFrame：下一块的首帧。借它当"下一帧"多算一份插值，本块末帧的结果才与整段处理一致；
    /// 多算出来的那几帧丢掉不算（只并入前 Frames.Count × ifMult 帧）。
    /// report：非空时（涡轮）把块内进度交给进度中枢汇总，不直接推给 UI。</summary>
    private async Task<bool> InterpBlockAsync(ProcessingContext ctx, TurboBlock block, string turboRoot,
        string ifFrames, int ifMult, string jThreads, bool cpu, string? lookaheadFrame, CancellationToken ct,
        Action<ProcessProgress>? report = null)
    {
        var blockDir = Path.Combine(turboRoot, $"block_{block.Index:D4}");
        var ifOut = Path.Combine(blockDir, "if_out");

        try
        {
            if (!ctx.Options.Interpolation)
            {
                try { if (Directory.Exists(blockDir)) Directory.Delete(blockDir, true); } catch { }
                return true;
            }

            // 整块都是重复帧（动画定格段）：超分照跑但没有帧要补，跳过补帧
            if (block.KeptOffsets is { Count: 0 })
                return true;

            var srcForIf = ctx.Options.SuperResolution
                ? Path.Combine(blockDir, "sr_out")
                : Path.Combine(blockDir, "sr_in");
            // 去重时补帧只喂"保留帧"子集：该块的超分产物是全部帧，这里按保留帧下标挑帧并重新连续编号，
            // 否则 RIFE 会按全部帧数放大输出比例，回填时整块取帧会错位。
            var ifInput = srcForIf;
            var ifInputCount = block.IfFrameCount;
            if (block.KeptOffsets is not null)
            {
                var subset = Path.Combine(blockDir, "if_in");
                // 同样逐个删+带重试复制（整体 Directory.Delete 遇到一个被占用的文件就全失败）
                await Task.Run(() =>
                {
                    if (Directory.Exists(subset))
                    {
                        foreach (var f in Directory.GetFiles(subset, "*.png")) TryDeleteWithRetry(f);
                    }
                    else Directory.CreateDirectory(subset);

                    var n = 0;
                    foreach (var off in block.KeptOffsets)
                    {
                        var src = Path.Combine(srcForIf, $"{off + 1:D8}.png");
                        if (!File.Exists(src)) continue;
                        CopyWithRetry(src, Path.Combine(subset, $"{++n:D8}.png"));
                    }
                }, ct);

                var onDisk = Directory.GetFiles(subset, "*.png").Length;
                if (onDisk != block.KeptOffsets.Count)
                {
                    _logger.Warn($"[涡轮] 补帧块 {block.Index} 的保留帧子集数量不符（{onDisk}/{block.KeptOffsets.Count}）");
                    return false;
                }
                ifInput = subset;
            }

            var ifCount = ifInputCount * ifMult;
            // 借下一块的首帧当"下一帧"：多喂一帧进去，末帧的插值就与整段处理一致。
            // 结果多出来的那一份插值丢弃，只并入前 ifCount 帧。
            var toolCount = ifCount;
            if (!string.IsNullOrEmpty(lookaheadFrame) && File.Exists(lookaheadFrame))
            {
                toolCount = ifCount + ifMult;
                File.Copy(lookaheadFrame, Path.Combine(ifInput, $"{ifInputCount + 1:D8}.png"), overwrite: true);
            }
            var useTorchBlock = ctx.IfDriverKey.Length > 0;
            var args = useTorchBlock
                // 与逐阶段路径保持一致：涡轮块里也要把「降低部分画质」落到 PyTorch 的 --fp16。
                // 之前这里误把「安全帧率」当 sceneDetect 传进来，既开了不该开的切镜检测，又漏了 --fp16。
                ? VfiTorchCommandBuilder.Build(ctx.Tools.VfiDriverScript, ctx.IfDriverKey, ifInput, ifOut, toolCount,
                    VfiTorchCommandBuilder.ScaleFor(ctx.Video.Width), sceneDetect: false,
                    threads: Math.Clamp(ctx.Settings.ThreadCount, 1, 32), fp16: ctx.Settings.LowerQualityForVram)
                : RifeCommandBuilder.Build(ifInput, ifOut, ctx.IfModel, ifMult, toolCount, jThreads,
                    ctx.Settings.LowerQualityForVram, useCpu: cpu);
            var exit = await RunStageWithDirectoryPolling(ProcessStage.Interpolating,
                $"[涡轮] 补帧块 {block.Index}", toolCount, useTorchBlock ? ctx.Tools.TorchPythonExe : ctx.Tools.RifeExe, args, ifOut, ct,
                report: report);
            if (exit != 0)
            {
                ct.ThrowIfCancellationRequested();
                _logger.Warn($"[涡轮] 补帧块 {block.Index} 失败（exit={exit}）");
                return false;
            }
            await MergeBlockFramesAsync(ifOut, ifFrames, block.IfStartNumber, ct, ifCount);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[涡轮] 补帧块 {block.Index} 异常：{ex.Message}");
            return false;
        }
        finally
        {
            // 块产物已并入最终目录，删掉块目录（4K 帧很占地方）
            try { if (Directory.Exists(blockDir)) Directory.Delete(blockDir, true); } catch { }
        }
    }

    /// <summary>把块产物按全局连续编号并入最终目录（工具输出的是块内 1..N 连续编号，需要整体平移）。
    /// maxCount：最多并入多少帧 —— 补帧借了下一块的首帧多算一份插值，多出来的要丢掉。</summary>
    private static async Task MergeBlockFramesAsync(string srcDir, string dstDir, int startNumber,
        CancellationToken ct, int maxCount = int.MaxValue)
    {
        var files = Directory.GetFiles(srcDir, "*.png");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(dstDir);
        await Task.Run(() =>
        {
            var n = Math.Min(files.Length, maxCount);
            for (var i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(files[i], Path.Combine(dstDir, $"{startNumber + i:D8}.png"), overwrite: true);
            }
        }, ct);
    }

    private static int CountFrames(string dir)
        => Directory.Exists(dir) ? Directory.GetFiles(dir, "*.png").Length : 0;

    /// <summary>搬移帧文件：遇到"文件被其他进程占用"等瞬时错误时退避重试，最后再用复制+删除兜底。
    /// 全部失败返回 false，由调用方把该帧改判为保留（保证判决表与磁盘文件数一致）。</summary>
    private static bool TryMoveFrame(string src, string dst)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
                return true;
            }
            catch (IOException) { /* 共享冲突：稍等再试（预览解码/杀毒扫描通常是瞬时的） */ }
            catch (UnauthorizedAccessException) { }
            Thread.Sleep(120 * (attempt + 1));
        }

        // 兜底：复制（只需读共享）成功后再删源文件；删源失败则回滚，避免两处都留有该帧
        try
        {
            File.Copy(src, dst, overwrite: true);
            try { File.Delete(src); return true; }
            catch { try { File.Delete(dst); } catch { } return false; }
        }
        catch { return false; }
    }

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

    /// <summary>清理某阶段的残留帧（中断/崩溃后重跑时用）。删除量可达上万帧，必须在后台线程做并上报进度：
    /// 这些调用都发生在 UI 线程上的 RunAsync 里，同步删几千个文件会把窗口卡成"未响应"。</summary>
    private async Task CleanPartialOutputAsync(string dir, ProcessStage stage, string stageText, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.png");
        if (files.Length == 0) return;
        // 先立刻推一条 0/N：否则大目录要删到第 200 个文件才有反馈，界面看起来像没反应
        var startText = $"{stageText}（清理上次残留）0/{files.Length}";
        _logger.Info(startText);
        ProgressChanged?.Invoke(new ProcessProgress
        {
            Stage = stage,
            StageText = stageText,
            Current = 0,
            Total = files.Length,
            PercentDisplay = true,
            CustomDetail = startText
        });
        await Task.Run(() =>
        {
            for (var i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                try { TryDeleteWithRetry(files[i], attempts: 3); } catch { }
                if ((i + 1) % 200 == 0 || i + 1 == files.Length)
                {
                    var text = $"{stageText}（清理上次残留）{i + 1}/{files.Length}";
                    _logger.Info(text);
                    ProgressChanged?.Invoke(new ProcessProgress
                    {
                        Stage = stage,
                        StageText = stageText,
                        Current = i + 1,
                        Total = files.Length,
                        PercentDisplay = true,
                        CustomDetail = text
                    });
                }
            }
        }, ct);
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

    /// <summary>CPU 编码参数（通用回退方案）。crf 来自高级页「CPU CRF」：0~51，越小越不压缩、体积越大。</summary>
    private static string CpuEncodeArgs(string preset, int crf) =>
        $"libx265 -preset {preset} -crf {Math.Clamp(crf, 0, 51)}";

    /// <summary>候选 GPU 硬件编码器（按 NVIDIA → AMD → Intel 顺序），失败时上层会自动回退 CPU。</summary>
    private static readonly string[] HwEncoderCandidates = { "hevc_nvenc", "hevc_amf", "hevc_qsv" };

    /// <summary>按「编码器 + 质量模式」生成参数。
    /// mode："" = 默认档（与历史行为一致，CQ 18）/ "cq" = 恒定质量 / "qp" = 固定量化参数；quality 取 0~51，越小画质越好。
    /// 注意 AMF 没有独立的 CQ 参数，CQ 与 QP 在 AMF 上都落到 cqp 的 qp_i/qp_p。</summary>
    private static string HwEncodeArgs(string encoder, string mode, int quality)
    {
        var q = Math.Clamp(quality, 0, 51);
        return encoder switch
        {
            "hevc_nvenc" => mode == "qp"
                ? $"hevc_nvenc -preset p7 -tune hq -rc constqp -qp {q}"
                : $"hevc_nvenc -preset p7 -tune hq -rc vbr -cq {q}",
            "hevc_amf" => $"hevc_amf -quality quality -rc cqp -qp_i {q} -qp_p {q}",
            "hevc_qsv" => mode == "qp"
                ? $"hevc_qsv -preset medium -q {q}"
                : $"hevc_qsv -preset medium -global_quality {q}",
            _ => encoder
        };
    }

    /// <summary>质量模式显示名（用于合并日志）</summary>
    private static string QualityModeText(string mode) => mode switch
    {
        "cq" => "CQ 恒定质量",
        "qp" => "QP 固定量化",
        _ => "默认"
    };

    /// <summary>「保留原视频内嵌字幕」生效时的字幕来源：成片是从帧序列重建的，原文件的字幕不会自己跟过来，
    /// 必须把原文件当第二个输入映射。未勾选 / 原文件没有字幕流 / 没有输入文件时返回 null（不映射，行为与历史一致）。</summary>
    private static string? SubtitleSource(ProcessingContext ctx) =>
        ctx.Options.KeepSubtitles && ctx.Video.HasSubtitles && !string.IsNullOrWhiteSpace(ctx.InputVideo)
            ? ctx.InputVideo
            : null;

    private static string? _detectedHwEncoder;

    /// <summary>探测 FFmpeg 可用的 GPU 硬件编码器（NVENC → AMF → QSV，覆盖 NVIDIA/AMD/Intel），返回编码器名。
    /// 编码器由显卡自动决定（N 卡 NVENC / A 卡 AMF / Intel QSV），界面不再提供手动选择。
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
            foreach (var name in HwEncoderCandidates)
            {
                // 非 NVIDIA 显卡直接跳过 NVENC（否则每次合并都会白尝试失败再回退）
                if (!gpu.IsNvidia && name.StartsWith("hevc_nvenc", StringComparison.OrdinalIgnoreCase)) continue;
                if (text.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    _detectedHwEncoder = name;
                    return name;
                }
            }
        }
        catch { }
        _detectedHwEncoder = "";
        return null;
    }

    /// <summary>ffprobe 读产物的音轨规格 + 文件信息里的 comment 标记，用于"音频到底怎么处理的"的最终实测。
    /// 这是判断 AudioSR 有没有真正介入最直接的依据：
    /// 介入 → pcm_s24le / 48000 Hz / Comment = AudioSR 标记；未介入 → 重采样 96000 Hz + "未使用"标记。</summary>
    private static string? ProbeAudioSummary(string ffprobeExe, string path)
    {
        try
        {
            using var p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffprobeExe,
                    Arguments = "-v error -select_streams a:0 -show_entries stream=codec_name,sample_rate,channels "
                              + $"-show_entries format_tags=comment -of default=nw=1 \"{path}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                }
            };
            p.Start();
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return text.Replace("\r\n", " / ").Replace("\n", " / ");
        }
        catch { return null; }
    }
}
