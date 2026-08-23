using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Easy4K.Models;
using Easy4K.Services;
using Easy4K.Services.CommandBuilders;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace Easy4K.ViewModels;

/// <summary>主界面状态与逻辑中枢。CommunityToolkit.Mvvm [ObservableProperty] 生成属性。
/// 维护勾选联动、模型按 scale 过滤、显存警告、输出分辨率/帧率自动计算。</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly Logger _logger;
    private readonly SettingsService _settings;
    public ToolPaths Tools { get; }
    private readonly EnvironmentDetector _env;
    private readonly VideoInfoDetector _videoDetector;
    private readonly ProcessingOrchestrator _orchestratorAssigned;
    private readonly ProcessRunner _runner;
    private CancellationTokenSource? _cts;
    private AppSettings _app;
    private readonly ToolPathConfig _pathConfig;
    private readonly CpuGpuMonitor _monitor = new();
    private DateTime _startTime;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

    public Logger Logger => _logger;
    public ObservableCollection<LogEntry> Logs => _logger.LogEntries;

    /// <summary>进度推送事件（后台线程触发，UI 层需自行调度回 UI 线程）</summary>
    public event Action<ProcessProgress>? ProgressChanged;
    /// <summary>处理开始（用于自动切到"进行中"页）</summary>
    public event Action? ProcessingStarted;
    /// <summary>处理结束（含结果，用于弹完成窗体）</summary>
    public event Action<ProcessingResult>? ProcessingCompleted;

    /// <summary>自测/自动化模式下抑制完成弹窗（true 时不弹）</summary>
    public bool SuppressCompletionDialog { get; set; }

    public MainViewModel(Logger logger, SettingsService settings, ToolPaths tools,
        EnvironmentDetector env, VideoInfoDetector videoDetector, ProcessingOrchestrator orchestrator,
        ProcessRunner runner, AppSettings app, ToolPathConfig toolCfg)
    {
        _logger = logger;
        _settings = settings;
        Tools = tools;
        _env = env;
        _videoDetector = videoDetector;
        _orchestratorAssigned = orchestrator;
        _runner = runner;
        _app = app;
        _pathConfig = toolCfg;
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        // 日志追加调度到 UI 线程，保证 ObservableCollection 只在 UI 线程修改
        _logger.UiDispatcher = action => _dispatcherQueue.TryEnqueue(() => action());

        // 默认路径：从 exe 向上查找 Tools 目录确定项目根目录
        var baseDir = AppContext.BaseDirectory;
        var rootDir = FindProjectRoot(baseDir);
        _tempRoot = Path.Combine(rootDir, app.TempRoot);
        _outputRoot = Path.Combine(rootDir, app.OutputRoot);
        if (!Directory.Exists(_tempRoot)) Directory.CreateDirectory(_tempRoot);
        if (!Directory.Exists(_outputRoot)) Directory.CreateDirectory(_outputRoot);

        // 默认模型/倍率
        _srScale = app.DefaultSrScale;
        _ifMultiplier = app.DefaultIfMultiplier;
        _srModel = app.DefaultSrModel;
        _ifModel = app.DefaultIfModel;
        _useSuperMultiThread = app.UseSuperMultiThread;
        _useSafeFrameRate = app.UseSafeFrameRate;

        RefreshSrModels();
        RefreshIfModels();

        _gpu = _env.Gpu;
        // 启动时检测：非 RTX 显卡自动禁用 HDR 转换
        _isHdrEnabled = _gpu.SupportsHdr && Tools.NvEncExists;
        if (!_isHdrEnabled)
        {
            _sdrToHdr = false;
            if (!_gpu.IsRtx)
                _logger?.Warn($"当前显卡 {_gpu.Name} 非 RTX 系列，HDR 转换已自动禁用");
            else if (!Tools.NvEncExists)
                _logger?.Warn("NVEncC64 未安装，HDR 转换已禁用");
        }
        UpdateWarnings();

        // CPU/GPU 采样：回调在后台线程，调度回 UI 线程更新属性（供圆形进度条绑定）
        _monitor.Sampled += (cpu, gpu) =>
        {
            _dispatcherQueue.TryEnqueue(() => { CpuUsage = cpu; GpuUsage = gpu; });
        };
    }

    // ===================== 路径 =====================
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputResolutionText))]
    [NotifyPropertyChangedFor(nameof(TargetFpsText))]
    [NotifyPropertyChangedFor(nameof(FinalOutputName))]
    private string _inputVideo = "";

    [ObservableProperty] private string _tempRoot = "";
    [ObservableProperty] private string _outputRoot = "";

    // ===================== 处理选项 =====================
    // 拆分帧：可独立勾选，但其他选项勾选时自动强制勾选它
    [ObservableProperty]
    private bool _splitFrames = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputResolutionText))]
    [NotifyPropertyChangedFor(nameof(FinalOutputName))]
    private bool _superResolution = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetFpsText))]
    [NotifyPropertyChangedFor(nameof(FinalOutputName))]
    private bool _interpolation = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FinalOutputName))]
    private bool _mergeVideo = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FinalOutputName))]
    [NotifyPropertyChangedFor(nameof(SdrToHdrVis))]
    private bool _sdrToHdr = false;

    // ===================== 超分设置 =====================
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputResolutionText))]
    [NotifyPropertyChangedFor(nameof(FinalOutputName))]
    private int _srScale = 2;

    [ObservableProperty] private string _srModel = "";
    public ObservableCollection<string> SrModels { get; } = new();

    // ===================== 补帧设置 =====================
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetFpsText))]
    [NotifyPropertyChangedFor(nameof(FinalOutputName))]
    private int _ifMultiplier = 2;

    [ObservableProperty] private string _ifModel = "";
    public ObservableCollection<string> IfModels { get; } = new();

    // ===================== 音频设置 =====================
    // 不再选外部音频文件：改为从原视频提取音频（拆分音频按钮）+ 合并原音频进新视频（勾选）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FinalOutputName))]
    private bool _mergeAudio = true;
    // 拆分后音频文件的临时路径
    public string ExtractedAudioPath => System.IO.Path.Combine(TempRoot, "audio.flac").Replace('\\', '/');

    // ===================== HDR 设置 =====================
    [ObservableProperty] private int _hdrSaturation = 200;
    [ObservableProperty] private int _hdrContrast = 200;

    // HDR 是否可用（非 RTX 自动禁用）
    [ObservableProperty] private bool _isHdrEnabled = true;

    /// <summary>HDR 参数区可见性（bool → Visibility，供 x:Bind 绑定）</summary>
    public Microsoft.UI.Xaml.Visibility SdrToHdrVis
        => SdrToHdr ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    // ===================== 运行状态 =====================
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    private bool _isProcessing;

    [ObservableProperty]
    private bool _useSuperMultiThread;

    [ObservableProperty]
    private bool _useSafeFrameRate;

    partial void OnUseSuperMultiThreadChanged(bool value)
    {
        if (value && UseSafeFrameRate) UseSafeFrameRate = false;
        _app.UseSuperMultiThread = value;
        _settings.Save(_app, _pathConfig);
    }

    partial void OnUseSafeFrameRateChanged(bool value)
    {
        if (value && UseSuperMultiThread) UseSuperMultiThread = false;
        _app.UseSafeFrameRate = value;
        _settings.Save(_app, _pathConfig);
    }

    /// <summary>开启超级多线程时由 UI 调用：连续弹两次确认警告。</summary>
    public event Func<string, string, Task<bool>>? ShowDualWarningDialog;

    /// <summary>安全帧率降级状态（供 ProgressPage 显示降级横幅）</summary>
    [ObservableProperty]
    private bool _isDegraded;

    /// <summary>降级提示文本</summary>
    [ObservableProperty]
    private string _degradeNotice = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private double _progressPercent;
    public string ProgressText => ProgressPercent > 0 ? $"{ProgressPercent:0.#}%" : "";
    [ObservableProperty] private string _progressDetail = "";
    [ObservableProperty] private ProcessStage _stage = ProcessStage.Idle;

    /// <summary>最新产出帧路径（预览图实时刷新）</summary>
    [ObservableProperty] private string _latestFramePath = "";
    /// <summary>CPU 使用率 0-100（圆形进度条）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuUsageText))]
    private double _cpuUsage;
    /// <summary>GPU 使用率 0-100（圆形进度条）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuUsageText))]
    private double _gpuUsage;

    public string CpuUsageText => $"{CpuUsage:0}%";
    public string GpuUsageText => $"{GpuUsage:0}%";

    // ===================== 检测信息 =====================
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputResolutionText))]
    [NotifyPropertyChangedFor(nameof(TargetFpsText))]
    [NotifyPropertyChangedFor(nameof(FinalOutputName))]
    [NotifyPropertyChangedFor(nameof(VideoInfoText))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private VideoInfo? _video;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VramWarning))]
    private GpuInfo _gpu = new();

    [ObservableProperty] private string _vramWarning = "";
    [ObservableProperty] private string _warnings = "";

    // ===================== 派生属性 =====================
    public string VideoInfoText => Video is null || !Video.IsValid
        ? "未检测"
        : $"分辨率 {Video.ResolutionText}    帧率 {Video.FpsText}    时长 {Video.DurationText}    码率 {Video.BitRateText}";

    public string OutputResolutionText
    {
        get
        {
            if (Video is null || !Video.IsValid) return "未检测";
            var w = SuperResolution ? Video.Width * SrScale : Video.Width;
            var h = SuperResolution ? Video.Height * SrScale : Video.Height;
            return $"{w} x {h} ({VideoInfo.ResolutionName(w, h)})";
        }
    }

    public string TargetFpsText
    {
        get
        {
            if (Video is null || !Video.IsValid) return "未检测";
            var fps = Interpolation ? Video.FrameRate * IfMultiplier : Video.FrameRate;
            return $"{fps:0.00} fps";
        }
    }

    public string FinalOutputName
    {
        get
        {
            if (Video is null || !Video.IsValid || string.IsNullOrEmpty(InputVideo)) return "未计算";
            var w = SuperResolution ? Video.Width * SrScale : Video.Width;
            var h = SuperResolution ? Video.Height * SrScale : Video.Height;
            var fps = Interpolation ? Video.FrameRate * IfMultiplier : Video.FrameRate;
            return OutputNamer.Build(InputVideo, SuperResolution, Interpolation, SdrToHdr, w, h, fps, MergeAudio);
        }
    }

    // VramWarning 由 source-gen 生成属性，值在 UpdateWarnings() 中赋值
    public bool CanStart => !IsProcessing && Video is not null && Video.IsValid && File.Exists(InputVideo);
    public bool CanStop => IsProcessing;

    /// <summary>根据当前 GPU/模型/倍率重算显存警告</summary>
    private string ComputeVramWarning()
    {
        if (Gpu.VramMB == 0) return "";
        if (!Interpolation) return "";
        var needs8 = GpuInfo.Requires8Gb(IfModel);
        if (needs8 && Gpu.VramMB < 6144)
            return $"[RED] 模型 {IfModel} 需要 6GB+ 显存，当前 {(int)Math.Round(Gpu.VramMB / 1024.0)}GB 不足，建议使用 rife-v4.6，否则可能爆显存导致程序崩溃";
        if (needs8 && Gpu.VramMB < 8192)
            return $"[YELLOW] 模型 {IfModel} 推荐 8GB+ 显存，当前 {(int)Math.Round(Gpu.VramMB / 1024.0)}GB 可能运行缓慢";
        return $"[GREEN] 显卡满足要求，可以运行";
    }

    // ===================== 命令（由 code-behind 直接调用） =====================

    /// <summary>设置输入视频并触发检测</summary>
    public async Task SetInputVideoAsync(string path)
    {
        InputVideo = path;
        Video = null;
        OnPropertyChanged(nameof(VideoInfoText));
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        await Task.Run(() =>
        {
            var info = _videoDetector.Detect(path);
            return info;
        }).ContinueWith(t =>
        {
            if (t.Result is not null)
            {
                Video = t.Result;
                UpdateWarnings();
                _logger.Info($"已检测视频: {t.Result.ResolutionText}  {t.Result.FpsText}  时长 {t.Result.DurationText}  {t.Result.TotalFrames} 帧");
            }
            else
            {
                _logger.Error("视频信息检测失败，请确认文件可读且 FFprobe 可用");
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void SetTempRoot(string path) => TempRoot = path;
    public void SetOutputRoot(string path) => OutputRoot = path;

    /// <summary>从输入视频拆分音频到 {TempRoot}/audio.flac（按钮调用）</summary>
    public async Task ExtractAudioAsync()
    {
        if (string.IsNullOrEmpty(InputVideo) || !File.Exists(InputVideo))
        {
            _logger.Error("请先选择有效的输入视频");
            return;
        }
        if (Video is null || !Video.HasAudio)
        {
            _logger.Warn("原视频无音频流，无需拆分");
            return;
        }
        Directory.CreateDirectory(TempRoot);
        var outPath = ExtractedAudioPath;
        _logger.Info($"开始拆分音频: {Path.GetFileName(InputVideo)} → {Path.GetFileName(outPath)}");
        var args = FFmpegCommandBuilder.ExtractAudio(InputVideo, outPath);
        _logger.Command($"ffmpeg {args}");
        using var cts = new CancellationTokenSource();
        var exit = await _runner.RunAsync(Tools.FFmpegExe, args, ct: cts.Token);
        if (exit == 0 && File.Exists(outPath))
            _logger.Success($"音频拆分完成: {outPath}");
        else
            _logger.Error("音频拆分失败");
    }

    // ===================== 勾选联动 =====================
    // 规则：
    //   勾选 超分/补帧/合并视频/合并原音频 → 自动勾选拆分帧
    //   取消 拆分帧 → 自动取消所有依赖项
    //   SDR→HDR 依赖 合并视频

    partial void OnSplitFramesChanged(bool value)
    {
        if (!value)
        {
            // 取消拆分帧 → 取消所有依赖项
            _superResolution = false; OnPropertyChanged(nameof(SuperResolution));
            _interpolation = false; OnPropertyChanged(nameof(Interpolation));
            _mergeVideo = false; OnPropertyChanged(nameof(MergeVideo));
            _mergeAudio = false; OnPropertyChanged(nameof(MergeAudio));
            _sdrToHdr = false; OnPropertyChanged(nameof(SdrToHdr));
        }
        UpdateWarnings();
    }

    partial void OnSuperResolutionChanged(bool value)
    {
        if (value) SplitFrames = true; // 强制勾选拆分帧（用属性 setter 触发通知，否则 UI 不刷新）
        UpdateWarnings();
    }

    partial void OnInterpolationChanged(bool value)
    {
        if (value) SplitFrames = true; // 强制勾选拆分帧（用属性 setter 触发通知，否则 UI 不刷新）
        UpdateWarnings();
    }

    partial void OnMergeVideoChanged(bool value)
    {
        if (value) SplitFrames = true; // 强制勾选拆分帧（用属性 setter 触发通知，否则 UI 不刷新）
        UpdateWarnings();
    }

    partial void OnIfMultiplierChanged(int value) => UpdateWarnings();

    partial void OnIfModelChanged(string value) => UpdateWarnings();

    partial void OnSrScaleChanged(int value)
    {
        RefreshSrModels();
        UpdateWarnings();
    }

    partial void OnMergeAudioChanged(bool value)
    {
        if (value)
        {
            SplitFrames = true; // 强制勾选拆分帧（用属性 setter 触发通知，否则 UI 不刷新）
            if (Video is null || !Video.HasAudio)
                _logger.Warn("原视频无音频流，合并原音频将无效");
        }
        UpdateWarnings();
    }

    partial void OnSdrToHdrChanged(bool value)
    {
        if (value && !Gpu.SupportsHdr)
        {
            _logger.Warn($"当前显卡 {Gpu.Name} 不支持 SDR→HDR（需要 NVIDIA RTX 20/30/40/50 系列），HDR 选项将被忽略");
        }
        else if (value && !Tools.NvEncExists)
        {
            _logger.Warn("NVEncC64 未安装，HDR 转换无法执行，将仅输出 SDR");
        }
    }

    private void UpdateWarnings()
    {
        VramWarning = ComputeVramWarning();
        var warns = new System.Text.StringBuilder();
        if (Gpu.VramMB > 0)
            warns.AppendLine($"当前显卡: {Gpu.Name} ({(int)Math.Round(Gpu.VramMB / 1024.0)}GB)");

        if (!string.IsNullOrEmpty(VramWarning))
            warns.AppendLine(VramWarning);

        // 估算临时文件占用：每帧 PNG 约 2MB（1080p）→ 4K 约 8MB
        if (Video is not null && Video.IsValid && Video.TotalFrames > 0)
        {
            var perFrameMb = (SuperResolution ? Video.Width * SrScale : Video.Width) >= 3840 ? 8.0 : 2.0;
            var totalMb = Video.TotalFrames * perFrameMb * 1.0; // input
            if (SuperResolution) totalMb += Video.TotalFrames * 8.0; // 4k
            if (Interpolation) totalMb += Video.TotalFrames * IfMultiplier * 8.0;
            warns.AppendLine($"预计临时文件占用: {totalMb / 1024.0:F1} GB");
        }

        if (!Tools.CoreToolsOk)
            warns.AppendLine("[缺少核心工具] FFmpeg/RealESRGAN/RIFE 未就绪，请检查 Tools 目录");

        Warnings = warns.ToString().TrimEnd();
    }

    // ===================== 模型列表 =====================

    private void RefreshSrModels()
    {
        var prev = SrModel;
        SrModels.Clear();
        foreach (var m in RealEsrganCommandBuilder.ListModels(Tools.RealEsrganModelsRoot, SrScale))
            SrModels.Add(m);

        // 优先恢复原选择；否则取第一个；再尝试默认 realesr-animevideov3-{xN}
        var preferred = $"realesr-animevideov3-x{SrScale}";
        if (SrModels.Contains(prev)) SrModel = prev;
        else if (SrModels.Contains(preferred)) SrModel = preferred;
        else if (SrModels.Count > 0) SrModel = SrModels[0];
        else SrModel = "";
    }

    private void RefreshIfModels()
    {
        IfModels.Clear();
        var all = RifeCommandBuilder.ListModels(Tools.RifeModelsRoot);
        // 优先显示规格书要求的常用模型；其他作为可选
        var preferred = RifeCommandBuilder.PreferredModels.Intersect(all).ToList();
        var rest = all.Except(preferred).ToList();
        foreach (var m in preferred.Concat(rest))
            IfModels.Add(m);

        if (IfModels.Contains(IfModel)) { /* keep */ }
        else if (IfModels.Contains(_app.DefaultIfModel)) IfModel = _app.DefaultIfModel;
        else if (IfModels.Count > 0) IfModel = IfModels[0];
    }

    // ===================== 处理流程 =====================

    public async Task StartAsync()
    {
        if (IsProcessing) return;
        if (Video is null || !Video.IsValid) { _logger.Error("请先选择有效的输入视频"); return; }
        if (!File.Exists(InputVideo)) { _logger.Error("输入视频文件不存在"); return; }
        if (!Directory.Exists(TempRoot)) Directory.CreateDirectory(TempRoot);
        if (!Directory.Exists(OutputRoot)) Directory.CreateDirectory(OutputRoot);

        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(OutputRoot);

        _cts = new CancellationTokenSource();
        IsProcessing = true;
        ProgressPercent = 0;
        ProgressDetail = "准备中...";
        Stage = ProcessStage.Idle;
        LatestFramePath = "";
        IsDegraded = false;
        DegradeNotice = "";
        _startTime = DateTime.Now;
        _monitor.Start(250); // 250ms 采样，CPU/GPU 更实时
        ProcessingStarted?.Invoke();

        _logger.Info($"开始处理: {Path.GetFileName(InputVideo)}");

        var ctx = new ProcessingContext
        {
            InputVideo = InputVideo,
            TempRoot = TempRoot.Replace('\\', '/'),
            OutputRoot = OutputRoot.Replace('\\', '/'),
            Video = Video,
            Options = new ProcessingOptions
            {
                SplitFrames = SplitFrames,
                SuperResolution = SuperResolution,
                Interpolation = Interpolation,
                MergeVideo = MergeVideo,
                MergeAudio = MergeAudio,
                SdrToHdr = SdrToHdr && IsHdrEnabled
            },
            SrModel = SrModel,
            SrScale = SrScale,
            IfModel = IfModel,
            IfMultiplier = IfMultiplier,
            HdrSaturation = NvEncCommandBuilder.ClampParam(HdrSaturation),
            HdrContrast = NvEncCommandBuilder.ClampParam(HdrContrast),
            Gpu = Gpu,
            Settings = _app,
            Tools = Tools
        };

        _orchestratorAssigned.ProgressChanged += OnProgressChanged;

        string? result = null;
        try
        {
            result = await _orchestratorAssigned.RunAsync(ctx, _cts.Token);
            Stage = result is null ? ProcessStage.Failed : ProcessStage.Done;
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("处理已停止");
            Stage = ProcessStage.Stopped;
        }
        catch (Exception ex)
        {
            _logger.Error($"处理异常: {ex.Message}");
            Stage = ProcessStage.Failed;
        }
        finally
        {
            _orchestratorAssigned.ProgressChanged -= OnProgressChanged;
            _monitor.Stop();
            IsProcessing = false;
        }

        // 成功完成 → 弹完成窗体（自测模式抑制）
        if (Stage == ProcessStage.Done && result is not null && !SuppressCompletionDialog)
        {
            ProcessingCompleted?.Invoke(new ProcessingResult
            {
                Success = true,
                OutputPath = result,
                StepsText = BuildStepsText(),
                Elapsed = DateTime.Now - _startTime
            });
        }
    }

    /// <summary>根据勾选项生成步骤描述文本</summary>
    private string BuildStepsText()
    {
        var parts = new List<string>();
        if (SplitFrames) parts.Add("拆帧");
        if (SuperResolution) parts.Add($"超分×{SrScale}");
        if (Interpolation) parts.Add($"补帧×{IfMultiplier}");
        if (MergeVideo) parts.Add("合并");
        if (MergeAudio) parts.Add("音频");
        return parts.Count > 0 ? string.Join(" → ", parts) : "无";
    }

    private void OnProgressChanged(ProcessProgress p)
    {
        // 后台线程 → 调度回 UI 线程更新进度/预览属性（供进行中页绑定）
        _dispatcherQueue.TryEnqueue(() =>
        {
            // 降级通知（安全帧率触发时）
            if (!string.IsNullOrEmpty(p.DegradeNotice))
            {
                IsDegraded = true;
                DegradeNotice = p.DegradeNotice;
                return;
            }

            // 预览专用更新（只有 LatestFramePath、无进度数据）→ 只刷预览图，不动进度条，
            // 否则拆帧时轮询任务每 200ms 把 Total=0 的进度推来，进度条会乱蹦
            if (p.Total <= 0 && !string.IsNullOrEmpty(p.LatestFramePath))
            {
                LatestFramePath = p.LatestFramePath;
                return;
            }

            ProgressPercent = p.Percent;
            ProgressDetail = p.DetailText;
            Stage = p.Stage;
            if (!string.IsNullOrEmpty(p.LatestFramePath))
                LatestFramePath = p.LatestFramePath;
        });
        // 触发 ViewModel 级事件，供 code-behind 处理预览图 Image.Source
        ProgressChanged?.Invoke(p);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _logger.Warn("正在停止...");
    }

    /// <summary>强制终止所有外部工具进程（关闭窗口时兜底，防止残留进程占用 GPU/文件）。</summary>
    public void KillToolProcesses()
    {
        foreach (var name in new[] { "ffmpeg", "ffprobe", "realesrgan-ncnn-vulkan", "rife-ncnn-vulkan", "NVEncC64" })
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                }
            }
            catch { }
        }
    }

    /// <summary>软件内自测：自动选择视频、按掩码勾选功能、跑处理链路，报告写文件后退出。
    /// stages 位掩码: 1=拆帧 2=超分 4=补帧 8=合并 16=音频（默认 15=全流程）。
    /// 由 App 检测 --selftest 参数调用（必须在 UI 线程执行）。</summary>
    public async Task RunSelfTestAsync(string videoPath, string reportPath, int stages = 15)
    {
        try
        {
            _logger.Info($"=== 自测模式启动: {videoPath} (掩码 {stages}) ===");
            SuppressCompletionDialog = true; // 自测自动化不弹完成窗体
            CleanTemp(); // 清理上次残留帧，避免断点续传误判导致跳过阶段
            await SetInputVideoAsync(videoPath);
            await Task.Delay(300); // 等视频检测完成

            if (Video is null || !Video.IsValid)
            {
                _logger.Error("自测: 视频检测失败，无法继续");
                FinishSelfTest(reportPath);
                return;
            }

            // 按掩码勾选（利用属性联动：勾选超分/补帧/合并/音频会自动强制勾选拆帧）
            SplitFrames = (stages & 1) != 0;
            SuperResolution = (stages & 2) != 0;
            Interpolation = (stages & 4) != 0;
            MergeVideo = (stages & 8) != 0;
            MergeAudio = (stages & 16) != 0 && Video.HasAudio;
            SdrToHdr = false;     // 非 RTX 自动禁用

            var parts = new System.Collections.Generic.List<string>();
            if (SplitFrames) parts.Add("拆帧");
            if (SuperResolution) parts.Add($"超分×{SrScale}");
            if (Interpolation) parts.Add($"补帧×{IfMultiplier}");
            if (MergeVideo) parts.Add("合并");
            if (MergeAudio) parts.Add("音频");
            _logger.Info($"自测[{stages}]: {string.Join("+", parts)}");
            await StartAsync();

            FinishSelfTest(reportPath);
        }
        catch (Exception ex)
        {
            _logger.Error($"自测异常: {ex}");
            FinishSelfTest(reportPath);
        }
    }

    private void FinishSelfTest(string reportPath)
    {
        try
        {
            File.WriteAllText(reportPath, string.Join(Environment.NewLine,
                _logger.Snapshot().Select(e => e.ToString())));
            _logger.Info($"自测报告已写入: {reportPath}");
        }
        catch (Exception ex)
        {
            _logger.Error($"写入自测报告失败: {ex.Message}");
        }
        // 稍等让日志/文件落地后退出
        var _ = Task.Delay(800).ContinueWith(_ => Application.Current?.Exit(), TaskScheduler.Default);
    }

    public string CleanTemp()
    {
        try
        {
            if (Directory.Exists(TempRoot))
            {
                // 只清理 TempRoot 下的子目录和文件，不删除 TempRoot 本身，
                // 避免 TempRoot 与 OutputRoot 存在父子关系时误删输出
                var count = 0;
                foreach (var d in Directory.GetDirectories(TempRoot))
                {
                    try { Directory.Delete(d, recursive: true); count++; } catch { }
                }
                foreach (var f in Directory.GetFiles(TempRoot))
                {
                    try { File.Delete(f); count++; } catch { }
                }
                var msg = count > 0
                    ? $"已清理 {count} 个临时项目: {TempRoot}"
                    : "临时目录为空，无需清理";
                _logger.Success(msg);
                return msg;
            }
            else
            {
                var msg = "临时目录不存在，无需清理";
                _logger.Info(msg);
                return msg;
            }
        }
        catch (Exception ex)
        {
            var msg = $"清理临时目录失败: {ex.Message}";
            _logger.Error(msg);
            return msg;
        }
    }

    /// <summary>从 startDir 向上查找含 Tools 子目录的项目根目录。</summary>
    private static string FindProjectRoot(string startDir)
    {
        var dir = startDir;
        for (int i = 0; i < 10; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "Tools")))
                return dir;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        // 兜底：exe 所在目录
        return startDir;
    }

    public void ClearLog() => _logger.Clear();

    /// <summary>复制全部日志到剪贴板（纯文本，含时间戳与级别）。</summary>
    public void CopyLog()
    {
        var sb = new StringBuilder();
        foreach (var e in _logger.LogEntries)
            sb.AppendLine(e.ToString());
        var text = sb.ToString();
        if (string.IsNullOrEmpty(text))
        {
            _logger.Info("日志为空，无可复制内容");
            return;
        }
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(text);
            Clipboard.SetContent(pkg);
            _logger.Info($"已复制 {_logger.LogEntries.Count} 条日志到剪贴板");
        }
        catch (Exception ex)
        {
            _logger.Error($"复制日志失败: {ex.Message}");
        }
    }

    // ===================== 环境/快捷 =====================

    public string CheckEnvironment()
    {
        var results = _env.CheckAll();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("==== 环境检测结果 ====");
        foreach (var r in results)
        {
            sb.Append($"[{r.Status}] {r.Name}");
            if (!string.IsNullOrEmpty(r.Extra)) sb.Append($"  ({r.Extra})");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(r.Hint)) sb.AppendLine($"        → {r.Hint}");
        }
        sb.AppendLine("====================");
        var text = sb.ToString();
        _logger.Info(text);
        return text;
    }

    public string CheckGpu()
    {
        var g = _env.Gpu;
        var text = $"显卡: {g.Name}\n显存: {g.VramText}\nNVIDIA: {g.IsNvidia}\nRTX 系列: {(g.IsRtx ? $"RTX {g.Series}0" : "否")}\n驱动: {g.DriverVersion}\n支持 HDR: {g.SupportsHdr}";
        _logger.Info(text);
        return text;
    }

    public void SaveSettings()
    {
        _app.DefaultSrModel = SrModel;
        _app.DefaultIfModel = IfModel;
        _app.DefaultSrScale = SrScale;
        _app.DefaultIfMultiplier = IfMultiplier;
        _settings.Save(_app, _pathConfig);
    }
}
