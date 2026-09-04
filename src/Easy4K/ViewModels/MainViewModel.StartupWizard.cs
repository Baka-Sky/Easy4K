using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Easy4K.Models;
using Easy4K.Services;

namespace Easy4K.ViewModels;

/// <summary>
/// OOBE 欢迎向导 / 处理前测试 / HTML 报告的辅助实现（partial 扩展）。
/// 向导只在首次运行显示；正式处理开始前，软件会先用 Res 的 1 秒测试视频按当前勾选的流程跑一遍
/// （进度文案统一显示"测试中"），验证能正常启动后再继续处理用户视频，测试残留每次自动清理；
/// 正式处理完成自动生成 HTML 报告并打开。
/// </summary>
public partial class MainViewModel
{
    /// <summary>处理前测试结果</summary>
    public enum PreProcessTestOutcome
    {
        /// <summary>测试通过 → 清理残留后继续正式处理</summary>
        Passed,
        /// <summary>测试未通过 → 弹报错并取消本次处理</summary>
        Failed,
        /// <summary>测试被"跳过测试"按钮跳过 → 仍继续正式处理</summary>
        Skipped,
        /// <summary>测试被"停止"取消 → 取消本次处理，不启动正式流程</summary>
        Canceled
    }

    /// <summary>用户点击"跳过测试"的请求标记（与"停止"区分：跳过仍继续正式处理，停止则取消整个操作）</summary>
    private bool _skipPreTestRequested;

    /// <summary>用户点击进行中页的"跳过测试"：停止当前测试，随后仍会开始正式处理。</summary>
    public void RequestSkipPreTest()
    {
        _skipPreTestRequested = true;
        Stop();
    }

    // ===================== 首次向导状态 =====================
    /// <summary>向导是否已完成（由配置 SetupCompleted 驱动）</summary>
    public bool SetupCompleted => _app.SetupCompleted;

    /// <summary>是否正处于处理前测试（UI 据此把阶段文字显示为"测试中"并显示"跳过测试"按钮）</summary>
    [ObservableProperty] private bool _isStartupSelfTest;

    /// <summary>Res 资源目录（exe 旁的 Res，存放 logo/音乐/测试视频）</summary>
    public static string ResDir => Path.Combine(AppContext.BaseDirectory, "Res");

    /// <summary>1 秒处理前测试视频路径</summary>
    public static string StartupTestVideoPath => Path.Combine(ResDir, "Easy4K_test_1s.mp4");

    // ===================== 向导结果持久化 =====================
    /// <summary>应用向导收集到的默认配置并标记向导完成。</summary>
    public void ApplyWelcomeConfig(
        bool split, bool sr, bool interpolation, bool merge, bool mergeAudio, bool hdr,
        bool startupSelfTest, string reportDir, bool reportEnabled, bool reportAutoOpen,
        string theme)
    {
        _app.DefaultSplitFrames = split;
        _app.DefaultSuperResolution = sr;
        _app.DefaultInterpolation = interpolation;
        _app.DefaultMergeVideo = merge;
        _app.DefaultMergeAudio = mergeAudio;
        _app.DefaultSdrToHdr = hdr;
        _app.StartupSelfTest = startupSelfTest;
        _app.ReportDir = reportDir;
        _app.ReportEnabled = reportEnabled;
        _app.ReportAutoOpen = reportAutoOpen;
        _app.Theme = theme;
        _app.SetupCompleted = true;
        _settings.Save(_app, _pathConfig);
    }

    /// <summary>读取报告输出目录的绝对路径（相对路径基于运行根目录，同 Output/Temp 的解析方式）</summary>
    public string ResolveReportDir()
    {
        var dir = string.IsNullOrWhiteSpace(_app.ReportDir) ? "Reports" : _app.ReportDir;
        if (Path.IsPathRooted(dir)) return dir;
        var root = FindProjectRoot(AppContext.BaseDirectory);
        return Path.Combine(root, dir);
    }

    // ===================== 处理前测试 =====================
    /// <summary>确保 Res 里有 1 秒测试视频（缺则用 ffmpeg 生成：testsrc2 彩条 + 440Hz 正弦音，便于验证含音频链路）。</summary>
    public async Task<bool> EnsureStartupTestVideoAsync()
    {
        try
        {
            if (File.Exists(StartupTestVideoPath) && new FileInfo(StartupTestVideoPath).Length > 0) return true;
            Directory.CreateDirectory(ResDir);
            if (!Tools.FFmpegExists) { _logger.Warn("处理前测试: FFmpeg 未就绪，无法生成测试视频"); return false; }

            _logger.Info("处理前测试: 正在生成 1 秒测试视频...");
            var psi = new ProcessStartInfo
            {
                FileName = Tools.FFmpegExe,
                WorkingDirectory = ResDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("testsrc2=size=640x360:rate=30:duration=1");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("sine=frequency=440:sample_rate=48000:duration=1");
            psi.ArgumentList.Add("-shortest");
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
            psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
            psi.ArgumentList.Add(StartupTestVideoPath);

            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync();
            return File.Exists(StartupTestVideoPath) && new FileInfo(StartupTestVideoPath).Length > 0;
        }
        catch (Exception ex)
        {
            _logger.Error($"处理前测试: 生成测试视频失败 {ex.Message}");
            return false;
        }
    }

    /// <summary>处理前测试：每次点击"开始处理"（且未勾选"跳过测试"）时，先用 Res 的 1 秒测试视频
    /// 把【当前 UI 勾选的整套流程/模型】完整跑一遍，验证能正常启动并跑通后再继续处理正式视频。
    /// 测试使用 Res 下独立临时/输出目录，不污染用户正式 Temp/Output；无论通过/失败/跳过，
    /// 结束时都自动删除测试残留。Failed 时调用方应弹报错并取消本次正式处理；
    /// Skipped 表示被"跳过测试"按钮跳过（仍继续正式处理）；Canceled 表示被"停止"取消（不启动正式流程）。</summary>
    public async Task<(PreProcessTestOutcome Outcome, string? Reason)> RunPreProcessTestAsync()
    {
        if (IsProcessing) return (PreProcessTestOutcome.Canceled, null);
        _skipPreTestRequested = false;
        var outcome = PreProcessTestOutcome.Passed;
        string? reason = null;
        try
        {
            _logger.Info("==== 处理前测试开始（1 秒测试视频 × 当前勾选流程）====");
            if (!Tools.CoreToolsOk)
            {
                _logger.Error("处理前测试: 核心工具(FFmpeg/RealESRGAN/RIFE)未就绪");
                return (PreProcessTestOutcome.Failed, "核心工具(FFmpeg/RealESRGAN/RIFE)未就绪");
            }
            if (!await EnsureStartupTestVideoAsync())
            {
                _logger.Error("处理前测试: 1 秒测试视频不可用");
                return (PreProcessTestOutcome.Failed, "1 秒测试视频不可用或生成失败");
            }

            // 探测测试视频
            var info = _videoDetector.Detect(StartupTestVideoPath);
            if (info is null || !info.IsValid)
            {
                _logger.Error("处理前测试: 测试视频探测失败");
                return (PreProcessTestOutcome.Failed, "1 秒测试视频探测失败");
            }

            var prevShowPreview = ShowPreview;
            var selftestTemp = Path.Combine(ResDir, "selftest_temp");
            var selftestOut = Path.Combine(ResDir, "selftest_out");
            try
            {
                // 独立临时/输出目录：先清上次异常残留，保证每次测试从干净状态开始
                if (Directory.Exists(selftestTemp)) Directory.Delete(selftestTemp, true);
                if (Directory.Exists(selftestOut)) Directory.Delete(selftestOut, true);
                Directory.CreateDirectory(selftestTemp);
                Directory.CreateDirectory(selftestOut);

                // 与正式处理同一套当前勾选参数，仅把输入换成 1 秒测试视频、目录换成独立目录
                var needFrames = SuperResolution || Interpolation || MergeVideo || MergeAudio;
                var options = new ProcessingOptions
                {
                    SplitFrames = SplitFrames || needFrames, // 视频测试必须有帧序列：步骤吃帧就强制拆帧
                    SuperResolution = SuperResolution,
                    Interpolation = Interpolation,
                    MergeVideo = MergeVideo,
                    MergeAudio = MergeAudio && info.HasAudio,
                    SdrToHdr = SdrToHdr && IsHdrEnabled,
                    IfEngine = IfEngine
                };

                var ctx = new ProcessingContext
                {
                    InputVideo = StartupTestVideoPath,
                    TempRoot = selftestTemp.Replace('\\', '/'),
                    OutputRoot = selftestOut.Replace('\\', '/'),
                    ExternalFramesDir = "",
                    Video = info,
                    Options = options,
                    SrModel = SrModel,
                    SrScale = Math.Clamp(SrScale, 2, 4),
                    IfModel = IfModel,
                    IfMultiplier = IfMultiplier,
                    HdrSaturation = Math.Clamp(HdrSaturation, 0, 200),
                    HdrContrast = Math.Clamp(HdrContrast, 0, 200),
                    Gpu = _env.Gpu,
                    Settings = _app,
                    Tools = Tools
                };

                _logger.Info($"处理前测试选项: 超分 {ctx.SrModel}×{ctx.SrScale} / 补帧引擎 {ctx.Options.IfEngine} / 模型 {ctx.IfModel}×{ctx.IfMultiplier}");

                // 复用正式处理的进度/预览管线，阶段文案统一显示"测试中..."
                _cts = new CancellationTokenSource();
                IsStartupSelfTest = true;
                SuppressCompletionDialog = true;
                IsProcessing = true;
                IsPaused = false;
                ProgressPercent = 0;
                ProgressDetail = "测试中...";
                Stage = ProcessStage.Idle;
                _startTime = DateTime.Now;
                _monitor.Start(250);
                ProcessingStarted?.Invoke();

                _orchestratorAssigned.ProgressChanged += OnProgressChanged;
                try
                {
                    var result = await _orchestratorAssigned.RunAsync(ctx, _cts.Token);
                    Stage = result is null ? ProcessStage.Failed : ProcessStage.Done;
                    if (result is null)
                    {
                        outcome = PreProcessTestOutcome.Failed;
                        reason = "按当前勾选的流程处理 1 秒测试视频失败，未能正常跑通（请查看日志/命令输出定位，例如模型与显卡不兼容、工具缺失等）";
                    }
                }
                catch (OperationCanceledException)
                {
                    var skipped = _skipPreTestRequested;
                    _skipPreTestRequested = false;
                    Stage = ProcessStage.Stopped;
                    if (skipped)
                    {
                        _logger.Warn("用户选择跳过处理前测试，将直接开始正式处理");
                        outcome = PreProcessTestOutcome.Skipped;
                    }
                    else
                    {
                        _logger.Warn("处理前测试被停止，本次处理已取消");
                        outcome = PreProcessTestOutcome.Canceled;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"处理前测试异常: {ex.Message}");
                    Stage = ProcessStage.Failed;
                    outcome = PreProcessTestOutcome.Failed;
                    reason = $"处理前测试执行异常：{ex.Message}";
                }
                finally
                {
                    _orchestratorAssigned.ProgressChanged -= OnProgressChanged;
                }
            }
            finally
            {
                _monitor.Stop();
                IsProcessing = false;
                IsPaused = false;
                SuppressCompletionDialog = false;
                IsStartupSelfTest = false;
                _cts = null;
                // 恢复用户预览偏好，供随后的正式处理使用
                ShowPreview = prevShowPreview;
                PreviewBlocked = false;
                // 自动清理本次测试残留（无论通过/失败/跳过，都会执行）
                try { if (Directory.Exists(selftestTemp)) Directory.Delete(selftestTemp, true); } catch { }
                try { if (Directory.Exists(selftestOut)) Directory.Delete(selftestOut, true); } catch { }
            }

            _logger.Info(outcome switch
            {
                PreProcessTestOutcome.Passed => "处理前测试通过，测试残留已清理，开始正式处理",
                PreProcessTestOutcome.Failed => "处理前测试未通过，测试残留已清理，取消正式处理",
                PreProcessTestOutcome.Skipped => "处理前测试被跳过，测试残留已清理，开始正式处理",
                _ => "处理前测试被停止，测试残留已清理，取消本次处理"
            });
            return (outcome, reason);
        }
        catch (Exception ex)
        {
            _logger.Error($"处理前测试异常: {ex.Message}");
            try { _cts = null; } catch { }
            return (PreProcessTestOutcome.Failed, $"处理前测试异常：{ex.Message}");
        }
    }

    // ===================== HTML 报告 =====================
    /// <summary>正式处理（非自检）完成后生成 HTML 报告并可选自动打开。</summary>
    public void TryGenerateReport(string? outputPath, string stepsText, TimeSpan elapsed)
    {
        if (outputPath is null || !_app.ReportEnabled || IsStartupSelfTest) return;
        try
        {
            var optionLines = BuildReportOptionLines();
            // 本次运行期间的命令（取开始时间之后产生的 CMD 级日志）
            var commands = Logger.Snapshot()
                .Where(e => e.Level == LogLevel.Command && e.Timestamp >= _startTime)
                .Select(e => e.Message.Trim())
                .Where(m => m.Length > 0)
                .Take(200)
                .ToList();

            var frames = SampleFramesFromTemp();
            var file = HtmlReportService.Generate(
                ResolveReportDir(), Path.GetFileName(InputVideo), outputPath, stepsText,
                optionLines, commands, frames, elapsed, isSelfTest: false);
            if (file is null) return;

            _logger.Success($"HTML 报告已生成: {file}");
            if (_app.ReportAutoOpen)
            {
                try { Process.Start(new ProcessStartInfo { FileName = file, UseShellExecute = true }); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"生成 HTML 报告失败: {ex.Message}");
        }
    }

    private List<string> BuildReportOptionLines()
    {
        var lines = new List<string>();
        if (SplitFrames) lines.Add("拆分帧");
        if (SuperResolution) lines.Add($"超分辨率：{SrModel} ×{SrScale}");
        if (Interpolation) lines.Add($"补帧：引擎 {IfEngine}，模型 {IfModel} ×{IfMultiplier}");
        if (MergeVideo) lines.Add("合并视频");
        if (MergeAudio) lines.Add("合并原音频");
        if (SdrToHdr) lines.Add("SDR→HDR 转换");
        lines.Add($"线程数：{ThreadCount}");
        if (UseCpuProcessing) lines.Add("CPU 处理模式");
        if (UseSafeFrameRate) lines.Add("安全帧率开启");
        return lines;
    }

    /// <summary>从本次临时目录随机抽取最多 N 张输出中间帧（4k_frames/output_frames），供报告内嵌。</summary>
    private List<string> SampleFramesFromTemp(int maxPerDir = 3)
    {
        var result = new List<string>();
        var dirs = new[] { Path.Combine(TempRoot, "output_frames"), Path.Combine(TempRoot, "4k_frames"), Path.Combine(TempRoot, "input_frames") };
        var rnd = new Random();
        foreach (var d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            try
            {
                var files = Directory.GetFiles(d, "*.png")
                    .OrderBy(_ => rnd.Next())
                    .Take(maxPerDir);
                result.AddRange(files);
            }
            catch { }
        }
        return result.Take(maxPerDir * 2).ToList();
    }
}
