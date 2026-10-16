using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Easy4K.Models;
using Easy4K.Services;
using Easy4K.Services.CommandBuilders;
using Easy4K.ViewModels;
using Microsoft.UI.Dispatching;

namespace Easy4K.Services.RemoteControl;

/// <summary>远程控制服务端：监听 11451 端口，用「一行一条 JSON」的文本协议接受
/// Android 客户端（Easy4K-Android-UserClient）的指令，把 Easy4K 的全部处理能力
/// （输入/输出视频、输出目录、超分/补帧模型、启动/停止/暂停）暴露出去。
///
/// 设计要点：
///  · 全程只在 UI 线程改 ViewModel（DispatcherQueue 编组），服务端自身跑在线程池。
///  · 所有响应/事件都带 "type" 字段；请求可选带 "id"，响应会原样回带。
///  · 上传：upload_begin / upload_chunk / upload_end，分块 base64，落盘到 RemoteUploads。
///  · 启动处理时自动清理不匹配的临时缓存，并对所有会弹窗的路径做兜底，避免无人值守时卡死。</summary>
public sealed class RemoteControlServer
{
    public const int Port = 11451;
    public const int ProtocolVersion = 1;

    /// <summary>当前活动的服务实例（全局唯一，供 UI 覆盖层与 MainWindow 弹窗守卫判断）</summary>
    public static RemoteControlServer? Current { get; private set; }

    /// <summary>远程控制是否正在进行（此时应抑制本机所有弹窗）</summary>
    public static bool IsActive => Current is { IsRunning: true };

    private readonly MainViewModel _vm;
    private readonly DispatcherQueue _ui;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    private TcpClient? _client;
    private Channel<string>? _outbound;
    private Task? _writerTask;

    private readonly SemaphoreSlim _startLock = new(1, 1);

    // ——— 进度页的实时推流（手机端可开关）———
    private volatile bool _pushPreview;
    private volatile bool _pushStats;
    private Task? _pushTask;
    private string _lastPreviewPath = "";
    private int _previewEmptyTicks;
    private bool _previewErrorLogged;

    private FileStream? _uploadStream;
    private string? _uploadPath;
    private long _uploadExpected;

    public RemoteControlServer(MainViewModel vm, DispatcherQueue ui)
    {
        _vm = vm;
        _ui = ui;
    }

    /// <summary>服务是否在监听</summary>
    public bool IsRunning { get; private set; }

    /// <summary>是否已有客户端连上</summary>
    public bool HasClient { get; private set; }

    /// <summary>已连接客户端自报的名字（hello 里的 client）</summary>
    public string ClientName { get; private set; } = "";

    /// <summary>覆盖层显示的状态文字</summary>
    public string StatusText { get; private set; } = "";

    /// <summary>状态变化（可能在后台线程触发），UI 需自行编组</summary>
    public event Action? StatusChanged;

    /// <summary>本机可供客户端连接的 IPv4 地址，按「手机最可能连得上」排序。
    /// 必须排除虚拟网卡（VMware / Hyper-V vEthernet / Clash-Mihomo / WSL / VPN 等）——
    /// 直接取第一个地址很容易拿到 198.18.0.1 这类代理虚拟地址，手机照着输会连不上。</summary>
    public static IReadOnlyList<string> GetLocalIPv4Candidates()
    {
        var list = new List<(int Score, string Ip)>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var type = ni.NetworkInterfaceType;
                if (type == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                if (type == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;

                var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
                var virtualAdapter = false;
                foreach (var v in VirtualHints)
                    if (name.Contains(v)) { virtualAdapter = true; break; }

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;

                    var score = 0;
                    if (ip.StartsWith("192.168.", StringComparison.Ordinal)) score += 40;
                    else if (ip.StartsWith("10.", StringComparison.Ordinal)) score += 30;
                    else if (IsPrivate172(ip)) score += 25;
                    else score += 5;

                    if (type == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211) score += 15;
                    else if (type == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet) score += 12;

                    if (virtualAdapter) score -= 100;
                    list.Add((score, ip));
                }
            }
        }
        catch { }

        return list.OrderByDescending(x => x.Score)
                   .Select(x => x.Ip)
                   .Distinct()
                   .ToList();
    }

    /// <summary>本机最可能被手机连上的地址（首选）</summary>
    public static string GetLocalIPv4() => GetLocalIPv4Candidates().FirstOrDefault() ?? "127.0.0.1";

    private static readonly string[] VirtualHints =
    {
        "vmware", "virtualbox", "vethernet", "hyper-v", "mihomo", "clash", "tailscale",
        "zerotier", "wsl", "docker", "loopback", "bluetooth", "radmin", "virtual", "vpn", "tap"
    };

    private static bool IsPrivate172(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length == 4
            && parts[0] == "172"
            && int.TryParse(parts[1], out var b)
            && b >= 16 && b <= 31;
    }

    // ===================== 生命周期 =====================

    public void Start()
    {
        if (IsRunning) return;
        Current = this;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        IsRunning = true;
        SetStatus("正在等待客户端设备链接");
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));

        // 处理结果/日志/失败事件 → 转发给客户端
        _vm.ProcessingCompleted += OnProcessingCompleted;
        _vm.StartFailed += OnStartFailed;
        _vm.TurboDisabledRequired += OnTurboDisabledRequired; // 开跑前复检到机械盘 → 推给客户端
        _vm.Logs.CollectionChanged += OnLogAdded;
        _vm.PropertyChanged += OnVmPropertyChanged; // 单文件超分的状态/进度
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _vm.ProcessingCompleted -= OnProcessingCompleted;
        _vm.StartFailed -= OnStartFailed;
        _vm.TurboDisabledRequired -= OnTurboDisabledRequired;
        _vm.Logs.CollectionChanged -= OnLogAdded;
        _vm.PropertyChanged -= OnVmPropertyChanged;

        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;

        try { _outbound?.Writer.TryComplete(); } catch { }
        try { _client?.Close(); } catch { }
        _client = null;
        _outbound = null;

        CloseUpload();
        CloseDownload();

        if (_acceptTask is not null) { try { await _acceptTask; } catch { } }
        _acceptTask = null;

        if (Current == this) Current = null;
        HasClient = false;
        ClientName = "";
        _pushPreview = false;
        _pushStats = false;
        _pushTask = null;
        SetStatus("");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception) { break; }

            // 单客户端模型：新连接直接顶掉旧的（手机重连很常见）
            var previous = _client;
            _client = client;
            try { previous?.Close(); } catch { }

            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        Channel<string>? ch = null;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
            var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

            ch = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            _outbound = ch;

            HasClient = true;
            SetStatus("客户端已连接");
            StartPushLoop(ct);
            _writerTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var line in ch.Reader.ReadAllAsync())
                    {
                        try { await writer.WriteLineAsync(line); }
                        catch { break; }
                    }
                }
                catch { }
            });

            Send(new { type = "hello_ack", app = "Easy4K", version = _vm.Version, protocol = ProtocolVersion });

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                line = line.Trim();
                if (line.Length == 0) continue;
                try { await DispatchAsync(line); }
                catch (Exception ex) { SendError(ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            try { ch.Writer.TryComplete(); } catch { }
            try { client.Close(); } catch { }
            // 只有「当前连接仍然是自己」时才清理全局状态。
            // 否则新连接刚接管就被上一个连接的收尾逻辑误关掉（表现为手机端握手超时）。
            if (ReferenceEquals(_client, client))
            {
                _client = null;
                _outbound = null;
                HasClient = false;
                ClientName = "";
                _pushPreview = false;
                _pushStats = false;
                _pushTask = null;
                CloseUpload();
                CloseDownload();
                SetStatus("正在等待客户端设备链接");
            }
        }
    }

    // ===================== 协议分发 =====================

    private async Task DispatchAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeEl)) { SendError("缺少 type 字段"); return; }
        var type = typeEl.GetString() ?? "";
        var id = root.TryGetProperty("id", out var idEl) ? idEl : default;

        switch (type)
        {
            case "hello":
                if (root.TryGetProperty("client", out var c)) ClientName = c.GetString() ?? "";
                Send(new { type = "hello_ack", app = "Easy4K", version = _vm.Version, protocol = ProtocolVersion }, id);
                break;

            case "ping":
                Send(new { type = "pong", time = DateTimeOffset.Now.ToUnixTimeMilliseconds() }, id);
                break;

            case "capabilities":
                Send(await BuildCapabilitiesAsync(), id);
                break;

            case "list_drives":
                Send(ListDrives(), id);
                break;

            case "list_dir":
            {
                // kind: video（默认）/ image / audio / any —— 单文件超分要列图片与音频
                var path = GetString(root, "path");
                Send(ListDir(path, GetString(root, "kind") ?? "video"), id);
                break;
            }

            case "mkdir":
                // 手机端浏览电脑目录时新建文件夹（parent = 当前所在目录，name = 新文件夹名）
                Send(MakeDir(GetString(root, "parent"), GetString(root, "name")), id);
                break;

            case "set_input":
            {
                var path = GetString(root, "path");
                var info = await ApplyInputAsync(path);
                Send(info, id);
                break;
            }

            case "upload_begin":
                Send(await UploadBeginAsync(root), id);
                break;

            case "upload_chunk":
                await UploadChunkAsync(root);
                Send(new { type = "ack", ok = true }, id);
                break;

            case "upload_end":
            {
                var info = await UploadEndAsync(root);
                Send(info, id);
                break;
            }

            case "set_stream":
            {
                // 手机端开关「图片预览 / CPU-GPU 占用」推流（外网由手机端自己提示风险后再打开）
                _pushPreview = GetBool(root, "preview", _pushPreview);
                _pushStats = GetBool(root, "stats", _pushStats);
                if (_pushPreview)
                {
                    _lastPreviewPath = "";
                    _previewEmptyTicks = 0;
                    _previewErrorLogged = false;
                }
                Send(new { type = "stream", preview = _pushPreview, stats = _pushStats }, id);
                break;
            }

            case "set_config":
                Send(await ApplyConfigAsync(root), id);
                break;

            case "start":
                // 注意：这里绝不能 await StartProcessingAsync —— 一旦 await，读循环会被占住到处理结束，
                // 处理中的 stop / pause / set_stream 就永远读不到（表现为"暂停和停止没用"）。
                Send(new { type = "ack", ok = true, stage = "starting" }, id);
                _ = StartProcessingAsync();
                break;

            case "stop":
                Send(new { type = "ack", ok = true, stage = "stopping" }, id);
                await UiAsync(() => { _vm.Stop(); return Task.CompletedTask; });
                break;

            case "pause":
            {
                // 回真实挂起状态：桌面端可能因为当前没有工具进程在跑而拒绝暂停（此时要保持 UI 一致）
                var paused = await UiAsync(() =>
                {
                    _vm.PauseProcessing();
                    return Task.FromResult(_vm.IsPaused);
                });
                Send(new { type = "paused", paused }, id);
                break;
            }

            case "resume":
            {
                var paused = await UiAsync(() =>
                {
                    _vm.ResumeProcessing();
                    return Task.FromResult(_vm.IsPaused);
                });
                Send(new { type = "paused", paused }, id);
                break;
            }

            // 清理临时目录里的旧缓存（客户端在「临时目录有旧文件」弹窗里选「删除旧缓存」时调用）。
            // 注意：必须**清完再回 ack** —— 先回 ack 的话客户端会立刻重新 start，
            // 此时缓存还没删掉，又会判定「缓存不符」，弹窗反复弹、看起来像「删除不生效」。
            // 另外放到独立任务里跑，避免长时间占住读循环（清理期间还要能收 stop / ping）。
            case "clean_temp":
            {
                // id 指向的 JsonDocument 会在 DispatchAsync 返回时被释放；异步任务里再用它就会抛异常，
                // 导致 ack 发不出去、客户端一直等（表现为「清完缓存就卡住，后续拆帧/补帧都不开始」）。
                // 所以先把 id 克隆一份再进异步。
                var ackId = id.ValueKind == JsonValueKind.Undefined ? default : id.Clone();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await UiAsync(async () => { await _vm.CleanTempInAsync(_vm.TempRoot); });
                        SendNotice("临时目录旧缓存已清理");
                        Send(new { type = "ack", ok = true, stage = "cleaned" }, ackId);
                    }
                    catch (Exception ex)
                    {
                        SendError($"清理临时目录失败：{ex.Message}");
                        Send(new { type = "ack", ok = false, stage = "cleaned", error = ex.Message }, ackId);
                    }
                });
                break;
            }

            // 客户端勾选涡轮模式前先问一句：临时目录（或指定路径）所在磁盘是固态还是机械。
            // 与桌面端一样的判定：机械盘撑不住涡轮的多路并发读写，客户端据此弹窗提醒并阻止开启。
            case "disk_info":
            {
                var probe = root.TryGetProperty("path", out var pp) && pp.ValueKind == JsonValueKind.String
                    ? pp.GetString() ?? ""
                    : "";
                var target = string.IsNullOrWhiteSpace(probe) ? _vm.TempRoot : probe;
                var kind = await Task.Run(() => StorageMediaDetector.DetectForPath(target));
                Send(new
                {
                    type = "disk_info",
                    ok = true,
                    kind = kind switch
                    {
                        StorageMediaDetector.MediaKind.Ssd => "ssd",
                        StorageMediaDetector.MediaKind.Hdd => "hdd",
                        _ => "unknown",
                    },
                    path = target,
                    tempRoot = _vm.TempRoot,
                }, id);
                break;
            }

            // ——— 单文件超分（图片 / 音频，与主管线互斥）———
            case "sf_set_input":
                Send(await SfSetInputAsync(root), id);
                break;

            case "sf_set_config":
                Send(await SfSetConfigAsync(root), id);
                break;

            case "sf_start":
                // 同 start：绝不能 await，否则读循环被占住，处理中的 sf_stop / set_stream 收不到
                Send(new { type = "ack", ok = true, stage = "sf-starting" }, id);
                _ = UiAsync(() => _vm.StartSingleFileAsync());
                break;

            case "sf_stop":
                Send(new { type = "ack", ok = true }, id);
                await UiAsync(() => { _vm.StopSingleFile(); return Task.CompletedTask; });
                break;

            // ——— 成品文件回传（手机主动拉取电脑上的成品）———
            case "download_begin":
                Send(await DownloadBeginAsync(root), id);
                break;

            case "download_read":
                Send(await DownloadReadAsync(root), id);
                break;

            case "download_finish":
                CloseDownload();
                Send(new { type = "ack", ok = true }, id);
                break;

            default:
                SendError($"未知指令: {type}");
                break;
        }
    }

    // ===================== 能力清单 =====================
    // 客户端在「连接转圈」期间请求此接口，据此拿到显卡信息 + 模型目录：
    // 非 N 卡时隐藏 4 个 PyTorch 补帧族（available=false），只留 NCNN-RIFE。

    private Task<object> BuildCapabilitiesAsync() => UiAsync(() =>
    {
        var gpu = _vm.Gpu;
        var nvidia = gpu.IsNvidia;

        var srFamilies = SrCatalog.Families.Select((f, i) => new
        {
            index = i,
            label = f.Label,
            scene = f.Scene,
            stars = f.Stars,
            available = true, // 超分全部走 ncnn + Vulkan，A/N/核显通用
            models = f.Variants.Select(v => new
            {
                name = v.Name,
                scales = v.Scales,
                scene = v.Scene,
                stars = v.Stars
            }).ToArray()
        }).ToArray();

        var vfiFamilies = VfiCatalog.Families.Select((f, i) =>
        {
            var torch = f.IsTorch;
            var models = i == 0
                ? RifeCommandBuilder.ListModels(_vm.Tools.RifeModelsRoot)
                : f.Variants.Select(v => v.Name).ToArray();
            return new
            {
                index = i,
                label = f.Label,
                scene = f.Scene,
                stars = f.Stars,
                torch,
                available = !torch || nvidia, // PyTorch 族仅 N 卡可用
                models
            };
        }).ToArray();

        return Task.FromResult<object>(new
        {
            type = "capabilities",
            version = _vm.Version,
            protocol = ProtocolVersion,
            gpu = new
            {
                name = gpu.Name,
                vramMB = gpu.VramMB,
                vram = gpu.VramText,
                isNvidia = nvidia,
                isRtx = gpu.IsRtx,
                series = gpu.Series,
                driver = gpu.DriverVersion,
                hdr = gpu.SupportsHdr
            },
            sr = new { families = srFamilies },
            vfi = new { families = vfiFamilies },
            options = new
            {
                superResolution = _vm.SuperResolution,
                interpolation = _vm.Interpolation,
                mergeVideo = _vm.MergeVideo,
                mergeAudio = _vm.MergeAudio,
                sdrToHdr = _vm.SdrToHdr,
                splitFrames = _vm.SplitFrames,
                dedup = _vm.DedupEnabled,
                audioSr = _vm.AudioSrEnabled,
                turboMode = _vm.TurboMode,
                // 保留原视频内嵌字幕：hasSubtitles 让客户端知道该不该解锁这个开关（服务端探测到的当前视频字幕流）
                keepSubtitles = _vm.KeepSubtitles,
                hasSubtitles = _vm.HasSubtitles,
                // 编码质量（高级页）：CPU CRF 与硬件编码 CQ/QP —— 两个远控客户端都要能改这几项
                crf = _vm.Crf,
                hwQualityMode = _vm.HwQualityMode,
                hwQuality = _vm.HwQuality,
                hdrEnabled = _vm.IsHdrEnabled,
                ifMultiplierLocked = _vm.IfMultiplierLocked
            },
            config = new
            {
                srScale = _vm.SrScale,
                srEngineIndex = _vm.SrEngineIndex,
                srModel = _vm.SrModel,
                ifMultiplier = _vm.IfMultiplier,
                ifEngineIndex = _vm.IfEngineIndex,
                ifModel = _vm.IfModel,
                targetFps = _vm.TargetFps
            },
            // 单文件超分（图片 / 音频）：客户端用它渲染「单文件超分」卡片
            sf = new
            {
                kindIndex = _vm.SfKindIndex,
                input = _vm.SfInput,
                imageScale = _vm.SfImageScale,
                imageModel = _vm.SfImageModel,
                imageModels = _vm.SfImageModels.ToArray(),
                audioPrecision = _vm.SfAudioPrecision,
                running = _vm.SfRunning,
                canStart = _vm.CanStartSingle,
                status = _vm.SfStatus
            },
            paths = new
            {
                input = _vm.InputVideo,
                outputRoot = _vm.OutputRoot,
                tempRoot = _vm.TempRoot,
                outputName = _vm.FinalOutputName
            },
            state = new
            {
                isProcessing = _vm.IsProcessing,
                isPaused = _vm.IsPaused,
                sfRunning = _vm.SfRunning,
                canStart = _vm.CanStart,
                canStartReason = _vm.CanStartReason
            }
        });
    });

    // ===================== 文件浏览 / 输入视频 =====================

    private static object ListDrives()
    {
        var list = new List<object>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    list.Add(new { name = d.Name, isDir = true, size = 0L, label = d.VolumeLabel, kind = d.DriveType.ToString() });
                }
                catch { }
            }
        }
        catch { }
        return new { type = "drives", entries = list };
    }

    private static readonly string[] VideoExts =
        { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv", ".ts", ".m2ts", ".wmv", ".mpg", ".mpeg", ".m4v", ".rmvb" };
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
    private static readonly string[] AudioExts = { ".wav", ".mp3", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma" };

    private static object ListDir(string? path, string kind = "video")
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            // 不带路径 → 返回盘符列表
            return ListDrives();
        }
        var kindLc = (kind ?? "video").ToLowerInvariant();
        var entries = new List<object>();
        try
        {
            var di = new DirectoryInfo(path);
            if (!di.Exists) return new { type = "dir", path, error = "目录不存在", entries = Array.Empty<object>() };

            foreach (var d in di.GetDirectories())
            {
                try { entries.Add(new { name = d.Name, isDir = true, size = 0L }); } catch { }
            }
            foreach (var f in di.GetFiles())
            {
                var ext = f.Extension.ToLowerInvariant();
                var ok = kindLc switch
                {
                    "image" => ImageExts.Contains(ext),
                    "audio" => AudioExts.Contains(ext),
                    "any" => VideoExts.Contains(ext) || ImageExts.Contains(ext) || AudioExts.Contains(ext),
                    _ => VideoExts.Contains(ext)
                };
                if (!ok) continue;
                try { entries.Add(new { name = f.Name, isDir = false, size = f.Length }); } catch { }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new { type = "dir", path, error = "没有访问权限", entries = Array.Empty<object>() };
        }
        catch (Exception ex)
        {
            return new { type = "dir", path, error = ex.Message, entries = Array.Empty<object>() };
        }
        return new { type = "dir", path, entries };
    }

    /// <summary>在电脑上新建文件夹（手机端浏览目录时用）。重名、非法名、无权限都返回明确原因，
    /// 不抛异常——手机端拿到 error 直接显示。</summary>
    private static object MakeDir(string? parent, string? name)
    {
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
            return new { type = "mkdir", ok = false, error = "目录或名称不能为空" };
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new { type = "mkdir", ok = false, error = "名称包含非法字符" };

        var full = Path.Combine(parent, name);
        try
        {
            if (Directory.Exists(full) || File.Exists(full))
                return new { type = "mkdir", ok = false, error = "同名文件或文件夹已存在" };
            Directory.CreateDirectory(full);
            return new { type = "mkdir", ok = true, path = full };
        }
        catch (Exception ex)
        {
            return new { type = "mkdir", ok = false, error = ex.Message };
        }
    }

    /// <summary>把电脑上的文件设为输入视频，并返回检测到的视频信息</summary>
    private Task<object> ApplyInputAsync(string? path) => UiAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return (object)new { type = "video_info", valid = false, error = "文件不存在", path };
        await _vm.SetInputVideoAsync(path);
        return BuildVideoInfo(path);
    });

    private object BuildVideoInfo(string path)
    {
        var v = _vm.Video;
        if (v is null || !v.IsValid)
            return new { type = "video_info", valid = false, path, error = "视频信息检测失败（FFprobe 不可用或文件不可读）" };
        return new
        {
            type = "video_info",
            valid = true,
            path,
            width = v.Width,
            height = v.Height,
            resolution = v.ResolutionText,
            fps = v.FrameRate,
            fpsText = v.FpsText,
            duration = v.Duration.TotalSeconds,
            durationText = v.DurationText,
            totalFrames = v.TotalFrames,
            bitrate = v.BitRate,
            audioCodec = v.AudioCodec,
            hasAudio = v.HasAudio,
            // 有没有字幕流：客户端据此解锁「保留原视频内嵌字幕」（没字幕就该禁用）
            hasSubtitles = v.HasSubtitles
        };
    }

    // ===================== 上传（手机视频 → 电脑） =====================

    private string UploadsDir => Path.Combine(AppPaths.WritableRoot, "RemoteUploads");

    private Task<object> UploadBeginAsync(JsonElement root) => UiAsync(() =>
    {
        var name = GetString(root, "name") ?? "upload.mp4";
        var size = root.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;

        CloseUpload();
        Directory.CreateDirectory(UploadsDir);
        var safe = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(safe)) safe = "upload.mp4";
        var target = Path.Combine(UploadsDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safe}");

        _uploadPath = target;
        _uploadExpected = size;
        _uploadStream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

        return Task.FromResult<object>(new { type = "upload_ready", path = target, size });
    });

    private async Task UploadChunkAsync(JsonElement root)
    {
        if (_uploadStream is null) throw new InvalidOperationException("尚未 upload_begin");
        var data = GetString(root, "data") ?? "";
        var bytes = Convert.FromBase64String(data);
        await _uploadStream.WriteAsync(bytes);
        await _uploadStream.FlushAsync();
    }

    private Task<object> UploadEndAsync(JsonElement root) => UiAsync(async () =>
    {
        var path = _uploadPath;
        if (_uploadStream is null || path is null) throw new InvalidOperationException("尚未 upload_begin");
        await _uploadStream.FlushAsync();
        await _uploadStream.DisposeAsync();
        _uploadStream = null;
        _uploadPath = null;

        if (_uploadExpected > 0)
        {
            var len = new FileInfo(path).Length;
            if (len != _uploadExpected)
                return (object)new { type = "video_info", valid = false, path, error = $"上传不完整（收到 {len} / 期望 {_uploadExpected} 字节）" };
        }

        await _vm.SetInputVideoAsync(path);
        return BuildVideoInfo(path);
    });

    private void CloseUpload()
    {
        try { _uploadStream?.Dispose(); } catch { }
        _uploadStream = null;
        _uploadPath = null;
        _uploadExpected = 0;
    }

    // ===================== 单文件超分（图片 / 音频） =====================
    // 与主管线完全隔离：客户端选一张图片或一个音频 → sf_start 直接跑 Real-ESRGAN / AudioSR。

    private Task<object> SfSetInputAsync(JsonElement root) => UiAsync(() =>
    {
        var path = GetString(root, "path") ?? "";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Task.FromResult<object>(new { type = "sf_info", valid = false, error = "文件不存在", path });

        // 按扩展名判定类型：图片走 Real-ESRGAN，其余一律按音频走 AudioSR
        var ext = Path.GetExtension(path).ToLowerInvariant();
        _vm.SfKindIndex = ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" ? 0 : 1;
        _vm.SfInput = path;
        return Task.FromResult<object>(BuildSfInfo(valid: true, error: ""));
    });

    private Task<object> SfSetConfigAsync(JsonElement root) => UiAsync(() =>
    {
        // 参数可能直接在根上，也可能包在 config 里
        JsonElement cfg;
        if (root.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object) cfg = c;
        else cfg = root;

        int Int(string key, int cur) =>
            cfg.TryGetProperty(key, out var e) && e.TryGetInt32(out var v) ? v : cur;
        string Str(string key, string cur) =>
            cfg.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? cur : cur;

        var kind = Int("kindIndex", _vm.SfKindIndex);
        if (kind is 0 or 1) _vm.SfKindIndex = kind;

        var scale = Int("imageScale", _vm.SfImageScale);
        if (scale > 0) _vm.SfImageScale = scale; // 会触发模型表按倍率重建

        var model = Str("imageModel", "");
        if (model.Length > 0 && _vm.SfImageModels.Contains(model)) _vm.SfImageModel = model;

        var prec = Str("audioPrecision", "");
        if (prec.Length > 0) _vm.SfAudioPrecision = prec;

        var outRoot = Str("outputRoot", "");
        if (outRoot.Length > 0) _vm.SetOutputRoot(outRoot);

        return Task.FromResult<object>(BuildSfInfo(valid: true, error: ""));
    });

    /// <summary>单文件卡片信息：类型 / 可选模型 / 倍率 / 精度 / 输出路径 / 是否可开始。</summary>
    private object BuildSfInfo(bool valid, string error)
    {
        var text = _vm.SfOutputText;
        var outPath = text.StartsWith("输出：", StringComparison.Ordinal)
            ? text["输出：".Length..].Replace('/', '\\')
            : "";
        return new
        {
            type = "sf_info",
            valid,
            error,
            kindIndex = _vm.SfKindIndex,
            isImage = _vm.SfIsImage,
            input = _vm.SfInput,
            imageScale = _vm.SfImageScale,
            imageModel = _vm.SfImageModel,
            imageModels = _vm.SfImageModels.ToArray(),
            audioPrecision = _vm.SfAudioPrecision,
            output = outPath,
            canStart = _vm.CanStartSingle,
            running = _vm.SfRunning,
            status = _vm.SfStatus
        };
    }

    /// <summary>单文件状态/进度推送（客户端据此刷新进度页）。</summary>
    private void SendSfState()
    {
        var text = _vm.SfOutputText;
        var outPath = text.StartsWith("输出：", StringComparison.Ordinal)
            ? text["输出：".Length..].Replace('/', '\\')
            : "";
        Send(new
        {
            type = "sf_state",
            running = _vm.SfRunning,
            isImage = _vm.SfIsImage,
            status = _vm.SfStatus,
            percent = Math.Round(_vm.ProgressPercent, 1),
            detail = _vm.ProgressDetail,
            output = outPath
        });
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 只在单文件任务相关属性变化时推送，避免主管线的高频进度事件把通道刷爆
        var relevant = e.PropertyName is nameof(MainViewModel.SfRunning)
            or nameof(MainViewModel.SfStatus)
            or nameof(MainViewModel.SfInput)
            or nameof(MainViewModel.SfImageScale)
            or nameof(MainViewModel.SfImageModel)
            or nameof(MainViewModel.SfAudioPrecision);
        if (!relevant && _vm.SfRunning &&
            e.PropertyName is nameof(MainViewModel.ProgressPercent) or nameof(MainViewModel.ProgressDetail))
            relevant = true;
        if (relevant) SendSfState();
    }

    // ===================== 成品文件回传（手机拉取） =====================
    // 分段读取：客户端先 download_begin 拿大小，再按 offset 循环 download_read，
    // 最后 download_finish。分块由客户端驱动，天然有流控，不会一次性把大文件灌爆通道。

    private FileStream? _downloadStream;
    private string? _downloadPath;
    private long _downloadSize;

    private Task<object> DownloadBeginAsync(JsonElement root) => UiAsync(() =>
    {
        var path = GetString(root, "path") ?? "";
        CloseDownload();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Task.FromResult<object>(new { type = "download_ready", ok = false, error = "文件不存在", path });

        var fi = new FileInfo(path);
        _downloadPath = path;
        _downloadSize = fi.Length;
        _downloadStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
        return Task.FromResult<object>(new { type = "download_ready", ok = true, name = fi.Name, size = _downloadSize });
    });

    private async Task<object> DownloadReadAsync(JsonElement root)
    {
        var stream = _downloadStream;
        if (stream is null) return new { type = "download_data", ok = false, error = "尚未 download_begin" };

        var offset = root.TryGetProperty("offset", out var oe) && oe.TryGetInt64(out var o) ? o : 0;
        var len = root.TryGetProperty("length", out var le) && le.TryGetInt32(out var l) ? l : 256 * 1024;
        if (len <= 0) len = 256 * 1024;
        if (len > 4 * 1024 * 1024) len = 4 * 1024 * 1024; // 单块上限 4MB，防止 base64 行过长

        var remaining = _downloadSize - offset;
        if (remaining <= 0)
            return new { type = "download_data", ok = true, offset, data = "", eof = true };

        var take = (int)Math.Min(len, remaining);
        var buf = new byte[take];
        stream.Seek(offset, SeekOrigin.Begin);
        var read = await stream.ReadAsync(buf.AsMemory(0, take));
        var eof = offset + read >= _downloadSize;
        return new
        {
            type = "download_data",
            ok = true,
            offset,
            data = Convert.ToBase64String(buf, 0, read),
            eof
        };
    }

    private void CloseDownload()
    {
        try { _downloadStream?.Dispose(); } catch { }
        _downloadStream = null;
        _downloadPath = null;
        _downloadSize = 0;
    }

    // ===================== 配置 =====================

    private Task<object> ApplyConfigAsync(JsonElement root) => UiAsync(() =>
    {
        JsonElement cfg;
        if (root.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object) cfg = c;
        else cfg = root;

        bool Bool(string key, bool cur) =>
            cfg.TryGetProperty(key, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : cur;
        int Int(string key, int cur) =>
            cfg.TryGetProperty(key, out var e) && e.TryGetInt32(out var v) ? v : cur;
        double Dbl(string key, double cur) =>
            cfg.TryGetProperty(key, out var e) && e.TryGetDouble(out var v) ? v : cur;
        string Str(string key, string cur) =>
            cfg.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? cur : cur;

        // 勾选类
        _vm.SuperResolution = Bool("superResolution", _vm.SuperResolution);
        _vm.Interpolation = Bool("interpolation", _vm.Interpolation);
        _vm.MergeVideo = Bool("mergeVideo", _vm.MergeVideo);
        _vm.MergeAudio = Bool("mergeAudio", _vm.MergeAudio);
        _vm.SplitFrames = Bool("splitFrames", _vm.SplitFrames);
        _vm.DedupEnabled = Bool("dedup", _vm.DedupEnabled);
        _vm.AudioSrEnabled = Bool("audioSr", _vm.AudioSrEnabled);
        if (_vm.IsHdrEnabled) _vm.SdrToHdr = Bool("sdrToHdr", _vm.SdrToHdr);
        _vm.TurboMode = Bool("turboMode", _vm.TurboMode);
        // 保留内嵌字幕：服务端最终仍会按「当前视频确实有字幕流」再兜一道（见 ProcessingOptions 组装处）
        _vm.KeepSubtitles = Bool("keepSubtitles", _vm.KeepSubtitles);

        // 编码质量（高级页）：CPU CRF 与硬件编码 CQ/QP，范围都夹到 0~51；模式只接受空/cq/qp
        _vm.Crf = Math.Clamp(Int("crf", _vm.Crf), 0, 51);
        _vm.HwQuality = Math.Clamp(Int("hwQuality", _vm.HwQuality), 0, 51);
        var hwMode = Str("hwQualityMode", _vm.HwQualityMode);
        if (hwMode is "" or "cq" or "qp") _vm.HwQualityMode = hwMode;

        // 路径
        var outRoot = Str("outputRoot", "");
        if (!string.IsNullOrWhiteSpace(outRoot)) _vm.SetOutputRoot(outRoot);
        var tempDir = Str("tempRoot", "");
        if (!string.IsNullOrWhiteSpace(tempDir)) _vm.SetTempRoot(tempDir);

        // 超分：先算法 → 再倍率 → 再模型（顺序不能反，SwitchSrEngine 会按倍率重建模型表）
        var srEngine = Int("srEngineIndex", _vm.SrEngineIndex);
        if (srEngine != _vm.SrEngineIndex) _vm.SwitchSrEngine(srEngine);
        _vm.SrScale = Int("srScale", _vm.SrScale);
        var srModel = Str("srModel", "");
        if (!string.IsNullOrWhiteSpace(srModel) && _vm.SrModels.Contains(srModel)) _vm.SrModel = srModel;

        // 补帧
        _vm.IfEngineIndex = Int("ifEngineIndex", _vm.IfEngineIndex);
        var ifModel = Str("ifModel", "");
        if (!string.IsNullOrWhiteSpace(ifModel)) _vm.IfModel = ifModel;
        _vm.IfMultiplier = Math.Clamp(Int("ifMultiplier", _vm.IfMultiplier), 1, 16);
        var targetFps = Dbl("targetFps", _vm.TargetFps);
        _vm.TargetFps = targetFps <= 0 ? 0 : targetFps;

        var cacheInfo = _vm.CheckTempCache(_vm.TempRoot);

        return Task.FromResult<object>(new
        {
            type = "config",
            options = new
            {
                superResolution = _vm.SuperResolution,
                interpolation = _vm.Interpolation,
                mergeVideo = _vm.MergeVideo,
                mergeAudio = _vm.MergeAudio,
                sdrToHdr = _vm.SdrToHdr,
                splitFrames = _vm.SplitFrames,
                dedup = _vm.DedupEnabled,
                audioSr = _vm.AudioSrEnabled,
                turboMode = _vm.TurboMode,
                // 保留原视频内嵌字幕：hasSubtitles 让客户端知道该不该解锁这个开关（服务端探测到的当前视频字幕流）
                keepSubtitles = _vm.KeepSubtitles,
                hasSubtitles = _vm.HasSubtitles,
                // 编码质量（高级页）：CPU CRF 与硬件编码 CQ/QP —— 两个远控客户端都要能改这几项
                crf = _vm.Crf,
                hwQualityMode = _vm.HwQualityMode,
                hwQuality = _vm.HwQuality,
                // RIFE v2/v3 这类老模型只支持 ×2，客户端据此把「补帧倍率」按钮隐藏/禁用
                ifMultiplierLocked = _vm.IfMultiplierLocked
            },
            config = new
            {
                srScale = _vm.SrScale,
                srEngineIndex = _vm.SrEngineIndex,
                srModel = _vm.SrModel,
                srModels = _vm.SrModels.ToArray(),
                ifMultiplier = _vm.IfMultiplier,
                ifEngineIndex = _vm.IfEngineIndex,
                ifModel = _vm.IfModel,
                ifModels = _vm.GetIfModels().ToArray(),
                targetFps = _vm.TargetFps
            },
            paths = new { input = _vm.InputVideo, outputRoot = _vm.OutputRoot, tempRoot = _vm.TempRoot, outputName = _vm.FinalOutputName },
            state = new
            {
                canStart = _vm.CanStart,
                canStartReason = _vm.CanStartReason,
                // 临时目录是否残留了别的视频的旧缓存：客户端在开始处理前先问一次，
                // 有旧缓存就先弹窗让用户选，避免白上传一遍手机视频
                tempCacheStatus = cacheInfo.Status switch
                {
                    MainViewModel.TempCacheStatus.Mismatch => "mismatch",
                    MainViewModel.TempCacheStatus.Match => "match",
                    _ => "none"
                },
                tempCacheSource = cacheInfo.SourceVideo
            }
        });
    });

    // ===================== 启动处理 =====================

    private async Task StartProcessingAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            // 临时目录里有别的视频留下的旧缓存 → 不再默默清掉，先推一条事件让客户端弹窗，
            // 由用户选「更换文件夹」或「删除旧缓存」（清完 / 换完客户端会再发一次 start）。
            var (cacheStatus, cacheSource, tempRoot) = await UiAsync(() =>
            {
                var (st, src) = _vm.CheckTempCache(_vm.TempRoot);
                return Task.FromResult((st, src, _vm.TempRoot));
            });
            if (cacheStatus == MainViewModel.TempCacheStatus.Mismatch)
            {
                // 关键：必须先发一条 state=idle 把客户端「处理中」的状态清掉。
                // 否则手机端 processing 一直是 true，之后用户选完「删除/更换」再点开始会被
                // 客户端的 busy 判断直接挡掉 —— 表现为「清理完就不进行后续操作了」。
                SendState("idle", "临时目录里有旧缓存，已暂停启动");
                Send(new
                {
                    type = "temp_cache",
                    status = "mismatch",
                    source = cacheSource,
                    tempRoot
                });
                return;
            }

            var canStart = await UiAsync(() => Task.FromResult(_vm.CanStart));
            if (!canStart)
            {
                var reason = await UiAsync(() => Task.FromResult(_vm.CanStartReason));
                SendState("idle", string.IsNullOrWhiteSpace(reason) ? "当前状态无法启动" : reason);
                Send(new { type = "error", message = string.IsNullOrWhiteSpace(reason) ? "当前状态无法启动" : reason });
                return;
            }

            SendState("processing", "已开始处理");
            await UiAsync(async () => { await _vm.StartAsync(); });
            await SendVmStageAsync();
        }
        catch (Exception ex)
        {
            Send(new { type = "error", message = ex.Message });
        }
        finally
        {
            _startLock.Release();
        }
    }

    private Task SendVmStageAsync() => UiAsync(() =>
    {
        var state = _vm.Stage switch
        {
            ProcessStage.Done => "done",
            ProcessStage.Failed => "failed",
            ProcessStage.Stopped => "stopped",
            ProcessStage.Idle => "idle",
            _ => "idle"
        };
        SendState(state, _vm.ProgressDetail);
        return Task.CompletedTask;
    });

    // ===================== VM 事件转发 =====================

    private void OnProcessingCompleted(ProcessingResult r) => Send(new
    {
        type = "result",
        success = r.Success,
        output = r.OutputPath,
        steps = r.StepsText,
        elapsed = r.Elapsed.TotalSeconds
    });

    private void OnStartFailed(string msg) => Send(new { type = "error", message = msg });

    /// <summary>开跑前复检到临时目录在机械盘 → 涡轮被强制关闭。
    /// 本机弹窗在远程模式下被抑制（见 MainWindow 的 TurboDisabledRequired 处理），
    /// 必须把原因推给客户端，否则手机上还显示着「涡轮已开」，用户以为设置了却没生效。</summary>
    private void OnTurboDisabledRequired(string reason) => Send(new { type = "turbo_disabled", reason });

    private void OnLogAdded(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null) return;
        foreach (var item in e.NewItems)
        {
            if (item is not Easy4K.Models.LogEntry le) continue;
            Send(new { type = "log", level = le.Level.ToString().ToLowerInvariant(), text = le.Message });
        }
    }

    /// <summary>由 MainViewModel 的进度事件驱动（在 MainWindow 里转发进来）</summary>
    public void PushProgress(ProcessProgress p)
    {
        // 只转发「带总帧数」的进度事件。
        // 拆帧/超分/补帧阶段每 200ms 会发一次 Total=0 的预览轮询事件（只为刷新画面），
        // 它的 Percent 恒为 0；一起转发会把手机端进度条反复归零 → 表现为拆帧时进度条抽搐。
        if (p.Total <= 0) return;

        Send(new
        {
            type = "progress",
            stage = p.Stage.ToString(),
            stageText = p.StageText,
            detail = p.DetailText,
            percent = Math.Round(p.Percent, 1),
            current = p.Current,
            total = p.Total,
            percentDisplay = p.PercentDisplay
        });
    }

    /// <summary>由 MainViewModel 的 ProcessingStarted 驱动</summary>
    public void PushStarted() => SendState("processing", "已开始处理");

    // ===================== 进度页推流（图片预览 / CPU-GPU 占用） =====================
    // 连接后默认不推，由手机端用 set_stream 打开。
    // 内网默认全开；外网由手机端先提示「可能被运营商判定为 PCDN」再自行打开。

    private void StartPushLoop(CancellationToken ct)
    {
        _pushTask = Task.Run(async () =>
        {
            var lastStats = DateTime.MinValue;
            var lastPreview = DateTime.MinValue;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(250, ct);

                    if (_pushStats && (DateTime.UtcNow - lastStats).TotalMilliseconds >= 1000)
                    {
                        lastStats = DateTime.UtcNow;
                        var (cpu, gpu) = await UiAsync(() => Task.FromResult((_vm.CpuUsage, _vm.GpuUsage)));
                        Send(new { type = "stats", cpu = Math.Round(cpu, 1), gpu = Math.Round(gpu, 1) });
                    }

                    if (_pushPreview && (DateTime.UtcNow - lastPreview).TotalMilliseconds >= 1000)
                    {
                        lastPreview = DateTime.UtcNow;
                        var path = await UiAsync(() => Task.FromResult(_vm.LatestFramePath));
                        if (string.IsNullOrEmpty(path))
                        {
                            // 拆帧阶段本来就没有预览帧，这里只提示一次，不算错误
                            if (++_previewEmptyTicks == 8)
                                _vm.Logger.Info("手机端图片预览：当前阶段暂无可预览帧（拆帧阶段属正常，超分/补帧阶段会出现）");
                        }
                        else if (path != _lastPreviewPath && File.Exists(path))
                        {
                            var b64 = await BuildPreviewBase64Async(path);
                            if (b64 is not null)
                            {
                                _lastPreviewPath = path;
                                Send(new { type = "preview", data = b64 });
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, ct);
    }

    /// <summary>把最新帧压成 480px 宽的 JPEG（base64），供手机端做图片预览。失败返回 null。</summary>
    private async Task<string?> BuildPreviewBase64Async(string path)
    {
        try
        {
            // 帧路径在管线里是正斜杠（H:/Easy4K/...），而 StorageFile 只接受反斜杠的 Win32 路径，
            // 传原样会抛 COMException（0x80070057），所以这里必须先归一化。
            var winPath = path.Replace('/', '\\');
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(winPath);
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth == 0) return null;

            var w = Math.Min(480u, decoder.PixelWidth);
            var h = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * (double)w / decoder.PixelWidth));
            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = w,
                ScaledHeight = h,
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Linear
            };
            var pixels = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var raw = pixels.DetachPixelData();

            using var outStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var props = new Windows.Graphics.Imaging.BitmapPropertySet
            {
                { "ImageQuality", new Windows.Graphics.Imaging.BitmapTypedValue(0.72f, Windows.Foundation.PropertyType.Single) }
            };
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, outStream, props);
            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                w, h, 96, 96, raw);
            await encoder.FlushAsync();

            var size = (uint)outStream.Size;
            if (size == 0) return null;
            using var reader = new Windows.Storage.Streams.DataReader(outStream.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            var buffer = new byte[size];
            reader.ReadBytes(buffer);
            return Convert.ToBase64String(buffer);
        }
        catch (Exception ex)
        {
            // 只报一次，避免每秒刷屏（同时把原因回传到手机端日志，方便用户自查）
            if (!_previewErrorLogged)
            {
                _previewErrorLogged = true;
                _vm.Logger.Warn($"手机端图片预览不可用：{ex.GetType().Name} {ex.Message}（{path}）");
            }
            return null;
        }
    }

    // ===================== 发送工具 =====================

    private static string? GetString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static bool GetBool(JsonElement root, string key, bool cur) =>
        root.TryGetProperty(key, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? e.GetBoolean()
            : cur;

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke();
    }

    private void SendState(string state, string detail) => Send(new { type = "state", state, detail });

    private void SendNotice(string text) => Send(new { type = "notice", text });

    private void SendError(string message) => Send(new { type = "error", message });

    private void Send(object payload, JsonElement id = default)
    {
        string json;
        try
        {
            if (id.ValueKind != JsonValueKind.Undefined)
            {
                var node = JsonSerializer.SerializeToNode(payload)!.AsObject();
                node["id"] = JsonValue.Create(id.Clone());
                json = node.ToJsonString();
            }
            else json = JsonSerializer.Serialize(payload);
        }
        catch { return; }
        _outbound?.Writer.TryWrite(json);
    }

    // ===================== UI 线程编组 =====================

    private Task UiAsync(Func<Task> fn)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(async () =>
        {
            try { await fn(); tcs.SetResult(true); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
            tcs.SetException(new InvalidOperationException("UI 线程不可用"));
        return tcs.Task;
    }

    private Task<T> UiAsync<T>(Func<Task<T>> fn)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(async () =>
        {
            try { tcs.SetResult(await fn()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
            tcs.SetException(new InvalidOperationException("UI 线程不可用"));
        return tcs.Task;
    }
}
