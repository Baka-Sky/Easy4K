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
/// 实现 BUG-03/04/06/07/08 等运行时修复，进度解析，阶段级断点续传。</summary>
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

        var targetFrames = ctx.Options.Interpolation ? totalFrames * ctx.IfMultiplier : totalFrames;
        var outFps = ctx.Options.Interpolation ? ctx.Video.FrameRate * ctx.IfMultiplier : ctx.Video.FrameRate;
        var outWidth = ctx.Video.Width * (ctx.Options.SuperResolution ? ctx.SrScale : 1);
        var outHeight = ctx.Video.Height * (ctx.Options.SuperResolution ? ctx.SrScale : 1);
        var useNvenc = ctx.Gpu.IsNvidia && ctx.Gpu.IsRtx;

        // 输入帧目录
        var inputDirForSr = ctx.Options.SuperResolution ? srFrames : inputFrames;
        var inputDirForIf = srFrames; // 补帧基于超分结果，无超分则基于原帧
        if (!ctx.Options.SuperResolution) inputDirForIf = inputFrames;

        // ============ 阶段1：拆帧 ============
        if (ctx.Options.SplitFrames)
        {
            if (IsStageDone(inputFrames, totalFrames))
            {
                _logger.Info($"[跳过] 拆帧已存在 {CountFrames(inputFrames)} 帧: {inputFrames}");
            }
            else
            {
                _logger.Info($"开始拆帧: {Path.GetFileName(ctx.InputVideo)}");
                var (args, _) = FFmpegCommandBuilder.SplitFrames(ctx.InputVideo, inputFrames);
                _logger.Command($"ffmpeg {args}");
                var exit = await RunStageWithProgress(ProcessStage.Splitting, "拆帧中", totalFrames,
                    ctx.Tools.FFmpegExe, args, ParseFfmpegFrame, inputFrames, ct);
                if (exit != 0) return Fail("拆帧失败");
                _logger.Success($"拆帧完成: {CountFrames(inputFrames)} 帧");
            }
        }

        // ============ 阶段2：超分 ============
        if (ctx.Options.SuperResolution)
        {
            if (IsStageDone(srFrames, totalFrames))
            {
                _logger.Info($"[跳过] 超分已存在 {CountFrames(srFrames)} 帧: {srFrames}");
            }
            else
            {
                _logger.Info($"超分开始: 模型 {ctx.SrModel} ×{ctx.SrScale}");
                var lowVram = ctx.Gpu.VramMB > 0 && ctx.Gpu.VramMB < 6144;
                var args = RealEsrganCommandBuilder.Build(inputFrames, srFrames, ctx.SrModel, ctx.SrScale, ctx.Gpu, ctx.Settings.SrThreads, lowVram);
                _logger.Command($"realesrgan-ncnn-vulkan {args}");
                var exit = await RunStageWithDirectoryPolling(ProcessStage.SuperRes, "超分中", totalFrames,
                    ctx.Tools.RealEsrganExe, args, srFrames, ct);
                if (exit != 0)
                {
                    // BUG-06：显存不足重试一次
                    if (await IsOomRetryable())
                    {
                        _logger.Warn("检测到显存不足，降级为 -u -j 1:1:1 重试");
                        args = RealEsrganCommandBuilder.Build(inputFrames, srFrames, ctx.SrModel, ctx.SrScale, ctx.Gpu, 1, true);
                        _logger.Command($"realesrgan-ncnn-vulkan {args}");
                        exit = await RunStageWithDirectoryPolling(ProcessStage.SuperRes, "超分中(低显存)", totalFrames,
                            ctx.Tools.RealEsrganExe, args, srFrames, ct);
                    }
                    if (exit != 0) return Fail("超分失败");
                }
                _logger.Success($"超分完成: {CountFrames(srFrames)} 帧");
            }
        }

        // ============ 阶段3：补帧 ============
        if (ctx.Options.Interpolation)
        {
            if (IsStageDone(ifFrames, targetFrames))
            {
                _logger.Info($"[跳过] 补帧已存在 {CountFrames(ifFrames)} 帧: {ifFrames}");
            }
            else
            {
                _logger.Info($"补帧开始: 模型 {ctx.IfModel} ×{ctx.IfMultiplier} ({ctx.Video.FrameRate:0.##}→{outFps:0.##}fps)");
                var lowVram = ctx.Gpu.VramMB > 0 && ctx.Gpu.VramMB < 6144;
                var args = RifeCommandBuilder.Build(inputDirForIf, ifFrames, ctx.IfModel, targetFrames, ctx.Settings.IfThreads, lowVram);
                _logger.Command($"rife-ncnn-vulkan {args}");
                var exit = await RunStageWithDirectoryPolling(ProcessStage.Interpolating, "补帧中", targetFrames,
                    ctx.Tools.RifeExe, args, ifFrames, ct);

                // BUG-04：rife-v4.25/v4.26 命令行版会报 "layer MemoryData not exists" → 回退 v4.6
                if (exit != 0 && _lastStderr.Contains("MemoryData not exists", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn($"模型 {ctx.IfModel} 不被命令行版支持（BUG-04），自动切换为 rife-v4.6");
                    ifFrames = CleanPartialOutput(ifFrames);
                    var args2 = RifeCommandBuilder.Build(inputDirForIf, ifFrames, "rife-v4.6", targetFrames, ctx.Settings.IfThreads, lowVram);
                    _logger.Command($"rife-ncnn-vulkan {args2}");
                    exit = await RunStageWithDirectoryPolling(ProcessStage.Interpolating, "补帧中(回退v4.6)", targetFrames,
                        ctx.Tools.RifeExe, args2, ifFrames, ct);
                }
                if (exit != 0 && await IsOomRetryable())
                {
                    // 如果开启安全帧率，尝试降级
                    var safe = ctx.Settings.UseSafeFrameRate;
                    _logger.Warn(safe ? "显存不足，自动降级安全模式重试" : "显存不足，尝试低显存重试");
                    ifFrames = CleanPartialOutput(ifFrames);
                    var args3 = RifeCommandBuilder.Build(inputDirForIf, ifFrames, ctx.IfModel, targetFrames, safe ? 1 : 1, true);
                    _logger.Command($"rife-ncnn-vulkan {args3}");
                    exit = await RunStageWithDirectoryPolling(ProcessStage.Interpolating, safe ? "补帧中(安全降级)" : "补帧中(低显存)", targetFrames,
                        ctx.Tools.RifeExe, args3, ifFrames, ct);
                    // 安全帧率降级后通知 UI 显示横幅
                    if (safe && exit == 0)
                    {
                        ProgressChanged?.Invoke(new ProcessProgress { DegradeNotice = "已降级运行：安全帧率模式（1线程低显存）" });
                    }
                }
                if (exit != 0) return Fail("补帧失败");
                _logger.Success($"补帧完成: {CountFrames(ifFrames)} 帧");
            }
        }

        // ============ 阶段4：合并视频 ============
        string currentVideo = "";
        if (ctx.Options.MergeVideo)
        {
            var framesForMerge = ctx.Options.Interpolation ? ifFrames : (ctx.Options.SuperResolution ? srFrames : inputFrames);
            var fpsForMerge = ctx.Options.Interpolation ? outFps : ctx.Video.FrameRate;
            _logger.Info("开始合并视频");
            var args = FFmpegCommandBuilder.MergeFrames(framesForMerge, tempVideo, fpsForMerge, ctx.Settings.EncodePreset, useNvenc);
            _logger.Command($"ffmpeg {args}");
            var total = ctx.Options.Interpolation ? targetFrames : totalFrames;
            var exit = await RunStageWithProgress(ProcessStage.Merging, "合并中", total,
                ctx.Tools.FFmpegExe, args, ParseFfmpegFrame, null, ct);
            if (exit != 0) return Fail("合并视频失败");
            _logger.Success("合并视频完成");
            currentVideo = tempVideo;
        }
        else
        {
            // 不合并就无最终视频可继续；规格书主链路必合并，这里兜底
            currentVideo = tempVideo;
        }

        // ============ 阶段5：合并原音频 ============
        // 流程：从原视频提取音频 → 合并到当前视频（PCM 2.0 24bit 96kHz）
        if (ctx.Options.MergeAudio && ctx.Video.HasAudio)
        {
            var audioPath = Path.Combine(tempRoot, "audio.flac").Replace('\\', '/');
            // 若未提前拆分音频，则现在提取
            if (!File.Exists(audioPath))
            {
                _logger.Info("从原视频提取音频...");
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
                var args = FFmpegCommandBuilder.EmbedAudio(currentVideo, audioPath, audioEmbedded);
                _logger.Command($"ffmpeg {args}");
                var exit = await RunStageAsync(ProcessStage.AddingAudio, "合并音频",
                    ctx.Tools.FFmpegExe, args, ct);
                if (exit != 0) return Fail("音频合并失败");
                _logger.Success("音频合并完成");
                currentVideo = audioEmbedded;
            }
        }
        else if (ctx.Options.MergeAudio && !ctx.Video.HasAudio)
        {
            _logger.Warn("原视频无音频流，跳过合并原音频");
        }

        // ============ 阶段6：SDR→HDR 转换 ============
        // 未勾选合并视频时无最终视频，跳过输出步骤（中间帧保留在 Temp）
        if (!ctx.Options.MergeVideo)
        {
            _logger.Info("未勾选合并视频，跳过最终视频输出（中间帧保留在临时目录）");
            ProgressChanged?.Invoke(new ProcessProgress { Stage = ProcessStage.Done, StageText = "完成" });
            return tempVideo;
        }

        var finalName = OutputNamer.Build(ctx.InputVideo, ctx.Options.SuperResolution, ctx.Options.Interpolation,
            ctx.Options.SdrToHdr, outWidth, outHeight, outFps, ctx.Options.MergeAudio);
        var finalPath = Path.Combine(ctx.OutputRoot, finalName).Replace('\\', '/');

        if (ctx.Options.SdrToHdr)
        {
            if (!ctx.Gpu.SupportsHdr)
            {
                _logger.Error("SDR→HDR 转换需要 NVIDIA RTX 显卡，当前显卡不支持，跳过 HDR 步骤");
                // 落地为 SDR：重新命名
                finalName = OutputNamer.Build(ctx.InputVideo, ctx.Options.SuperResolution, ctx.Options.Interpolation,
                    false, outWidth, outHeight, outFps, ctx.Options.MergeAudio);
                finalPath = Path.Combine(ctx.OutputRoot, finalName).Replace('\\', '/');
                File.Copy(currentVideo, finalPath, overwrite: true);
            }
            else if (!ctx.Tools.NvEncExists)
            {
                _logger.Error("NVEncC64 未安装，跳过 HDR 转换（输出 SDR）");
                finalName = OutputNamer.Build(ctx.InputVideo, ctx.Options.SuperResolution, ctx.Options.Interpolation,
                    false, outWidth, outHeight, outFps, ctx.Options.MergeAudio);
                finalPath = Path.Combine(ctx.OutputRoot, finalName).Replace('\\', '/');
                File.Copy(currentVideo, finalPath, overwrite: true);
            }
            else
            {
                _logger.Info($"HDR 转换开始: saturation={ctx.HdrSaturation}, contrast={ctx.HdrContrast}");
                var args = NvEncCommandBuilder.Build(currentVideo, finalPath, ctx.HdrSaturation, ctx.HdrContrast);
                _logger.Command($"NVEncC64 {args}");
                var exit = await RunStageWithProgress(ProcessStage.HdrConverting, "HDR转换中", 100,
                    ctx.Tools.NvEncExe, args, ParseNvencProgress, null, ct);
                if (exit != 0) return Fail("HDR 转换失败");
                _logger.Success("HDR 转换完成");
            }
        }
        else
        {
            Directory.CreateDirectory(ctx.OutputRoot);
            File.Copy(currentVideo, finalPath, overwrite: true);
        }

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
        // NVEncC 输出形如 "Frames: 2450/3929" 或 "23.45%"
        var m = Regex.Match(line, @"(\d+)/(\d+)");
        if (m.Success && long.TryParse(m.Groups[1].Value, out var c) && long.TryParse(m.Groups[2].Value, out var t))
            return c;
        var p = NvencPercentRe.Match(line);
        return p.Success && double.TryParse(p.Groups[1].Value, out var pct) ? (long)pct : null;
    }

    // ---- 工具方法 ----

    private string _lastStderr = "";

    private async Task<int> RunStageWithProgress(ProcessStage stage, string stageText, long total,
        string exe, string args, Func<string, long?> frameParser, string? previewDir, CancellationToken ct)
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
                    Total = total
                });
                // 节流：每秒记一条实时进度，让命令输出区有内容
                var now = DateTime.Now;
                if ((now - lastLog).TotalSeconds >= 1)
                {
                    lastLog = now;
                    _logger.Info($"{stageText} 已处理 {v.Value}/{total}");
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
    /// Real-ESRGAN 输出 0.00%~98.33% 的帧内 tile 进度；RIFE 无进度输出，仅靠帧数。</summary>
    private async Task<int> RunStageWithDirectoryPolling(ProcessStage stage, string stageText, long total,
        string exe, string args, string outputDir, CancellationToken ct)
    {
        var captured = new System.Text.StringBuilder();
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 当前帧内的 tile 进度（0-100），由 stderr 百分比更新
        double innerPercent = 0;
        var innerLock = new object();

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
                            StageText = stageText,
                            Current = (long)Math.Floor(current),
                            Total = total,
                            LatestFramePath = FindLatestFrame(outputDir) ?? ""
                        });
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

    private Task<bool> IsOomRetryable()
    {
        var s = _lastStderr;
        return Task.FromResult(s.Contains("vkAllocateMemory failed", StringComparison.OrdinalIgnoreCase) ||
                               s.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
                               s.Contains("CUDA out of memory", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStageDone(string framesDir, long expected)
    {
        if (!Directory.Exists(framesDir)) return false;
        var count = CountFrames(framesDir);
        return count >= expected && expected > 0;
    }

    private static int CountFrames(string dir)
        => Directory.Exists(dir) ? Directory.GetFiles(dir, "*.png").Length : 0;

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
}
