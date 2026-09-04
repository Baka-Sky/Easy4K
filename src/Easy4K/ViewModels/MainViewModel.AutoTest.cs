using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Easy4K.Models;
using Easy4K.Services;
using Easy4K.Services.CommandBuilders;

namespace Easy4K.ViewModels;

/// <summary>
/// GUI 全面自动测试（不加命令行/后台进程，走与正式处理完全相同的 Orchestrator 管线，UI 全程可见可停）。
/// 覆盖：全部超分模型、全部补帧模型(NCNN+Offical)、各可用倍率、音频合并、GPU加速/安全帧率/降画质/HDR/CPU模式特殊组合。
/// 不模拟鼠标、不改用户正式 Temp/Output（各自独立临时目录）；结果写入 Res\autotest_summary.txt，GUI 继续可用。
/// </summary>
public partial class MainViewModel
{
    /// <summary>是否处于自动测试（UI 进度带用例名）</summary>
    [ObservableProperty] private bool _isAutoTest;

    /// <summary>自动测试结束事件（主窗口据此隐藏导航、回主页）</summary>
    public event Action<bool>? AutoTestFinished;

    /// <summary>当前用例进度展示用的索引/总数/名称（OnProgressChanged 读取，定义在本文件以保持自检逻辑不动）</summary>
    private int _autoTestIndex;
    private int _autoTestCount;
    private string _autoTestCaseName = "";

    /// <summary>单条自动测试用例</summary>
    private sealed record AutoCase(
        string Name,
        ProcessingOptions Options,
        string? SrModel,
        int SrScale,
        string IfEngine,
        string? IfModel,
        int IfMultiplier,
        bool Safe,
        bool LowerQuality,
        bool GpuAccel,
        bool Cpu,
        int Threads);

    private sealed record AutoResult(AutoCase Case, bool Ok, TimeSpan Elapsed, string? Output, string? Note);

    /// <summary>
    /// 跑全功能/全模型自动测试。fullMatrix=false：全部模型×2，另对代表性模型补跑 ×3/×4/×5；
    /// fullMatrix=true：每个 v4/Offical 模型跑 ×2~×5 全矩阵（用例数最多、最慢）。
    /// 不模拟鼠标；每例输出独立临时目录，成功即清理，失败保留目录便于排查。
    /// </summary>
    public async Task RunAutoTestAllAsync(bool fullMatrix = false)
    {
        if (IsProcessing) { _logger.Warn("自动测试: 已有处理进行中，忽略本次请求"); return; }
        if (!Tools.CoreToolsOk)
        {
            _logger.Error("自动测试: FFmpeg/RealESRGAN/RIFE 未就绪，无法开始");
            return;
        }

        _cts = new CancellationTokenSource();
        var startAll = DateTime.Now;
        IsAutoTest = true;
        IsStartupSelfTest = false;
        SuppressCompletionDialog = true;
        _logger.Info("==== 自动测试开始（GUI 全功能/全模型，全程可见可停止）====");

        try
        {
            if (!await EnsureStartupTestVideoAsync())
            {
                _logger.Error("自动测试: 测试视频不可用");
                AutoTestFinished?.Invoke(false);
                return;
            }
            var info = _videoDetector.Detect(StartupTestVideoPath);
            if (info is null || !info.IsValid)
            {
                _logger.Error("自动测试: 测试视频检测失败");
                AutoTestFinished?.Invoke(false);
                return;
            }

            var root = Path.Combine(ResDir, "autotest");
            var failRoot = Path.Combine(root, "fail");
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch { }
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(failRoot);

            var cases = BuildAutoTestCases(info, fullMatrix);
            _autoTestCount = cases.Count;
            _autoTestIndex = 0;
            _logger.Info($"自动测试用例总数: {cases.Count}（fullMatrix={(fullMatrix ? "全倍率2~5" : "全模型x2+代表性全倍率")}），输入: 1s 测试视频 {info.Width}x{info.Height}@{info.FrameRate:F0}fps");

            var results = new List<AutoResult>();
            IsProcessing = true;
            IsPaused = false;
            Stage = ProcessStage.Idle;
            ProgressPercent = 0;
            ProgressDetail = "自动测试准备中...";
            _startTime = DateTime.Now;
            _monitor.Start(250);
            ProcessingStarted?.Invoke();

            _orchestratorAssigned.ProgressChanged += OnProgressChanged;
            var cancelled = false;
            try
            {
                foreach (var c in cases)
                {
                    if (_cts.IsCancellationRequested) { cancelled = true; break; }
                    _autoTestIndex++;
                    _autoTestCaseName = c.Name;

                    var temp = Path.Combine(root, $"run{_autoTestIndex:D3}_temp");
                    var outp = Path.Combine(root, $"run{_autoTestIndex:D3}_out");
                    Directory.CreateDirectory(temp);
                    Directory.CreateDirectory(outp);

                    _logger.Info($"---- [{_autoTestIndex}/{_autoTestCount}] {c.Name} ----");
                    ProgressPercent = 0;
                    ProgressDetail = $"自动测试 {_autoTestIndex}/{_autoTestCount}：{c.Name}";

                    var ctx = new ProcessingContext
                    {
                        InputVideo = StartupTestVideoPath,
                        TempRoot = temp.Replace('\\', '/'),
                        OutputRoot = outp.Replace('\\', '/'),
                        ExternalFramesDir = "",
                        Video = info,
                        Options = c.Options,
                        SrModel = c.SrModel ?? "",
                        SrScale = c.SrScale,
                        IfModel = c.IfModel ?? "",
                        IfMultiplier = c.IfMultiplier,
                        HdrSaturation = 200,
                        HdrContrast = 200,
                        Gpu = _env.Gpu,
                        Settings = new AppSettings
                        {
                            UseSafeFrameRate = c.Safe,
                            LowerQualityForVram = c.LowerQuality,
                            UseGpuAcceleration = c.GpuAccel,
                            UseCpuProcessing = c.Cpu,
                            ThreadCount = Math.Clamp(c.Threads, 1, 32),
                            EncodePreset = _app.EncodePreset
                        },
                        Tools = Tools
                    };
                    if (ctx.Options.IfEngine == "Offical") ctx.Options.IfEngine = "Offical";

                    var sw = Stopwatch.StartNew();
                    string? output = null;
                    var note = "";
                    try
                    {
                        output = await _orchestratorAssigned.RunAsync(ctx, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                        note = "已取消";
                        _logger.Warn($"[{_autoTestIndex}/{_autoTestCount}] {c.Name} 被取消");
                    }
                    catch (Exception ex)
                    {
                        note = ex.Message;
                        _logger.Error($"[{_autoTestIndex}/{_autoTestCount}] {c.Name} 异常: {ex.Message}");
                    }
                    sw.Stop();

                    var ok = output is not null;
                    results.Add(new AutoResult(c, ok, sw.Elapsed, output, note));
                    _logger.Info($"---- [{_autoTestIndex}/{_autoTestCount}] {c.Name} → {(ok ? "通过" : "失败")}，耗时 {sw.Elapsed.TotalSeconds:F1}s{(ok ? "" : $"（{note}）")} ----");
                    if (ok) _logger.Success($"输出: {output}");
                    Stage = ok ? ProcessStage.Done : ProcessStage.Failed;

                    // 成功清理目录；失败保留便于定位
                    try
                    {
                        if (ok) Directory.Delete(temp, true);
                        else { var keep = Path.Combine(failRoot, $"run{_autoTestIndex:D3}_{Sanitize(c.Name)}"); if (Directory.Exists(keep)) Directory.Delete(keep, true); Directory.Move(temp, keep); }
                        if (Directory.Exists(outp)) Directory.Delete(outp, true);
                    }
                    catch { }

                    if (cancelled) break;
                }
            }
            finally
            {
                _orchestratorAssigned.ProgressChanged -= OnProgressChanged;
                _monitor.Stop();
                IsProcessing = false;
                IsPaused = false;
                SuppressCompletionDialog = false;
                IsAutoTest = false;
                _cts = null;
            }

            var elapsedAll = DateTime.Now - startAll;
            WriteAutoTestSummary(root, results, elapsedAll, cancelled);
            _logger.Info("==== 自动测试结束 ====");
            AutoTestFinished?.Invoke(!cancelled && results.Count > 0 && results.All(r => r.Ok));
        }
        catch (Exception ex)
        {
            _logger.Error($"自动测试异常: {ex.Message}");
            try { _cts = null; } catch { }
            IsProcessing = false;
            IsAutoTest = false;
            SuppressCompletionDialog = false;
            AutoTestFinished?.Invoke(false);
        }
    }

    // ===================== 用例构造 =====================

    private List<AutoCase> BuildAutoTestCases(VideoInfo info, bool fullMatrix)
    {
        var cases = new List<AutoCase>();
        var gpu = _env.Gpu;

        void Add(bool split, bool sr, bool if_, bool merge, bool audio, bool hdr,
            string engine, string? srModel, int srScale, string? ifModel, int ifMult,
            string name, bool safe = false, bool lower = false, bool gpuAccel = false, bool cpu = false, int threads = 4)
        {
            cases.Add(new AutoCase(
                name,
                new ProcessingOptions
                {
                    SplitFrames = split,
                    SuperResolution = sr,
                    Interpolation = if_,
                    MergeVideo = merge,
                    MergeAudio = audio,
                    SdrToHdr = hdr,
                    IfEngine = engine
                },
                srModel, srScale, engine, ifModel, ifMult, safe, lower, gpuAccel, cpu, threads));
        }

        // ---------- 超分：全部模型（x2/x3/x4） ----------
        for (var scale = 2; scale <= 4; scale++)
        {
            foreach (var m in RealEsrganCommandBuilder.ListModels(Tools.RealEsrganModelsRoot, scale))
                Add(true, true, false, true, true, false, "NCNN", m, scale, null, 2, $"超分 ×{scale} {m}（含拆帧+合并+音频）");
        }

        // ---------- 补帧 NCNN：全部模型 ----------
        var allNcnn = RifeCommandBuilder.ListModels(Tools.RifeModelsRoot);
        var probes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "rife-v4.6", "rife-v4.12-lite", "rife-v4.18", "rife-v4.22-lite", "rife-v4.25", "rife-v4.26" };
        foreach (var m in allNcnn)
        {
            var isV4 = m.StartsWith("rife-v4", StringComparison.OrdinalIgnoreCase);
            // v2/v3 老模型仅支持 ×2
            var mults = isV4
                ? fullMatrix ? new[] { 2, 3, 4, 5 } : probes.Contains(m) ? new[] { 2, 3, 4, 5 } : new[] { 2 }
                : new[] { 2 };
            foreach (var mult in mults)
                Add(true, false, true, true, true, false, "NCNN", null, 2, m, mult,
                    $"补帧 NCNN {m} ×{mult}");
        }

        // ---------- 补帧 Offical：全部模型 ----------
        var offical = OfficalRifeCommandBuilder.ListModels(Tools.OfficalRifeModelsRoot);
        var offProbes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "official_4.6", "official_4.15", "official_4.26" };
        foreach (var m in offical)
        {
            var mults = fullMatrix ? new[] { 2, 3, 4, 5 } : offProbes.Contains(m) ? new[] { 2, 3, 4, 5 } : new[] { 2 };
            foreach (var mult in mults)
                Add(true, false, true, true, true, false, "Offical", null, 2, m, mult,
                    $"补帧 Offical {m} ×{mult}");
        }

        // ---------- 特殊组合 ----------
        var dftSr = "realesr-animevideov3-x2";
        var srList2 = RealEsrganCommandBuilder.ListModels(Tools.RealEsrganModelsRoot, 2);
        if (!srList2.Contains(dftSr) && srList2.Count > 0) dftSr = srList2[0];
        var ncnnDefault = allNcnn.Contains("rife-v4.6") ? "rife-v4.6" : allNcnn.FirstOrDefault() ?? "rife-v4.6";

        // 完整默认流程（超分+补帧+合并+音频）
        Add(true, true, true, true, true, false, "NCNN", dftSr, 2, ncnnDefault, 2, "组合 完整流程（超分×2+补帧NCNN×2+合并+音频）");
        // GPU 加速
        Add(true, true, true, true, true, false, "NCNN", dftSr, 2, ncnnDefault, 2,
            "组合 GPU加速（FFmpeg硬件解码/编码优先）", gpuAccel: true);
        // 安全帧率 + 降画质
        Add(true, true, true, true, true, false, "NCNN", dftSr, 2, ncnnDefault, 2,
            "组合 安全帧率+降低画质", safe: true, lower: true);
        // SDR→HDR（仅 RTX + NVEncC 可用）
        if (gpu.SupportsHdr && Tools.NvEncExists)
            Add(true, true, false, true, true, true, "NCNN", dftSr, 2, null, 2, "组合 SDR→HDR（NVEncC 硬件编码）");
        // CPU 模式（代表性单模型，速度最慢放最后）
        Add(true, true, false, true, true, false, "NCNN", dftSr, 2, null, 2,
            "组合 CPU处理模式（超分代表性模型，最慢）", safe: true, cpu: true, threads: 8);

        return cases;
    }

    private static string Sanitize(string name)
    {
        var invalids = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var ch in name)
            sb.Append(invalids.Contains(ch) ? '_' : ch);
        return sb.ToString();
    }

    private void WriteAutoTestSummary(string root, List<AutoResult> results, TimeSpan elapsed, bool cancelled)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Easy4K 自动测试总结");
            sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"总耗时: {elapsed.TotalMinutes:F1} 分钟");
            sb.AppendLine($"结束原因: {(cancelled ? "手动停止" : "全部跑完")}");
            sb.AppendLine($"用例: {results.Count} 通过: {results.Count(r => r.Ok)} 失败: {results.Count(r => !r.Ok)}");
            sb.AppendLine(new string('-', 60));
            foreach (var r in results)
                sb.AppendLine($"{(r.Ok ? "[通过]" : "[失败]")} {r.Case.Name}  {r.Elapsed.TotalSeconds:F1}s{(r.Note is null ? "" : $"  {r.Note}")}");
            var file = Path.Combine(root, "AutoTestSummary.txt");
            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            _logger.Success($"自动测试总结已写入: {file}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"写入自动测试总结失败: {ex.Message}");
        }
    }
}
