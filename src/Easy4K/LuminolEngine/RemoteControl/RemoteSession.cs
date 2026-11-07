using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Easy4K.Models;
using Easy4K.ViewModels;
using Microsoft.UI.Dispatching;

namespace Easy4K.LuminolEngine.RemoteControl;

/// <summary>本机作为客户端遥控另一台 Easy4K 的会话。
///
/// 设计目标：**不在软件里另起一套界面**，而是把远端状态镜像进本机 MainViewModel，
/// 让「主页 / 高级 / 进行中」三个页面直接操作远端 —— 选视频、配模型、启停、看进度日志。
/// 具体做法：
///  · 连接后把远端 capabilities/config 灌进 VM（页面照常显示）；
///  · VM 上相关选项变化 → 自动下发 set_config 到远端；
///  · 远端 progress/state/result/log → 写回 VM 的进度与日志。
/// 会话是进程内单例（<see cref="Current"/>），未连接时所有分流逻辑都不生效，本机处理完全不变。</summary>
public sealed class RemoteSession
{
    /// <summary>当前活动会话（未连接为 null）</summary>
    public static RemoteSession? Current { get; private set; }

    /// <summary>是否正在遥控远端（用于抑制本机弹窗、分流操作）</summary>
    public static bool IsActive => Current is { IsConnected: true };

    /// <summary>远端已进入处理状态（客户端据此切到「进行中」页；不依赖 IsProcessing 属性变化，
    /// 因为重连/残留状态下该值可能本来就是 true，属性不会触发变更通知）。</summary>
    public static event Action? EnteredProcessing;

    /// <summary>会话结束（对端关闭、网络中断、或本机主动断开）。参数为原因文字。</summary>
    public static event Action<string>? SessionEnded;

    private readonly MainViewModel _vm;
    private readonly DispatcherQueue _ui;
    private readonly RemoteControlClient _client = new();

    /// <summary>远端→VM 的写入期间为 true，避免把镜像回来的值又当成用户改动推回远端</summary>
    private bool _applying;

    private bool _paused;
    private long _lastFrame = -1;
    /// <summary>远端临时目录旧缓存触发的自动清理次数（防死循环，最多 2 次）</summary>
    private int _autoCleanAttempt;

    /// <summary>连上之前本机的输出/临时目录 + 输入视频：断开时要把页面换回它们，
    /// 否则会一直显示对方电脑的路径与视频信息</summary>
    private string _localOutputRoot = "";
    private string _localTempRoot = "";
    private string _localInputVideo = "";
    private VideoInfo? _localVideo;

    public bool IsConnected => _client.IsConnected;
    public string Host { get; private set; } = "";
    public int Port { get; private set; } = RemoteControlServer.Port;
    public string Address => $"{Host}:{Port}";

    /// <summary>连接过程中的步骤文字（供连接页显示「正在做什么」）</summary>
    public event Action<string>? Step;

    public RemoteSession(MainViewModel vm, DispatcherQueue ui)
    {
        _vm = vm;
        _ui = ui;
    }

    // ===================== 连接 / 断开 =====================

    public async Task<JsonElement> ConnectAsync(string host, int port, Func<Task<string?>>? requestKey = null)
    {
        _client.Step += s => Step?.Invoke(s);
        var caps = await _client.ConnectAsync(host, port, Environment.MachineName, requestKey);
        Host = host;
        Port = port;
        Current = this;

        _client.ProgressReceived += OnProgress;
        _client.LogReceived += OnLog;
        _client.StateReceived += OnState;
        _client.ResultReceived += OnResult;
        _client.NoticeReceived += OnNotice;
        _client.ErrorReceived += OnError;
        _client.FailureReportReceived += OnFailureReport;
        _client.TempCacheReceived += OnTempCache;
        _client.Disconnected += OnDisconnected;
        _vm.PropertyChanged += OnVmPropertyChanged;

        // 记下本机状态，断开时换回来（远端路径/视频只在本会话期间显示）
        _localOutputRoot = _vm.OutputRoot;
        _localTempRoot = _vm.TempRoot;
        _localInputVideo = _vm.InputVideo;
        _localVideo = _vm.Video;

        ApplyOptions(caps);
        // 内外网区分（与手机端规则一致）：
        //  · 内网连接：占用每秒一条小消息，默认开；图片预览默认关；
        //  · 外网连接：预览与占用都默认关（持续上传有被运营商判定为 PCDN 的风险）。
        var lan = IsLanHost(host);
        await _client.SetStreamAsync(preview: false, stats: lan);
        _vm.Logger.Info(lan
            ? "内网连接：已开启 CPU/GPU 占用推流（图片预览默认关闭）"
            : "外网连接：已关闭图片预览与 CPU/GPU 占用推流，避免持续上传被判定为 PCDN");
        // 首次下发配置：顺带把远端的模型列表拉回来填进页面
        await PushConfigAsync(null);

        _vm.Logger.Info($"已接管远端 Easy4K {Address}（v{RemoteControlClient.GetStr(caps, "version")}）");
        return caps;
    }

    /// <summary>对端是否为内网地址：回环 / 10.0.0.0/8 / 172.16.0.0/12 / 192.168.0.0/16 /
    /// 169.254.0.0/16 / 100.64.0.0/10(CGNAT) / IPv6 链路本地与 fc00::/7。
    /// 域名与其它公网 IP 一律按外网处理（保守：外网不推预览与占用）。</summary>
    private static bool IsLanHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        var h = host.Trim().Trim('[', ']');
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (h.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(h, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
        return false;
    }

    public async Task DisconnectAsync()
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _client.ProgressReceived -= OnProgress;
        _client.LogReceived -= OnLog;
        _client.StateReceived -= OnState;
        _client.ResultReceived -= OnResult;
        _client.NoticeReceived -= OnNotice;
        _client.ErrorReceived -= OnError;
        _client.FailureReportReceived -= OnFailureReport;
        _client.TempCacheReceived -= OnTempCache;
        _client.Disconnected -= OnDisconnected;

        await _client.DisconnectAsync();
        if (Current == this) Current = null;
        // 断开后页面回到本机：显卡、路径、输入视频都要换回本机，否则一直显示对方电脑的
        Run(() =>
        {
            _vm.InputVideo = _localInputVideo;
            _vm.Video = _localVideo;
            if (!string.IsNullOrEmpty(_localOutputRoot)) _vm.OutputRoot = _localOutputRoot;
            if (!string.IsNullOrEmpty(_localTempRoot)) _vm.TempRoot = _localTempRoot;
            // 放最后：ApplyGpu 内部会重算依赖显卡与视频的提示条（预计临时文件占用等）
            _vm.ApplyGpu(null);
        });
    }

    private void OnDisconnected(string reason)
    {
        // 对端关闭 / 网络中断：本机必须立刻回到「未连接」状态，
        // 否则横幅一直显示已连接、主页仍显示对方路径，操作全部打在空气里。
        Run(() =>
        {
            _vm.Logger.Warn($"远端连接已断开：{reason}");
            _vm.IsProcessing = false;
            _vm.IsPaused = false;
            _vm.Stage = ProcessStage.Idle;
            _vm.ProgressDetail = "远端连接已断开";
            _vm.InputVideo = _localInputVideo;
            _vm.Video = _localVideo;
            if (!string.IsNullOrEmpty(_localOutputRoot)) _vm.OutputRoot = _localOutputRoot;
            if (!string.IsNullOrEmpty(_localTempRoot)) _vm.TempRoot = _localTempRoot;
            _vm.ApplyGpu(null);
        });
        if (Current == this) Current = null;
        SessionEnded?.Invoke(reason);
    }

    // ===================== 能力 / 配置 =====================

    /// <summary>把远端能力清单里的勾选项与输出目录灌进 VM（页面据此显示远端配置）。</summary>
    private void ApplyOptions(JsonElement caps)
    {
        var opt = RemoteControlClient.Sub(caps, "options");
        var paths = RemoteControlClient.Sub(caps, "paths");
        var gpu = RemoteControlClient.Sub(caps, "gpu");
        Run(() =>
        {
            _applying = true;
            try
            {
                // 显卡：遥控时显存状态条、显卡检测、模型可用性都按**远端**显卡算，
                // 否则本机顶配卡会让用户误以为远端小显存卡也能跑 8G 起的补帧模型。
                _vm.ApplyGpu(gpu.ValueKind == JsonValueKind.Object
                    ? new GpuInfo
                    {
                        Name = RemoteControlClient.GetStr(gpu, "name"),
                        VramMB = RemoteControlClient.GetInt(gpu, "vramMB"),
                        IsNvidia = RemoteControlClient.GetBool(gpu, "isNvidia"),
                        IsRtx = RemoteControlClient.GetBool(gpu, "isRtx"),
                        Series = RemoteControlClient.GetInt(gpu, "series"),
                        DriverVersion = RemoteControlClient.GetStr(gpu, "driver"),
                    }
                    : null);
                _vm.SuperResolution = RemoteControlClient.GetBool(opt, "superResolution");
                _vm.Interpolation = RemoteControlClient.GetBool(opt, "interpolation");
                _vm.MergeVideo = RemoteControlClient.GetBool(opt, "mergeVideo");
                _vm.MergeAudio = RemoteControlClient.GetBool(opt, "mergeAudio");
                _vm.SplitFrames = RemoteControlClient.GetBool(opt, "splitFrames");
                _vm.DedupEnabled = RemoteControlClient.GetBool(opt, "dedup");
                _vm.AudioSrEnabled = RemoteControlClient.GetBool(opt, "audioSr");
                _vm.TurboMode = RemoteControlClient.GetBool(opt, "turboMode");
                // 新增选项也要反向同步：否则连上后本机高级页显示的是自己的旧值，而不是远端的实际设置
                _vm.KeepSubtitles = RemoteControlClient.GetBool(opt, "keepSubtitles");
                _vm.Crf = RemoteControlClient.GetInt(opt, "crf");
                _vm.HwQuality = RemoteControlClient.GetInt(opt, "hwQuality");
                var remoteHwMode = RemoteControlClient.GetStr(opt, "hwQualityMode");
                if (remoteHwMode is "" or "cq" or "qp") _vm.HwQualityMode = remoteHwMode;
                var rDedupMode = RemoteControlClient.GetStr(opt, "dedupMode");
                if (rDedupMode is "performance" or "uhd") _vm.DedupMode = rDedupMode;
                var rAudioPrec = RemoteControlClient.GetStr(opt, "audioSrPrecision");
                if (rAudioPrec is "fp16" or "fp32") _vm.AudioSrPrecision = rAudioPrec;
                var outRoot = RemoteControlClient.GetStr(paths, "outputRoot");
                if (outRoot.Length > 0) _vm.OutputRoot = outRoot;
            }
            finally { _applying = false; }
        });
    }

    /// <summary>把 VM 上当前的选项下发到远端；返回的 config 用来校正模型列表。</summary>
    public async Task PushConfigAsync(object? payload)
    {
        if (!IsConnected) return;
        try
        {
            var res = await _client.RequestAsync("set_config", payload, 15000);
            ApplyConfig(res);
        }
        catch (Exception ex)
        {
            _vm.Logger.Warn($"远端配置下发失败：{ex.Message}");
        }
    }

    private void ApplyConfig(JsonElement res)
    {
        var opt = RemoteControlClient.Sub(res, "options");
        var cfg = RemoteControlClient.Sub(res, "config");
        var st = RemoteControlClient.Sub(res, "state");
        Run(() =>
        {
            _applying = true;
            try
            {
                _vm.SuperResolution = RemoteControlClient.GetBool(opt, "superResolution");
                _vm.Interpolation = RemoteControlClient.GetBool(opt, "interpolation");
                _vm.MergeVideo = RemoteControlClient.GetBool(opt, "mergeVideo");
                _vm.MergeAudio = RemoteControlClient.GetBool(opt, "mergeAudio");
                _vm.SplitFrames = RemoteControlClient.GetBool(opt, "splitFrames");
                _vm.DedupEnabled = RemoteControlClient.GetBool(opt, "dedup");
                _vm.AudioSrEnabled = RemoteControlClient.GetBool(opt, "audioSr");
                _vm.TurboMode = RemoteControlClient.GetBool(opt, "turboMode");
                // 新增选项也要反向同步：否则连上后本机高级页显示的是自己的旧值，而不是远端的实际设置
                _vm.KeepSubtitles = RemoteControlClient.GetBool(opt, "keepSubtitles");
                _vm.Crf = RemoteControlClient.GetInt(opt, "crf");
                _vm.HwQuality = RemoteControlClient.GetInt(opt, "hwQuality");
                var remoteHwMode = RemoteControlClient.GetStr(opt, "hwQualityMode");
                if (remoteHwMode is "" or "cq" or "qp") _vm.HwQualityMode = remoteHwMode;
                var rDedupMode = RemoteControlClient.GetStr(opt, "dedupMode");
                if (rDedupMode is "performance" or "uhd") _vm.DedupMode = rDedupMode;
                var rAudioPrec = RemoteControlClient.GetStr(opt, "audioSrPrecision");
                if (rAudioPrec is "fp16" or "fp32") _vm.AudioSrPrecision = rAudioPrec;

                _vm.SrScale = RemoteControlClient.GetInt(cfg, "srScale");
                _vm.SrEngineIndex = RemoteControlClient.GetInt(cfg, "srEngineIndex");
                FillModels(_vm.SrModels, cfg, "srModels");
                var sm = RemoteControlClient.GetStr(cfg, "srModel");
                if (sm.Length > 0) _vm.SrModel = sm;

                _vm.IfEngineIndex = RemoteControlClient.GetInt(cfg, "ifEngineIndex");
                _vm.IfModel = RemoteControlClient.GetStr(cfg, "ifModel");
                _vm.IfMultiplier = RemoteControlClient.GetInt(cfg, "ifMultiplier");
                _vm.TargetFps = RemoteControlClient.GetDbl(cfg, "targetFps");

                CanStart = RemoteControlClient.GetBool(st, "canStart");
                CanStartReason = RemoteControlClient.GetStr(st, "canStartReason");

                // 路径：远端当前实际在用的输入/输出/临时/帧目录，页面照它显示（否则会显示本机的旧路径）
                var resPaths = RemoteControlClient.Sub(res, "paths");
                var outRoot = RemoteControlClient.GetStr(resPaths, "outputRoot");
                if (outRoot.Length > 0) _vm.OutputRoot = outRoot;
                // 远端当前输入视频：输入框要跟着显示对方路径，否则界面看起来像「没选视频」
                _vm.InputVideo = RemoteControlClient.GetStr(resPaths, "input");
                var tRoot = RemoteControlClient.GetStr(resPaths, "tempRoot");
                if (tRoot.Length > 0) _vm.TempRoot = tRoot;
                var extFrames = RemoteControlClient.GetStr(resPaths, "externalFramesDir");
                _vm.SetExternalFramesFromRemote(extFrames);

                // 远端探测到的视频信息（帧文件夹模式下就是刚下发的手工参数），照它显示分辨率/帧率/时长
                if (res.TryGetProperty("video", out var vEl) && vEl.ValueKind == JsonValueKind.Object)
                    _vm.Video = ToVideoInfo(vEl);
            }
            finally { _applying = false; }
        });
    }

    private static void FillModels(System.Collections.ObjectModel.ObservableCollection<string> target, JsonElement cfg, string key)
    {
        if (cfg.ValueKind != JsonValueKind.Object || !cfg.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        target.Clear();
        foreach (var m in arr.EnumerateArray())
            if (m.ValueKind == JsonValueKind.String) target.Add(m.GetString() ?? "");
    }

    /// <summary>远端给出的「能否开始 / 原因」（主页按钮据此启用与提示）</summary>
    public bool CanStart { get; private set; }
    public string CanStartReason { get; private set; } = "";

    // ===================== VM 变化 → 远端 =====================

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_applying || !IsConnected) return;
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SuperResolution):
            case nameof(MainViewModel.Interpolation):
            case nameof(MainViewModel.MergeVideo):
            case nameof(MainViewModel.MergeAudio):
            case nameof(MainViewModel.SplitFrames):
            case nameof(MainViewModel.DedupEnabled):
            case nameof(MainViewModel.AudioSrEnabled):
            case nameof(MainViewModel.TurboMode):
            case nameof(MainViewModel.KeepSubtitles):
                _ = PushConfigAsync(new
                {
                    superResolution = _vm.SuperResolution,
                    interpolation = _vm.Interpolation,
                    mergeVideo = _vm.MergeVideo,
                    mergeAudio = _vm.MergeAudio,
                    splitFrames = _vm.SplitFrames,
                    dedup = _vm.DedupEnabled,
                    audioSr = _vm.AudioSrEnabled,
                    turboMode = _vm.TurboMode,
                    keepSubtitles = _vm.KeepSubtitles
                });
                break;

            // 编码质量（高级页）：CPU CRF 与硬件编码 CQ/QP。三项一起下发，
            // 避免只改一项时另外两项被本机旧值覆盖远端设置。
            case nameof(MainViewModel.Crf):
            case nameof(MainViewModel.HwQualityMode):
            case nameof(MainViewModel.HwQuality):
                _ = PushConfigAsync(new
                {
                    crf = _vm.Crf,
                    hwQualityMode = _vm.HwQualityMode,
                    hwQuality = _vm.HwQuality
                });
                break;

            // 档位类：去重「性能/完美」与音频超分「FP16/FP32」，都是单选，改动即下发
            case nameof(MainViewModel.DedupMode):
                _ = PushConfigAsync(new { dedupMode = _vm.DedupMode });
                break;
            case nameof(MainViewModel.AudioSrPrecision):
                _ = PushConfigAsync(new { audioSrPrecision = _vm.AudioSrPrecision });
                break;

            case nameof(MainViewModel.SrEngineIndex):
                _ = PushConfigAsync(new { srEngineIndex = _vm.SrEngineIndex });
                break;
            case nameof(MainViewModel.SrScale):
                _ = PushConfigAsync(new { srScale = _vm.SrScale });
                break;
            case nameof(MainViewModel.SrModel):
                if (!string.IsNullOrEmpty(_vm.SrModel)) _ = PushConfigAsync(new { srModel = _vm.SrModel });
                break;
            case nameof(MainViewModel.IfEngineIndex):
                _ = PushConfigAsync(new { ifEngineIndex = _vm.IfEngineIndex });
                break;
            case nameof(MainViewModel.IfModel):
                if (!string.IsNullOrEmpty(_vm.IfModel)) _ = PushConfigAsync(new { ifModel = _vm.IfModel });
                break;
            case nameof(MainViewModel.IfMultiplier):
                _ = PushConfigAsync(new { ifMultiplier = _vm.IfMultiplier });
                break;
            case nameof(MainViewModel.TargetFps):
                _ = PushConfigAsync(new { targetFps = _vm.TargetFps });
                break;
            case nameof(MainViewModel.OutputRoot):
                if (!string.IsNullOrWhiteSpace(_vm.OutputRoot))
                    _ = PushConfigAsync(new { outputRoot = _vm.OutputRoot });
                break;
            case nameof(MainViewModel.TempRoot):
                if (!string.IsNullOrWhiteSpace(_vm.TempRoot))
                    _ = PushConfigAsync(new { tempRoot = _vm.TempRoot });
                break;
            case nameof(MainViewModel.ExternalFramesDir):
                // 清除帧文件夹（空串也是有意义的指令，不能像上面那样跳过空值）
                _ = PushConfigAsync(new { framesDir = _vm.ExternalFramesDir });
                break;
        }
    }

    // ===================== 输入视频 =====================

    /// <summary>把远端电脑上的文件设为输入视频（返回远端探测到的信息）。</summary>
    public async Task<JsonElement> SetInputAsync(string path)
    {
        var res = await _client.RequestAsync("set_input", new { path }, 60000);
        Run(() =>
        {
            _applying = true;
            try
            {
                _vm.InputVideo = path;
                _vm.Video = ToVideoInfo(res);
            }
            finally { _applying = false; }
        });
        // 输入变化会影响远端「能否开始」，重新拉一次状态
        _ = PushConfigAsync(null);
        return res;
    }

    /// <summary>把远端推来的视频信息还原成完整 VideoInfo（编码/色彩/音轨/字幕/封面都要带上，
    /// 否则「视频详细信息」卡只有分辨率和帧率，其余全是空）。</summary>
    private static VideoInfo? ToVideoInfo(JsonElement e)
    {
        if (!RemoteControlClient.GetBool(e, "valid")) return null;
        var info = new VideoInfo
        {
            Width = RemoteControlClient.GetInt(e, "width"),
            Height = RemoteControlClient.GetInt(e, "height"),
            FrameRate = RemoteControlClient.GetDbl(e, "fps"),
            FrameRateRaw = RemoteControlClient.GetStr(e, "frameRateRaw"),
            AverageFrameRate = RemoteControlClient.GetDbl(e, "averageFps"),
            IsVariableFrameRate = RemoteControlClient.GetBool(e, "isVfr"),
            Duration = TimeSpan.FromSeconds(RemoteControlClient.GetDbl(e, "duration")),
            TotalFrames = (long)RemoteControlClient.GetDbl(e, "totalFrames"),
            BitRate = (long)RemoteControlClient.GetDbl(e, "bitrate"),
            VideoBitRate = (long)RemoteControlClient.GetDbl(e, "videoBitRate"),
            FileSize = (long)RemoteControlClient.GetDbl(e, "fileSize"),
            DisplayAspectRaw = RemoteControlClient.GetStr(e, "displayAspectRaw"),
            VideoCodec = RemoteControlClient.GetStr(e, "videoCodec"),
            Profile = RemoteControlClient.GetStr(e, "profile"),
            PixelFormat = RemoteControlClient.GetStr(e, "pixelFormat"),
            ColorSpace = RemoteControlClient.GetStr(e, "colorSpace"),
            ColorTransfer = RemoteControlClient.GetStr(e, "colorTransfer"),
            ColorRange = RemoteControlClient.GetStr(e, "colorRange"),
            FieldOrder = RemoteControlClient.GetStr(e, "fieldOrder"),
            VideoStreamCount = Math.Max(1, RemoteControlClient.GetInt(e, "videoStreamCount")),
            AudioCodec = RemoteControlClient.GetStr(e, "audioCodec"),
            AudioProfile = RemoteControlClient.GetStr(e, "audioProfile"),
            AudioSampleRate = RemoteControlClient.GetInt(e, "audioSampleRate"),
            AudioChannels = RemoteControlClient.GetInt(e, "audioChannels"),
            AudioChannelLayout = RemoteControlClient.GetStr(e, "audioChannelLayout"),
            AudioBitRate = (long)RemoteControlClient.GetDbl(e, "audioBitRate"),
            AudioTrackCount = RemoteControlClient.GetInt(e, "audioTrackCount"),
            HasSubtitles = RemoteControlClient.GetBool(e, "hasSubtitles"),
            SubtitleCount = RemoteControlClient.GetInt(e, "subtitleCount"),
            FormatName = RemoteControlClient.GetStr(e, "formatName"),
            ChapterCount = RemoteControlClient.GetInt(e, "chapterCount"),
            AttachmentCount = RemoteControlClient.GetInt(e, "attachmentCount"),
            MetadataTagCount = RemoteControlClient.GetInt(e, "metadataTagCount"),
            EncoderTag = RemoteControlClient.GetStr(e, "encoderTag"),
            CoverStreamIndex = e.TryGetProperty("coverStreamIndex", out var csi) && csi.TryGetInt32(out var csiV) ? csiV : -1,
            CoverCodec = RemoteControlClient.GetStr(e, "coverCodec"),
            CoverWidth = RemoteControlClient.GetInt(e, "coverWidth"),
            CoverHeight = RemoteControlClient.GetInt(e, "coverHeight"),
            CoverIsAttachment = RemoteControlClient.GetBool(e, "coverIsAttachment")
        };
        FillList(info.AudioLanguages, e, "audioLanguages");
        FillList(info.SubtitleLanguages, e, "subtitleLanguages");
        FillList(info.SubtitleCodecs, e, "subtitleCodecs");
        return info;
    }

    private static void FillList(List<string> target, JsonElement e, string key)
    {
        target.Clear();
        if (!e.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) target.Add(item.GetString() ?? "");
    }

    /// <summary>浏览远端目录（kind: video / image / audio；path 为空返回盘符）。</summary>
    public Task<JsonElement> ListDirAsync(string? path, string kind = "video")
        => path is null or ""
            ? _client.RequestAsync("list_dir", new { kind }, 20000)
            : _client.RequestAsync("list_dir", new { kind, path }, 20000);

    /// <summary>把输出目录改到**远端电脑**上的某个目录（路径在对方机器上，本机无法校验）。</summary>
    public Task SetOutputRootAsync(string path) => PushConfigAsync(new { outputRoot = path });

    /// <summary>把临时目录改到**远端电脑**上的某个目录，并回带远端对该目录的缓存评估
    /// （status: mismatch/match/none；有旧缓存时界面要弹三选一，与本机一致）。</summary>
    public async Task<(string Status, string Source)> SetTempRootAsync(string path)
    {
        var res = await _client.RequestAsync("set_config", new { tempRoot = path }, 30000);
        ApplyConfig(res);
        var st = RemoteControlClient.Sub(res, "state");
        return (RemoteControlClient.GetStr(st, "tempCacheStatus"), RemoteControlClient.GetStr(st, "tempCacheSource"));
    }

    /// <summary>让远端清空它自己的临时目录（界面选「覆盖老缓存」时调用）。清理可能要删几万帧，给足超时。</summary>
    public Task CleanTempAsync() => _client.RequestAsync("clean_temp", null, 300000);

    /// <summary>让远端使用（或传空串清除）外部帧文件夹。目录校验与 PNG 计数都在远端做。</summary>
    public async Task SetFramesDirAsync(string path)
    {
        var res = await _client.RequestAsync("set_config", new { framesDir = path ?? "" }, 60000);
        ApplyConfig(res);
    }

    /// <summary>帧文件夹模式没有原视频可探测，把用户在「帧文件夹参数」里填的分辨率/帧数/帧率下发到远端。</summary>
    public async Task SetManualVideoInfoAsync(int width, int height, long totalFrames, double fps)
    {
        var res = await _client.RequestAsync("set_config", new
        {
            manualWidth = width,
            manualHeight = height,
            manualTotalFrames = (int)Math.Clamp(totalFrames, 0, int.MaxValue),
            manualFps = fps
        }, 30000);
        ApplyConfig(res);
    }

    // ===================== 执行控制 =====================

    public async Task StartAsync()
    {
        if (!IsConnected) { _vm.Logger.Warn("尚未连接远端，忽略启动请求"); return; }
        await PushConfigAsync(null);
        // 先乐观进入处理态并切页：远端校验失败时会立刻回一条 state=idle 把状态撤销，
        // 否则用户点了「开始处理」界面毫无反应，只能去日志里找原因。
        Run(() =>
        {
            _vm.IsProcessing = true;
            _vm.IsPaused = false;
            _vm.ProgressPercent = 0;
            _vm.ProgressDetail = "正在让远端开始处理…";
        });
        EnteredProcessing?.Invoke();
        await _client.RequestAsync("start", null, 20000);
    }

    public Task StopAsync() => _client.RequestAsync("stop");

    public async Task<bool> PauseOrResumeAsync()
    {
        var res = await _client.RequestAsync(_paused ? "resume" : "pause");
        _paused = RemoteControlClient.GetBool(res, "paused");
        return _paused;
    }

    /// <summary>单文件超分（图片 / 音频）相关指令</summary>
    public Task<JsonElement> SfSetInputAsync(string path)
        => _client.RequestAsync("sf_set_input", new { path }, 30000);

    public Task<JsonElement> SfSetConfigAsync(object payload)
        => _client.RequestAsync("sf_set_config", new { config = payload }, 15000);

    public Task SfStartAsync() => _client.RequestAsync("sf_start", null, 20000);
    public Task SfStopAsync() => _client.RequestAsync("sf_stop");

    // ===================== 远端推送 → VM =====================

    private void OnProgress(JsonElement e)
    {
        if (!ReferenceEquals(Current, this)) return;
        Run(() =>
        {
            var pct = RemoteControlClient.GetDbl(e, "percent");
            var stageText = RemoteControlClient.GetStr(e, "stageText");
            var detail = RemoteControlClient.GetStr(e, "detail");
            _vm.ProgressPercent = Math.Clamp(pct, 0, 100);
            _vm.ProgressDetail = detail.Length > 0 ? $"{stageText} · {detail}" : stageText;
            _vm.IsProcessing = true;
        });
    }

    private void OnState(JsonElement e)
    {
        // 已断开/被新会话顶掉：旧连接残留的消息不能再改界面
        if (!ReferenceEquals(Current, this)) return;
        var state = RemoteControlClient.GetStr(e, "state");
        Run(() =>
        {
            switch (state)
            {
                case "processing":
                    _vm.IsProcessing = true;
                    _vm.IsPaused = false;
                    _autoCleanAttempt = 0;
                    if (_vm.Stage is ProcessStage.Done or ProcessStage.Failed or ProcessStage.Stopped)
                        _vm.Stage = ProcessStage.Idle;   // 清掉上一轮的终态标记
                    break;
                case "done":
                case "failed":
                case "stopped":
                case "idle":
                    _vm.IsProcessing = false;
                    _vm.IsPaused = false;
                    _paused = false;
                    // 失败的留在「进行中」页看日志（与本地失败一致），其余回主页
                    _vm.Stage = state switch
                    {
                        "done" => ProcessStage.Done,
                        "failed" => ProcessStage.Failed,
                        "stopped" => ProcessStage.Stopped,
                        _ => ProcessStage.Idle
                    };
                    break;
            }
        });
        if (state == "processing") EnteredProcessing?.Invoke();
    }

    private void OnResult(JsonElement e)
    {
        if (!ReferenceEquals(Current, this)) return;
        var ok = RemoteControlClient.GetBool(e, "success");
        var output = RemoteControlClient.GetStr(e, "output");
        if (!ok)
        {
            _vm.Logger.Warn("远端处理未产出成品");
            return;
        }

        _vm.Logger.Info($"远端处理完成：{output}");
        if (output.Length == 0) return;

        var name = Path.GetFileName(output);
        _ui.TryEnqueue(() => App.MainWindow?.ShowNotice(
            "远端处理完成", $"成品在远端：{name}", "下载成品到本机",
            () => _ = DownloadOutputAsync(output)));
    }

    /// <summary>远端拒绝启动：临时目录里有别的视频留下的旧缓存。
    /// 桌面客户端无人值守 → 自动清远端缓存后重试一次，避免「点了开始没反应」。</summary>
    private void OnTempCache(JsonElement e)
    {
        var source = RemoteControlClient.GetStr(e, "source");
        var tempRoot = RemoteControlClient.GetStr(e, "tempRoot");
        if (_autoCleanAttempt >= 2)
        {
            _vm.Logger.Error($"远端临时目录仍有旧缓存（{tempRoot}），已取消启动；请在远端手动清理临时目录");
            Run(() => _vm.ProgressDetail = "远端临时目录有旧缓存，已取消启动");
            return;
        }
        _autoCleanAttempt++;
        _vm.Logger.Warn($"远端临时目录里有旧缓存（来源：{(source.Length > 0 ? source : "未知")}），正在自动清理后重试…");
        Run(() => _vm.ProgressDetail = "正在清理远端旧缓存…");
        _ = CleanAndStartAsync();
    }

    private async Task CleanAndStartAsync()
    {
        try
        {
            await CleanTempAsync();
            await StartAsync();
        }
        catch (Exception ex)
        {
            _vm.Logger.Error($"清理远端缓存后重启失败：{ex.Message}");
        }
    }


    /// <summary>把远端成品下载到本机「下载」目录（对应手机端的「下载成品到手机」）。</summary>
    private async Task DownloadOutputAsync(string remotePath)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Directory.CreateDirectory(dir);
            var local = Path.Combine(dir, Path.GetFileName(remotePath));
            // 同名成品已存在时加时间戳，避免把用户之前下载的片子直接覆盖掉
            if (File.Exists(local))
            {
                var stem = Path.GetFileNameWithoutExtension(local);
                var ext = Path.GetExtension(local);
                local = Path.Combine(dir, $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
            }

            _vm.Logger.Info($"开始下载远端成品到本机：{local}");
            var lastDecile = -1;
            var bytes = await _client.DownloadFileAsync(remotePath, local, default, (done, total) =>
            {
                if (total <= 0) return;
                var decile = (int)(done * 100 / total) / 10;
                if (decile == lastDecile) return;
                lastDecile = decile;
                _vm.Logger.Info($"正在下载成品到本机… {done * 100 / total}%");
            });

            _vm.Logger.Info($"成品已下载到本机：{local}（{bytes} 字节）");
            _ui.TryEnqueue(() => App.MainWindow?.ShowNotice("成品已下载到本机", local, "打开所在文件夹",
                () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{local}\"",
                    UseShellExecute = true
                })));
        }
        catch (Exception ex)
        {
            _vm.Logger.Error($"下载远端成品失败：{ex.Message}");
        }
    }

    private void OnLog(JsonElement e)
    {
        var level = RemoteControlClient.GetStr(e, "level");
        var text = RemoteControlClient.GetStr(e, "text");
        if (text.Length == 0) return;
        if (level.Equals("error", StringComparison.OrdinalIgnoreCase)) _vm.Logger.Error($"[远端] {text}");
        else if (level.Equals("warn", StringComparison.OrdinalIgnoreCase)) _vm.Logger.Warn($"[远端] {text}");
        else _vm.Logger.Info($"[远端] {text}");
    }

    private void OnNotice(JsonElement e) => _vm.Logger.Info($"[远端] {RemoteControlClient.GetStr(e, "text")}");

    private void OnError(JsonElement e)
    {
        var message = RemoteControlClient.GetStr(e, "message");
        if (message.Length == 0) return;
        _vm.Logger.Error($"[远端] {message}");
        // 启动被拒等错误只写日志的话用户看不到（主页没有日志区），补一条通知条
        _ui.TryEnqueue(() => App.MainWindow?.ShowNotice("远端提示", message));
    }

    /// <summary>远端处理失败、服务端已写好完整报错文件 → 自动分块拉回本机报告目录。</summary>
    private void OnFailureReport(JsonElement e)
    {
        var remote = RemoteControlClient.GetStr(e, "path");
        if (remote.Length == 0) return;
        _ = DownloadReportAsync(remote);
    }

    private async Task DownloadReportAsync(string remotePath)
    {
        try
        {
            var dir = _vm.ResolveReportDir();
            Directory.CreateDirectory(dir);
            var name = Path.GetFileName(remotePath);
            if (string.IsNullOrEmpty(name)) name = $"Easy4K_远端失败报告_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            var local = Path.Combine(dir, name);

            var bytes = await _client.DownloadFileAsync(remotePath, local);
            _vm.Logger.Warn($"远端失败报告已下载到本机：{local}（{bytes} 字节）");
            _ui.TryEnqueue(() => App.MainWindow?.ShowNotice(
                "远端处理失败", $"完整报错已下载到本机：{local}", "打开报告",
                () => Easy4K.LuminolEngine.CrashReportService.Open(local)));
        }
        catch (Exception ex)
        {
            _vm.Logger.Warn($"远端失败报告下载失败：{ex.Message}");
        }
    }

    // ===================== 工具 =====================

    private void Run(Action a)
    {
        if (_ui.HasThreadAccess) { try { a(); } catch { } return; }
        _ui.TryEnqueue(() => { try { a(); } catch { } });
    }
}

