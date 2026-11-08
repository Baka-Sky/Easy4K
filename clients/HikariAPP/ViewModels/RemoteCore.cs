using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Android.Content;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HikariAPP2.Models;
using HikariAPP2.Services;

namespace HikariAPP2.ViewModels;

public enum Phase { Connect, Main, Progress }

public enum BrowsePurpose { InputVideo, OutputDir, SfInput, TempDirPick, TempDirRetry, FramesDir }

public sealed class LogEntry
{
    public string Level { get; init; } = "info";
    public string Text { get; init; } = "";
    public string Display => $"[{Level.ToUpperInvariant()}] {Text}";
}

/// <summary>
/// 远程会话的进程级单例（与旧版 RemoteCore 对应）：
///  · 连接 / 握手 / 安全密钥 / 能力清单
///  · 手机视频上传、电脑文件选择、开始 / 停止 / 暂停
///  · 进度、日志、图片预览、CPU/GPU 占用、成品回传
///  · 单文件超分、临时目录旧缓存、涡轮机械盘检测、断线自动重连
///
/// 所有界面状态变更都在 UI 线程上完成；socket 读写与事件在后台线程，
/// 收到推送后统一 Post 回 UI 线程。
/// </summary>
public sealed class RemoteCore : ObservableObject
{
    public static RemoteCore Instance { get; } = new();

    private readonly Easy4kClient _client = new();
    private readonly Dispatcher _ui = Dispatcher.UIThread;
    private ISharedPreferences? _prefs;

    private bool _manualClose;
    private bool _reconnecting;
    private Task? _heartbeatTask;
    private CancellationTokenSource? _heartbeatCts;
    private long _lastNotifyTick;
    private bool _completionPending;

    private IStorageFile? _phoneFile;
    private string _phoneName = "";
    private long _phoneSize;

    private TaskCompletionSource<string?>? _keyTcs;

    private bool _applyingCaps;
    private BrowsePurpose _browsePurpose;

    private string _lastOutputPath = "";
    private bool _turboWarnedThisSession;

    private RemoteCore()
    {
        BuildChoices();
        BuildCommands();
    }

    // ===================== 生命周期 =====================

    public void Attach(Context context)
    {
        AndroidPlatform.Init(context);
        try
        {
            _prefs = context.GetSharedPreferences("easy4k_remote", FileCreationMode.Private);
            Host = _prefs?.GetString("host", "") ?? "";
            // 加密通道的证书指纹固定：第一次连上记下来，以后不一致就拒绝（防冒充）
            _client.GetPinnedFingerprint = h => _prefs?.GetString("tls_pin_" + h, "") ?? "";
            _client.SavePinnedFingerprint = (h, fp) =>
            {
                try
                {
                    var e = _prefs?.Edit();
                    e?.PutString("tls_pin_" + h, fp);
                    e?.Apply();
                }
                catch { }
            };
            var path = _prefs?.GetString("result_output", "") ?? "";
            if (path.Length > 0)
            {
                _resultOutput = path;
                _resultSourceKey = _prefs?.GetString("result_source_key", "") ?? "";
                _resultOld = true;
            }
        }
        catch { }
    }

    public void OnUiResumed()
    {
        if (!_completionPending) return;
        _completionPending = false;
        // 人已经回到 App 了，完成提醒收起，通知栏保持只有一条
        AndroidPlatform.CancelDone();
    }

    private const string IdleKeepAliveText = "已连接电脑，等待操作（处理完成后会提醒你）";

    // ===================== 页面 =====================

    private Phase _phase = Phase.Connect;
    public Phase Phase
    {
        get => _phase;
        private set
        {
            if (Set(ref _phase, value))
            {
                OnPropertyChanged(nameof(IsConnectPhase));
                OnPropertyChanged(nameof(IsMainPhase));
                OnPropertyChanged(nameof(IsProgressPhase));
            }
        }
    }

    public bool IsConnectPhase => _phase == Phase.Connect;
    public bool IsMainPhase => _phase == Phase.Main;
    public bool IsProgressPhase => _phase == Phase.Progress;

    // ===================== 连接 =====================

    private string _host = "";
    public string Host
    {
        get => _host;
        set
        {
            if (!Set(ref _host, value)) return;
            OnPropertyChanged(nameof(AddressText));
            OnPropertyChanged(nameof(CanConnect));
        }
    }

    public bool CanConnect => _host.Trim().Length > 0 && !_connecting;

    private bool _connecting;
    public bool Connecting
    {
        get => _connecting;
        private set
        {
            if (!Set(ref _connecting, value)) return;
            OnPropertyChanged(nameof(IsConnectFormVisible));
            OnPropertyChanged(nameof(CanConnect));
        }
    }

    private string _connectStep = "";
    public string ConnectStep
    {
        get => _connectStep;
        private set => Set(ref _connectStep, value);
    }

    private string _connectError = "";
    public string ConnectError
    {
        get => _connectError;
        private set
        {
            if (Set(ref _connectError, value)) OnPropertyChanged(nameof(HasConnectError));
        }
    }

    public bool HasConnectError => _connectError.Length > 0;
    public bool IsConnectFormVisible => !_connecting;
    public string AddressText => _host.Length > 0 ? $"{_host}:{Easy4kClient.DefaultPort}" : "";

    private bool _keyPromptVisible;
    public bool KeyPromptVisible
    {
        get => _keyPromptVisible;
        private set => Set(ref _keyPromptVisible, value);
    }

    private string _keyInput = "";
    public string KeyInput
    {
        get => _keyInput;
        set => Set(ref _keyInput, value);
    }

    private string _versionText = "";
    public string VersionText
    {
        get => _versionText;
        private set => Set(ref _versionText, value);
    }

    // ===================== 输入视频 =====================

    private string _inputLabel = "未选择";
    public string InputLabel
    {
        get => _inputLabel;
        private set => Set(ref _inputLabel, value);
    }

    private VideoInfo? _videoInfo;
    public VideoInfo? VideoInfo
    {
        get => _videoInfo;
        private set
        {
            if (Set(ref _videoInfo, value))
            {
                OnPropertyChanged(nameof(HasVideoInfo));
                OnPropertyChanged(nameof(VideoInfoValid));
                OnPropertyChanged(nameof(ViResolution));
                OnPropertyChanged(nameof(ViFpsText));
                OnPropertyChanged(nameof(ViDurationText));
                OnPropertyChanged(nameof(ViFramesText));
                OnPropertyChanged(nameof(ViAudioText));
                OnPropertyChanged(nameof(ViError));
            }
        }
    }

    public bool HasVideoInfo => _videoInfo is not null;
    public bool VideoInfoValid => _videoInfo?.Valid == true;
    public string ViResolution => _videoInfo?.Resolution ?? "";
    public string ViFpsText => _videoInfo is { Fps: > 0 } v ? $"{v.Fps:0.##} fps" : "";
    public string ViDurationText => _videoInfo?.DurationText ?? "";
    public string ViFramesText => _videoInfo is { TotalFrames: > 0 } v ? $"{v.TotalFrames} 帧" : "";
    public string ViAudioText => _videoInfo?.AudioCodec ?? "";
    public string ViError => _videoInfo?.Error ?? "视频信息不可用";

    private string _computerPath = "";
    private string _inputKey = "";
    private string _resultSourceKey = "";

    // ===================== 输出 / 临时目录 =====================

    private string _outputRoot = "";
    public string OutputRoot
    {
        get => _outputRoot;
        private set
        {
            if (Set(ref _outputRoot, value)) OnPropertyChanged(nameof(OutputRootDisplay));
        }
    }

    private string _outputRootChoice = "";
    public string OutputRootDisplay => _outputRootChoice.Length > 0 ? _outputRootChoice
        : _outputRoot.Length > 0 ? _outputRoot : "由电脑端决定";

    private string _outputName = "";
    public string OutputName
    {
        get => _outputName;
        private set => Set(ref _outputName, value);
    }

    private string _tempRoot = "";
    public string TempRoot
    {
        get => _tempRoot;
        private set
        {
            if (Set(ref _tempRoot, value)) OnPropertyChanged(nameof(TempRootDisplay));
        }
    }

    public string TempRootDisplay => _tempRoot.Length > 0 ? _tempRoot : "由电脑端决定";

    private bool _tempCleaning;
    public bool TempCleaning
    {
        get => _tempCleaning;
        private set => Set(ref _tempCleaning, value);
    }

    private string _tempCleanMsg = "";
    public string TempCleanMsg
    {
        get => _tempCleanMsg;
        private set
        {
            if (Set(ref _tempCleanMsg, value))
            {
                OnPropertyChanged(nameof(HasTempCleanMsg));
                OnPropertyChanged(nameof(TempCleanOk));
            }
        }
    }

    public bool HasTempCleanMsg => _tempCleanMsg.Length > 0;
    public bool TempCleanOk => _tempCleanMsg.StartsWith("已清理", StringComparison.Ordinal);

    // ===================== 处理选项 =====================

    private bool _srEnabled = true;
    public bool SrEnabled
    {
        get => _srEnabled;
        set
        {
            if (!Set(ref _srEnabled, value)) return;
            OnPropertyChanged(nameof(StartButtonText));
            OnPropertyChanged(nameof(EstimatedOutputName));
        }
    }

    private bool _ifEnabled = true;
    public bool IfEnabled
    {
        get => _ifEnabled;
        set
        {
            if (!Set(ref _ifEnabled, value)) return;
            OnPropertyChanged(nameof(EstimatedOutputName));
            if (!_applyingCaps) _ = PushIfConfigAsync();
        }
    }

    private bool _mergeVideo = true;
    public bool MergeVideo { get => _mergeVideo; set => Set(ref _mergeVideo, value); }

    private bool _mergeAudio = true;
    public bool MergeAudio { get => _mergeAudio; set => Set(ref _mergeAudio, value); }

    private bool _splitFrames = true;
    public bool SplitFrames { get => _splitFrames; set => Set(ref _splitFrames, value); }

    private bool _sdrToHdr;
    public bool SdrToHdr { get => _sdrToHdr; set => Set(ref _sdrToHdr, value); }

    private bool _hdrEnabled;
    public bool HdrEnabled { get => _hdrEnabled; private set => Set(ref _hdrEnabled, value); }

    private bool _dedup;
    public bool Dedup
    {
        get => _dedup;
        set
        {
            if (Set(ref _dedup, value)) OnPropertyChanged(nameof(DedupModeVisible));
        }
    }

    public bool DedupModeVisible => _dedup;

    private bool _keepSubtitles;
    public bool KeepSubtitles { get => _keepSubtitles; set => Set(ref _keepSubtitles, value); }

    private bool _hasSubtitles;
    public bool HasSubtitles
    {
        get => _hasSubtitles;
        private set => Set(ref _hasSubtitles, value);
    }

    private bool _audioSr;
    public bool AudioSr
    {
        get => _audioSr;
        set
        {
            if (!Set(ref _audioSr, value)) return;
            OnPropertyChanged(nameof(AudioPrecisionVisible));
            if (_applyingCaps) return;
            AddLog("info", value ? "已开启音频超分（AudioSR 48kHz）" : "已关闭音频超分（AudioSR 48kHz）");
            PushConfigQuietly();
        }
    }

    public bool AudioPrecisionVisible => _audioSr;

    private bool _turboMode;
    public bool TurboMode
    {
        get => _turboMode;
        set
        {
            if (_turboMode == value) return;
            if (!value)
            {
                Set(ref _turboMode, false, nameof(TurboMode));
                _ = PushTurboConfigAsync(false);
            }
            else
            {
                _ = EvaluateTurboOnAsync();
            }
        }
    }

    private void SetTurboState(bool value)
    {
        if (_turboMode == value) return;
        _turboMode = value;
        OnPropertyChanged(nameof(TurboMode));
    }

    // ===================== 高级：编码质量 =====================

    private string _crfText = "18";
    public string CrfText
    {
        get => _crfText;
        set
        {
            var digits = new string((value ?? "").Where(char.IsDigit).Take(2).ToArray());
            if (Set(ref _crfText, digits)) { }
        }
    }

    private int Crf => int.TryParse(_crfText, out var v) ? Math.Clamp(v, 0, 51) : 18;

    private string _hwQualityText = "18";
    public string HwQualityText
    {
        get => _hwQualityText;
        set
        {
            var digits = new string((value ?? "").Where(char.IsDigit).Take(2).ToArray());
            Set(ref _hwQualityText, digits);
        }
    }

    private int HwQuality => int.TryParse(_hwQualityText, out var v) ? Math.Clamp(v, 0, 51) : 18;

    private string _hwQualityMode = "";
    public string HwQualityMode
    {
        get => _hwQualityMode;
        private set => Set(ref _hwQualityMode, value);
    }

    private string _dedupMode = "performance";
    public string DedupMode
    {
        get => _dedupMode;
        private set => Set(ref _dedupMode, value);
    }

    private string _audioSrPrecision = "fp16";
    public string AudioSrPrecision
    {
        get => _audioSrPrecision;
        private set => Set(ref _audioSrPrecision, value);
    }

    public ObservableCollection<ChoiceItem> HwModeChoices { get; } = new();
    public ObservableCollection<ChoiceItem> DedupModeChoices { get; } = new();
    public ObservableCollection<ChoiceItem> AudioPrecisionChoices { get; } = new();

    // ===================== 高级页：与桌面端 AdvancedPage 对齐的可选项 =====================

    private bool _sceneGuard;
    public bool SceneGuard
    {
        get => _sceneGuard;
        set { if (Set(ref _sceneGuard, value) && !_applyingCaps) PushConfigQuietly(); }
    }

    public ObservableCollection<ChoiceItem> TurboSegmentChoices { get; } = new();

    private int _turboSegments = 2;
    public int TurboSegments
    {
        get => _turboSegments;
        private set
        {
            if (!Set(ref _turboSegments, value)) return;
            BuildTurboSegmentChoices();
        }
    }

    private string _srDecodeText = "1";
    public string SrDecodeText
    {
        get => _srDecodeText;
        set { if (Set(ref _srDecodeText, FilterInt(value, 2)) && !_applyingCaps) PushConfigQuietly(); }
    }

    private string _srEncodeText = "2";
    public string SrEncodeText
    {
        get => _srEncodeText;
        set { if (Set(ref _srEncodeText, FilterInt(value, 2)) && !_applyingCaps) PushConfigQuietly(); }
    }

    private string _ifDecodeText = "1";
    public string IfDecodeText
    {
        get => _ifDecodeText;
        set { if (Set(ref _ifDecodeText, FilterInt(value, 2)) && !_applyingCaps) PushConfigQuietly(); }
    }

    private string _ifEncodeText = "2";
    public string IfEncodeText
    {
        get => _ifEncodeText;
        set { if (Set(ref _ifEncodeText, FilterInt(value, 2)) && !_applyingCaps) PushConfigQuietly(); }
    }

    private static string FilterInt(string? value, int digits)
        => new string((value ?? "").Where(char.IsDigit).Take(digits).ToArray());

    private int IntOf(string text, int fallback)
        => int.TryParse(text, out var v) ? Math.Clamp(v, 1, 32) : fallback;

    private List<string> _pipelineGpuOptions = new();
    public List<string> PipelineGpuOptions
    {
        get => _pipelineGpuOptions;
        private set => Set(ref _pipelineGpuOptions, value);
    }

    private int _pipelineGpuIndex;
    public int PipelineGpuIndex
    {
        get => _pipelineGpuIndex;
        set { if (Set(ref _pipelineGpuIndex, Math.Max(0, value)) && !_applyingCaps) PushConfigQuietly(); }
    }

    public bool HdrAiPath => _hdrAiPath;
    public bool HdrNvPath => !_hdrAiPath;

    private bool _hdrAiPath;
    private bool HdrAiPathSet
    {
        set
        {
            if (_hdrAiPath == value) return;
            _hdrAiPath = value;
            OnPropertyChanged(nameof(HdrAiPath));
            OnPropertyChanged(nameof(HdrNvPath));
        }
    }

    private string _hdrSaturationText = "200";
    public string HdrSaturationText
    {
        get => _hdrSaturationText;
        set { if (Set(ref _hdrSaturationText, FilterInt(value, 3)) && !_applyingCaps) PushConfigQuietly(); }
    }

    private string _hdrContrastText = "200";
    public string HdrContrastText
    {
        get => _hdrContrastText;
        set { if (Set(ref _hdrContrastText, FilterInt(value, 3)) && !_applyingCaps) PushConfigQuietly(); }
    }

    private string _hdrAiStrengthText = "100";
    public string HdrAiStrengthText
    {
        get => _hdrAiStrengthText;
        set { if (Set(ref _hdrAiStrengthText, FilterInt(value, 3)) && !_applyingCaps) PushConfigQuietly(); }
    }

    private bool _skipStartupSelfTest;
    public bool SkipStartupSelfTest
    {
        get => _skipStartupSelfTest;
        set { if (Set(ref _skipStartupSelfTest, value) && !_applyingCaps) PushConfigQuietly(); }
    }

    private bool _useSafeFrameRate;
    public bool UseSafeFrameRate
    {
        get => _useSafeFrameRate;
        set { if (Set(ref _useSafeFrameRate, value) && !_applyingCaps) PushConfigQuietly(); }
    }

    private bool _lowerQualityForVram;
    public bool LowerQualityForVram
    {
        get => _lowerQualityForVram;
        set { if (Set(ref _lowerQualityForVram, value) && !_applyingCaps) PushConfigQuietly(); }
    }

    private bool _useGpuAcceleration = true;
    public bool UseGpuAcceleration
    {
        get => _useGpuAcceleration;
        set { if (Set(ref _useGpuAcceleration, value) && !_applyingCaps) PushConfigQuietly(); }
    }

    private bool _useCpuProcessing;
    public bool UseCpuProcessing
    {
        get => _useCpuProcessing;
        set { if (Set(ref _useCpuProcessing, value) && !_applyingCaps) PushConfigQuietly(); }
    }

    private bool _tryOldFfmpegOnCrash;
    public bool TryOldFfmpegOnCrash
    {
        get => _tryOldFfmpegOnCrash;
        set { if (Set(ref _tryOldFfmpegOnCrash, value) && !_applyingCaps) PushConfigQuietly(); }
    }

    private bool _noReport;
    public bool NoReport
    {
        get => _noReport;
        set { if (Set(ref _noReport, value) && !_applyingCaps) PushConfigQuietly(); }
    }

    private bool _reportAutoOpen = true;
    public bool ReportAutoOpen
    {
        get => _reportAutoOpen;
        set { if (Set(ref _reportAutoOpen, value) && !_applyingCaps) PushConfigQuietly(); }
    }

    private string _reportDir = "Reports";
    public string ReportDir
    {
        get => _reportDir;
        set { if (Set(ref _reportDir, value ?? "") && !_applyingCaps) PushConfigQuietly(); }
    }

    private bool _preventAutoSleep;
    public bool PreventAutoSleep
    {
        get => _preventAutoSleep;
        set { if (Set(ref _preventAutoSleep, value) && !_applyingCaps) PushConfigQuietly(); }
    }

    // ===================== 帧文件夹 / 手动帧参数 =====================

    private string _externalFramesDir = "";
    public string ExternalFramesDir
    {
        get => _externalFramesDir;
        private set
        {
            if (!Set(ref _externalFramesDir, value)) return;
            OnPropertyChanged(nameof(FramesDirDisplay));
            OnPropertyChanged(nameof(HasExternalFramesDir));
            OnPropertyChanged(nameof(ManualInfoVisible));
        }
    }

    public string FramesDirDisplay => _externalFramesDir.Length > 0 ? _externalFramesDir : "未导入（默认先拆帧）";
    public bool HasExternalFramesDir => _externalFramesDir.Length > 0;
    public bool ManualInfoVisible => _externalFramesDir.Length > 0;

    private string _manualWidthText = "";
    public string ManualWidthText
    {
        get => _manualWidthText;
        set => Set(ref _manualWidthText, FilterInt(value, 5));
    }

    private string _manualHeightText = "";
    public string ManualHeightText
    {
        get => _manualHeightText;
        set => Set(ref _manualHeightText, FilterInt(value, 5));
    }

    private string _manualTotalFramesText = "";
    public string ManualTotalFramesText
    {
        get => _manualTotalFramesText;
        set => Set(ref _manualTotalFramesText, FilterInt(value, 9));
    }

    private string _manualFpsText = "";
    public string ManualFpsText
    {
        get => _manualFpsText;
        set
        {
            var filtered = new string((value ?? "").Where(c => char.IsDigit(c) || c == '.').Take(8).ToArray());
            Set(ref _manualFpsText, filtered);
        }
    }

    // ===================== 超分 =====================

    private List<SrFamily> _srFamilies = new();
    public List<SrFamily> SrFamilies
    {
        get => _srFamilies;
        private set => Set(ref _srFamilies, value);
    }

    private SrFamily? _selectedSrFamily;
    public SrFamily? SelectedSrFamily
    {
        get => _selectedSrFamily;
        set
        {
            if (!Set(ref _selectedSrFamily, value) || value is null) return;
            if (_applyingCaps) return;
            RebuildSrScales(value, null);
        }
    }

    public ObservableCollection<ChoiceItem> SrScaleChoices { get; } = new();

    private int _srScale = 2;
    public int SrScale
    {
        get => _srScale;
        private set
        {
            if (!Set(ref _srScale, value)) return;
            if (!_applyingCaps) RebuildSrModels(_selectedSrFamily, value, null);
            OnPropertyChanged(nameof(EstimatedOutputName));
        }
    }

    public ObservableCollection<string> SrModelChoices { get; } = new();

    private string _srModel = "";
    public string SrModel
    {
        get => _srModel;
        set
        {
            if (!Set(ref _srModel, value ?? "")) return;
            if (!_applyingCaps) UpdateSrSceneHint();
        }
    }

    private string _srSceneHint = "";
    public string SrSceneHint
    {
        get => _srSceneHint;
        private set
        {
            if (Set(ref _srSceneHint, value)) OnPropertyChanged(nameof(HasSrSceneHint));
        }
    }

    public bool HasSrSceneHint => _srSceneHint.Length > 0;

    // ===================== 补帧 =====================

    private List<VfiFamily> _vfiFamilies = new();
    public List<VfiFamily> UsableVfiFamilies
    {
        get => _vfiFamilies;
        private set => Set(ref _vfiFamilies, value);
    }

    private VfiFamily? _selectedVfiFamily;
    public VfiFamily? SelectedVfiFamily
    {
        get => _selectedVfiFamily;
        set
        {
            if (!Set(ref _selectedVfiFamily, value) || value is null) return;
            if (_applyingCaps) return;
            RebuildVfiModels(value, null);
            _ = PushIfConfigAsync();
        }
    }

    public ObservableCollection<string> VfiModelChoices { get; } = new();

    private string _ifModel = "";
    public string IfModel
    {
        get => _ifModel;
        set
        {
            if (!Set(ref _ifModel, value ?? "")) return;
            if (!_applyingCaps) _ = PushIfConfigAsync();
            UpdateVfiSceneHint();
        }
    }

    public ObservableCollection<ChoiceItem> IfMultiplierChoices { get; } = new();

    private int _ifMultiplier = 2;
    public int IfMultiplier
    {
        get => _ifMultiplier;
        private set
        {
            if (!Set(ref _ifMultiplier, value)) return;
            OnPropertyChanged(nameof(EstimatedOutputName));
        }
    }

    private bool _ifMultiplierLocked;
    public bool IfMultiplierLocked
    {
        get => _ifMultiplierLocked;
        private set
        {
            if (Set(ref _ifMultiplierLocked, value)) OnPropertyChanged(nameof(TargetFpsEnabled));
        }
    }

    public bool TargetFpsEnabled => !_ifMultiplierLocked;

    private string _targetFpsText = "";
    public string TargetFpsText
    {
        get => _targetFpsText;
        set
        {
            var filtered = new string((value ?? "").Where(c => char.IsDigit(c) || c == '.').Take(8).ToArray());
            Set(ref _targetFpsText, filtered);
        }
    }

    private double TargetFps => double.TryParse(_targetFpsText, out var v) && v > 0 ? v : 0;

    private string _vfiSceneHint = "";
    public string VfiSceneHint
    {
        get => _vfiSceneHint;
        private set
        {
            if (Set(ref _vfiSceneHint, value)) OnPropertyChanged(nameof(HasVfiSceneHint));
        }
    }

    public bool HasVfiSceneHint => _vfiSceneHint.Length > 0;

    private string _nonNvidiaHint = "";
    public string NonNvidiaHint
    {
        get => _nonNvidiaHint;
        private set
        {
            if (Set(ref _nonNvidiaHint, value)) OnPropertyChanged(nameof(HasNonNvidiaHint));
        }
    }

    public bool HasNonNvidiaHint => _nonNvidiaHint.Length > 0;

    // ===================== 单文件超分 =====================

    private int _sfKindIndex;
    public int SfKindIndex
    {
        get => _sfKindIndex;
        private set
        {
            if (!Set(ref _sfKindIndex, value)) return;
            OnPropertyChanged(nameof(IsSfImage));
            OnPropertyChanged(nameof(IsSfAudio));
            OnPropertyChanged(nameof(IsSfVideoAudio));
            OnPropertyChanged(nameof(IsSfVideoHdr));
            OnPropertyChanged(nameof(SfPrecisionVisible));
            OnPropertyChanged(nameof(SfHdrHintVisible));
            OnPropertyChanged(nameof(SfStartButtonText));
        }
    }

    public bool IsSfImage => _sfKindIndex == 0;
    public bool IsSfAudio => _sfKindIndex == 1;
    public bool IsSfVideoAudio => _sfKindIndex == 2;
    public bool IsSfVideoHdr => _sfKindIndex == 3;

    /// <summary>音频精度只对「音频」与「视频音频超分」有意义</summary>
    public bool SfPrecisionVisible => _sfKindIndex is 1 or 2;
    public bool SfHdrHintVisible => _sfKindIndex == 3;
    public string SfStartButtonText => _sfKindIndex switch
    {
        3 => "开始 HDR 转换",
        2 => "开始音频超分",
        _ => "开始单文件超分"
    };

    private bool _hdrAvailable;
    public bool HdrAvailable
    {
        get => _hdrAvailable;
        private set
        {
            if (!Set(ref _hdrAvailable, value)) return;
            OnPropertyChanged(nameof(SfCanStart));
            OnPropertyChanged(nameof(SfStartButtonEnabled));
        }
    }

    private string _sfInput = "";
    public string SfInput
    {
        get => _sfInput;
        private set
        {
            if (!Set(ref _sfInput, value)) return;
            OnPropertyChanged(nameof(SfInputDisplay));
            OnPropertyChanged(nameof(SfCanStart));
            OnPropertyChanged(nameof(SfStartButtonEnabled));
        }
    }

    public string SfInputDisplay => _sfInput.Length > 0 ? _sfInput : "未选择";

    public ObservableCollection<ChoiceItem> SfScaleChoices { get; } = new();

    private int _sfImageScale = 2;
    public int SfImageScale
    {
        get => _sfImageScale;
        private set => Set(ref _sfImageScale, value);
    }

    public ObservableCollection<string> SfImageModels { get; } = new();

    private string _sfImageModel = "";
    public string SfImageModel
    {
        get => _sfImageModel;
        set
        {
            if (Set(ref _sfImageModel, value ?? "") && !_applyingCaps) PushSfConfig();
        }
    }

    private string _sfAudioPrecision = "fp16";
    public string SfAudioPrecision
    {
        get => _sfAudioPrecision;
        private set => Set(ref _sfAudioPrecision, value);
    }

    public ObservableCollection<ChoiceItem> SfPrecisionChoices { get; } = new();

    private bool _sfRunning;
    public bool SfRunning
    {
        get => _sfRunning;
        private set
        {
            if (Set(ref _sfRunning, value)) OnPropertyChanged(nameof(SfStartButtonEnabled));
        }
    }

    private string _sfStatus = "";
    public string SfStatus
    {
        get => _sfStatus;
        private set
        {
            if (Set(ref _sfStatus, value)) OnPropertyChanged(nameof(HasSfStatus));
        }
    }

    public bool HasSfStatus => _sfStatus.Length > 0;
    public bool SfCanStart => _sfInput.Length > 0 && !AnyBusy && !_sfRunning && (!IsSfVideoHdr || _hdrAvailable);
    public bool SfStartButtonEnabled => SfCanStart;

    // ===================== 执行 / 结果 =====================

    private bool _processing;
    public bool Processing
    {
        get => _processing;
        private set
        {
            if (Set(ref _processing, value)) NotifyBusyChanged();
        }
    }

    private bool _paused;
    public bool Paused
    {
        get => _paused;
        private set
        {
            if (Set(ref _paused, value)) OnPropertyChanged(nameof(PauseButtonText));
        }
    }

    public string PauseButtonText => _paused ? "继续" : "暂停";

    private bool _uploading;
    public bool Uploading
    {
        get => _uploading;
        private set
        {
            if (Set(ref _uploading, value)) NotifyBusyChanged();
        }
    }

    private int _uploadPercent;
    public int UploadPercent
    {
        get => _uploadPercent;
        private set => Set(ref _uploadPercent, value);
    }

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        private set
        {
            if (Set(ref _progressPercent, value))
            {
                OnPropertyChanged(nameof(ProgressPercentText));
                OnPropertyChanged(nameof(ProgressFraction));
            }
        }
    }

    public string ProgressPercentText => $"{_progressPercent:0.0}%";
    public double ProgressFraction => Math.Clamp(_progressPercent / 100.0, 0, 1);

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    private string _resultText = "";
    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (Set(ref _resultText, value)) OnPropertyChanged(nameof(HasResultText));
        }
    }

    public bool HasResultText => _resultText.Length > 0;

    private bool _resultOk;
    public bool ResultOk
    {
        get => _resultOk;
        private set
        {
            if (Set(ref _resultOk, value)) OnPropertyChanged(nameof(ResultTitle));
        }
    }

    public string ResultTitle => _resultOk ? "处理完成" : "已结束";
    public string StreamGateSubtitle => _isLan ? "已关闭 · 点击开启" : "外网连接下已关闭";

    private string _resultOutput = "";
    public string ResultOutput
    {
        get => _resultOutput;
        private set
        {
            if (Set(ref _resultOutput, value))
            {
                OnPropertyChanged(nameof(ShowResultCard));
                OnPropertyChanged(nameof(ResultCardTitle));
            }
        }
    }

    private bool _resultOld;
    public bool ResultOld
    {
        get => _resultOld;
        private set
        {
            if (Set(ref _resultOld, value)) OnPropertyChanged(nameof(ResultCardTitle));
        }
    }

    public string ResultCardTitle => _resultOld ? "把成品下载到手机（旧成品）" : "把成品下载到手机";

    public bool ShowResultCard =>
        _resultOutput.Length > 0 && (_inputKey.Length == 0 || _inputKey == _resultSourceKey);

    private bool _downloading;
    public bool Downloading
    {
        get => _downloading;
        private set => Set(ref _downloading, value);
    }

    private int _downloadPercent;
    public int DownloadPercent
    {
        get => _downloadPercent;
        private set
        {
            if (Set(ref _downloadPercent, value)) OnPropertyChanged(nameof(DownloadFraction));
        }
    }

    public double DownloadFraction => Math.Clamp(_downloadPercent / 100.0, 0, 1);

    private string _downloadMessage = "";
    public string DownloadMessage
    {
        get => _downloadMessage;
        private set
        {
            if (Set(ref _downloadMessage, value)) OnPropertyChanged(nameof(HasDownloadMessage));
        }
    }

    public bool HasDownloadMessage => _downloadMessage.Length > 0;

    private bool _downloadOk;
    public bool DownloadOk
    {
        get => _downloadOk;
        private set => Set(ref _downloadOk, value);
    }

    public bool Busy => _processing || _uploading;
    public bool AnyBusy => Busy || _sfRunning;
    public bool CanStart => !AnyBusy && (_phoneFile is not null || _computerPath.Length > 0);
    public string StartButtonText => _phoneFile is not null ? "上传并开始处理" : "开始处理";
    public string EstimatedOutputName
    {
        get
        {
            var baseName = _computerPath.Length > 0
                ? Path.GetFileNameWithoutExtension(_computerPath)
                : _phoneName.Length > 0 ? Path.GetFileNameWithoutExtension(_phoneName) : "output";
            var parts = new List<string>();
            if (_srEnabled) parts.Add($"{_srScale}x");
            if (_ifEnabled) parts.Add($"{_ifMultiplier}fps");
            return parts.Count == 0 ? "—" : $"{baseName}_{string.Join('_', parts)}.mp4";
        }
    }

    private void NotifyBusyChanged()
    {
        OnPropertyChanged(nameof(Busy));
        OnPropertyChanged(nameof(AnyBusy));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(SfCanStart));
        OnPropertyChanged(nameof(SfStartButtonEnabled));
        OnPropertyChanged(nameof(StartButtonText));
    }

    // ===================== 进度页推流 =====================

    private bool _isLan = true;
    public bool IsLan
    {
        get => _isLan;
        private set
        {
            if (!Set(ref _isLan, value)) return;
            OnPropertyChanged(nameof(ProgressSubtitle));
            OnPropertyChanged(nameof(StreamGateSubtitle));
        }
    }

    private bool _previewEnabled;
    public bool PreviewEnabled
    {
        get => _previewEnabled;
        private set
        {
            if (Set(ref _previewEnabled, value)) OnPropertyChanged(nameof(ProgressSubtitle));
        }
    }

    private bool _statsEnabled;
    public bool StatsEnabled
    {
        get => _statsEnabled;
        private set
        {
            if (Set(ref _statsEnabled, value)) OnPropertyChanged(nameof(ProgressSubtitle));
        }
    }

    public string ProgressSubtitle =>
        $"{(_isLan ? "内网连接" : "外网连接")} · {(_previewEnabled ? "图片预览已开" : "图片预览已关")} · {(_statsEnabled ? "占用已开" : "占用已关")}";

    private Bitmap? _previewImage;
    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (Set(ref _previewImage, value)) OnPropertyChanged(nameof(HasPreviewImage));
        }
    }

    public bool HasPreviewImage => _previewImage is not null;

    private double _cpu;
    public double Cpu
    {
        get => _cpu;
        private set
        {
            if (Set(ref _cpu, value)) OnPropertyChanged(nameof(CpuText));
        }
    }

    public string CpuText => $"{_cpu:0}%";

    private double _gpu;
    public double Gpu
    {
        get => _gpu;
        private set
        {
            if (Set(ref _gpu, value)) OnPropertyChanged(nameof(GpuText));
        }
    }

    public string GpuText => $"{_gpu:0}%";

    public double CpuFraction => Math.Clamp(_cpu / 100, 0, 1);
    public double GpuFraction => Math.Clamp(_gpu / 100, 0, 1);

    public ObservableCollection<LogEntry> Logs { get; } = new();

    public bool HasLogs => Logs.Count > 0;

    private void AddLog(string level, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        // logcat 立即输出，界面集合则合并刷新：
        // 流水线一秒能推几十条日志，逐条 Post 到 UI 线程会把主线程压到 ANR（完成瞬间尤其明显）。
        try { Android.Util.Log.Info("Hikari", $"[{level}] {text}"); } catch { }
        _logQueue.Enqueue(new LogEntry { Level = level, Text = text });
        if (System.Threading.Interlocked.Exchange(ref _logFlushScheduled, 1) == 1) return;
        _ = FlushLogsSoonAsync();
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<LogEntry> _logQueue = new();
    private int _logFlushScheduled;

    /// <summary>合并刷新：180ms 内到达的日志一次性加进集合，避免 UI 线程被日志刷爆。</summary>
    private async Task FlushLogsSoonAsync()
    {
        try { await Task.Delay(180); } catch { }
        _ui.Post(() =>
        {
            System.Threading.Interlocked.Exchange(ref _logFlushScheduled, 0);
            var added = false;
            while (_logQueue.TryDequeue(out var e)) { Logs.Add(e); added = true; }
            if (!added) return;
            while (Logs.Count > 300) Logs.RemoveAt(0);
            OnPropertyChanged(nameof(HasLogs));
        });
    }

    // ===================== 对话框状态 =====================

    private bool _turboWarnVisible;
    public bool TurboWarnVisible
    {
        get => _turboWarnVisible;
        private set => Set(ref _turboWarnVisible, value);
    }

    private string _turboWarnText = "";
    public string TurboWarnText
    {
        get => _turboWarnText;
        private set => Set(ref _turboWarnText, value);
    }

    private bool _tempCacheVisible;
    public bool TempCacheVisible
    {
        get => _tempCacheVisible;
        private set => Set(ref _tempCacheVisible, value);
    }

    private string _tempCacheSource = "";
    public string TempCacheSource
    {
        get => _tempCacheSource;
        private set
        {
            if (Set(ref _tempCacheSource, value)) OnPropertyChanged(nameof(TempCacheMessage));
        }
    }

    private string _tempCachePath = "";
    public string TempCachePath
    {
        get => _tempCachePath;
        private set
        {
            if (Set(ref _tempCachePath, value)) OnPropertyChanged(nameof(TempCacheMessage));
        }
    }

    public string TempCacheMessage =>
        $"电脑上的临时目录里有别的视频留下的旧缓存（来自：{_tempCacheSource}），" +
        "与这次要处理的视频不符，直接处理会被旧缓存误导而中断。\n\n" +
        $"临时目录：{_tempCachePath}\n\n" +
        "请选择更换临时目录，或先删掉里面的旧缓存。";

    private int _cleanStep;
    public int CleanStep
    {
        get => _cleanStep;
        private set
        {
            if (!Set(ref _cleanStep, value)) return;
            OnPropertyChanged(nameof(CleanConfirmVisible));
            OnPropertyChanged(nameof(CleanConfirmTitle));
            OnPropertyChanged(nameof(CleanConfirmText));
            OnPropertyChanged(nameof(CleanConfirmButtonText));
        }
    }

    public bool CleanConfirmVisible => _cleanStep > 0;
    public string CleanConfirmTitle => _cleanStep == 1 ? "清理临时文件" : "再次确认";
    public string CleanConfirmText => _cleanStep == 1
        ? "确定要删除临时目录中的帧和中间产物吗？删除后无法恢复。"
        : "再次确认：即将删除临时目录中的全部中间产物（帧、音频、视频缓存），此操作不可撤销。";
    public string CleanConfirmButtonText => _cleanStep == 1 ? "继续" : "确认清理";

    private bool _cleanRetry;

    private bool _riskVisible;
    public bool RiskVisible
    {
        get => _riskVisible;
        private set => Set(ref _riskVisible, value);
    }

    private bool _riskForPreview = true;
    public bool RiskForPreview
    {
        get => _riskForPreview;
        private set
        {
            if (Set(ref _riskForPreview, value)) OnPropertyChanged(nameof(RiskTitle));
        }
    }

    public string RiskTitle => _riskForPreview ? "外网开启图片预览的风险" : "外网开启占用统计的风险";

    // ===================== 电脑目录浏览器 =====================

    private bool _browseVisible;
    public bool BrowseVisible
    {
        get => _browseVisible;
        private set => Set(ref _browseVisible, value);
    }

    private string _browseTitle = "";
    public string BrowseTitle
    {
        get => _browseTitle;
        private set => Set(ref _browseTitle, value);
    }

    private bool _browseFolderMode;
    public bool BrowseFolderMode
    {
        get => _browseFolderMode;
        private set
        {
            if (!Set(ref _browseFolderMode, value)) return;
            OnPropertyChanged(nameof(BrowseCanPickCurrent));
        }
    }

    private string _browseCurrentPath = "";
    public string BrowseCurrentPath
    {
        get => _browseCurrentPath;
        private set
        {
            if (!Set(ref _browseCurrentPath, value)) return;
            OnPropertyChanged(nameof(BrowseHeader));
            OnPropertyChanged(nameof(BrowseCanPickCurrent));
            OnPropertyChanged(nameof(BrowseHasParent));
        }
    }

    public string BrowseHeader => _browseCurrentPath.Length > 0 ? _browseCurrentPath : "此电脑";
    public bool BrowseCanPickCurrent => _browseFolderMode && _browseCurrentPath.Length > 0;
    public bool BrowseHasParent => _browseCurrentPath.Length > 0;

    private bool _browseLoading;
    public bool BrowseLoading
    {
        get => _browseLoading;
        private set => Set(ref _browseLoading, value);
    }

    private string _browseError = "";
    public string BrowseError
    {
        get => _browseError;
        private set
        {
            if (Set(ref _browseError, value)) OnPropertyChanged(nameof(HasBrowseError));
        }
    }

    public bool HasBrowseError => _browseError.Length > 0;

    public ObservableCollection<DirEntry> BrowseEntries { get; } = new();

    private bool _newFolderVisible;
    public bool NewFolderVisible
    {
        get => _newFolderVisible;
        private set => Set(ref _newFolderVisible, value);
    }

    private string _newFolderName = "";
    public string NewFolderName
    {
        get => _newFolderName;
        set => Set(ref _newFolderName, value);
    }

    private string _newFolderError = "";
    public string NewFolderError
    {
        get => _newFolderError;
        private set
        {
            if (Set(ref _newFolderError, value)) OnPropertyChanged(nameof(HasNewFolderError));
        }
    }

    public bool HasNewFolderError => _newFolderError.Length > 0;

    // ===================== 命令 =====================

    public ICommand ConnectCommand { get; private set; } = null!;
    public ICommand SubmitKeyCommand { get; private set; } = null!;
    public ICommand CancelKeyCommand { get; private set; } = null!;
    public ICommand DisconnectCommand { get; private set; } = null!;
    public ICommand PickPhoneVideoCommand { get; private set; } = null!;
    public ICommand PickComputerVideoCommand { get; private set; } = null!;
    public ICommand PickOutputDirCommand { get; private set; } = null!;
    public ICommand PickTempDirCommand { get; private set; } = null!;
    public ICommand PickFramesDirCommand { get; private set; } = null!;
    public ICommand ClearFramesDirCommand { get; private set; } = null!;
    public ICommand CleanTempCommand { get; private set; } = null!;
    public ICommand StartCommand { get; private set; } = null!;
    public ICommand StopCommand { get; private set; } = null!;
    public ICommand PauseResumeCommand { get; private set; } = null!;
    public ICommand EnterProgressCommand { get; private set; } = null!;
    public ICommand LeaveProgressCommand { get; private set; } = null!;
    public ICommand DownloadResultCommand { get; private set; } = null!;
    public ICommand EnablePreviewCommand { get; private set; } = null!;
    public ICommand DisablePreviewCommand { get; private set; } = null!;
    public ICommand EnableStatsCommand { get; private set; } = null!;
    public ICommand DisableStatsCommand { get; private set; } = null!;
    public ICommand SfBrowseCommand { get; private set; } = null!;
    public ICommand SfStartCommand { get; private set; } = null!;
    public ICommand SfStopCommand { get; private set; } = null!;
    public ICommand DismissTurboWarnCommand { get; private set; } = null!;
    public ICommand SwitchDiskCommand { get; private set; } = null!;
    public ICommand TempCacheCleanCommand { get; private set; } = null!;
    public ICommand TempCacheChangeDirCommand { get; private set; } = null!;
    public ICommand TempCacheCancelCommand { get; private set; } = null!;
    public ICommand CleanConfirmCommand { get; private set; } = null!;
    public ICommand CleanCancelCommand { get; private set; } = null!;
    public ICommand RiskConfirmCommand { get; private set; } = null!;
    public ICommand RiskCancelCommand { get; private set; } = null!;
    public ICommand BrowseCancelCommand { get; private set; } = null!;
    public ICommand BrowseUpCommand { get; private set; } = null!;
    public ICommand BrowseEntryCommand { get; private set; } = null!;
    public ICommand BrowsePickCurrentCommand { get; private set; } = null!;
    public ICommand BrowseNewFolderCommand { get; private set; } = null!;
    public ICommand NewFolderCancelCommand { get; private set; } = null!;
    public ICommand NewFolderCreateCommand { get; private set; } = null!;

    /// <summary>手机视频选择器（由界面层用 TopLevel.StorageProvider 注入）。</summary>
    public Func<Task<IStorageFile?>>? PickVideoFile { get; set; }

    private void BuildCommands()
    {
        ConnectCommand = new RelayCommand(ConnectFromInput);
        SubmitKeyCommand = new RelayCommand(() => CompleteKey(_keyInput));
        CancelKeyCommand = new RelayCommand(() => CompleteKey(null));
        DisconnectCommand = new RelayCommand(Disconnect);
        PickPhoneVideoCommand = new RelayCommand(PickPhoneVideoAsync);
        PickComputerVideoCommand = new RelayCommand(() => OpenBrowser(BrowsePurpose.InputVideo, "选择电脑上的视频", "video", false));
        PickOutputDirCommand = new RelayCommand(() => OpenBrowser(BrowsePurpose.OutputDir, "选择输出目录", "video", true));
        PickTempDirCommand = new RelayCommand(() => OpenBrowser(BrowsePurpose.TempDirPick, "选择临时目录", "video", true));
        PickFramesDirCommand = new RelayCommand(() => OpenBrowser(BrowsePurpose.FramesDir, "选择电脑上的帧文件夹", "image", true));
        ClearFramesDirCommand = new RelayCommand(ClearFramesDirAsync);
        CleanTempCommand = new RelayCommand(() => { _cleanRetry = false; CleanStep = 1; });
        StartCommand = new RelayCommand(StartAsync);
        StopCommand = new RelayCommand(StopAsync);
        PauseResumeCommand = new RelayCommand(async () => { if (_paused) await ResumeAsync(); else await PauseAsync(); });
        EnterProgressCommand = new RelayCommand(EnterProgress);
        LeaveProgressCommand = new RelayCommand(LeaveProgress);
        DownloadResultCommand = new RelayCommand(DownloadResultAsync);
        EnablePreviewCommand = new RelayCommand(() => RequestStream(preview: true));
        DisablePreviewCommand = new RelayCommand(() => { PreviewEnabled = false; PushStream(); });
        EnableStatsCommand = new RelayCommand(() => RequestStream(preview: false));
        DisableStatsCommand = new RelayCommand(() => { StatsEnabled = false; PushStream(); });
        SfBrowseCommand = new RelayCommand(OpenSfBrowser);
        SfKindImageCommand = new RelayCommand(() => SetSfKind(0));
        SfKindAudioCommand = new RelayCommand(() => SetSfKind(1));
        SfKindVideoAudioCommand = new RelayCommand(() => SetSfKind(2));
        SfKindVideoHdrCommand = new RelayCommand(() => SetSfKind(3));
        SfStartCommand = new RelayCommand(SfStartAsync);
        SfStopCommand = new RelayCommand(SfStopAsync);
        DismissTurboWarnCommand = new RelayCommand(() => { TurboWarnVisible = false; TurboWarnText = ""; });
        SwitchDiskCommand = new RelayCommand(() =>
        {
            TurboWarnVisible = false;
            TurboWarnText = "";
            OpenBrowser(BrowsePurpose.TempDirPick, "选择电脑上的临时目录", "video", true);
        });
        TempCacheCleanCommand = new RelayCommand(() =>
        {
            TempCacheVisible = false;
            _cleanRetry = true;
            CleanStep = 2;
        });
        TempCacheChangeDirCommand = new RelayCommand(() =>
        {
            TempCacheVisible = false;
            OpenBrowser(BrowsePurpose.TempDirRetry, "选择电脑上的临时目录", "video", true);
        });
        TempCacheCancelCommand = new RelayCommand(() =>
        {
            TempCacheVisible = false;
            ResultText = "已取消启动（临时目录里有旧缓存）";
        });
        CleanConfirmCommand = new RelayCommand(ConfirmClean);
        CleanCancelCommand = new RelayCommand(() => { CleanStep = 0; _cleanRetry = false; });
        RiskConfirmCommand = new RelayCommand(() =>
        {
            RiskVisible = false;
            if (_riskForPreview) { PreviewEnabled = true; PushStream(); }
            else { StatsEnabled = true; PushStream(); }
        });
        RiskCancelCommand = new RelayCommand(() => RiskVisible = false);
        BrowseCancelCommand = new RelayCommand(() => BrowseVisible = false);
        BrowseUpCommand = new RelayCommand(() => _ = LoadDirAsync(ProtocolParser.ParentOf(_browseCurrentPath)));
        BrowseEntryCommand = new RelayCommand(p => _ = BrowseEntryAsync(p as DirEntry));
        BrowsePickCurrentCommand = new RelayCommand(() => PickPath(_browseCurrentPath));
        BrowseNewFolderCommand = new RelayCommand(() =>
        {
            NewFolderName = "";
            NewFolderError = "";
            NewFolderVisible = true;
        });
        NewFolderCancelCommand = new RelayCommand(() => NewFolderVisible = false);
        NewFolderCreateCommand = new RelayCommand(CreateFolderAsync);
    }

    private void BuildChoices()
    {
        BuildHwModes();
        BuildDedupModes();
        BuildAudioPrecisions();
        BuildSfPrecisions();
        BuildSfScales();
        BuildMultiplierChoices();
        BuildTurboSegmentChoices();
    }

    private void BuildTurboSegmentChoices()
    {
        TurboSegmentChoices.Clear();
        for (var i = 1; i <= 5; i++)
        {
            var n = i;
            TurboSegmentChoices.Add(new ChoiceItem($"{n} 段", () =>
            {
                TurboSegments = n;
                if (!_applyingCaps) PushConfigQuietly();
            }, _turboSegments == n));
        }
    }

    private void BuildHwModes()
    {
        HwModeChoices.Clear();
        HwModeChoices.Add(new ChoiceItem("默认", () => SetHwQualityModeFromUi(""), _hwQualityMode == ""));
        HwModeChoices.Add(new ChoiceItem("CQ 恒定质量", () => SetHwQualityModeFromUi("cq"), _hwQualityMode == "cq"));
        HwModeChoices.Add(new ChoiceItem("QP 固定量化", () => SetHwQualityModeFromUi("qp"), _hwQualityMode == "qp"));
    }

    private void BuildDedupModes()
    {
        DedupModeChoices.Clear();
        DedupModeChoices.Add(new ChoiceItem("性能模式", () => SetDedupModeFromUi("performance"), _dedupMode != "uhd"));
        DedupModeChoices.Add(new ChoiceItem("完美模式", () => SetDedupModeFromUi("uhd"), _dedupMode == "uhd"));
    }

    private void BuildAudioPrecisions()
    {
        AudioPrecisionChoices.Clear();
        AudioPrecisionChoices.Add(new ChoiceItem("FP16 低精度", () => SetAudioPrecision("fp16"), _audioSrPrecision != "fp32"));
        AudioPrecisionChoices.Add(new ChoiceItem("FP32 高精度", () => SetAudioPrecision("fp32"), _audioSrPrecision == "fp32"));
    }

    private void SetAudioPrecision(string p)
    {
        var v = p == "fp32" ? "fp32" : "fp16";
        if (_audioSrPrecision == v) return;
        AudioSrPrecision = v;
        BuildAudioPrecisions();
        AddLog("info", v == "fp32" ? "音频超分精度：FP32 高精度（CPU 也行）" : "音频超分精度：FP16 低精度（仅 GPU）");
        PushConfigQuietly();
    }

    private void BuildSfPrecisions()
    {
        SfPrecisionChoices.Clear();
        SfPrecisionChoices.Add(new ChoiceItem("fp16（快，仅 GPU）", () => SetSfPrecision("fp16"), _sfAudioPrecision == "fp16"));
        SfPrecisionChoices.Add(new ChoiceItem("fp32（完美，CPU 也行）", () => SetSfPrecision("fp32"), _sfAudioPrecision == "fp32"));
    }

    private void SetSfPrecision(string p)
    {
        SfAudioPrecision = p;
        BuildSfPrecisions();
        PushSfConfig();
    }

    private void BuildSfScales()
    {
        SfScaleChoices.Clear();
        foreach (var sc in new[] { 2, 3, 4 })
            SfScaleChoices.Add(new ChoiceItem($"{sc}x", () => { SfImageScale = sc; BuildSfScales(); PushSfConfig(); }, _sfImageScale == sc));
    }

    private void BuildMultiplierChoices()
    {
        IfMultiplierChoices.Clear();
        var values = new List<int> { 2, 3, 4, 5, 6, 8 };
        if (!values.Contains(_ifMultiplier)) values.Add(_ifMultiplier);
        values.Sort();
        foreach (var m in values)
            IfMultiplierChoices.Add(new ChoiceItem($"{m}x", () => { IfMultiplier = m; BuildMultiplierChoices(); }, _ifMultiplier == m));
    }

    // ===================== 连接 =====================

    private void ConnectFromInput()
    {
        var host = _host.Trim();
        if (host.Length == 0) return;
        _ = ConnectInternalAsync(host, autoReconnect: false);
    }

    private static (string host, int port) ParseHost(string raw)
    {
        var h = raw.Trim();
        if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) h = h[7..];
        if (h.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) h = h[8..];
        var slash = h.IndexOfAny(new[] { '/', ' ' });
        if (slash >= 0) h = h[..slash];
        var port = Easy4kClient.DefaultPort;

        if (h.StartsWith('['))
        {
            var close = h.IndexOf(']');
            if (close > 1)
            {
                var tail = h[(close + 1)..];
                if (tail.StartsWith(':') && int.TryParse(tail[1..], out var p) && p is > 0 and <= 65535)
                    port = p;
                return (h[1..close].Trim(), port);
            }
        }

        if (h.Count(c => c == ':') >= 2) return (h, port);

        var idx = h.IndexOf(':');
        if (idx > 0 && int.TryParse(h[(idx + 1)..], out var pp) && pp is > 0 and <= 65535)
            return (h[..idx], pp);

        return (h, port);
    }

    private async Task ConnectInternalAsync(string host, bool autoReconnect, bool keyRetried = false)
    {
        _manualClose = false;
        _turboWarnedThisSession = false;
        var (target, port) = ParseHost(host);

        Host = host.Trim();
        Connecting = true;
        KeyPromptVisible = false;
        if (!autoReconnect)
        {
            ConnectError = "";
            Logs.Clear();
            Phase = Phase.Connect;
        }
        ConnectStep = autoReconnect ? $"连接断开，正在自动重连 {target}:{port} …" : $"正在连接电脑 {target}:{port} …";

        try
        {
            Subscribe(_client);
            var deviceName = AndroidPlatform.DeviceDisplayName();
            await _client.ConnectAsync(target, port, deviceName, RequestKeyAsync);

            ConnectStep = "正在检测电脑显卡…";
            var capsResp = await _client.RequestAsync("capabilities", null, 25000);
            ConnectStep = "正在读取超分 / 补帧模型列表…";
            var caps = ProtocolParser.ParseCapabilities(capsResp);
            await Task.Delay(200);

            var lan = await Task.Run(() => AndroidPlatform.DetectLan(target));
            ApplyCapabilities(caps, lan, autoReconnect, deviceName);

            Connecting = false;
            ConnectError = "";
            // 自动重连成功时保持在原页面：处理到一半跳回设置页会让进度/结果/提示全部消失
            if (!autoReconnect || Phase == Phase.Connect) Phase = Phase.Main;
            ConnectStep = "";

            StartSessionKeepAlive();
            WarnTurboIfHddOnConnect();

            if (!autoReconnect)
            {
                try
                {
                    var e = _prefs?.Edit();
                    e?.PutString("host", _host.Trim());
                    e?.Apply();
                }
                catch { }
            }
            else
            {
                AddLog("success", "连接已自动恢复");
            }
        }
        catch (Exception ex)
        {
            Connecting = false;
            ConnectStep = "";

            // 用的是保存下来的密钥但被拒：说明电脑端换过密钥，清掉后带输入框重试一次
            if (ex is UnauthorizedAccessException && _usingSavedKey && !keyRetried && !autoReconnect)
            {
                ClearSavedKey();
                try { await _client.DisconnectAsync(); } catch { }
                await ConnectInternalAsync(host, autoReconnect: false, keyRetried: true);
                return;
            }
            if (ex is UnauthorizedAccessException) ClearSavedKey();

            if (!autoReconnect)
            {
                _manualClose = true;
                try { await _client.DisconnectAsync(); } catch { }
                CloseAllDialogs();
                Phase = Phase.Connect;
                ConnectError = $"无法连接 {target}:{port}\n{ex.Message}";
            }
        }
    }

    private void Subscribe(Easy4kClient client)
    {
        client.Message -= OnMessage;
        client.PreviewReceived -= OnPreview;
        client.Disconnected -= OnClientDisconnected;
        client.Message += OnMessage;
        client.PreviewReceived += OnPreview;
        client.Disconnected += OnClientDisconnected;
    }

    private void ApplyCapabilities(Capabilities caps, bool lan, bool autoReconnect, string deviceName)
    {
        _applyingCaps = true;
        try
        {
            VersionText = $"Easy4K v{caps.Version} · {caps.Gpu.Name}";
            SrFamilies = caps.SrFamilies;
            UsableVfiFamilies = caps.UsableVfiFamilies;

            SrEnabled = caps.SuperResolution;
            IfEnabled = caps.Interpolation;
            MergeVideo = caps.MergeVideo;
            MergeAudio = caps.MergeAudio;
            SplitFrames = caps.SplitFrames;
            SdrToHdr = caps.SdrToHdr;
            HdrEnabled = caps.HdrEnabled;
            HdrAvailable = caps.HdrEnabled;
            Dedup = caps.Dedup;
            KeepSubtitles = caps.KeepSubtitles;
            HasSubtitles = caps.HasSubtitles;
            AudioSr = caps.AudioSr;
            SetTurboState(caps.TurboMode);
            SceneGuard = caps.SceneGuard;
            TurboSegments = Math.Clamp(caps.TurboSegments, 1, 5);
            SrDecodeText = caps.SrDecodeThreads.ToString();
            SrEncodeText = caps.SrEncodeThreads.ToString();
            IfDecodeText = caps.IfDecodeThreads.ToString();
            IfEncodeText = caps.IfEncodeThreads.ToString();
            PipelineGpuOptions = caps.PipelineGpuOptions;
            PipelineGpuIndex = Math.Clamp(caps.PipelineGpuIndex, 0, Math.Max(0, caps.PipelineGpuOptions.Count - 1));
            HdrAiPathSet = caps.HdrAiPath;
            HdrSaturationText = caps.HdrSaturation.ToString();
            HdrContrastText = caps.HdrContrast.ToString();
            HdrAiStrengthText = caps.HdrAiStrength.ToString();
            SkipStartupSelfTest = caps.SkipStartupSelfTest;
            UseSafeFrameRate = caps.UseSafeFrameRate;
            LowerQualityForVram = caps.LowerQualityForVram;
            UseGpuAcceleration = caps.UseGpuAcceleration;
            UseCpuProcessing = caps.UseCpuProcessing;
            TryOldFfmpegOnCrash = caps.TryOldFfmpegOnCrash;
            NoReport = caps.NoReport;
            ReportAutoOpen = caps.ReportAutoOpen;
            ReportDir = caps.ReportDir;
            PreventAutoSleep = caps.PreventAutoSleep;
            ExternalFramesDir = caps.ExternalFramesDir;
            CrfText = caps.Crf.ToString();
            HwQualityText = caps.HwQuality.ToString();
            HwQualityMode = caps.HwQualityMode is "" or "cq" or "qp" ? caps.HwQualityMode : "";
            DedupMode = caps.DedupMode == "uhd" ? "uhd" : "performance";
            AudioSrPrecision = caps.AudioSrPrecision == "fp32" ? "fp32" : "fp16";
            IfMultiplierLocked = caps.IfMultiplierLocked;
            TargetFpsText = caps.TargetFps > 0 ? (caps.TargetFps % 1 == 0 ? ((int)caps.TargetFps).ToString() : caps.TargetFps.ToString("0.##")) : "";

            SelectedSrFamily = caps.SrFamilies.FirstOrDefault(f => f.Index == caps.SrEngineIndex) ?? caps.SrFamilies.FirstOrDefault();
            RebuildSrScales(SelectedSrFamily, caps.SrScale);
            RebuildSrModels(SelectedSrFamily, _srScale, caps.SrModel);
            UpdateSrSceneHint();

            var usable = caps.UsableVfiFamilies;
            SelectedVfiFamily = usable.FirstOrDefault(f => f.Index == caps.IfEngineIndex) ?? usable.FirstOrDefault();
            RebuildVfiModels(SelectedVfiFamily, caps.IfModel);
            IfMultiplier = Math.Clamp(caps.IfMultiplier, 1, 16);
            BuildMultiplierChoices();
            UpdateVfiSceneHint();

            OutputRoot = caps.OutputRoot;
            _outputRootChoice = "";
            OnPropertyChanged(nameof(OutputRootDisplay));
            TempRoot = caps.TempRoot;
            OutputName = caps.OutputName;

            SfKindIndex = caps.SfKindIndex;
            SfInput = caps.SfInput;
            SfImageScale = caps.SfImageScale;
            SfImageModel = caps.SfImageModel;
            SfImageModels.Clear();
            foreach (var m in caps.SfImageModels) SfImageModels.Add(m);
            SfAudioPrecision = caps.SfAudioPrecision;
            SfRunning = caps.SfRunning;
            SfStatus = caps.SfStatus;
            BuildSfScales();
            BuildSfPrecisions();

            IsLan = lan;
            PreviewEnabled = lan;
            StatsEnabled = lan;
            Processing = caps.IsProcessing;
            Paused = caps.IsPaused;

            if (!autoReconnect)
            {
                _phoneFile = null;
                _phoneName = "";
                _computerPath = "";
                _inputKey = "";
                InputLabel = "未选择";
                VideoInfo = null;
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(StartButtonText));
                OnPropertyChanged(nameof(EstimatedOutputName));
                OnPropertyChanged(nameof(ShowResultCard));
            }

            if (caps.LastResult is { Success: true } lr && lr.Output.Length > 0)
            {
                ResultOutput = lr.Output;
                ResultOk = true;
                ResultText = $"处理完成\n输出：{lr.Output}\n步骤：{lr.Steps}";
                _resultSourceKey = _inputKey;
                _lastOutputPath = lr.Output;
                OnPropertyChanged(nameof(ShowResultCard));
            }

            NonNvidiaHint = caps.Gpu.IsNvidia
                ? ""
                : $"当前电脑是 {caps.Gpu.Name}（非 NVIDIA），已隐藏 RIFE / DRBA / GMFSS / GIMM-VFI 四个仅 N 卡可用的补帧模型族。";

            AddLog("success", $"已连接 Easy4K v{caps.Version}");
            AddLog("info", $"本机名称：{deviceName}（电脑端会显示「客户端已连接：{deviceName}」）");
            AddLog("info", $"电脑显卡：{caps.Gpu.Name}（显存 {caps.Gpu.VramText}）");
            AddLog("info", lan
                ? "连接方式：内网（图片预览 / CPU-GPU 占用默认全开）"
                : "连接方式：外网（仅保留进度与日志；预览与占用需手动开启）");
            if (!caps.Gpu.IsNvidia)
                AddLog("warn", "检测到非 NVIDIA 显卡，已隐藏 4 个仅 N 卡可用的补帧模型族（RIFE / DRBA / GMFSS / GIMM-VFI）");
        }
        finally
        {
            _applyingCaps = false;
        }
    }

    private void RebuildSrScales(SrFamily? fam, int? prefer)
    {
        var scales = fam?.Models.SelectMany(m => m.Scales).Distinct().OrderBy(x => x).ToList() ?? new List<int> { 2 };
        if (scales.Count == 0) scales.Add(2);
        var target = prefer is not null && scales.Contains(prefer.Value)
            ? prefer.Value
            : scales.Contains(_srScale) ? _srScale : scales[0];

        SrScaleChoices.Clear();
        foreach (var sc in scales)
        {
            var s = sc;
            SrScaleChoices.Add(new ChoiceItem($"{s}x", () =>
            {
                SrScale = s;
                BuildSrScaleChoices(s);
            }, s == target));
        }

        _srScale = target;
        OnPropertyChanged(nameof(SrScale));
        RebuildSrModels(fam, target, null);
        OnPropertyChanged(nameof(EstimatedOutputName));
    }

    private void BuildSrScaleChoices(int selected)
    {
        foreach (var c in SrScaleChoices)
            c.IsSelected = c.Label == $"{selected}x";
    }

    private void RebuildSrModels(SrFamily? fam, int scale, string? prefer)
    {
        var models = fam?.ModelsFor(scale).Select(m => m.Name).ToList() ?? new List<string>();
        SrModelChoices.Clear();
        foreach (var m in models) SrModelChoices.Add(m);

        var selected = prefer is not null && models.Contains(prefer) ? prefer
            : models.Contains(_srModel) ? _srModel : models.FirstOrDefault() ?? _srModel;
        _srModel = selected;
        OnPropertyChanged(nameof(SrModel));
        UpdateSrSceneHint();
    }

    private void UpdateSrSceneHint()
    {
        var fam = _selectedSrFamily;
        var model = fam?.Models.FirstOrDefault(m => m.Name == _srModel);
        var scene = model?.Scene.Length > 0 ? model.Scene : fam?.Scene ?? "";
        var stars = model?.Stars > 0 ? model!.Stars : fam?.Stars ?? 0;
        SrSceneHint = string.IsNullOrWhiteSpace(scene) ? ""
            : stars > 0 ? $"{scene} · {new string('★', Math.Min(stars, 5))}" : scene;
    }

    private void RebuildVfiModels(VfiFamily? fam, string? prefer)
    {
        var models = fam?.Models ?? new List<string>();
        VfiModelChoices.Clear();
        foreach (var m in models) VfiModelChoices.Add(m);

        var selected = prefer is not null && models.Contains(prefer) ? prefer
            : models.Contains(_ifModel) ? _ifModel : models.FirstOrDefault() ?? _ifModel;
        _ifModel = selected;
        OnPropertyChanged(nameof(IfModel));
        UpdateVfiSceneHint();
    }

    private void UpdateVfiSceneHint()
    {
        var fam = _selectedVfiFamily;
        if (fam is null) { VfiSceneHint = ""; return; }
        VfiSceneHint = fam.Stars > 0
            ? $"{fam.Scene} · {new string('★', Math.Min(fam.Stars, 5))}"
            : fam.Scene;
    }

    private string? _autoKey;

    /// <summary>自动化联调入口（仅在 adb 带 hikari_autotest 附加参数时使用）：
    /// 用给定密钥连接 → 选中电脑上的文件 → 直接开始处理。</summary>
    public void BeginAutoTest(string host, string key, string file, bool stats = false)
    {
        _autoKey = key;
        _ = _ui.InvokeAsync(async () =>
        {
            try
            {
                await ConnectInternalAsync(host, autoReconnect: false);
                if (_phase != Phase.Main)
                {
                    AddLog("error", "自动测试：连接失败");
                    return;
                }
                if (stats)
                {
                    // 连接成功后才能打开占用推流：连接时 StatsEnabled 会按内外网重算，
                    // 提前设置会被覆盖掉。
                    _statsEnabled = true;
                    StatsEnabled = true;
                    AddLog("info", "自动测试：已强制开启占用推流");
                    PushStream();
                }
                if (file.Length > 0)
                {
                    await SelectComputerFileAsync(file);
                    await Task.Delay(400);
                }
                await StartAsync();
            }
            catch (Exception ex)
            {
                AddLog("error", $"自动测试异常：{ex}");
            }
            finally
            {
                _autoKey = null;
            }
        });
    }

    private bool _usingSavedKey;

    private Task<string?> RequestKeyAsync()
    {
        if (!string.IsNullOrEmpty(_autoKey))
            return Task.FromResult<string?>(_autoKey);

        // 之前成功用过的密钥存着：断线重连 / 切后台回来 / 重开 App 都不再打扰用户，
        // 只有电脑端换过密钥（授权失败）时才清掉并重新弹输入框。
        var saved = _prefs?.GetString("remote_key", "") ?? "";
        if (saved.Length > 0)
        {
            _usingSavedKey = true;
            return Task.FromResult<string?>(saved);
        }

        _usingSavedKey = false;
        _ui.Post(() =>
        {
            KeyInput = "";
            KeyPromptVisible = true;
        });
        _keyTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _keyTcs.Task;
    }

    private void CompleteKey(string? key)
    {
        KeyPromptVisible = false;
        var trimmed = key?.Trim() ?? "";
        if (trimmed.Length > 0)
        {
            try
            {
                var e = _prefs?.Edit();
                e?.PutString("remote_key", trimmed);
                e?.Apply();
            }
            catch { }
        }
        _keyTcs?.TrySetResult(string.IsNullOrWhiteSpace(trimmed) ? null : trimmed);
        _keyTcs = null;
        KeyInput = "";
    }

    private void ClearSavedKey()
    {
        try
        {
            var e = _prefs?.Edit();
            e?.Remove("remote_key");
            e?.Apply();
        }
        catch { }
        _usingSavedKey = false;
    }

    /// <summary>把仍在显示的所有遮罩弹窗关掉：切页 / 断线后不能把它们留在界面上挡住操作。</summary>
    private void CloseAllDialogs()
    {
        KeyPromptVisible = false;
        TurboWarnVisible = false;
        TempCacheVisible = false;
        _cleanStep = 0;
        OnPropertyChanged(nameof(CleanConfirmVisible));
        RiskVisible = false;
        BrowseVisible = false;
    }

    private void Disconnect()
    {
        _manualClose = true;
        // 不能在这里同步 Wait：DisconnectAsync 的续体需要回到 UI 线程，同步等待会死锁
        _ = _client.DisconnectAsync();
        StopSessionKeepAlive();
        CloseAllDialogs();
        Phase = Phase.Connect;
        Connecting = false;
        _phoneFile = null;
        _phoneName = "";
        _computerPath = "";
        _inputKey = "";
        InputLabel = "未选择";
        VideoInfo = null;
        VersionText = "";
        Processing = false;
        Paused = false;
        SfRunning = false;
        ProgressPercent = 0;
        ProgressText = "";
        ResultText = "";
        PreviewImage = null;
        Cpu = 0;
        Gpu = 0;
        Logs.Clear();
    }

    // ===================== 保活 / 心跳 / 自动重连 =====================

    private void StartSessionKeepAlive()
    {
        AndroidPlatform.StartKeepAlive(IdleKeepAliveText);
        AndroidPlatform.AcquireSessionLocks();
        StartHeartbeat();
    }

    private void StopSessionKeepAlive()
    {
        try { _heartbeatCts?.Cancel(); } catch { }
        _heartbeatCts = null;
        _heartbeatTask = null;
        AndroidPlatform.StopKeepAlive();
        AndroidPlatform.ReleaseSessionLocks();
        _completionPending = false;
    }

    private void BackToIdleKeepAlive() => AndroidPlatform.StartKeepAlive(IdleKeepAliveText);

    private void StartHeartbeat()
    {
        try { _heartbeatCts?.Cancel(); } catch { }
        _heartbeatCts = new CancellationTokenSource();
        var ct = _heartbeatCts.Token;
        _heartbeatTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(20_000, ct);
                    if (!_client.IsConnected) break;

                    if (_client.LastReceivedAt > 0 &&
                        System.Environment.TickCount64 - _client.LastReceivedAt > 60_000)
                    {
                        try { await _client.DisconnectAsync(); } catch { }
                        break;
                    }

                    await _client.SendAsync(new JsonObject { ["type"] = "ping" }, ct);
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        }, ct);
    }

    private void OnClientDisconnected(string reason)
    {
        if (_manualClose) return;
        _ui.Post(() =>
        {
            if (_manualClose) return;
            AddLog("warn", reason);
            _ = ScheduleReconnectAsync();
        });
    }

    private async Task ScheduleReconnectAsync()
    {
        if (_reconnecting || _manualClose) return;
        var host = _host;
        if (host.Length == 0) return;

        _reconnecting = true;
        try
        {
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                await Task.Delay(attempt == 1 ? 1500 : Math.Min(3000 * attempt, 15000));
                if (_manualClose) return;
                Connecting = true;
                // 不切页面：停在这一页继续显示进度，重连成功后直接续上
                ConnectStep = $"连接断开，正在自动重连 {host} …（第 {attempt} 次）";
                if (attempt == 1) AddLog("warn", "连接断开，正在自动重连…");
                try
                {
                    await _client.DisconnectAsync();
                }
                catch { }
                await ConnectInternalAncoreAsync(host, attempt);
                if (_client.IsConnected) return;
            }

            if (_manualClose) return;
            StopSessionKeepAlive();
            Connecting = false;
            Processing = false;
            Paused = false;
            CloseAllDialogs();
            Phase = Phase.Connect;
            ConnectError = "与电脑的连接已断开，自动重连 5 次均失败。请确认电脑端仍在等待连接后手动重连";
        }
        finally
        {
            _reconnecting = false;
        }
    }

    // ConnectInternalAsync 的重连版本：失败不弹错误，只返回
    private async Task ConnectInternalAncoreAsync(string host, int attempt)
    {
        var (target, port) = ParseHost(host);
        try
        {
            ConnectStep = $"正在重连电脑 {target}:{port} …";
            var deviceName = AndroidPlatform.DeviceDisplayName();
            await _client.ConnectAsync(target, port, deviceName, RequestKeyAsync);
            var capsResp = await _client.RequestAsync("capabilities", null, 25000);
            var caps = ProtocolParser.ParseCapabilities(capsResp);
            var lan = await Task.Run(() => AndroidPlatform.DetectLan(target));
            ApplyCapabilities(caps, lan, autoReconnect: true, deviceName);
            Connecting = false;
            // 保持在原页面（处理中的进度页不能被切走）
            if (Phase == Phase.Connect) Phase = Phase.Main;
            ConnectStep = "";
            StartSessionKeepAlive();
            AddLog("success", $"连接已自动恢复（第 {attempt} 次尝试）");
        }
        catch
        {
            // 继续下一次尝试
        }
    }

    // ===================== 选项推送 =====================

    private void PushConfigQuietly()
    {
        if (_processing || _connecting || !_client.IsConnected) return;
        _ = Task.Run(async () =>
        {
            try { await SendConfigAsync(); } catch { }
        });
    }

    private Task PushIfConfigAsync()
    {
        if (!_client.IsConnected) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try
            {
                var cfg = new JsonObject
                {
                    ["interpolation"] = _ifEnabled,
                    ["ifEngineIndex"] = _selectedVfiFamily?.Index ?? 0,
                    ["ifModel"] = _ifModel,
                    ["ifMultiplier"] = _ifMultiplier
                };
                await _client.SendAsync(new JsonObject { ["type"] = "set_config", ["config"] = cfg });
            }
            catch { }
        });
    }

    private async Task SendConfigAsync()
    {
        if (!_client.IsConnected) return;
        var cfg = new JsonObject
        {
            ["superResolution"] = _srEnabled,
            ["interpolation"] = _ifEnabled,
            ["mergeVideo"] = _mergeVideo,
            ["mergeAudio"] = _mergeAudio,
            ["splitFrames"] = _splitFrames,
            ["sdrToHdr"] = _sdrToHdr,
            ["srEngineIndex"] = _selectedSrFamily?.Index ?? 0,
            ["srScale"] = _srScale,
            ["srModel"] = _srModel,
            ["ifEngineIndex"] = _selectedVfiFamily?.Index ?? 0,
            ["ifModel"] = _ifModel,
            ["ifMultiplier"] = _ifMultiplier,
            ["dedup"] = _dedup,
            ["audioSr"] = _audioSr,
            ["turboMode"] = _turboMode,
            ["keepSubtitles"] = _keepSubtitles,
            ["crf"] = Crf,
            ["hwQualityMode"] = _hwQualityMode,
            ["hwQuality"] = HwQuality,
            ["dedupMode"] = _dedupMode,
            ["audioSrPrecision"] = _audioSrPrecision,
            ["targetFps"] = TargetFps,
            // —— 高级页全部可选项 ——
            ["sceneGuard"] = _sceneGuard,
            ["turboSegments"] = Math.Clamp(_turboSegments, 1, 5),
            ["srDecodeThreads"] = IntOf(_srDecodeText, 1),
            ["srEncodeThreads"] = IntOf(_srEncodeText, 2),
            ["ifDecodeThreads"] = IntOf(_ifDecodeText, 1),
            ["ifEncodeThreads"] = IntOf(_ifEncodeText, 2),
            ["pipelineGpuIndex"] = _pipelineGpuIndex,
            ["hdrSaturation"] = int.TryParse(_hdrSaturationText, out var hs) ? Math.Clamp(hs, 0, 200) : 200,
            ["hdrContrast"] = int.TryParse(_hdrContrastText, out var hc) ? Math.Clamp(hc, 0, 200) : 200,
            ["hdrAiStrength"] = int.TryParse(_hdrAiStrengthText, out var ha) ? Math.Clamp(ha, 50, 150) : 100,
            ["skipStartupSelfTest"] = _skipStartupSelfTest,
            ["useSafeFrameRate"] = _useSafeFrameRate,
            ["lowerQualityForVram"] = _lowerQualityForVram,
            ["useGpuAcceleration"] = _useGpuAcceleration,
            ["useCpuProcessing"] = _useCpuProcessing,
            ["tryOldFfmpegOnCrash"] = _tryOldFfmpegOnCrash,
            ["noReport"] = _noReport,
            ["reportAutoOpen"] = _reportAutoOpen,
            ["reportDir"] = _reportDir,
            ["preventAutoSleep"] = _preventAutoSleep
        };
        if (_outputRootChoice.Length > 0) cfg["outputRoot"] = _outputRootChoice;

        // 帧文件夹模式：手工分辨率/帧数/帧率要一起下发，否则远端显示与输出命名都是错的
        if (_externalFramesDir.Length > 0)
        {
            if (int.TryParse(_manualWidthText, out var mw) && mw > 0) cfg["manualWidth"] = mw;
            if (int.TryParse(_manualHeightText, out var mh) && mh > 0) cfg["manualHeight"] = mh;
            if (long.TryParse(_manualTotalFramesText, out var mf) && mf >= 0) cfg["manualTotalFrames"] = (int)Math.Min(mf, int.MaxValue);
            if (double.TryParse(_manualFpsText, out var mfps) && mfps > 0) cfg["manualFps"] = mfps;
        }

        await _client.SendAsync(new JsonObject { ["type"] = "set_config", ["config"] = cfg });
    }

    // ===================== 输入视频 =====================

    private async Task PickPhoneVideoAsync()
    {
        if (PickVideoFile is null) return;
        var file = await PickVideoFile();
        if (file is null) return;

        _phoneFile = file;
        _phoneName = file.Name;
        _computerPath = "";
        _inputKey = file.Name;
        VideoInfo = null;
        InputLabel = $"手机视频：{file.Name}";
        ResultText = "";
        ResultOld = _resultOld || (file.Name.Length > 0 && file.Name == _resultSourceKey);
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(EstimatedOutputName));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ShowResultCard));
        AddLog("info", $"已选择手机视频：{file.Name}（启动处理时自动上传到电脑）");

        try
        {
            var props = await file.GetBasicPropertiesAsync();
            _phoneSize = props.Size is null ? 0 : Convert.ToInt64(props.Size);
        }
        catch { _phoneSize = 0; }
    }

    private async Task SelectComputerFileAsync(string path)
    {
        try
        {
            var r = await _client.RequestAsync("set_input", new JsonObject { ["path"] = path }, 30000);
            var vi = ProtocolParser.ParseVideoInfo(r);
            _phoneFile = null;
            _phoneName = "";
            // 只有电脑端确认能读到时才把路径当作输入，否则「开始处理」会拿电脑上
            // 上一个输入视频去跑，用户完全看不出来发生了什么。
            _computerPath = vi.Valid ? path : "";
            _inputKey = vi.Valid ? Path.GetFileName(path) : "";
            VideoInfo = vi;
            InputLabel = vi.Valid ? $"电脑视频：{path}" : "";
            ResultText = vi.Valid ? "" : (vi.Error.Length > 0 ? vi.Error : "无法读取该视频");
            ResultOld = _resultOld || (_inputKey.Length > 0 && _inputKey == _resultSourceKey);
            OnPropertyChanged(nameof(StartButtonText));
            OnPropertyChanged(nameof(EstimatedOutputName));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(ShowResultCard));

            if (vi.Valid)
                AddLog("info", $"已选择电脑视频：{path}（{vi.Resolution} {vi.Fps:0.##}fps {vi.DurationText}）");
            else
                AddLog("error", vi.Error.Length > 0 ? vi.Error : "无法读取该视频");
        }
        catch (Exception ex)
        {
            AddLog("error", $"读取电脑视频信息失败：{ex.Message}");
        }
    }

    // ===================== 开始 / 停止 / 暂停 =====================

    private async Task StartAsync()
    {
        if (AnyBusy) return;

        var lan = _isLan;
        Phase = Phase.Progress;
        ResultText = "";
        PreviewImage = null;
        ProgressPercent = 0;
        ProgressText = "准备中…";
        PreviewEnabled = lan || _previewEnabled;
        StatsEnabled = lan || _statsEnabled;
        PushStream();

        AndroidPlatform.StartKeepAlive("正在处理，完成后会提醒你");
        _completionPending = false;
        _lastNotifyTick = 0;

        try
        {
            // 0) 先问电脑：临时目录里有没有别的视频留下的旧缓存
            var pre = await _client.RequestAsync("set_config", new JsonObject { ["config"] = new JsonObject() }, 20000);
            var preState = Easy4kClient.Obj(pre, "state");
            if (Easy4kClient.GetStr(preState, "tempCacheStatus") == "mismatch")
            {
                Processing = false;
                TempCacheSource = Easy4kClient.GetStr(preState, "tempCacheSource");
                if (TempCacheSource.Length == 0) TempCacheSource = "其他视频";
                TempCachePath = Easy4kClient.GetStr(Easy4kClient.Obj(pre, "paths"), "tempRoot");
                TempCacheVisible = true;
                ProgressText = "临时目录有旧缓存，等待你选择";
                ResultText = "";
                BackToIdleKeepAlive();
                return;
            }

            // 1) 手机视频 → 先上传到电脑
            if (_phoneFile is not null)
            {
                if (!await UploadPhoneVideoAsync()) 
                {
                    BackToIdleKeepAlive();
                    return;
                }
            }
            else if (_computerPath.Length == 0)
            {
                ResultText = "请先选择输入视频";
                BackToIdleKeepAlive();
                return;
            }

            // 2) 下发处理配置
            await SendConfigAsync();
            await Task.Delay(150);

            // 3) 启动
            Processing = true;
            Paused = false;
            ProgressText = "已开始处理";
            var ack = await _client.RequestAsync("start", null, 20000);
            if (ack.ValueKind == JsonValueKind.Undefined)
            {
                Processing = false;
                ResultText = "启动指令超时";
                BackToIdleKeepAlive();
            }
        }
        catch (Exception ex)
        {
            Processing = false;
            ResultText = $"启动失败：{ex.Message}";
            AddLog("error", ResultText);
            BackToIdleKeepAlive();
        }
    }

    private async Task<bool> UploadPhoneVideoAsync()
    {
        var file = _phoneFile;
        if (file is null) return false;

        var name = file.Name;
        var size = _phoneSize;

        var begin = await _client.RequestAsync("upload_begin",
            new JsonObject { ["name"] = name, ["size"] = size }, 15000);
        if (begin.ValueKind == JsonValueKind.Undefined)
        {
            ResultText = "上传初始化失败，请检查手机与电脑的连接";
            return false;
        }

        Uploading = true;
        UploadPercent = 0;
        ProgressPercent = 0;
        ProgressText = "正在上传手机视频到电脑…";

        try
        {
            await using var input = await file.OpenReadAsync();
            var buffer = new byte[256 * 1024];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer);
                if (read <= 0) break;

                var b64 = Convert.ToBase64String(buffer, 0, read);
                await _client.SendAsync(new JsonObject { ["type"] = "upload_chunk", ["data"] = b64 });
                total += read;

                var pct = size > 0 ? (int)Math.Clamp(total * 100 / size, 0, 100) : 0;
                UploadPercent = pct;
                ProgressPercent = pct;
                ProgressText = $"正在上传手机视频到电脑… {pct}%";
            }

            var end = await _client.RequestAsync("upload_end", null, 120000);
            Uploading = false;
            var vi = ProtocolParser.ParseVideoInfo(end);
            if (!vi.Valid)
            {
                VideoInfo = vi;
                ResultText = vi.Error.Length > 0 ? vi.Error : "上传失败";
                return false;
            }

            VideoInfo = vi;
            _computerPath = vi.Path;
            _phoneFile = null;
            _phoneName = "";
            _phoneSize = 0;
            InputLabel = $"电脑视频：{vi.Path}";
            ProgressText = "上传完成，正在启动处理…";
            OnPropertyChanged(nameof(StartButtonText));
            OnPropertyChanged(nameof(CanStart));
            AddLog("success", $"手机视频已上传：{name} → {vi.Path}");
            return true;
        }
        catch (Exception ex)
        {
            Uploading = false;
            ResultText = $"上传失败：{ex.Message}";
            AddLog("error", ResultText);
            return false;
        }
    }

    private async Task StopAsync()
    {
        ProgressText = "正在停止…";
        if (_sfRunning && !_processing)
        {
            var s = await _client.RequestAsync("sf_stop", null, 8000);
            if (s.ValueKind == JsonValueKind.Undefined) AddLog("error", "停止指令发送失败（连接可能已断开）");
            else AddLog("warn", "已发送停止指令");
            return;
        }

        var ack = await _client.RequestAsync("stop", null, 8000);
        if (ack.ValueKind == JsonValueKind.Undefined) AddLog("error", "停止指令发送失败（连接可能已断开）");
        else AddLog("warn", "已发送停止指令");
    }

    private async Task PauseAsync()
    {
        var r = await _client.RequestAsync("pause", null, 8000);
        var p = r.ValueKind == JsonValueKind.Undefined || Easy4kClient.GetBool(r, "paused", true);
        Paused = p;
        ProgressText = p ? "已暂停" : "无需暂停";
        AddLog(p ? "warn" : "info", p ? "处理已暂停" : "当前没有可暂停的工具进程");
    }

    private async Task ResumeAsync()
    {
        var r = await _client.RequestAsync("resume", null, 8000);
        var p = r.ValueKind != JsonValueKind.Undefined && Easy4kClient.GetBool(r, "paused");
        Paused = p;
        ProgressText = "已继续";
        AddLog(p ? "warn" : "info", p ? "继续失败，仍处于暂停" : "处理已继续");
    }

    // ===================== 临时目录 / 涡轮 =====================

    private void ConfirmClean()
    {
        if (_cleanStep == 1)
        {
            CleanStep = 2;
            return;
        }

        CleanStep = 0;
        if (_cleanRetry)
        {
            _cleanRetry = false;
            _ = CleanTempAndRetryAsync();
        }
        else
        {
            _ = CleanTempNowAsync();
        }
    }

    private async Task CleanTempNowAsync()
    {
        if (_tempCleaning) return;
        TempCleaning = true;
        TempCleanMsg = "";
        try
        {
            var r = await _client.RequestAsync("clean_temp", null, 180000);
            TempCleaning = false;
            if (r.ValueKind == JsonValueKind.Undefined)
            {
                TempCleanMsg = "清理超时，请重试";
            }
            else if (Easy4kClient.GetBool(r, "ok", true))
            {
                TempCleanMsg = "已清理电脑上的临时目录";
                AddLog("success", "已清理电脑上的临时目录");
            }
            else
            {
                TempCleanMsg = $"清理失败：{Easy4kClient.GetStr(r, "error")}";
            }
        }
        catch (Exception ex)
        {
            TempCleaning = false;
            TempCleanMsg = $"清理失败：{ex.Message}";
        }
    }

    private async Task CleanTempAndRetryAsync()
    {
        TempCacheVisible = false;
        Processing = false;
        Uploading = false;
        ResultText = "正在清理电脑上的旧缓存…";
        try
        {
            var r = await _client.RequestAsync("clean_temp", null, 180000);
            if (r.ValueKind == JsonValueKind.Undefined) AddLog("error", "清理临时目录超时");
        }
        catch (Exception ex) { AddLog("error", $"清理临时目录失败：{ex.Message}"); }
        await StartAsync();
    }

    private async Task SetTempRootAndRetryAsync(string path)
    {
        TempCacheVisible = false;
        TempCachePath = path;
        TempRoot = path;
        Processing = false;
        Uploading = false;
        ResultText = "已更换临时目录，正在重新启动…";
        try
        {
            var r = await _client.RequestAsync("set_config",
                new JsonObject { ["config"] = new JsonObject { ["tempRoot"] = path } }, 20000);
            if (r.ValueKind == JsonValueKind.Undefined) AddLog("error", "切换临时目录超时");
        }
        catch (Exception ex) { AddLog("error", $"切换临时目录失败：{ex.Message}"); }
        await StartAsync();
        _ = RecheckTurboDiskAsync(path);
    }

    private async Task SetTempRootAsync(string path)
    {
        TempRoot = path;
        try
        {
            var r = await _client.RequestAsync("set_config",
                new JsonObject { ["config"] = new JsonObject { ["tempRoot"] = path } }, 20000);
            if (r.ValueKind == JsonValueKind.Undefined) AddLog("error", "切换临时目录超时");
            else AddLog("success", $"临时目录已切换：{path}");
        }
        catch (Exception ex) { AddLog("error", $"切换临时目录失败：{ex.Message}"); }
        _ = RecheckTurboDiskAsync(path);
    }

    private void SetOutputRoot(string path)
    {
        _outputRootChoice = path;
        OnPropertyChanged(nameof(OutputRootDisplay));
        _ = RecheckTurboDiskAsync(path);
    }

    private async Task EvaluateTurboOnAsync()
    {
        var (kind, path) = await QueryDiskKindAsync(null);
        _ui.Post(() =>
        {
            if (kind == "hdd")
            {
                SetTurboState(false);
                ShowTurboWarn(path.Length > 0 ? path : _outputRoot);
            }
            else
            {
                SetTurboState(true);
                _ = PushTurboConfigAsync(true);
            }
        });
    }

    private Task PushTurboConfigAsync(bool value)
    {
        if (!_client.IsConnected) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try
            {
                await _client.SendAsync(new JsonObject
                {
                    ["type"] = "set_config",
                    ["config"] = new JsonObject { ["turboMode"] = value }
                });
            }
            catch { }
        });
    }

    private void WarnTurboIfHddOnConnect()
    {
        if (_turboWarnedThisSession || !_turboMode) return;
        _turboWarnedThisSession = true;
        _ = RecheckTurboDiskAsync(null);
    }

    private async Task RecheckTurboDiskAsync(string? path)
    {
        if (!_turboMode) return;
        var (kind, got) = await QueryDiskKindAsync(path);
        if (kind == "hdd")
        {
            _ui.Post(() =>
            {
                SetTurboState(false);
                ShowTurboWarn(got.Length > 0 ? got : path ?? _outputRoot);
                _ = PushTurboConfigAsync(false);
            });
        }
    }

    private void ShowTurboWarn(string path)
    {
        TurboWarnText =
            "涡轮模式会让拆帧、去重判决、回填、超分、补帧同时读写临时目录，" +
            "机械硬盘的随机读写能力撑不住这种吞吐，开启后很可能比普通模式更慢。\n\n" +
            $"当前目录：\n{path}\n\n" +
            "该目录位于机械硬盘（HDD）。建议先改到固态硬盘（SSD / NVMe）再开启。";
        TurboWarnVisible = true;
    }

    private async Task<(string kind, string path)> QueryDiskKindAsync(string? path)
    {
        try
        {
            var payload = new JsonObject();
            if (!string.IsNullOrWhiteSpace(path)) payload["path"] = path;
            var r = await _client.RequestAsync("disk_info", payload, 15000);
            if (r.ValueKind == JsonValueKind.Undefined) return ("unknown", "");
            return (Easy4kClient.GetStr(r, "kind") is { Length: > 0 } k ? k : "unknown",
                    Easy4kClient.GetStr(r, "path"));
        }
        catch
        {
            return ("unknown", "");
        }
    }

    // ===================== 推流 =====================

    private void EnterProgress()
    {
        var wantPreview = _isLan || _previewEnabled;
        var wantStats = _isLan || _statsEnabled;
        PreviewEnabled = wantPreview;
        StatsEnabled = wantStats;
        Phase = Phase.Progress;
        PushStream();
    }

    private void LeaveProgress()
    {
        Phase = Phase.Main;
        PushStream(forceOff: true);
    }

    private void RequestStream(bool preview)
    {
        if (_isLan)
        {
            if (preview) PreviewEnabled = true; else StatsEnabled = true;
            PushStream();
        }
        else
        {
            RiskForPreview = preview;
            RiskVisible = true;
        }
    }

    private void PushStream(bool forceOff = false)
    {
        var preview = !forceOff && _previewEnabled;
        var stats = !forceOff && _statsEnabled;
        _ = Task.Run(async () =>
        {
            try
            {
                await _client.RequestAsync("set_stream",
                    new JsonObject { ["preview"] = preview, ["stats"] = stats }, 8000);
            }
            catch { }
        });
    }

    // ===================== 单文件超分 =====================

    private void OpenSfBrowser()
    {
        var (kind, title) = _sfKindIndex switch
        {
            0 => ("image", "选择电脑上的图片"),
            1 => ("audio", "选择电脑上的音频"),
            _ => ("video", "选择电脑上的视频")
        };
        OpenBrowser(BrowsePurpose.SfInput, title, kind, false);
    }

    private void SetSfKind(int kind)
    {
        SfKindIndex = kind;
        PushSfConfig();
    }

    private void PushSfConfig()
    {
        if (!_client.IsConnected) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var cfg = new JsonObject
                {
                    ["kindIndex"] = _sfKindIndex,
                    ["imageScale"] = _sfImageScale,
                    ["imageModel"] = _sfImageModel,
                    ["audioPrecision"] = _sfAudioPrecision
                };
                var r = await _client.RequestAsync("sf_set_config", new JsonObject { ["config"] = cfg }, 15000);
                ApplySfInfo(r);
            }
            catch { }
        });
    }

    private void ApplySfInfo(JsonElement r)
    {
        if (r.ValueKind == JsonValueKind.Undefined) return;
        var info = ProtocolParser.ParseSfInfo(r);
        _ui.Post(() =>
        {
            if (!info.Valid)
            {
                AddLog("error", info.Error.Length > 0 ? info.Error : "单文件不可用");
                return;
            }

            _applyingCaps = true;
            try
            {
                SfKindIndex = info.KindIndex;
                SfInput = info.Input;
                SfImageScale = info.ImageScale;
                SfImageModels.Clear();
                foreach (var m in info.ImageModels) SfImageModels.Add(m);
                SfImageModel = info.ImageModel;
                SfAudioPrecision = info.AudioPrecision;
                SfRunning = info.Running;
                SfStatus = info.Status;
                BuildSfScales();
                BuildSfPrecisions();
            }
            finally { _applyingCaps = false; }

            if (info.Input.Length > 0) AddLog("info", $"已选择单文件：{info.Input}");
            OnPropertyChanged(nameof(SfCanStart));
            OnPropertyChanged(nameof(SfStartButtonEnabled));
        });
    }

    private async Task SfStartAsync()
    {
        if (AnyBusy) return;
        Phase = Phase.Progress;
        SfRunning = true;
        ResultText = "";
        ResultOutput = "";
        DownloadMessage = "";
        DownloadOk = false;
        ProgressPercent = 0;
        ProgressText = "单文件超分准备中…";
        AndroidPlatform.StartKeepAlive("正在单文件超分，完成后会提醒你");
        _completionPending = false;
        _lastNotifyTick = 0;

        try
        {
            var ack = await _client.RequestAsync("sf_start", null, 20000);
            if (ack.ValueKind == JsonValueKind.Undefined)
            {
                SfRunning = false;
                ResultText = "启动指令超时";
                BackToIdleKeepAlive();
            }
        }
        catch (Exception ex)
        {
            SfRunning = false;
            ResultText = $"启动失败：{ex.Message}";
            BackToIdleKeepAlive();
        }
    }

    private async Task SfStopAsync()
    {
        try
        {
            await _client.RequestAsync("sf_stop", null, 8000);
            AddLog("warn", "已发送停止指令");
        }
        catch (Exception ex) { AddLog("error", $"停止失败：{ex.Message}"); }
    }

    // ===================== 成品回传手机 =====================

    private async Task DownloadResultAsync()
    {
        if (_resultOutput.Length == 0)
        {
            DownloadOk = false;
            DownloadMessage = "没有可下载的成品";
            return;
        }
        if (_downloading) return;

        Downloading = true;
        DownloadPercent = 0;
        DownloadMessage = "";
        DownloadOk = false;

        try
        {
            var begin = await _client.RequestAsync("download_begin",
                new JsonObject { ["path"] = _resultOutput }, 20000);
            if (begin.ValueKind == JsonValueKind.Undefined || !Easy4kClient.GetBool(begin, "ok"))
            {
                Downloading = false;
                DownloadMessage = $"下载初始化失败：{(begin.ValueKind == JsonValueKind.Undefined ? "超时" : Easy4kClient.GetStr(begin, "error"))}";
                return;
            }

            var name = Easy4kClient.GetStr(begin, "name");
            if (name.Length == 0) name = "easy4k_output.mp4";
            var size = (long)Easy4kClient.GetDbl(begin, "size");

            await using var sink = AndroidPlatform.OpenDownloadSink(name);
            if (sink is null)
            {
                Downloading = false;
                DownloadMessage = "无法创建手机下载文件";
                return;
            }

            long offset = 0;
            while (true)
            {
                var r = await _client.RequestAsync("download_read", new JsonObject
                {
                    ["offset"] = offset,
                    ["length"] = 256 * 1024
                }, 30000);

                if (r.ValueKind == JsonValueKind.Undefined || !Easy4kClient.GetBool(r, "ok"))
                {
                    Downloading = false;
                    DownloadMessage = $"下载中断：{(r.ValueKind == JsonValueKind.Undefined ? "超时" : Easy4kClient.GetStr(r, "error"))}";
                    try { await _client.RequestAsync("download_finish", null, 5000); } catch { }
                    return;
                }

                var b64 = Easy4kClient.GetStr(r, "data");
                if (b64.Length > 0)
                {
                    var bytes = Convert.FromBase64String(b64);
                    await sink.WriteAsync(bytes);
                    offset += bytes.Length;
                }

                if (size > 0)
                {
                    var pct = (int)Math.Clamp(offset * 100 / size, 0, 100);
                    DownloadPercent = pct;
                    ProgressText = $"正在下载成品到手机… {pct}%";
                }

                if (Easy4kClient.GetBool(r, "eof")) break;
            }

            try { await _client.RequestAsync("download_finish", null, 5000); } catch { }

            Downloading = false;
            DownloadPercent = 100;
            DownloadOk = true;
            DownloadMessage = $"已保存到手机下载目录：{name}";
            AddLog("success", $"成品已下载到手机：{name}");
        }
        catch (Exception ex)
        {
            Downloading = false;
            DownloadMessage = $"下载失败：{ex.Message}";
            AddLog("error", DownloadMessage);
        }
    }

    // ===================== 目录浏览 =====================

    private void OpenBrowser(BrowsePurpose purpose, string title, string kind, bool folderMode)
    {
        _browsePurpose = purpose;
        _browseKind = kind;
        BrowseTitle = title;
        BrowseFolderMode = folderMode;
        BrowseVisible = true;
        BrowseError = "";
        BrowseEntries.Clear();
        BrowseCurrentPath = "";
        _ = LoadDirAsync(null);
    }

    private string _browseKind = "video";

    private async Task LoadDirAsync(string? path)
    {
        if (!_client.IsConnected) return;
        BrowseLoading = true;
        BrowseError = "";
        try
        {
            var payload = new JsonObject { ["kind"] = _browseKind };
            if (!string.IsNullOrWhiteSpace(path)) payload["path"] = path;
            var r = await _client.RequestAsync("list_dir", payload, 15000);
            var listing = ProtocolParser.ParseDirListing(r);

            BrowseEntries.Clear();
            if (!string.IsNullOrEmpty(listing.Path))
            {
                var parentPath = ProtocolParser.ParentOf(listing.Path);
                BrowseEntries.Add(new DirEntry
                {
                    Name = ".. 返回上级",
                    IsDir = true,
                    IsParent = true,
                    FullPath = parentPath ?? ""
                });
            }

            foreach (var e in listing.Entries)
            {
                // 选目录时只列目录，避免把某个文件误当成目录；选文件时目录也要能点进去
                if (BrowseFolderMode && !e.IsDir) continue;
                BrowseEntries.Add(e);
            }

            BrowseCurrentPath = listing.Path;
            BrowseError = listing.Error;
        }
        catch (Exception ex)
        {
            BrowseError = $"读取目录失败：{ex.Message}";
        }
        finally
        {
            BrowseLoading = false;
        }
    }

    private async Task BrowseEntryAsync(DirEntry? entry)
    {
        if (entry is null) return;
        if (entry.IsParent)
        {
            await LoadDirAsync(entry.FullPath.Length > 0 ? entry.FullPath : null);
            return;
        }
        if (entry.IsDir)
        {
            await LoadDirAsync(entry.FullPath);
            return;
        }
        PickPath(entry.FullPath);
    }

    private void PickPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        BrowseVisible = false;
        switch (_browsePurpose)
        {
            case BrowsePurpose.InputVideo:
                _ = SelectComputerFileAsync(path);
                break;
            case BrowsePurpose.OutputDir:
                SetOutputRoot(path);
                break;
            case BrowsePurpose.SfInput:
                _ = SfSelectInputAsync(path);
                break;
            case BrowsePurpose.TempDirPick:
                _ = SetTempRootAsync(path);
                break;
            case BrowsePurpose.TempDirRetry:
                _ = SetTempRootAndRetryAsync(path);
                break;
            case BrowsePurpose.FramesDir:
                _ = SetFramesDirAsync(path);
                break;
        }
    }

    /// <summary>导入电脑上的 PNG 帧文件夹（跳过拆帧），并同步远端返回的外部帧目录。</summary>
    private async Task SetFramesDirAsync(string path)
    {
        try
        {
            var r = await _client.RequestAsync("set_config",
                new JsonObject { ["config"] = new JsonObject { ["framesDir"] = path } }, 120000);
            var got = Easy4kClient.GetStr(Easy4kClient.Obj(r, "paths"), "externalFramesDir");
            ExternalFramesDir = got.Length > 0 ? got : path;
            AddLog("success", $"已导入帧文件夹：{ExternalFramesDir}");
        }
        catch (Exception ex)
        {
            AddLog("error", $"导入帧文件夹失败：{ex.Message}");
        }
    }

    private async Task ClearFramesDirAsync()
    {
        try
        {
            await _client.RequestAsync("set_config",
                new JsonObject { ["config"] = new JsonObject { ["framesDir"] = "" } }, 20000);
            ExternalFramesDir = "";
            AddLog("info", "已清除帧文件夹，恢复先拆帧");
        }
        catch (Exception ex)
        {
            AddLog("error", $"清除帧文件夹失败：{ex.Message}");
        }
    }

    private async Task SfSelectInputAsync(string path)
    {
        try
        {
            var desiredKind = _sfKindIndex;
            var r = await _client.RequestAsync("sf_set_input", new JsonObject { ["path"] = path }, 30000);
            ApplySfInfo(r);

            // 服务端按扩展名把「非图片」一律当成音频；本地选的是「视频音频超分 / 视频 HDR」时，
            // 要把类型再推回去，否则会跑成单音频超分。
            if (desiredKind >= 2 && _sfKindIndex != desiredKind)
            {
                SfKindIndex = desiredKind;
                PushSfConfig();
            }
        }
        catch (Exception ex)
        {
            AddLog("error", $"读取单文件信息失败：{ex.Message}");
        }
    }

    private async Task CreateFolderAsync()
    {
        var name = _newFolderName.Trim();
        if (name.Length == 0) return;
        try
        {
            var payload = new JsonObject { ["name"] = name };
            if (_browseCurrentPath.Length > 0) payload["parent"] = _browseCurrentPath;
            var r = await _client.RequestAsync("mkdir", payload, 15000);
            if (r.ValueKind == JsonValueKind.Undefined)
            {
                NewFolderError = "创建超时，请重试";
                return;
            }
            if (Easy4kClient.GetBool(r, "ok"))
            {
                NewFolderVisible = false;
                await LoadDirAsync(_browseCurrentPath);
            }
            else
            {
                var err = Easy4kClient.GetStr(r, "error");
                NewFolderError = err.Length > 0 ? err : "创建失败";
            }
        }
        catch (Exception ex)
        {
            NewFolderError = $"创建失败：{ex.Message}";
        }
    }

    // ===================== 消息处理 =====================

    private void OnPreview(byte[] bytes)
    {
        _ui.Post(() =>
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                PreviewImage = new Bitmap(ms);
            }
            catch { }
        });
    }

    private void OnMessage(JsonElement root)
    {
        var clone = root.Clone();
        // 消息处理跑在 UI 线程上，任何未捕获异常都会直接让整个进程闪退；
        // 这里兜底记录日志，保证服务端少字段 / 新字段不会把 App 打死。
        _ui.Post(() =>
        {
            try
            {
                HandleMessage(clone);
            }
            catch (Exception ex)
            {
                AddLog("error", $"处理消息失败：{ex.Message}");
                CrashLog.Write(ex);
            }
        });
    }

    private void HandleMessage(JsonElement o)
    {
        var type = Easy4kClient.GetStr(o, "type");
        switch (type)
        {
            case "progress":
            {
                var pct = Easy4kClient.GetDbl(o, "percent");
                var detail = Easy4kClient.GetStr(o, "detail");
                if (detail.Length == 0) detail = Easy4kClient.GetStr(o, "stageText");
                ProgressPercent = pct;
                ProgressText = detail;
                NotifyProgress(detail, pct);
                break;
            }

            case "state":
            {
                var st = Easy4kClient.GetStr(o, "state");
                var detail = Easy4kClient.GetStr(o, "detail");
                switch (st)
                {
                    case "processing":
                        Processing = true;
                        Paused = false;
                        if (detail.Length > 0) ProgressText = detail;
                        break;
                    case "done":
                        Processing = false;
                        Paused = false;
                        ProgressPercent = 100;
                        ProgressText = "处理完成";
                        if (ResultText.Length == 0) ResultText = "处理完成";
                        ResultOk = true;
                        NotifyFinished(true);
                        break;
                    case "failed":
                        Processing = false;
                        Paused = false;
                        ResultText = "处理失败，请查看日志";
                        ResultOk = false;
                        NotifyFinished(false);
                        break;
                    case "stopped":
                        Processing = false;
                        Paused = false;
                        ResultText = "已停止";
                        ResultOk = false;
                        BackToIdleKeepAlive();
                        break;
                    case "idle":
                        Processing = false;
                        Paused = false;
                        break;
                }
                break;
            }

            case "result":
            {
                var ok = Easy4kClient.GetBool(o, "success");
                var output = Easy4kClient.GetStr(o, "output");
                var steps = Easy4kClient.GetStr(o, "steps");
                _lastOutputPath = output;
                Processing = false;
                Paused = false;
                ResultOk = ok;
                if (ok)
                {
                    ResultOutput = output;
                    _resultSourceKey = _inputKey;
                    ResultOld = false;
                    ResultText = $"处理完成\n输出：{output}\n步骤：{steps}";
                    PersistResult(output, _inputKey);
                    OnPropertyChanged(nameof(ShowResultCard));
                }
                else
                {
                    ResultText = "处理未产出成品";
                }
                AddLog(ok ? "success" : "error", ok ? $"处理完成：{output}" : "处理未产出成品");
                break;
            }

            case "sf_info":
                ApplySfInfo(o);
                break;

            case "sf_state":
            {
                var running = Easy4kClient.GetBool(o, "running");
                var status = Easy4kClient.GetStr(o, "status");
                var percent = Easy4kClient.GetDbl(o, "percent");
                var detail = Easy4kClient.GetStr(o, "detail");
                var output = Easy4kClient.GetStr(o, "output");
                SfRunning = running;
                SfStatus = status;
                ProgressPercent = percent;
                ProgressText = detail.Length > 0 ? detail : status;
                if (output.Length > 0)
                {
                    ResultOutput = output;
                    _resultSourceKey = _inputKey;
                    ResultOld = false;
                    PersistResult(output, _inputKey);
                    OnPropertyChanged(nameof(ShowResultCard));
                }
                if (!running)
                {
                    BackToIdleKeepAlive();
                    if (status.Length > 0)
                    {
                        var ok = status.StartsWith("完成", StringComparison.Ordinal);
                        ResultOk = ok;
                        ResultText = status;
                        NotifyFinished(ok);
                    }
                }
                break;
            }

            case "error":
            {
                var m = Easy4kClient.GetStr(o, "message");
                if (m.Length > 0)
                {
                    ResultText = m;
                    ResultOk = false;
                    AddLog("error", m);
                }
                break;
            }

            case "notice":
                AddLog("info", Easy4kClient.GetStr(o, "text"));
                break;

            case "config":
                HandleConfigPush(o);
                break;

            case "turbo_disabled":
            {
                var reason = Easy4kClient.GetStr(o, "reason");
                SetTurboState(false);
                if (reason.Length > 0) ShowTurboWarn(reason);
                break;
            }

            case "temp_cache":
                if (Easy4kClient.GetStr(o, "status") == "mismatch")
                {
                    TempCacheSource = Easy4kClient.GetStr(o, "source");
                    if (TempCacheSource.Length == 0) TempCacheSource = "其他视频";
                    TempCachePath = Easy4kClient.GetStr(o, "tempRoot");
                    TempCacheVisible = true;
                    AddLog("warn", "临时目录里有旧缓存，等待你选择更换文件夹或删除旧缓存");
                }
                break;

            case "log":
                AddLog(Easy4kClient.GetStr(o, "level"), Easy4kClient.GetStr(o, "text"));
                break;

            case "stats":
                Cpu = Easy4kClient.GetDbl(o, "cpu", _cpu);
                Gpu = Easy4kClient.GetDbl(o, "gpu", _gpu);
                break;

            case "paused":
                Paused = Easy4kClient.GetBool(o, "paused", _paused);
                break;

            case "stream":
                PreviewEnabled = Easy4kClient.GetBool(o, "preview");
                StatsEnabled = Easy4kClient.GetBool(o, "stats");
                break;
        }
    }

    private void HandleConfigPush(JsonElement o)
    {
        var opt = Easy4kClient.Obj(o, "options");
        var cfg = Easy4kClient.Obj(o, "config");
        var st = Easy4kClient.Obj(o, "state");
        var lr = Easy4kClient.Obj(o, "lastResult");

        if (opt.ValueKind == JsonValueKind.Object)
        {
            IfMultiplierLocked = Easy4kClient.GetBool(opt, "ifMultiplierLocked", _ifMultiplierLocked);
            HasSubtitles = Easy4kClient.GetBool(opt, "hasSubtitles", _hasSubtitles);
            if (opt.TryGetProperty("sdrToHdr", out _)) SdrToHdr = Easy4kClient.GetBool(opt, "sdrToHdr", _sdrToHdr);
            if (opt.TryGetProperty("keepSubtitles", out _)) KeepSubtitles = Easy4kClient.GetBool(opt, "keepSubtitles", _keepSubtitles);

            // 高级页选项：服务端会回夹紧后的真实值，这里同步（避免来回推送）
            if (opt.TryGetProperty("sceneGuard", out _))
            {
                // 加个遮罩，防止同步时又触发 set_config
                var was = _applyingCaps;
                _applyingCaps = true;
                try
                {
                    SceneGuard = Easy4kClient.GetBool(opt, "sceneGuard", _sceneGuard);
                    TurboSegments = Math.Clamp(Easy4kClient.GetInt(opt, "turboSegments", _turboSegments), 1, 5);
                    SrDecodeText = Easy4kClient.GetInt(opt, "srDecodeThreads", 1).ToString();
                    SrEncodeText = Easy4kClient.GetInt(opt, "srEncodeThreads", 2).ToString();
                    IfDecodeText = Easy4kClient.GetInt(opt, "ifDecodeThreads", 1).ToString();
                    IfEncodeText = Easy4kClient.GetInt(opt, "ifEncodeThreads", 2).ToString();
                    PipelineGpuIndex = Easy4kClient.GetInt(opt, "pipelineGpuIndex", _pipelineGpuIndex);
                    HdrSaturationText = Easy4kClient.GetInt(opt, "hdrSaturation", 200).ToString();
                    HdrContrastText = Easy4kClient.GetInt(opt, "hdrContrast", 200).ToString();
                    HdrAiStrengthText = Easy4kClient.GetInt(opt, "hdrAiStrength", 100).ToString();
                    HdrAiPathSet = Easy4kClient.GetBool(opt, "hdrAiPath");
                    SkipStartupSelfTest = Easy4kClient.GetBool(opt, "skipStartupSelfTest");
                    UseSafeFrameRate = Easy4kClient.GetBool(opt, "useSafeFrameRate");
                    LowerQualityForVram = Easy4kClient.GetBool(opt, "lowerQualityForVram");
                    UseGpuAcceleration = Easy4kClient.GetBool(opt, "useGpuAcceleration", true);
                    UseCpuProcessing = Easy4kClient.GetBool(opt, "useCpuProcessing");
                    TryOldFfmpegOnCrash = Easy4kClient.GetBool(opt, "tryOldFfmpegOnCrash");
                    NoReport = Easy4kClient.GetBool(opt, "noReport");
                    ReportAutoOpen = Easy4kClient.GetBool(opt, "reportAutoOpen", true);
                    ReportDir = Easy4kClient.GetStr(opt, "reportDir") is { Length: > 0 } rd ? rd : _reportDir;
                    PreventAutoSleep = Easy4kClient.GetBool(opt, "preventAutoSleep");
                }
                finally { _applyingCaps = was; }
            }
        }

        var paths = Easy4kClient.Obj(o, "paths");
        if (paths.ValueKind == JsonValueKind.Object && paths.TryGetProperty("externalFramesDir", out _))
            ExternalFramesDir = Easy4kClient.GetStr(paths, "externalFramesDir");

        if (cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty("ifMultiplier", out _))
        {
            IfMultiplier = Math.Clamp(Easy4kClient.GetInt(cfg, "ifMultiplier", _ifMultiplier), 1, 16);
            BuildMultiplierChoices();
        }

        if (st.ValueKind == JsonValueKind.Object)
        {
            Processing = Easy4kClient.GetBool(st, "isProcessing", _processing);
            Paused = Easy4kClient.GetBool(st, "isPaused", _paused);
        }

        var ok = Easy4kClient.GetBool(lr, "success");
        var outPath = Easy4kClient.GetStr(lr, "output");
        if (ok && outPath.Length > 0 && (_resultOutput.Length == 0 || _resultOld))
        {
            ResultOutput = outPath;
            ResultOk = true;
            ResultText = $"处理完成\n输出：{outPath}\n步骤：{Easy4kClient.GetStr(lr, "steps")}";
            _resultSourceKey = _inputKey;
            ResultOld = false;
            OnPropertyChanged(nameof(ShowResultCard));
        }
    }

    private void NotifyProgress(string detail, double percent)
    {
        var now = System.Environment.TickCount64;
        if (now - _lastNotifyTick < 1000) return;
        _lastNotifyTick = now;
        // 与界面保持同一套小数位，别一个 71% 一个 71.4%
        var text = detail.Length == 0 ? $"正在处理 {percent:0.0}%" : $"正在处理 {percent:0.0}% · {detail}";
        AndroidPlatform.StartKeepAlive(text);
    }

    private void NotifyFinished(bool ok)
    {
        var name = Path.GetFileName(_lastOutputPath);
        var text = !ok ? "处理未完成，打开 App 可查看日志"
            : name.Length > 0 ? $"已生成：{name}"
            : "处理已完成，打开 App 查看结果";
        AndroidPlatform.NotifyDone(ok ? "HikariAPP 处理完成" : "HikariAPP 处理失败", text);
        _completionPending = true;
    }

    private void PersistResult(string path, string key)
    {
        try
        {
            var e = _prefs?.Edit();
            if (e is null) return;
            e.PutString("result_output", path);
            e.PutString("result_source_key", key);
            e.Apply();
        }
        catch { }
    }

    public ICommand SfKindImageCommand { get; private set; } = null!;
    public ICommand SfKindAudioCommand { get; private set; } = null!;
    public ICommand SfKindVideoAudioCommand { get; private set; } = null!;
    public ICommand SfKindVideoHdrCommand { get; private set; } = null!;

    // 供界面层调用的选项入口（ToggleSwitch / ChoiceItem 用）
    /// <summary>Android 返回键：优先关弹窗 / 从进度页回设置页；返回 false 表示交给系统退出。</summary>
    public bool HandleBack()
    {
        if (NewFolderVisible) { NewFolderVisible = false; return true; }
        if (BrowseVisible) { BrowseVisible = false; return true; }
        if (RiskVisible) { RiskVisible = false; return true; }
        if (CleanConfirmVisible) { CleanStep = 0; _cleanRetry = false; return true; }
        if (TempCacheVisible) { TempCacheVisible = false; ResultText = "已取消启动（临时目录里有旧缓存）"; return true; }
        if (TurboWarnVisible) { TurboWarnVisible = false; TurboWarnText = ""; return true; }
        if (_phase == Phase.Progress) { LeaveProgress(); return true; }
        return false;
    }

    public void SetSfKindFromUi(int kind) => SetSfKind(kind);
    public void SetSfScaleFromUi(int scale)
    {
        SfImageScale = scale;
        BuildSfScales();
        PushSfConfig();
    }
    public void SetHwQualityModeFromUi(string mode)
    {
        HwQualityMode = mode is "" or "cq" or "qp" ? mode : "";
        BuildHwModes();
    }
    public void SetDedupModeFromUi(string mode)
    {
        DedupMode = mode == "uhd" ? "uhd" : "performance";
        BuildDedupModes();
    }
}
