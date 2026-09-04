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
/// OOBE 欢迎向导 / 启动自检 / HTML 报告的辅助实现（partial 扩展）。
/// 向导只在首次运行显示；自检每次启动先在 Res 测试视频上跑一遍保存的默认步骤（进度文案统一显示"测试中"），
/// 跑完才进入正式使用；正式处理完成自动生成 HTML 报告并打开。
/// </summary>
public partial class MainViewModel
{
    // ===================== 首次向导状态 =====================
    /// <summary>向导是否已完成（由配置 SetupCompleted 驱动）</summary>
    public bool SetupCompleted => _app.SetupCompleted;

    /// <summary>启动自检开关（配置）</summary>
    public bool StartupSelfTestEnabled => _app.StartupSelfTest;

    /// <summary>是否正处于启动自检（UI 据此把阶段文字改成"测试中"并显示"跳过自检"按钮）</summary>
    [ObservableProperty] private bool _isStartupSelfTest;

    /// <summary>Res 资源目录（exe 旁的 Res，存放 logo/音乐/测试视频）</summary>
    public static string ResDir => Path.Combine(AppContext.BaseDirectory, "Res");

    /// <summary>1 秒启动测试视频路径</summary>
    public static string StartupTestVideoPath => Path.Combine(ResDir, "Easy4K_test_1s.mp4");

    /// <summary>启动自检结束事件（主窗口据此隐藏导航、切回主页）</summary>
    public event Action<bool>? StartupSelfTestFinished;

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

    // ===================== 启动自检 =====================
    /// <summary>确保 Res 里有 1 秒测试视频（缺则用 ffmpeg 生成：testsrc2 彩条 + 440Hz 正弦音，便于验证含音频链路）。</summary>
    public async Task<bool> EnsureStartupTestVideoAsync()
    {
        try
        {
            if (File.Exists(StartupTestVideoPath) && new FileInfo(StartupTestVideoPath).Length > 0) return true;
            Directory.CreateDirectory(ResDir);
            if (!Tools.FFmpegExists) { _logger.Warn("自检: FFmpeg 未就绪，无法生成测试视频"); return false; }

            _logger.Info("自检: 正在生成 1 秒测试视频...");
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
            _logger.Error($"自检: 生成测试视频失败 {ex.Message}");
            return false;
        }
    }

    /// <summary>运行启动自检：用 Res 测试视频按 appsettings 里保存的默认步骤跑一遍，完成后回主页。
    /// 自检使用 Res 下独立临时/输出目录，不污染用户正式配置的 Temp/Output，也不改写 appsettings 的目录。
    /// 自检期间不触发 StartAsync 的缓存校验/完成窗体等交互流程。</summary>
    public async Task RunStartupSelfTestAsync()
    {
        if (IsProcessing) return;
        _cts = new CancellationTokenSource();
        var ok = false;
        try
        {
            _logger.Info("==== 启动自检开始 ====");
            if (!Tools.CoreToolsOk)
            {
                _logger.Warn("启动自检: 核心工具(FFmpeg/RealESRGAN/RIFE)未就绪，跳过自检直接进入软件");
                StartupSelfTestFinished?.Invoke(false);
                return;
            }
            if (!await EnsureStartupTestVideoAsync())
            {
                _logger.Error("启动自检: 测试视频不可用，跳过自检直接进入软件");
                StartupSelfTestFinished?.Invoke(false);
                return;
            }

            // 探测测试视频
            var info = _videoDetector.Detect(StartupTestVideoPath);
            if (info is null || !info.IsValid)
            {
                _logger.Error("启动自检: 测试视频检测失败");
                StartupSelfTestFinished?.Invoke(false);
                return;
            }

            var selftestTemp = Path.Combine(ResDir, "selftest_temp");
            var selftestOut = Path.Combine(ResDir, "selftest_out");
            try
            {
                // 独立临时目录（避免清掉用户之前处理留下的缓存帧）
                if (Directory.Exists(selftestTemp)) Directory.Delete(selftestTemp, true);
                if (Directory.Exists(selftestOut)) Directory.Delete(selftestOut, true);
                Directory.CreateDirectory(selftestTemp);
                Directory.CreateDirectory(selftestOut);

                // 按保存的默认步骤勾选本地副本（不改动 UI 勾选状态）
                var split = _app.DefaultSplitFrames;
                // 任何后续阶段都需要帧序列：默认未勾拆帧但勾了超分/补帧/合并时强制拆帧，避免自检失败
                var needFrames = _app.DefaultSuperResolution || _app.DefaultInterpolation || _app.DefaultMergeVideo || _app.DefaultMergeAudio;
                if (!split && needFrames) split = true;
                var options = new ProcessingOptions
                {
                    SplitFrames = split,
                    SuperResolution = _app.DefaultSuperResolution,
                    Interpolation = _app.DefaultInterpolation,
                    MergeVideo = _app.DefaultMergeVideo,
                    MergeAudio = _app.DefaultMergeAudio && info.HasAudio,
                    SdrToHdr = false, // 自检不跑 HDR
                    IfEngine = _app.DefaultIfEngine == "Offical" ? "Offical" : "NCNN"
                };

                var ctx = new ProcessingContext
                {
                    InputVideo = StartupTestVideoPath,
                    TempRoot = selftestTemp.Replace('\\', '/'),
                    OutputRoot = selftestOut.Replace('\\', '/'),
                    ExternalFramesDir = "",
                    Video = info,
                    Options = options,
                    SrModel = _app.DefaultSrModel,
                    SrScale = Math.Clamp(_app.DefaultSrScale, 2, 4),
                    IfModel = _app.DefaultIfModel,
                    IfMultiplier = _app.DefaultIfMultiplier,
                    HdrSaturation = 200,
                    HdrContrast = 200,
                    Gpu = _env.Gpu,
                    Settings = _app,
                    Tools = Tools
                };

                _logger.Info($"启动自检选项: 超分 {ctx.SrModel}×{ctx.SrScale} / 补帧引擎 {ctx.Options.IfEngine} / 模型 {ctx.IfModel}×{ctx.IfMultiplier}");

                // 驱动进度 UI：与正式处理共用同一进度/预览管线
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
                    ok = result is not null;
                }
                catch (OperationCanceledException)
                {
                    _logger.Warn("启动自检被跳过/停止");
                    Stage = ProcessStage.Stopped;
                }
                catch (Exception ex)
                {
                    _logger.Error($"启动自检异常: {ex.Message}");
                    Stage = ProcessStage.Failed;
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
                // 清理自检残留
                try { if (Directory.Exists(selftestTemp)) Directory.Delete(selftestTemp, true); } catch { }
                try { if (Directory.Exists(selftestOut)) Directory.Delete(selftestOut, true); } catch { }
            }

            _logger.Info(ok ? "启动自检通过" : "启动自检未通过（仍可尝试正式处理）");
            StartupSelfTestFinished?.Invoke(ok);
        }
        catch (Exception ex)
        {
            _logger.Error($"启动自检异常: {ex.Message}");
            try { _cts = null; } catch { }
            StartupSelfTestFinished?.Invoke(false);
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
