using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Easy4K.Models;
using Easy4K.Services;
using Easy4K.Services.CommandBuilders;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
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
    /// <summary>启动确认弹窗（临时目录冲突 / CPU 最终警告）的等待器：UI 弹窗处理完成后调 DialogResult 唤醒</summary>
    private TaskCompletionSource<bool>? _dialogTcs;
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
    /// <summary>临时目录缓存与当前视频不符（UI 弹窗引导重新选择/清理）。
    /// 参数 true = 点击"开始处理"时触发（累计愤怒警告计数）；false = 选视频/换目录时触发（只弹普通提示）。</summary>
    public event Action<bool>? TempCacheMismatchDetected;
    /// <summary>清理临时文件时请求 UI 释放预览帧句柄（Image.Source 不清空会锁住帧文件删不掉）</summary>
    public event Action? CleanRequested;
    /// <summary>启动前 CPU 最终警告（UI 弹窗确认"不建议开启 CPU 处理"；确认后调 DialogResult(true) 继续）</summary>
    public event Action? CpuFinalWarningRequired;

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
        // 引擎仅跟随手动"保存当前设置为默认"恢复（不自动记住上次使用的引擎）
        _ifEngine = app.DefaultIfEngine == "Offical" ? "Offical" : "NCNN";
        _useSafeFrameRate = app.UseSafeFrameRate;
        _lowerQualityForVram = app.LowerQualityForVram;
        _useGpuAcceleration = app.UseGpuAcceleration;
        _useCpuProcessing = app.UseCpuProcessing;
        _threadCount = Math.Clamp(app.ThreadCount, 1, 32);
        _hdrSaturation = Math.Clamp(app.HdrSaturation, 0, 200);
        _hdrContrast = Math.Clamp(app.HdrContrast, 0, 200);
        _suppressRedWarning = app.SuppressRedWarning;

        RefreshSrModels();

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
    [NotifyPropertyChangedFor(nameof(HasInputVideo))]
    [NotifyPropertyChangedFor(nameof(ImportFramesEnabled))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private string _inputVideo = "";

    /// <summary>是否已选择输入视频（用于互斥与清除按钮可用性）</summary>
    public bool HasInputVideo => !string.IsNullOrEmpty(InputVideo);

    /// <summary>手动导入的外部帧文件夹（非空时跳过拆帧，直接用它作为输入帧）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExternalFrames))]
    [NotifyPropertyChangedFor(nameof(BrowseInputEnabled))]
    [NotifyPropertyChangedFor(nameof(SplitFramesLocked))]
    [NotifyPropertyChangedFor(nameof(MergeAudioEnabled))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private string _externalFramesDir = "";

    public bool HasExternalFrames => !string.IsNullOrEmpty(ExternalFramesDir);

    /// <summary>已选帧文件夹时输入视频不可再浏览（二选一）</summary>
    public bool BrowseInputEnabled => !HasExternalFrames;

    /// <summary>已选输入视频时帧文件夹不可再导入（二选一）</summary>
    public bool ImportFramesEnabled => !HasInputVideo;

    /// <summary>帧文件夹模式下拆分帧环节由外部帧替代，拆帧开关禁用（仅置灰，保持勾选表示沿用外部帧）</summary>
    public bool SplitFramesLocked => !HasExternalFrames;

    /// <summary>帧文件夹模式下合并原音频无意义，开关禁用（仅置灰，不改变勾选状态）</summary>
    public bool MergeAudioEnabled => !HasExternalFrames;

    [ObservableProperty] private string _tempRoot = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private string _outputRoot = "";

    /// <summary>临时目录缓存与当前视频不符且用户未处理（点了取消）→ 阻断启动按钮。
    /// 清理缓存或更换匹配目录后由 RefreshCacheBlock 重新评估解除。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(ExtractAudioEnabled))]
    private bool _cacheBlocked;

    // ===================== 处理选项 =====================
    // 拆分帧：可独立勾选，但其他选项勾选时自动强制勾选它
    [ObservableProperty]
    private bool _splitFrames = true;
    /// <summary>导入帧文件夹前的拆分帧勾选状态（清除帧文件夹时恢复）</summary>
    private bool _splitFramesBeforeFrames = true;

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

    /// <summary>补帧倍率是否锁定（v2/v3 老模型仅支持 x2 时禁用倍率下拉框）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IfMultiplierEnabled))]
    private bool _ifMultiplierLocked;

    /// <summary>倍率下拉框是否可用（锁定 v2/v3 时禁用，其余可用）</summary>
    public bool IfMultiplierEnabled => !IfMultiplierLocked;

    [ObservableProperty] private string _ifModel = "";

    /// <summary>补帧模型种类："NCNN"（rife-ncnn-vulkan）或 "Offical"（PyTorch pkl 模型）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IfModel))]
    private string _ifEngine = "NCNN";
    /// <summary>可选模型种类列表</summary>
    public ObservableCollection<string> IfEngines { get; } = new() { "NCNN", "Offical" };

    // ===================== 音频设置 =====================
    // 不再选外部音频文件：改为从原视频提取音频（拆分音频按钮）+ 合并原音频进新视频（勾选）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FinalOutputName))]
    private bool _mergeAudio = true;
    /// <summary>导入帧文件夹前的合并音频勾选状态（清除帧文件夹时恢复，避免"禁用但仍打勾"）</summary>
    private bool _mergeAudioBeforeFrames = true;
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
    [NotifyPropertyChangedFor(nameof(CanClean))]
    private bool _isProcessing;

    /// <summary>当前处理是否处于暂停状态（工具进程已挂起）</summary>
    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _useSafeFrameRate;

    /// <summary>降低部分画质以降低显存占用（-u UHD 模式，realesrgan/rife 均生效，可与安全帧率共存）</summary>
    [ObservableProperty]
    private bool _lowerQualityForVram;

    /// <summary>使 FFmpeg 尝试使用 GPU 加速（拆帧 -hwaccel / 合并帧 GPU 编码器，失败自动回退 CPU）</summary>
    [ObservableProperty]
    private bool _useGpuAcceleration = true;

    /// <summary>使用 CPU 处理所有模型（超分/补帧 NCNN -g -1、Offical 强制 CPU），速度慢</summary>
    [ObservableProperty]
    private bool _useCpuProcessing;

    /// <summary>是否显示图片预览（处理中可随时开关）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewHintText))]
    private bool _showPreview = true;

    /// <summary>合并/HDR/音频阶段不产生新帧，自动关闭预览并禁用预览开关（避免预览停在旧画面）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewToggleEnabled))]
    private bool _previewBlocked;

    /// <summary>预览开关是否可操作（阻断阶段禁用）</summary>
    public bool IsPreviewToggleEnabled => !PreviewBlocked;

    public string PreviewHintText => ShowPreview
        ? "预览区：处理开始后实时显示最新帧"
        : "图片预览已关闭";

    /// <summary>用户自定义线程数（滑块 1-32），命令 -j 1:{n}:{n}</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThreadLabel))]
    [NotifyPropertyChangedFor(nameof(ThreadWarnText))]
    [NotifyPropertyChangedFor(nameof(ThreadWarnVis))]
    private int _threadCount = 2;

    partial void OnUseSafeFrameRateChanged(bool value)
    {
        _app.UseSafeFrameRate = value;
        _settings.Save(_app, _pathConfig);
    }

    partial void OnLowerQualityForVramChanged(bool value)
    {
        _app.LowerQualityForVram = value;
        _settings.Save(_app, _pathConfig);
    }

    partial void OnUseGpuAccelerationChanged(bool value)
    {
        _app.UseGpuAcceleration = value;
        _settings.Save(_app, _pathConfig);
    }

    partial void OnUseCpuProcessingChanged(bool value)
    {
        _app.UseCpuProcessing = value;
        _settings.Save(_app, _pathConfig);
        _logger.Info(value ? "已开启 CPU 处理模式：超分/补帧模型将全部使用 CPU 推理" : "已关闭 CPU 处理模式");
        // CPU 模式下安全帧率/降低画质/GPU加速无效：先取消勾选再禁用（UI 监听本属性禁用），避免"勾着却灰显"的别扭状态
        if (value)
        {
            UseSafeFrameRate = false;
            LowerQualityForVram = false;
            UseGpuAcceleration = false;
        }
    }

    partial void OnThreadCountChanged(int value)
    {
        _app.ThreadCount = Math.Clamp(value, 1, 32);
        _settings.Save(_app, _pathConfig);
    }

    partial void OnHdrSaturationChanged(int value)
    {
        _app.HdrSaturation = Math.Clamp(value, 0, 200);
        _settings.Save(_app, _pathConfig);
    }

    partial void OnHdrContrastChanged(int value)
    {
        _app.HdrContrast = Math.Clamp(value, 0, 200);
        _settings.Save(_app, _pathConfig);
    }

    /// <summary>滑块当前值对应的 -j 线程串（如 1:8:8）</summary>
    public string ThreadLabel => $"1:{ThreadCount}:{ThreadCount}";

    /// <summary>超过 1:8:8 时的警告文案</summary>
    public string ThreadWarnText => ThreadCount > 8
        ? "警告：线程超过 1:8:8，高并发提交可能引发 Vulkan 设备丢失或显存溢出，请谨慎使用"
        : "";

    /// <summary>警告可见性（超过 1:8:8 才显示）</summary>
    public Microsoft.UI.Xaml.Visibility ThreadWarnVis
        => ThreadCount > 8 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>安全帧率降级状态（供 ProgressPage 显示降级横幅）</summary>
    [ObservableProperty]
    private bool _isDegraded;

    /// <summary>是否正在清理临时文件（主页进度条显示）</summary>
    [ObservableProperty]
    private bool _isCleaning;

    /// <summary>清理临时文件进度 0-100（主页进度条）</summary>
    [ObservableProperty]
    private double _cleanProgressPercent;

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

    /// <summary>输入+输出均配置完整才可启动：输出目录与临时目录非空，且（有外部帧文件夹 或 已检测到有效输入视频文件）。
    /// 临时目录缓存不符不再禁用按钮——改为点击启动时弹窗引导（清理/更换/取消），避免"按钮灰了但不知道为什么"。</summary>
    public bool CanStart => !IsProcessing
        && !string.IsNullOrWhiteSpace(OutputRoot)
        && !string.IsNullOrWhiteSpace(TempRoot)
        && (HasExternalFrames || (Video is not null && Video.IsValid && File.Exists(InputVideo)));
    /// <summary>拆分音频需基于真实输入视频，帧文件夹模式（无视频文件）时禁用</summary>
    public bool ExtractAudioEnabled => CanStart && !HasExternalFrames;
    public bool CanStop => IsProcessing;
    /// <summary>处理进行中禁止清理临时文件（避免清理与工具进程抢文件/误删中间产物）</summary>
    public bool CanClean => !IsProcessing;

    // ===================== 显卡状态（绿=可运行 / 黄=勉强 / 红=不行） =====================
    public enum GpuStatusGrade { None, Green, Yellow, Red }

    private static readonly SolidColorBrush GreenBrush = new(Color.FromArgb(255, 46, 125, 50));
    private static readonly SolidColorBrush YellowBrush = new(Color.FromArgb(255, 249, 168, 37));
    private static readonly SolidColorBrush RedBrush = new(Color.FromArgb(255, 198, 40, 40));
    private static readonly SolidColorBrush GrayBrush = new(Color.FromArgb(255, 128, 128, 128));
    private static readonly SolidColorBrush WhiteBrush = new(Color.FromArgb(255, 255, 255, 255));
    private static readonly SolidColorBrush BlackBrush = new(Color.FromArgb(255, 0, 0, 0));
    /// <summary>黄色文字（"不再爆红"时替代红色提示，对应 XAML 里原来的 Orange）</summary>
    private static readonly SolidColorBrush OrangeWarnBrush = new(Color.FromArgb(255, 255, 165, 0));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuStatusText))]
    [NotifyPropertyChangedFor(nameof(GpuStatusBrush))]
    [NotifyPropertyChangedFor(nameof(GpuStatusForeground))]
    [NotifyPropertyChangedFor(nameof(GpuStatusVis))]
    [NotifyPropertyChangedFor(nameof(VramWarningBrush))]
    private GpuStatusGrade _gpuStatusLevel = GpuStatusGrade.None;

    [ObservableProperty] private string _gpuStatusText = "";

    /// <summary>以后不再爆红：勾选后红色级警告以黄色代替显示（未勾选保持红色）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuStatusBrush))]
    [NotifyPropertyChangedFor(nameof(GpuStatusForeground))]
    [NotifyPropertyChangedFor(nameof(GpuStatusVis))]
    [NotifyPropertyChangedFor(nameof(VramWarningBrush))]
    private bool _suppressRedWarning;

    partial void OnSuppressRedWarningChanged(bool value)
    {
        _app.SuppressRedWarning = value;
        _settings.Save(_app, _pathConfig);
        UpdateWarnings();
    }

    /// <summary>显卡状态是否以黄色显示（红色级 + "不再爆红" 时变黄）</summary>
    private bool IsRedSuppressed => GpuStatusLevel == GpuStatusGrade.Red && SuppressRedWarning;

    public SolidColorBrush GpuStatusBrush => IsRedSuppressed ? YellowBrush : GpuStatusLevel switch
    {
        GpuStatusGrade.Red => RedBrush,
        GpuStatusGrade.Yellow => YellowBrush,
        GpuStatusGrade.Green => GreenBrush,
        _ => GrayBrush
    };

    public SolidColorBrush GpuStatusForeground => (GpuStatusLevel == GpuStatusGrade.Yellow || IsRedSuppressed) ? BlackBrush : WhiteBrush;

    public Microsoft.UI.Xaml.Visibility GpuStatusVis
        => GpuStatusLevel == GpuStatusGrade.None
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>补帧卡片状态小字颜色：红色级未勾选"不再爆红"时显示红色，勾选后显示黄色，其余保持黄色。</summary>
    public SolidColorBrush VramWarningBrush => IsRedSuppressed
        ? OrangeWarnBrush
        : GpuStatusLevel == GpuStatusGrade.Red ? RedBrush : OrangeWarnBrush;

    /// <summary>计算显卡状态等级与描述文字（绿=可运行 / 黄=勉强 / 红=不行）</summary>
    private (GpuStatusGrade Level, string Text) ComputeGpuStatus()
    {
        if (Gpu.VramMB == 0) return (GpuStatusGrade.None, "");
        if (!Interpolation) return (GpuStatusGrade.Green, "显卡满足要求，可以运行");
        var needs8 = GpuInfo.Requires8Gb(IfModel);
        if (needs8 && Gpu.VramMB < 6144)
            return (GpuStatusGrade.Red, $"模型 {IfModel} 需要 6GB+ 显存，当前 {(int)Math.Round(Gpu.VramMB / 1024.0)}GB 不足，可能爆显存导致崩溃");
        if (needs8 && Gpu.VramMB < 8192)
            return (GpuStatusGrade.Yellow, $"模型 {IfModel} 推荐 8GB+ 显存，当前 {(int)Math.Round(Gpu.VramMB / 1024.0)}GB 可能运行缓慢");
        return (GpuStatusGrade.Green, "显卡满足要求，可以运行");
    }

    // ===================== 命令（由 code-behind 直接调用） =====================

    /// <summary>设置输入视频并触发检测（浏览/菜单/启动恢复用）</summary>
    public async Task SetInputVideoAsync(string path)
    {
        InputVideo = path?.Trim() ?? "";
        await DetectVideoAsync(InputVideo);
    }

    /// <summary>手填输入视频路径时，InputVideo 已由输入框 TwoWay 绑定实时更新，这里只做检测、不写回，
    /// 避免每次输入都被回写覆盖导致输入框无法正常输入。</summary>
    public async Task DetectVideoAsync(string path)
    {
        path = path?.Trim() ?? "";
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

            // 换视频后立即检查临时目录缓存是否来自其他视频，是则提醒用户处理
            // （不等点"开始"才提示，避免"无法启动"的困惑）
            var (cstatus, csource) = CheckTempCache(TempRoot);
            // 缓存匹配/无缓存 → 放行；不匹配 → 阻断（点弹窗取消后启动按钮保持禁用）
            CacheBlocked = cstatus == TempCacheStatus.Mismatch;
            if (cstatus == TempCacheStatus.Mismatch)
            {
                _logger.Warn($"临时目录缓存来自其他视频（{csource}），处理当前视频前请更换临时目录或清理缓存");
                TempCacheMismatchDetected?.Invoke(false); // 选视频触发：只弹普通提示，不计入愤怒计数
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>清除输入视频（输入视频与帧文件夹二选一，清除后可改选帧文件夹）</summary>
    public void ClearInputVideo()
    {
        InputVideo = "";
        Video = null;
        _logger.Info("已清除输入视频");
    }

    /// <summary>使用帧文件夹（无输入视频文件）时手动设置视频参数，替代 FFprobe 检测。</summary>
    public void SetManualVideoInfo(int width, int height, long totalFrames, double fps)
    {
        Video = new VideoInfo
        {
            Width = width,
            Height = height,
            FrameRate = fps,
            FrameRateRaw = fps.ToString("0.###"),
            TotalFrames = totalFrames,
            Duration = fps > 0 ? TimeSpan.FromSeconds(totalFrames / fps) : TimeSpan.Zero,
            AudioCodec = "" // 手动参数默认无音频（帧文件夹时合并音频本就禁用）
        };
        UpdateWarnings();
        _logger.Info($"已设置帧文件夹参数: {width} x {height}, {totalFrames} 帧, {fps:0.##} fps");
    }

    public void SetTempRoot(string path)
    {
        TempRoot = path;
    }

    /// <summary>临时目录文本框改动即持久化到配置文件（否则重启后丢失，清理会错误地指向默认 Temp 目录）。
    /// 空值不持久化（清空输入框只是临时禁用启动，重启后仍恢复上次有效目录）。</summary>
    partial void OnTempRootChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _app.TempRoot = value;
            _settings.Save(_app, _pathConfig);
        }
        // 换了新目录后重新评估是否仍阻断启动（新目录无缓存/匹配 → 立即放行）
        RefreshCacheBlock();
    }

    /// <summary>重新评估临时目录缓存阻断状态：缓存匹配/无缓存/已清理 → 放行；
    /// 仍与当前视频不符 → 保持阻断（启动按钮禁用）。</summary>
    public void RefreshCacheBlock()
    {
        var (s, _) = CheckTempCache(TempRoot);
        CacheBlocked = s == TempCacheStatus.Mismatch;
    }

    public void SetOutputRoot(string path) => OutputRoot = path;

    /// <summary>输出目录改动即持久化到配置文件（否则重启后丢失，输出会错误地回到默认 Output 目录）。
    /// 空值不持久化（清空输入框只是临时禁用启动，重启后仍恢复上次有效目录）。</summary>
    partial void OnOutputRootChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _app.OutputRoot = value;
            _settings.Save(_app, _pathConfig);
        }
    }

    /// <summary>手动导入外部帧文件夹（跳过拆帧）。校验目录存在且有 PNG 帧，否则拒绝。</summary>
    public void ImportFrameFolder(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _logger.Error("帧文件夹不存在，导入失败");
            return;
        }
        var pngCount = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.png").Length : 0;
        if (pngCount == 0)
        {
            _logger.Error("所选文件夹内没有 PNG 帧文件，导入失败");
            return;
        }
        ExternalFramesDir = dir;
        // 帧文件夹没有原视频可提取音频：记录原勾选状态并取消勾选（该开关在帧文件夹模式下同时被禁用）
        _mergeAudioBeforeFrames = MergeAudio;
        MergeAudio = false;
        // 帧文件夹已提供帧：拆帧环节被替代，记录原勾选状态并取消勾选，避免"禁用但仍打勾"
        _splitFramesBeforeFrames = SplitFrames;
        SplitFrames = false;
        _logger.Info($"已导入外部帧文件夹: {dir}（{pngCount} 帧），处理时将跳过拆帧");
    }

    /// <summary>清除外部帧文件夹，恢复默认先拆帧。若仍有依赖项勾选而拆分帧未勾选，强制勾选拆分帧保证流程完整。</summary>
    public void ClearExternalFrames()
    {
        ExternalFramesDir = "";
        MergeAudio = _mergeAudioBeforeFrames; // 恢复导入帧文件夹前的勾选状态
        SplitFrames = _splitFramesBeforeFrames; // 恢复导入帧文件夹前的勾选状态
        // 恢复强捆绑：清除外部帧后，若仍勾选了依赖项但拆分帧未勾选，强制勾选拆分帧
        if (!SplitFrames && (SuperResolution || Interpolation || MergeVideo || MergeAudio))
            SplitFrames = true;
        _logger.Info("已取消外部帧文件夹，将按默认流程先拆帧");
    }

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
            _logger.Warn("输入视频无音频流，无需提取");
            return;
        }
        Directory.CreateDirectory(TempRoot);
        var outPath = ExtractedAudioPath;
        _logger.Info($"开始提取音频: {Path.GetFileName(InputVideo)} → {Path.GetFileName(outPath)}");
        var args = FFmpegCommandBuilder.ExtractAudio(InputVideo, outPath);
        _logger.Command($"ffmpeg {args}");
        using var cts = new CancellationTokenSource();
        var exit = await _runner.RunAsync(Tools.FFmpegExe, args, ct: cts.Token);
        if (exit == 0 && File.Exists(outPath))
            _logger.Success($"音频提取完成: {outPath}");
        else
            _logger.Error("音频提取失败");
    }

    // ===================== 勾选联动 =====================
    // 规则（未导入外部帧时）：
    //   勾选 超分/补帧/合并视频/合并原音频 → 自动勾选拆分帧
    //   取消 拆分帧 → 自动取消所有依赖项
    //   SDR→HDR 依赖 合并视频
    // 导入外部帧后解除与拆分帧的强捆绑：补帧/超分/合并可单独勾选运行，不再强制勾选拆分帧

    partial void OnSplitFramesChanged(bool value)
    {
        // 导入外部帧后解除强捆绑：取消拆分帧不影响其他选项（帧已由外部导入提供）
        if (HasExternalFrames) return;
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
        if (value && !HasExternalFrames) SplitFrames = true; // 强制勾选拆分帧（用属性 setter 触发通知，否则 UI 不刷新）
        UpdateWarnings();
    }

    partial void OnInterpolationChanged(bool value)
    {
        if (value && !HasExternalFrames) SplitFrames = true; // 强制勾选拆分帧（用属性 setter 触发通知，否则 UI 不刷新）
        UpdateWarnings();
    }

    partial void OnMergeVideoChanged(bool value)
    {
        if (value && !HasExternalFrames) SplitFrames = true; // 强制勾选拆分帧（用属性 setter 触发通知，否则 UI 不刷新）
        UpdateWarnings();
    }

    partial void OnIfMultiplierChanged(int value)
    {
        _app.DefaultIfMultiplier = value;
        _settings.Save(_app, _pathConfig);
        UpdateWarnings();
    }

    partial void OnIfModelChanged(string value)
    {
        // v2/v3 老模型仅支持 2 倍补帧：锁定倍率并强制 x2；模型即时保存到配置
        RefreshIfMultiplierLock(value);
        _app.DefaultIfModel = value;
        _settings.Save(_app, _pathConfig);
        UpdateWarnings();
    }

    /// <summary>v2/v3 老模型仅支持 2 倍补帧：锁定倍率并强制 x2（显式调用，不依赖属性通知时序）</summary>
    public void RefreshIfMultiplierLock(string model)
    {
        var oldModel = IfEngine != "Offical"
            && model.StartsWith("rife-v", StringComparison.OrdinalIgnoreCase)
            && !model.StartsWith("rife-v4", StringComparison.OrdinalIgnoreCase);
        IfMultiplierLocked = oldModel;
        if (oldModel && IfMultiplier != 2) IfMultiplier = 2;
    }

    partial void OnSrModelChanged(string value)
    {
        _app.DefaultSrModel = value;
        _settings.Save(_app, _pathConfig);
    }

    partial void OnSrScaleChanged(int value)
    {
        RefreshSrModels();
        _app.DefaultSrScale = value;
        _settings.Save(_app, _pathConfig);
        UpdateWarnings();
    }

    partial void OnMergeAudioChanged(bool value)
    {
        if (value)
        {
            if (!HasExternalFrames) SplitFrames = true; // 强制勾选拆分帧（用属性 setter 触发通知，否则 UI 不刷新）
            if (Video is null || !Video.HasAudio)
                _logger.Warn("输入视频无音频流，合并原音频将无效");
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
        var (level, text) = ComputeGpuStatus();
        GpuStatusLevel = level;
        GpuStatusText = text;
        // 补帧卡片里的状态小字：红色提示在"不再爆红"勾选时以黄色代替（颜色由 VramWarningBrush 控制），不勾选保持红色
        VramWarning = text;

        var warns = new System.Text.StringBuilder();
        if (Gpu.VramMB > 0)
            warns.AppendLine($"当前显卡: {Gpu.Name} ({(int)Math.Round(Gpu.VramMB / 1024.0)}GB)");

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

    partial void OnIfEngineChanged(string value)
    {
        // 模型选择框由 MainPage code-behind 统一重建（切换引擎时整体重新加载下拉框，
        // 彻底避开 ComboBox 绑定联动导致的 0x80070490 闪退），这里只刷新警告
        UpdateWarnings();
    }

    /// <summary>获取某引擎下的模型列表（纯数据，不含任何 UI 状态，供 MainPage 重建模型下拉框用）</summary>
    public IReadOnlyList<string> GetIfModelsForEngine(string engine)
    {
        var result = new List<string>();
        switch (engine)
        {
            case "Offical":
                // Offical 引擎：列出 Tools\officalrife\models\official_*（pkl 模型）
                result.AddRange(OfficalRifeCommandBuilder.ListModels(Tools.OfficalRifeModelsRoot));
                break;

            case "NCNN":
                // NCNN 引擎：rife-ncnn-vulkan 模型，优先规格书常用模型
                var all = RifeCommandBuilder.ListModels(Tools.RifeModelsRoot);
                var preferred = RifeCommandBuilder.PreferredModels.Intersect(all).ToList();
                var rest = all.Except(preferred).ToList();
                result.AddRange(preferred.Concat(rest));
                break;
        }
        return result;
    }

    /// <summary>配置里的默认补帧模型（NCNN 引擎优先选中它）</summary>
    public string DefaultIfModel => _app.DefaultIfModel;

    // ===================== 处理流程 =====================

    /// <summary>启动前确认流程（顺序执行）：
    /// 1) 临时目录有旧文件/缓存不符 → 弹窗引导（重新选择临时目录/清理缓存/取消），未解决则阻断启动；
    /// 2) CPU 处理模式开启 → 弹最终警告"不建议开启 CPU 处理"，取消则不启动。
    /// 弹窗由 MainWindow 弹出并处理，处理完成后回调 DialogResult 唤醒本流程继续。</summary>
    public async Task<bool> ConfirmStartAsync()
    {
        // 1. 临时目录冲突优先处理
        while (true)
        {
            var (status, _) = CheckTempCache(TempRoot);
            if (status != TempCacheStatus.Mismatch) break;
            CacheBlocked = true;
            _dialogTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TempCacheMismatchDetected?.Invoke(true); // 点开始处理触发：累计愤怒计数
            var ok = await _dialogTcs.Task;
            if (!ok) return false; // 用户取消且仍未解决 → 阻断启动
        }
        CacheBlocked = false;

        // 2. CPU 最终警告（排在临时目录冲突之后）
        if (UseCpuProcessing)
        {
            _dialogTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CpuFinalWarningRequired?.Invoke();
            if (!await _dialogTcs.Task) return false; // 用户取消 → 不启动
        }
        return true;
    }

    /// <summary>UI 弹窗处理结果回调：resolved=true 表示已解决/已确认（继续），false 表示取消（阻断启动）</summary>
    public void DialogResult(bool resolved) => _dialogTcs?.TrySetResult(resolved);

    public async Task StartAsync()
    {
        if (IsProcessing) return;
        if (Video is null || !Video.IsValid) { _logger.Error("请先选择有效的输入视频（或导入帧文件夹并填写参数）"); return; }
        if (!HasExternalFrames && !File.Exists(InputVideo)) { _logger.Error("输入视频文件不存在"); return; }
        if (!Directory.Exists(TempRoot)) Directory.CreateDirectory(TempRoot);
        if (!Directory.Exists(OutputRoot)) Directory.CreateDirectory(OutputRoot);

        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(OutputRoot);

        // 校验临时目录缓存：与当前视频不符则阻止启动并提示（防旧缓存误导跳过步骤/产生错误结果）
        var (cacheStatus, cacheSource) = CheckTempCache(TempRoot);
        // 快捷键等路径触发启动时同样先维护阻断状态：仍不匹配 → 阻断并重新弹窗
        CacheBlocked = cacheStatus == TempCacheStatus.Mismatch;
        if (cacheStatus == TempCacheStatus.Mismatch)
        {
            _logger.Error($"临时目录缓存与当前视频不符（缓存来源：{cacheSource}），请重新选择临时目录或清理缓存后再启动");
            TempCacheMismatchDetected?.Invoke(true); // 点击开始处理触发：累计愤怒警告计数
            return;
        }

        // 记录本次任务指纹到 cache.json（供临时目录缓存检测，防旧缓存误导）
        WriteCacheInfo();

        _cts = new CancellationTokenSource();
        IsProcessing = true;
        IsPaused = false;
        ProgressPercent = 0;
        ProgressDetail = "准备中...";
        Stage = ProcessStage.Idle;
        LatestFramePath = "";
        IsDegraded = false;
        DegradeNotice = "";
        _startTime = DateTime.Now;
        _monitor.Start(250); // 250ms 采样，CPU/GPU 更实时
        ProcessingStarted?.Invoke();

        _logger.Info($"开始处理: {(HasExternalFrames ? $"帧文件夹 {Path.GetFileName(ExternalFramesDir.TrimEnd('\\', '/'))}" : Path.GetFileName(InputVideo))}");
        _logger.Info($"处理参数: 超分 {SrModel} ×{SrScale} / 补帧引擎 {IfEngine} / 补帧模型 {IfModel} ×{IfMultiplier}");

        var ctx = new ProcessingContext
        {
            InputVideo = InputVideo,
            TempRoot = TempRoot.Replace('\\', '/'),
            OutputRoot = OutputRoot.Replace('\\', '/'),
            ExternalFramesDir = ExternalFramesDir.Replace('\\', '/'),
            Video = Video,
            Options = new ProcessingOptions
            {
                SplitFrames = SplitFrames,
                SuperResolution = SuperResolution,
                Interpolation = Interpolation,
                MergeVideo = MergeVideo,
                MergeAudio = MergeAudio,
                SdrToHdr = SdrToHdr && IsHdrEnabled,
                IfEngine = IfEngine
            },
            SrModel = SrModel,
            SrScale = SrScale,
            IfModel = IfModel,
            IfMultiplier = IfMultiplier,
            // HDR 参数（滑块 0-200，NVEncC 最高 200）
            HdrSaturation = Math.Clamp(HdrSaturation, 0, 200),
            HdrContrast = Math.Clamp(HdrContrast, 0, 200),
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
            IsPaused = false;
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

    /// <summary>根据勾选项生成步骤描述文本（顺序与流水线一致：拆帧 → 超分 → 补帧 → 合并 → HDR → 音频）</summary>
    private string BuildStepsText()
    {
        var parts = new List<string>();
        if (SplitFrames) parts.Add("拆帧");
        if (SuperResolution) parts.Add($"超分×{SrScale}");
        if (Interpolation) parts.Add($"补帧×{IfMultiplier}");
        if (MergeVideo) parts.Add("合并");
        if (SdrToHdr) parts.Add("HDR");
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
            // 合并/HDR/音频阶段不产生新帧：自动关闭预览并禁用预览开关，避免预览停在旧画面
            var blockPreview = p.Stage is ProcessStage.Merging or ProcessStage.HdrConverting or ProcessStage.AddingAudio;
            PreviewBlocked = blockPreview;
            if (blockPreview && ShowPreview) ShowPreview = false;
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
        // Offical RIFE 引擎：仅终止本软件捆绑的便携 Python（按运行目录下的 python.exe 过滤，避免误杀用户其他 python）
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("python"))
            {
                try
                {
                    if (p.MainModule?.FileName.StartsWith(Tools.OfficalRifeDir, StringComparison.OrdinalIgnoreCase) == true)
                        p.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>软件内自测：自动选择视频、按掩码勾选功能、跑处理链路，报告写文件后退出。
    /// stages 位掩码: 1=拆帧 2=超分 4=补帧 8=合并 16=音频（默认 15=全流程）。
    /// 由 App 检测 --selftest 参数调用（必须在 UI 线程执行）。</summary>
    public async Task RunSelfTestAsync(string videoPath, string reportPath, int stages = 15)
    {
        try
        {
            _logger.Info($"=== 自测模式启动: {videoPath} (掩码 {stages}) ===");
            SuppressCompletionDialog = true;
            CleanTemp(); // 清理上次残留帧，避免旧帧干扰本次自测
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
        // 自测模式用 Environment.Exit 强制退出：Application.Exit() 会被窗口关闭拦截器
        // （OnAppWindowClosing 先 args.Cancel=true 再异步确认）吞掉，导致进程残留不退出
        var _ = Task.Delay(500).ContinueWith(_ => Environment.Exit(0), TaskScheduler.Default);
    }

    /// <summary>清理临时中间产物（当前 TempRoot）。只删除本软件已知的帧目录/中间文件，
    /// 绝不删除 TempRoot 下的未知内容（防止误删用户输出）。</summary>
    public string CleanTemp() => CleanTempIn(TempRoot);

    /// <summary>按目录清理中间产物（含 cache.json 元信息），白名单制。</summary>
    public string CleanTempIn(string dir)
    {
        // 软件产生的全部中间产物名（不含输出视频）
        string[] tempDirs = { "input_frames", "4k_frames", "output_frames" };
        string[] tempFiles = { "audio.flac", "temp_video.mkv", "hdr_video.mkv", "audio_embedded.mkv", "cache.json" };

        try
        {
            var count = 0;
            if (Directory.Exists(dir))
            {
                foreach (var d in tempDirs)
                {
                    var p = Path.Combine(dir, d);
                    if (Directory.Exists(p))
                    {
                        try { Directory.Delete(p, recursive: true); count++; } catch { }
                    }
                }
                foreach (var f in tempFiles)
                {
                    var p = Path.Combine(dir, f);
                    if (File.Exists(p))
                    {
                        try { File.Delete(p); count++; } catch { }
                    }
                }
            }
            var msg = count > 0
                ? $"已清理 {count} 项中间产物: {dir}"
                : "临时目录中没有待清理的中间产物";
            _logger.Success(msg);
            return msg;
        }
        catch (Exception ex)
        {
            var msg = $"清理临时目录失败: {ex.Message}";
            _logger.Error(msg);
            return msg;
        }
    }

    /// <summary>强制清理临时中间产物（白名单制）：直接整目录递归删除，带重试与只读属性处理。
    /// 清理前先停处理、强杀工具进程、清空预览句柄；大量删除放后台线程不阻塞 UI。</summary>
    public async Task<string> CleanTempInAsync(string dir)
    {
        // 强制删除：先停止处理并强杀可能占用文件的工具进程，再通知 UI 清空预览释放帧文件句柄
        if (IsProcessing) Stop();
        KillToolProcesses();
        CleanRequested?.Invoke();
        _logger.Info($"开始强制清理临时目录: {dir}");
        LogDebug($"CLEAN START dir={dir}");

        string[] tempDirs = { "input_frames", "4k_frames", "output_frames" };
        string[] tempFiles = { "audio.flac", "temp_video.mkv", "hdr_video.mkv", "audio_embedded.mkv", "cache.json" };

        IsCleaning = true;
        CleanProgressPercent = 0;
        try
        {
            // 后台整目录一次性递归删除（比逐文件快得多，删除也彻底）
            var result = await Task.Run(() =>
            {
                var deletedDirs = 0;
                var deletedFiles = 0;
                var failed = new List<string>();

                foreach (var d in tempDirs)
                {
                    var p = Path.Combine(dir, d);
                    if (Directory.Exists(p))
                    {
                        var err = TryDeleteDirectory(p);
                        if (err is null) deletedDirs++;
                        else failed.Add($"[目录] {d}: {err}");
                        LogDebug($"  dir {d}: {(err is null ? "OK" : "FAIL " + err)}");
                    }
                }
                foreach (var f in tempFiles)
                {
                    var p = Path.Combine(dir, f);
                    if (File.Exists(p))
                    {
                        var err = TryDeleteWithRetry(p);
                        if (err is null) deletedFiles++;
                        else failed.Add($"[文件] {f}: {err}");
                        LogDebug($"  file {f}: {(err is null ? "OK" : "FAIL " + err)}");
                    }
                }
                return (deletedDirs, deletedFiles, failed);
            });

            _dispatcherQueue.TryEnqueue(() => CleanProgressPercent = 100);
            var msg = result.failed.Count == 0
                ? $"已强制清理 {result.deletedDirs} 个目录、{result.deletedFiles} 个文件: {dir}"
                : $"已清理 {result.deletedDirs} 个目录、{result.deletedFiles} 个文件，以下项无法删除:";
            _logger.Success(msg);
            foreach (var f in result.failed)
            {
                _logger.Error($"无法删除: {f}");
            }
            LogDebug($"CLEAN DONE dirs={result.deletedDirs} files={result.deletedFiles} failed={result.failed.Count}");
            return msg;
        }
        finally
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                IsCleaning = false;
                CleanProgressPercent = 0;
            });
        }
    }

    /// <summary>诊断日志：追加写入 exe 目录的 DebugINFO.txt，便于定位清理等后台操作的真实结果。</summary>
    private static void LogDebug(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "DebugINFO.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\r\n");
        }
        catch { }
    }

    /// <summary>递归删除整个目录并重试（先清只读属性，句柄短暂占用时等待重试）。
    /// 整目录删除失败时退化为逐文件强制删除兜底。成功返回 null，失败返回具体原因。</summary>
    private static string? TryDeleteDirectory(string dir, int attempts = 5)
    {
        Exception? last = null;
        for (int i = 0; i < attempts; i++)
        {
            try { Directory.Delete(dir, recursive: true); return null; }
            catch (Exception ex)
            {
                last = ex;
                // 清除只读/系统属性后重试（某些工具输出的帧可能带只读属性）
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    }
                }
                catch { }
                if (i < attempts - 1) Thread.Sleep(500);
            }
        }
        // 整目录删除失败 → 逐文件强制删除兜底（哪些能删删哪些，最后再试一次删目录）
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                for (int j = 0; j < 5; j++)
                {
                    try { File.Delete(f); break; }
                    catch { if (j < 4) Thread.Sleep(300); }
                }
            }
            try { Directory.Delete(dir, recursive: true); return null; }
            catch (Exception ex) { last = ex; }
        }
        catch { }
        return last?.Message ?? "未知错误";
    }

    /// <summary>删除文件并重试几次，应对句柄短暂占用（进程刚被杀/预览解码中）。
    /// 成功返回 null，失败返回具体原因。</summary>
    private static string? TryDeleteWithRetry(string f, int attempts = 3)
    {
        Exception? last = null;
        for (int i = 0; i < attempts; i++)
        {
            try { File.Delete(f); return null; }
            catch (Exception ex)
            {
                last = ex;
                if (i < attempts - 1) Thread.Sleep(200);
            }
        }
        return last?.Message ?? "未知错误";
    }

    /// <summary>写入缓存元信息 cache.json：只记录输入视频指纹，供"临时目录缓存检测"识别来源视频。
    /// 不做断点续传；若临时目录里的缓存来自其他视频，选择目录/启动时会提示用户处理。</summary>
    public void WriteCacheInfo()
    {
        try
        {
            // 帧文件夹模式下以帧文件夹路径为指纹（无输入视频文件）
            var fingerprint = HasExternalFrames ? ExternalFramesDir : InputVideo;
            var fi = !string.IsNullOrEmpty(fingerprint) && File.Exists(fingerprint) ? new FileInfo(fingerprint) : null;
            var info = new
            {
                inputVideo = fingerprint,
                videoSize = fi?.Length ?? 0,
                videoModifiedTicks = fi?.LastWriteTimeUtc.Ticks ?? 0L,
                createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            File.WriteAllText(Path.Combine(TempRoot, "cache.json"),
                System.Text.Json.JsonSerializer.Serialize(info,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>暂停当前处理：立即挂起工具进程（可恢复）</summary>
    public void PauseProcessing()
    {
        if (!IsProcessing || IsPaused) return;
        _runner.SuspendCurrent();
        IsPaused = true;
        _logger.Warn("处理已暂停（工具进程已挂起）");
    }

    /// <summary>继续被暂停的处理</summary>
    public void ResumeProcessing()
    {
        if (!IsProcessing || !IsPaused) return;
        _runner.ResumeCurrent();
        IsPaused = false;
        _logger.Info("处理已继续");
    }

    /// <summary>临时目录缓存检测结果</summary>
    public enum TempCacheStatus { None, Match, Mismatch }

    /// <summary>读取目录中的旧缓存来源视频；无 cache.json 返回 null</summary>
    public (TempCacheStatus Status, string SourceVideo) CheckTempCache(string dir)
    {
        var p = Path.Combine(dir, "cache.json");
        if (!File.Exists(p)) return (TempCacheStatus.None, "");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
            if (doc.RootElement.TryGetProperty("inputVideo", out var v))
            {
                var cached = v.GetString() ?? "";
                // 与当前输入（视频文件或帧文件夹）一致 → 匹配（可安全复用该目录），否则 → 不符（需询问）
                var fingerprint = HasExternalFrames ? ExternalFramesDir : InputVideo;
                var match = string.Equals(cached, fingerprint, StringComparison.OrdinalIgnoreCase);
                return (match ? TempCacheStatus.Match : TempCacheStatus.Mismatch, cached);
            }
        }
        catch { }
        return (TempCacheStatus.Mismatch, "");
    }

    /// <summary>接受临时目录并覆盖其旧缓存（清空该目录中间产物后设置）</summary>
    public void AcceptTempRoot(string dir)
    {
        CleanTempIn(dir);
        TempRoot = dir;
        Directory.CreateDirectory(dir);
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

    /// <summary>本地版本号（config 文件 appsettings.json 里的 Version，标题栏显示 v{Version}）</summary>
    public string Version => _app.Version;

    /// <summary>配置文件中保存的主题模式（light/dark/system/acrylic）</summary>
    public string SavedTheme => _app.Theme;

    /// <summary>设置主题模式并立即写入配置文件（下次启动恢复）</summary>
    public void SetThemeMode(string theme)
    {
        _app.Theme = theme;
        _settings.Save(_app, _pathConfig);
    }

    public void SaveSettings()
    {
        _app.DefaultSrModel = SrModel;
        _app.DefaultIfModel = IfModel;
        _app.DefaultIfEngine = IfEngine;
        _app.DefaultSrScale = SrScale;
        _app.DefaultIfMultiplier = IfMultiplier;
        _settings.Save(_app, _pathConfig);
    }
}
