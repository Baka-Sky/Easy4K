using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Easy4K.Services.RemoteControl;

/// <summary>远程控制客户端：连接另一台 Easy4K 的 11451 服务端，用与手机端完全相同的
/// 「一行一条 JSON」协议遥控它（输入/输出视频、输出目录、超分/补帧模型、启动/停止/暂停）。
///
/// 与 <see cref="RemoteControlServer"/> 是同一份协议的两端：
///  · 请求带 "id"，服务端原样回带的响应 → 由 <see cref="RequestAsync"/> 的 Task 完成为结果；
///  · 不带 "id" 的消息（progress/log/state/result/preview…）→ 抛成事件。
///  · 连接步骤文字通过 <see cref="Step"/> 回调上报，供界面显示「正在做什么」。</summary>
public sealed class RemoteControlClient
{
    // ——— 推送事件（都已在后台读线程触发，界面侧需自行切回 UI 线程）———
    public event Action<JsonElement>? ConfigReceived;
    public event Action<JsonElement>? VideoInfoReceived;
    public event Action<JsonElement>? DirReceived;
    public event Action<JsonElement>? ProgressReceived;
    public event Action<JsonElement>? ResultReceived;
    public event Action<JsonElement>? StateReceived;
    public event Action<JsonElement>? StatsReceived;
    public event Action<JsonElement>? LogReceived;
    public event Action<JsonElement>? ErrorReceived;
    public event Action<JsonElement>? NoticeReceived;
    public event Action<JsonElement>? StreamReceived;
    public event Action<byte[]>? PreviewReceived;

    /// <summary>连接已断开（含对端关闭、网络中断）</summary>
    public event Action<string>? Disconnected;

    /// <summary>连接过程中的步骤文字（如「正在检测远端显卡与模型…」）</summary>
    public event Action<string>? Step;

    public bool IsConnected { get; private set; }
    public string Host { get; private set; } = "";
    public int Port { get; private set; } = RemoteControlServer.Port;
    public string RemoteVersion { get; private set; } = "";

    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _readTask;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _reqId;

    /// <summary>连接 → 握手 → 拉取能力清单，返回 capabilities 响应（由调用方直接应用，
    /// 不走事件，避免「事件已入队但界面还没构建完」的时序问题）。任一步失败都抛异常。</summary>
    public async Task<JsonElement> ConnectAsync(string host, int port, string clientName, CancellationToken ct = default)
    {
        await DisconnectAsync();

        Host = host.Trim();
        Port = port <= 0 ? RemoteControlServer.Port : port;

        Step?.Invoke($"正在连接 {Host}:{Port} …");
        _client = new TcpClient { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await _client.ConnectAsync(Host, Port, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"连接超时：{Host}:{Port} 无响应。请确认对方已「启动远程控制」，且防火墙放行 {Port} 端口。");
            }
        }

        var stream = _client.GetStream();
        _reader = new StreamReader(stream, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        IsConnected = true;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));

        Step?.Invoke("已建立连接，正在握手 …");
        var ack = await RequestAsync("hello", new { client = clientName }, 6000, ct);
        RemoteVersion = GetStr(ack, "version");

        // 握手时没带 id 的 hello_ack 会走事件通道，这里补一次能力请求（顺带确认对端协议可用）
        Step?.Invoke("正在检测远端显卡与模型列表 …");
        return await RequestAsync("capabilities", null, 10000, ct);
    }

    /// <summary>发一条请求并等它的响应。响应带 id 回来才算数，超时抛 TimeoutException。</summary>
    public async Task<JsonElement> RequestAsync(string type, object? payload = null, int timeoutMs = 10000, CancellationToken ct = default)
    {
        if (!IsConnected || _writer is null) throw new InvalidOperationException("尚未连接");
        var id = Interlocked.Increment(ref _reqId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            var node = payload is null
                ? new JsonObject()
                : JsonSerializer.SerializeToNode(payload)!.AsObject();
            node["type"] = type;
            node["id"] = id;

            await _writeLock.WaitAsync(ct);
            try { await _writer.WriteLineAsync(node.ToJsonString()); }
            finally { _writeLock.Release(); }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            await using var reg = timeout.Token.Register(() =>
                tcs.TrySetException(new TimeoutException($"等待「{type}」响应超时（{timeoutMs} ms）")));

            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>发一条不需要回应的指令（如 stop/pause）。</summary>
    public Task SendAsync(string type, object? payload = null, CancellationToken ct = default)
        => RequestAsync(type, payload, 8000, ct);

    /// <summary>请求远端把最新帧/占用推过来（默认都关，避免无谓流量）。</summary>
    public Task SetStreamAsync(bool preview, bool stats, CancellationToken ct = default)
        => RequestAsync("set_stream", new { preview, stats }, 8000, ct);

    public async Task DisconnectAsync()
    {
        var wasConnected = IsConnected;
        IsConnected = false;
        try { _cts?.Cancel(); } catch { }
        try { _client?.Close(); } catch { }
        try { if (_readTask is not null) await Task.WhenAny(_readTask, Task.Delay(1500)); } catch { }
        foreach (var kv in _pending) kv.Value.TrySetException(new IOException("连接已断开"));
        _pending.Clear();
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        _reader = null;
        _writer = null;
        _client = null;
        if (wasConnected) Disconnected?.Invoke("已断开连接");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _writeLock.Dispose();
    }

    // ===================== 读循环 =====================

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var reason = "连接已断开";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _reader!.ReadLineAsync(ct);
                if (line is null) { reason = "远端已关闭连接"; break; }
                line = line.Trim();
                if (line.Length == 0) continue;

                JsonElement root;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    root = doc.RootElement.Clone(); // Clone 后不依赖已释放的 JsonDocument
                }
                catch (JsonException) { continue; }

                // 带 id 且有人等 → 当成请求的响应
                if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id)
                    && _pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(root);
                    continue;
                }

                Dispatch(root);
            }
        }
        catch (OperationCanceledException) { reason = "已断开连接"; }
        catch (Exception ex) { reason = $"连接中断：{ex.Message}"; }
        finally
        {
            IsConnected = false;
            foreach (var kv in _pending) kv.Value.TrySetException(new IOException(reason));
            _pending.Clear();
            Disconnected?.Invoke(reason);
        }
    }

    private void Dispatch(JsonElement root)
    {
        var type = GetStr(root, "type");
        switch (type)
        {
            case "capabilities": break; // 只作为请求响应使用
            case "config": ConfigReceived?.Invoke(root); break;
            case "video_info": VideoInfoReceived?.Invoke(root); break;
            case "dir":
            case "drives": DirReceived?.Invoke(root); break;
            case "progress": ProgressReceived?.Invoke(root); break;
            case "result": ResultReceived?.Invoke(root); break;
            case "state": StateReceived?.Invoke(root); break;
            case "stats": StatsReceived?.Invoke(root); break;
            case "log": LogReceived?.Invoke(root); break;
            case "error": ErrorReceived?.Invoke(root); break;
            case "notice": NoticeReceived?.Invoke(root); break;
            case "stream": StreamReceived?.Invoke(root); break;
            case "preview":
            {
                var b64 = GetStr(root, "data");
                if (b64.Length == 0) break;
                try { PreviewReceived?.Invoke(Convert.FromBase64String(b64)); } catch { }
                break;
            }
        }
    }

    // ===================== 小工具 =====================

    public static string GetStr(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static bool GetBool(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    public static int GetInt(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.TryGetInt32(out var i) ? i : 0;

    public static double GetDbl(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.TryGetDouble(out var d) ? d : 0;

    public static JsonElement Sub(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
}
