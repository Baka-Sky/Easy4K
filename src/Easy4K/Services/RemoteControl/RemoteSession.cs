using System;
using System.Text.Json;
using System.Threading.Tasks;
using Easy4K.Models;
using Easy4K.ViewModels;
using Microsoft.UI.Dispatching;

namespace Easy4K.Services.RemoteControl;

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

    private readonly MainViewModel _vm;
    private readonly DispatcherQueue _ui;
    private readonly RemoteControlClient _client = new();

    /// <summary>远端→VM 的写入期间为 true，避免把镜像回来的值又当成用户改动推回远端</summary>
    private bool _applying;

    private bool _paused;
    private long _lastFrame = -1;

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

    public async Task<JsonElement> ConnectAsync(string host, int port)
    {
        _client.Step += s => Step?.Invoke(s);
        var caps = await _client.ConnectAsync(host, port, Environment.MachineName);
        Host = host;
        Port = port;
        Current = this;

        _client.ProgressReceived += OnProgress;
        _client.LogReceived += OnLog;
        _client.StateReceived += OnState;
        _client.ResultReceived += OnResult;
        _client.NoticeReceived += OnNotice;
        _client.ErrorReceived += OnError;
        _client.Disconnected += OnDisconnected;
        _vm.PropertyChanged += OnVmPropertyChanged;

        ApplyOptions(caps);
        // 占用每秒一条小消息，默认开；图片预览默认关（公网持续上传有 PCDN 风险）
        await _client.SetStreamAsync(preview: false, stats: true);
        // 首次下发配置：顺带把远端的模型列表拉回来填进页面
        await PushConfigAsync(null);

        _vm.Logger.Info($"已接管远端 Easy4K {Address}（v{RemoteControlClient.GetStr(caps, "version")}）");
        return caps;
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
        _client.Disconnected -= OnDisconnected;

        await _client.DisconnectAsync();
        if (Current == this) Current = null;
    }

    private void OnDisconnected(string reason)
    {
        Run(() =>
        {
            _vm.Logger.Warn($"远端连接已断开：{reason}");
            _vm.IsProcessing = false;
            _vm.IsPaused = false;
        });
    }

    // ===================== 能力 / 配置 =====================

    /// <summary>把远端能力清单里的勾选项与输出目录灌进 VM（页面据此显示远端配置）。</summary>
    private void ApplyOptions(JsonElement caps)
    {
        var opt = RemoteControlClient.Sub(caps, "options");
        var paths = RemoteControlClient.Sub(caps, "paths");
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

    private static VideoInfo? ToVideoInfo(JsonElement e)
    {
        if (!RemoteControlClient.GetBool(e, "valid")) return null;
        var info = new VideoInfo
        {
            Width = RemoteControlClient.GetInt(e, "width"),
            Height = RemoteControlClient.GetInt(e, "height"),
            FrameRate = RemoteControlClient.GetDbl(e, "fps"),
            AudioCodec = RemoteControlClient.GetStr(e, "audioCodec"),
            TotalFrames = (long)RemoteControlClient.GetDbl(e, "totalFrames"),
            Duration = TimeSpan.FromSeconds(RemoteControlClient.GetDbl(e, "duration")),
            // 远端视频有没有字幕流：决定本机那行「保留原视频内嵌字幕」能不能勾
            HasSubtitles = RemoteControlClient.GetBool(e, "hasSubtitles"),
        };
        return info;
    }

    /// <summary>浏览远端目录（kind: video / image / audio；path 为空返回盘符）。</summary>
    public Task<JsonElement> ListDirAsync(string? path, string kind = "video")
        => path is null or ""
            ? _client.RequestAsync("list_dir", new { kind }, 20000)
            : _client.RequestAsync("list_dir", new { kind, path }, 20000);

    // ===================== 执行控制 =====================

    public async Task StartAsync()
    {
        await PushConfigAsync(null);
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
        Run(() =>
        {
            var state = RemoteControlClient.GetStr(e, "state");
            switch (state)
            {
                case "processing":
                    _vm.IsProcessing = true;
                    _vm.IsPaused = false;
                    break;
                case "done":
                case "failed":
                case "stopped":
                case "idle":
                    _vm.IsProcessing = false;
                    _vm.IsPaused = false;
                    _paused = false;
                    break;
            }
        });
    }

    private void OnResult(JsonElement e)
    {
        if (RemoteControlClient.GetBool(e, "success"))
            _vm.Logger.Info($"远端处理完成：{RemoteControlClient.GetStr(e, "output")}");
        else
            _vm.Logger.Warn("远端处理未产出成品");
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

    private void OnError(JsonElement e) => _vm.Logger.Error($"[远端] {RemoteControlClient.GetStr(e, "message")}");

    // ===================== 工具 =====================

    private void Run(Action a)
    {
        if (_ui.HasThreadAccess) { try { a(); } catch { } return; }
        _ui.TryEnqueue(() => { try { a(); } catch { } });
    }
}

