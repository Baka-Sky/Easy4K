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
/// 覆盖：全部超分模型、全部补帧模型(NCNN)、各可用倍率、音频合并、GPU加速/安全帧率/降画质/HDR/CPU模式特殊组合。
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
        string? IfModel,
        int IfMultiplier,
        bool Safe,
        bool LowerQuality,
        bool GpuAccel,
        bool Cpu,
        int Threads,
        bool Dedup,
        string DedupMode,
        bool Turbo = false,
        int TurboSegments = 2);

    private sealed record AutoResult(AutoCase Case, bool Ok, TimeSpan Elapsed, string? Output, string? Note);

    /// <summary>
    /// 跑全功能/全模型自动测试。fullMatrix=false：全部模型×2，另对代表性模型补跑 ×3/×4/×5；
    /// fullMatrix=true：每个 v4 模型跑 ×2~×5 全矩阵（用例数最多、最慢）。
    /// 不模拟鼠标；每例输出独立临时目录，成功即清理，失败保留目录便于排查。
    /// </summary>
    public async Task RunAutoTestAllAsync(bool fullMatrix = false, bool dedupOnly = false, bool desktopReport = false)
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
        _logger.Info(dedupOnly
            ? "==== 帧去重专项测试开始（只跑去重用例，不加载超分模型、补帧用最轻 lite 模型）===="
            : "==== 自动测试开始（GUI 全功能/全模型，全程可见可停止）====");

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

            var cases = dedupOnly ? BuildDedupTestCases() : BuildAutoTestCases(info, fullMatrix);
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
                            EncodePreset = _app.EncodePreset,
                            DedupEnabled = c.Dedup,
                            DedupMode = string.IsNullOrEmpty(c.DedupMode) ? "performance" : c.DedupMode,
                            TurboMode = c.Turbo,
                            TurboSegments = Math.Clamp(c.TurboSegments, 1, 5)
                        },
                        Tools = Tools
                    };
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
            WriteAutoTestSummary(root, results, elapsedAll, cancelled, desktopReport);
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

    /// <summary>帧去重专项用例（--dedup-test）：不加载超分模型，补帧只用权重最小的 lite 模型，
    /// 目的是几十秒内在 GUI 里看清「去重进度 / 预览 / 四阶判决 / 回填」是否正常。</summary>
    private List<AutoCase> BuildDedupTestCases()
    {
        var cases = new List<AutoCase>();
        var models = RifeCommandBuilder.ListModels(Tools.RifeModelsRoot);
        // 权重最小的 lite 模型（v4.15/4.16/4.17-lite 实测都是 5134KB，最省）
        var lite = models.FirstOrDefault(m => m.Equals("rife-v4.16-lite", StringComparison.OrdinalIgnoreCase))
                   ?? models.FirstOrDefault(m => m.Contains("-lite", StringComparison.OrdinalIgnoreCase))
                   ?? models.FirstOrDefault() ?? "rife-v4.6";

        void Add(bool if_, bool merge, string name, string dedupMode, string? ifModel = null)
        {
            cases.Add(new AutoCase(
                name,
                new ProcessingOptions
                {
                    SplitFrames = true,
                    SuperResolution = false,
                    Interpolation = if_,
                    MergeVideo = merge,
                    MergeAudio = false,
                    SdrToHdr = false
                },
                null, 2, ifModel, 2, false, false, false, false, 4, true, dedupMode));
        }

        // 1/2：只拆帧 + 合并（不跑超分/补帧模型，仅去重判决）——最快看清去重进度与预览
        Add(false, true, "去重·性能模式（拆帧+合并，FP16 模型）", "performance");
        Add(false, true, "去重·完美模式（拆帧+合并，FP32 模型）", "uhd");
        // 3/4：最轻补帧 ×2 + 合并 —— 验证去重与补帧回填的联动
        Add(true, true, $"去重·性能模式 + 最轻补帧 {lite} ×2", "performance", lite);
        Add(true, true, $"去重·完美模式 + 最轻补帧 {lite} ×2", "uhd", lite);
        return cases;
    }

    private List<AutoCase> BuildAutoTestCases(VideoInfo info, bool fullMatrix)
    {
        var cases = new List<AutoCase>();
        var gpu = _env.Gpu;

        void Add(bool split, bool sr, bool if_, bool merge, bool audio, bool hdr,
            string? srModel, int srScale, string? ifModel, int ifMult,
            string name, bool safe = false, bool lower = false, bool gpuAccel = false, bool cpu = false,
            int threads = 4, bool dedup = false, string dedupMode = "performance",
            bool turbo = false, int turboSegments = 2)
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
                    SdrToHdr = hdr
                },
                srModel, srScale, ifModel, ifMult, safe, lower, gpuAccel, cpu, threads, dedup, dedupMode,
                turbo, turboSegments));
        }

        // ---------- 超分：全部算法 × 全部模型（x2/x3/x4），只跑本机已部署的引擎 ----------
        for (var scale = 2; scale <= 4; scale++)
        {
            for (var fi = 0; fi < SrCatalog.Families.Count; fi++)
            {
                foreach (var m in SrCatalog.ModelsFor(fi, scale))
                {
                    var exe = SrCommandBuilder.ExeFor(Tools, m);
                    if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) continue;
                    Add(true, true, false, true, true, false, m, scale, null, 2, $"超分 ×{scale} {m}（含拆帧+合并+音频）");
                }
            }
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
                Add(true, false, true, true, true, false, null, 2, m, mult,
                    $"补帧 NCNN {m} ×{mult}");
        }

        // ---------- 特殊组合 ----------
        var srList2 = SrCatalog.ModelsFor(0, 2).Where(m => File.Exists(SrCommandBuilder.ExeFor(Tools, m))).ToList();
        var dftSr = srList2.Contains("realesr-animevideov3-x2")
            ? "realesr-animevideov3-x2"
            : srList2.FirstOrDefault() ?? "realesr-animevideov3-x2";
        var ncnnDefault = allNcnn.Contains("rife-v4.6") ? "rife-v4.6" : allNcnn.FirstOrDefault() ?? "rife-v4.6";

        // 完整默认流程（超分+补帧+合并+音频）
        Add(true, true, true, true, true, false, dftSr, 2, ncnnDefault, 2, "组合 完整流程（超分×2+补帧NCNN×2+合并+音频）");
        // GPU 加速
        Add(true, true, true, true, true, false, dftSr, 2, ncnnDefault, 2,
            "组合 GPU加速（FFmpeg硬件解码/编码优先）", gpuAccel: true);
        // 安全帧率 + 降画质
        Add(true, true, true, true, true, false, dftSr, 2, ncnnDefault, 2,
            "组合 安全帧率+降低画质", safe: true, lower: true);
        // SDR→HDR（仅 RTX + NVEncC 可用）
        if (gpu.SupportsHdr && Tools.NvEncExists)
            Add(true, true, false, true, true, true, dftSr, 2, null, 2, "组合 SDR→HDR（NVEncC 硬件编码）");
        // 帧去重：性能模式（FP16 模型）与完美模式（FP32 模型）
        Add(true, true, true, true, true, false, dftSr, 2, ncnnDefault, 2,
            "组合 帧去重·性能模式（FP16）", dedup: true, dedupMode: "performance");
        Add(true, true, true, true, true, false, dftSr, 2, ncnnDefault, 2,
            "组合 帧去重·完美模式（FP32）", dedup: true, dedupMode: "uhd");
        // CPU 模式（代表性单模型，速度最慢放最后）
        Add(true, true, false, true, true, false, dftSr, 2, null, 2,
            "组合 CPU处理模式（超分代表性模型，最慢）", safe: true, cpu: true, threads: 8);

        // ---------- 涡轮分段（新方案）：按帧号切 N 段，多段流水线并行 ----------
        Add(true, true, true, true, true, false, dftSr, 2, ncnnDefault, 2,
            "涡轮·2 段（超分+补帧+合并+音频）", threads: 1, turbo: true, turboSegments: 2);
        Add(true, true, true, true, true, false, dftSr, 2, ncnnDefault, 2,
            "涡轮·3 段（超分+补帧+合并+音频）", threads: 1, turbo: true, turboSegments: 3);
        Add(true, true, false, true, true, false, dftSr, 2, null, 2,
            "涡轮·2 段（仅超分）", threads: 1, turbo: true, turboSegments: 2);
        Add(true, false, true, true, true, false, null, 2, ncnnDefault, 2,
            "涡轮·2 段（仅补帧）", threads: 1, turbo: true, turboSegments: 2);
        Add(true, true, true, true, true, false, dftSr, 2, ncnnDefault, 2,
            "涡轮·2 段 + 帧去重·性能模式", threads: 1, dedup: true, dedupMode: "performance",
            turbo: true, turboSegments: 2);
        Add(true, true, true, true, true, false, dftSr, 2, ncnnDefault, 2,
            "涡轮·2 段 + 帧去重·完美模式", threads: 1, dedup: true, dedupMode: "uhd",
            turbo: true, turboSegments: 2);
        Add(true, true, true, true, true, false, dftSr, 2, ncnnDefault, 2,
            "涡轮·5 段（最大分段压力测试）", threads: 1, turbo: true, turboSegments: 5);
        Add(true, true, true, true, true, false, dftSr, 2, ncnnDefault, 2,
            "涡轮·3 段 + 帧去重·完美模式", threads: 1, dedup: true, dedupMode: "uhd",
            turbo: true, turboSegments: 3);
        Add(true, false, false, true, true, false, null, 2, null, 2,
            "帧去重·性能模式（仅去重+合并，不超分不补帧）", dedup: true, dedupMode: "performance");
        Add(true, false, false, true, true, false, null, 2, null, 2,
            "帧去重·完美模式（仅去重+合并，不超分不补帧）", dedup: true, dedupMode: "uhd");

        return cases;
    }

    /// <summary>把单条用例写进 ViewModel（等价于用户在界面上逐项勾选/选择），
    /// 之后直接调 StartAsync() —— 与点「开始处理」、孤儿模式完全同一条入口。</summary>
    private void ApplyCaseToViewModel(AutoCase c, string tempRoot, string outputRoot)
    {
        TempRoot = tempRoot.Replace('\\', '/');
        OutputRoot = outputRoot.Replace('\\', '/');

        SplitFrames = c.Options.SplitFrames;
        SuperResolution = c.Options.SuperResolution;
        Interpolation = c.Options.Interpolation;
        MergeVideo = c.Options.MergeVideo;
        MergeAudio = c.Options.MergeAudio;
        SdrToHdr = c.Options.SdrToHdr;

        SrModel = c.SrModel ?? "";
        SrScale = c.SrScale;
        IfModel = c.IfModel ?? "";
        IfMultiplier = c.IfMultiplier;

        UseSafeFrameRate = c.Safe;
        LowerQualityForVram = c.LowerQuality;
        UseGpuAcceleration = c.GpuAccel;
        UseCpuProcessing = c.Cpu;
        ThreadCount = c.Threads;

        DedupEnabled = c.Dedup;
        DedupMode = string.IsNullOrEmpty(c.DedupMode) ? "performance" : c.DedupMode;

        TurboMode = c.Turbo;
        TurboSegments = Math.Clamp(c.TurboSegments, 1, 5);

        AudioSrEnabled = false;
        KeepSubtitles = false;
    }

    /// <summary>API 模式全组合测试：不直接用 orchestrator，而是每例先把选项写进 ViewModel，
    /// 再调 StartAsync()（与用户点「开始处理」、孤儿模式同一条入口），验证整条流水线是否真正联通。
    /// 报告写桌面，进程由调用方退出。</summary>
    public async Task RunAutoTestViaStartAsyncAsync(bool fullMatrix = false, bool desktopReport = true)
    {
        if (IsProcessing) { _logger.Warn("API 自动测试: 已有处理进行中，忽略本次请求"); return; }
        if (!Tools.CoreToolsOk)
        {
            _logger.Error("API 自动测试: FFmpeg/RealESRGAN/RIFE 未就绪，无法开始");
            return;
        }

        var startAll = DateTime.Now;
        var results = new List<AutoResult>();
        var root = Path.Combine(ResDir, "autotest_api");
        HeadlessAutoTest = true;
        SuppressCompletionDialog = true;
        IsAutoTest = true;

        // 测试会改一堆设置并落盘，跑完用备份把用户的 appsettings.json 原样还原
        var configBackup = Path.Combine(Path.GetTempPath(), $"easy4k_autotest_cfg_{Guid.NewGuid():N}.json");
        var configPath = Path.Combine(AppPaths.WritableRoot, "appsettings.json");
        try { if (File.Exists(configPath)) File.Copy(configPath, configBackup, true); } catch { }
        _settings.SuppressWrites = true;

        try
        {
            if (!await EnsureStartupTestVideoAsync())
            {
                _logger.Error("API 自动测试: 测试视频不可用");
                return;
            }
            var info = _videoDetector.Detect(StartupTestVideoPath);
            if (info is null || !info.IsValid)
            {
                _logger.Error("API 自动测试: 测试视频检测失败");
                return;
            }

            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            Directory.CreateDirectory(root);

            var cases = BuildAutoTestCases(info, fullMatrix);
            _autoTestCount = cases.Count;
            _autoTestIndex = 0;
            _logger.Info($"==== API 自动测试开始（走用户「开始处理」同一入口），共 {cases.Count} 例 ====");

            foreach (var c in cases)
            {
                _autoTestIndex++;
                var temp = Path.Combine(root, $"run{_autoTestIndex:D3}_temp");
                var outp = Path.Combine(root, $"run{_autoTestIndex:D3}_out");
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
                try { if (Directory.Exists(outp)) Directory.Delete(outp, true); } catch { }
                Directory.CreateDirectory(temp);
                Directory.CreateDirectory(outp);

                _logger.Info($"---- [{_autoTestIndex}/{_autoTestCount}] {c.Name} ----");
                ProgressDetail = $"API 自动测试 {_autoTestIndex}/{_autoTestCount}：{c.Name}";

                ApplyCaseToViewModel(c, temp, outp);
                await SetInputVideoAsync(StartupTestVideoPath);
                await Task.Delay(400);   // 等视频检测完成

                var sw = Stopwatch.StartNew();
                var note = "";
                try { await StartAsync(); }
                catch (OperationCanceledException) { note = "已取消"; }
                catch (Exception ex) { note = ex.Message; }
                sw.Stop();

                var produced = Path.Combine(outp, FinalOutputName);
                var ok = Stage == ProcessStage.Done && File.Exists(produced);
                if (Stage == ProcessStage.Done && !File.Exists(produced)) note = "产物不存在";
                else if (Stage == ProcessStage.Failed && string.IsNullOrEmpty(note)) note = "处理失败";
                // 涡轮要求固态临时目录：命中机械盘会被启动校验自动关掉，报告里注明（这一例实际测的是串行路径）
                if (c.Turbo && !TurboMode && string.IsNullOrEmpty(note))
                    note = "涡轮被自动关闭（临时目录在机械硬盘，按串行处理）";

                results.Add(new AutoResult(c, ok, sw.Elapsed, ok ? produced : null, note));
                _logger.Info($"---- [{_autoTestIndex}/{_autoTestCount}] {c.Name} → {(ok ? "通过" : "失败")}，耗时 {sw.Elapsed.TotalSeconds:F1}s{(string.IsNullOrEmpty(note) ? "" : $"（{note}）")} ----");

                try
                {
                    if (ok) { Directory.Delete(temp, true); Directory.Delete(outp, true); }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"API 自动测试异常: {ex.Message}");
        }
        finally
        {
            _settings.SuppressWrites = false;
            HeadlessAutoTest = false;
            SuppressCompletionDialog = false;
            IsAutoTest = false;

            // 还原用户配置（测试期间改过的选项不落盘）
            try
            {
                if (File.Exists(configBackup))
                {
                    File.Copy(configBackup, configPath, true);
                    File.Delete(configBackup);
                }
            }
            catch { }
        }

        WriteAutoTestSummary(root, results, DateTime.Now - startAll, false, desktopReport);
        _logger.Info("==== API 自动测试结束 ====");
    }

    // ===================== 孤儿模式式 API 自动测试（每例一个进程） =====================

    /// <summary>单例用例描述（父进程写 JSON，子进程按它设置 ViewModel 后调 StartAsync）。</summary>
    public sealed class ApiCaseSpec
    {
        public string Name { get; set; } = "";
        public string Input { get; set; } = "";
        public string TempRoot { get; set; } = "";
        public string OutputRoot { get; set; } = "";
        public bool SplitFrames { get; set; } = true;
        public bool SuperResolution { get; set; }
        public bool Interpolation { get; set; }
        public bool MergeVideo { get; set; }
        public bool MergeAudio { get; set; }
        public bool SdrToHdr { get; set; }
        public string SrModel { get; set; } = "";
        public int SrScale { get; set; } = 2;
        public string IfModel { get; set; } = "";
        public int IfMultiplier { get; set; } = 2;
        public bool Safe { get; set; }
        public bool LowerQuality { get; set; }
        public bool GpuAccel { get; set; }
        public bool Cpu { get; set; }
        public int Threads { get; set; } = 4;
        public bool Dedup { get; set; }
        public string DedupMode { get; set; } = "performance";
        public bool Turbo { get; set; }
        public int TurboSegments { get; set; } = 2;
        /// <summary>子进程把结果写到这个路径</summary>
        public string ResultPath { get; set; } = "";
    }

    public sealed class ApiCaseResult
    {
        public string Name { get; set; } = "";
        public bool Ok { get; set; }
        public double Seconds { get; set; }
        public string? Output { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>子进程：按用例 JSON 设置界面状态，再调 StartAsync()（与用户点「开始处理」同一条入口）。</summary>
    public async Task RunSingleApiCaseAsync(string jsonPath)
    {
        var result = new ApiCaseResult();
        ApiCaseSpec? spec = null;
        try
        {
            spec = System.Text.Json.JsonSerializer.Deserialize<ApiCaseSpec>(await File.ReadAllTextAsync(jsonPath));
            if (spec is null) return;
            result.Name = spec.Name;

            HeadlessAutoTest = true;
            SuppressCompletionDialog = true;

            ApplySpecToViewModel(spec);
            await SetInputVideoAsync(spec.Input);
            await Task.Delay(500);   // 等视频检测完成

            var sw = Stopwatch.StartNew();
            await StartAsync();
            sw.Stop();
            result.Seconds = sw.Elapsed.TotalSeconds;

            var produced = Path.Combine(spec.OutputRoot, FinalOutputName);
            result.Ok = Stage == ProcessStage.Done && File.Exists(produced);
            if (Stage == ProcessStage.Done && !File.Exists(produced)) result.Note = "产物不存在";
            else if (Stage == ProcessStage.Failed) result.Note = "处理失败";
            // 涡轮要求固态临时目录：命中机械盘会被启动校验自动关掉，注明这一例实际跑的是串行
            if (spec.Turbo && !TurboMode && string.IsNullOrEmpty(result.Note))
                result.Note = "涡轮被自动关闭（临时目录在机械硬盘）";
            result.Output = result.Ok ? produced : null;
        }
        catch (Exception ex)
        {
            result.Note = ex.Message;
        }
        finally
        {
            HeadlessAutoTest = false;
            SuppressCompletionDialog = false;
            if (spec is not null && !string.IsNullOrEmpty(spec.ResultPath))
            {
                try
                {
                    await File.WriteAllTextAsync(spec.ResultPath,
                        System.Text.Json.JsonSerializer.Serialize(result), Encoding.UTF8);
                }
                catch { }
            }
        }
    }

    private void ApplySpecToViewModel(ApiCaseSpec s)
    {
        TempRoot = s.TempRoot.Replace('\\', '/');
        OutputRoot = s.OutputRoot.Replace('\\', '/');

        SplitFrames = s.SplitFrames;
        SuperResolution = s.SuperResolution;
        Interpolation = s.Interpolation;
        MergeVideo = s.MergeVideo;
        MergeAudio = s.MergeAudio;
        SdrToHdr = s.SdrToHdr;

        SrModel = s.SrModel;
        SrScale = s.SrScale;
        IfModel = s.IfModel;
        IfMultiplier = s.IfMultiplier;

        UseSafeFrameRate = s.Safe;
        LowerQualityForVram = s.LowerQuality;
        UseGpuAcceleration = s.GpuAccel;
        UseCpuProcessing = s.Cpu;
        ThreadCount = s.Threads;

        DedupEnabled = s.Dedup;
        DedupMode = string.IsNullOrEmpty(s.DedupMode) ? "performance" : s.DedupMode;

        TurboMode = s.Turbo;
        TurboSegments = Math.Clamp(s.TurboSegments, 1, 5);

        AudioSrEnabled = false;
        KeepSubtitles = false;
    }

    /// <summary>父进程：逐例拉起一个 --api-case 子进程（跑完关闭再启动下一例），汇总结果写桌面报告。</summary>
    public async Task RunApiOrchestratorAsync(bool fullMatrix = false)
    {
        if (!Tools.CoreToolsOk)
        {
            _logger.Error("API 自动测试: FFmpeg/RealESRGAN/RIFE 未就绪，无法开始");
            return;
        }

        var startAll = DateTime.Now;
        var results = new List<AutoResult>();
        var root = Path.Combine(ResDir, "autotest_api");
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        Directory.CreateDirectory(root);

        if (!await EnsureStartupTestVideoAsync())
        {
            _logger.Error("API 自动测试: 测试视频不可用");
            return;
        }
        var info = _videoDetector.Detect(StartupTestVideoPath);
        if (info is null || !info.IsValid)
        {
            _logger.Error("API 自动测试: 测试视频检测失败");
            return;
        }

        var cases = BuildAutoTestCases(info, fullMatrix);
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Easy4K.exe");
        _logger.Info($"==== API 自动测试（每例独立进程）开始，共 {cases.Count} 例 ====");

        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            var dir = Path.Combine(root, $"run{i + 1:D3}");
            var tempDir = Path.Combine(dir, "temp");
            var outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(outDir);

            var caseJson = Path.Combine(dir, "case.json");
            var resultJson = Path.Combine(dir, "result.json");

            var spec = new ApiCaseSpec
            {
                Name = c.Name,
                Input = StartupTestVideoPath,
                TempRoot = tempDir,
                OutputRoot = outDir,
                SplitFrames = c.Options.SplitFrames,
                SuperResolution = c.Options.SuperResolution,
                Interpolation = c.Options.Interpolation,
                MergeVideo = c.Options.MergeVideo,
                MergeAudio = c.Options.MergeAudio,
                SdrToHdr = c.Options.SdrToHdr,
                SrModel = c.SrModel ?? "",
                SrScale = c.SrScale,
                IfModel = c.IfModel ?? "",
                IfMultiplier = c.IfMultiplier,
                Safe = c.Safe,
                LowerQuality = c.LowerQuality,
                GpuAccel = c.GpuAccel,
                Cpu = c.Cpu,
                Threads = c.Threads,
                Dedup = c.Dedup,
                DedupMode = c.DedupMode,
                Turbo = c.Turbo,
                TurboSegments = c.TurboSegments,
                ResultPath = resultJson
            };
            await File.WriteAllTextAsync(caseJson,
                System.Text.Json.JsonSerializer.Serialize(spec), Encoding.UTF8);

            _logger.Info($"---- [{i + 1}/{cases.Count}] {c.Name}：启动子进程 ----");
            var sw = Stopwatch.StartNew();
            ApiCaseResult? r = null;
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                psi.ArgumentList.Add("--api-case");
                psi.ArgumentList.Add(caseJson);
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
                    try { await proc.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException)
                    {
                        try { proc.Kill(true); } catch { }
                    }
                }
                if (File.Exists(resultJson))
                    r = System.Text.Json.JsonSerializer.Deserialize<ApiCaseResult>(await File.ReadAllTextAsync(resultJson));
            }
            catch (Exception ex)
            {
                _logger.Warn($"子进程启动/等待失败：{ex.Message}");
            }
            sw.Stop();

            var ok = r?.Ok == true;
            var note = r?.Note ?? "子进程未产出结果";
            results.Add(new AutoResult(c, ok, sw.Elapsed, r?.Output, note));
            _logger.Info($"---- [{i + 1}/{cases.Count}] {c.Name} → {(ok ? "通过" : "失败")}，耗时 {sw.Elapsed.TotalSeconds:F1}s{(string.IsNullOrEmpty(note) ? "" : $"（{note}）")} ----");

            try { if (ok) Directory.Delete(dir, true); } catch { }
        }

        WriteAutoTestSummary(root, results, DateTime.Now - startAll, false, desktopReport: true);
        _logger.Info("==== API 自动测试结束 ====");
    }

    private static string Sanitize(string name)
    {
        var invalids = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var ch in name)
            sb.Append(invalids.Contains(ch) ? '_' : ch);
        return sb.ToString();
    }

    private void WriteAutoTestSummary(string root, List<AutoResult> results, TimeSpan elapsed, bool cancelled,
        bool desktopReport = false)
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

            // API/无界面模式：把同一份报告再写到桌面，用户不需要进 Res 目录翻
            if (desktopReport)
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var reportPath = Path.Combine(desktop, $"Easy4K流水线全组合自动测试_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                var header = new StringBuilder();
                header.AppendLine("Easy4K 流水线全组合自动测试（API 模式）");
                header.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                header.AppendLine($"显卡: {_env.Gpu.Name} / 显存 {_env.Gpu.VramText}");
                header.AppendLine($"测试素材: {StartupTestVideoPath}");
                header.AppendLine("覆盖: 全部超分模型(×2/×3/×4) + 全部 NCNN 补帧模型(×2，full 模式 ×2~×5) + " +
                                  "完整流程/GPU加速/安全帧率+降画质/HDR/帧去重(性能·完美)/CPU模式 + 涡轮分段(2/3/5 段)");
                header.AppendLine(new string('=', 72));
                header.AppendLine(sb.ToString());
                File.WriteAllText(reportPath, header.ToString(), Encoding.UTF8);
                _logger.Success($"桌面报告已写入: {reportPath}");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"写入自动测试总结失败: {ex.Message}");
        }
    }
}
