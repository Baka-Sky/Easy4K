using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HikariAPP2.Services;

/// <summary>
/// Easy4K 远程控制协议客户端（与桌面端 RemoteControlClient 同一份协议）：
/// TCP + 一行一条 JSON（UTF-8）。
///
///  · 带 id 的请求 → 由 <see cref="RequestAsync"/> 的 Task 完成为响应；
///  · 不带 id 的推送（progress/log/state/result/preview…）→ <see cref="Message"/> 事件；
///  · 连接步骤文字通过 <see cref="Step"/> 上报，供连接页显示「正在做什么」。
/// </summary>
public sealed class Easy4kClient : IAsyncDisposable
{
    public const int DefaultPort = 11451;

    public event Action<JsonElement>? Message;
    public event Action<byte[]>? PreviewReceived;
    public event Action<string>? Step;
    public event Action<string>? Disconnected;

    public bool IsConnected { get; private set; }
    public string RemoteVersion { get; private set; } = "";

    /// <summary>最近一次收到任意一行数据的时间（TickCount64），用于识别半开连接。</summary>
    public long LastReceivedAt { get; private set; }

    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _reqId;
    private volatile bool _manualClose;

    // 服务端连上就主动推一条不带 id 的 hello_ack（不是对我们 hello 请求的回应），
    // 必须在读循环里单独接住，否则会当成普通事件丢掉、握手永远等不到。
    private readonly object _helloLock = new();
    private JsonElement? _helloAck;
    private TaskCompletionSource<JsonElement>? _helloTcs;

    /// <summary>读取已保存的电脑端证书指纹（按主机名），由上层用偏好设置实现。</summary>
    public Func<string, string?>? GetPinnedFingerprint { get; set; }
    /// <summary>首次连接时保存电脑端证书指纹（TOFU：防止以后被冒充）。</summary>
    public Action<string, string>? SavePinnedFingerprint { get; set; }

    private async Task<Stream> ConnectTlsAsync(NetworkStream stream, string host, CancellationToken ct)
    {
        var pinError = "";

        bool Accept(X509Certificate? cert)
        {
            if (cert is null) { pinError = "电脑端没有提供证书"; return false; }
            var fp = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
            var saved = GetPinnedFingerprint?.Invoke(host) ?? "";
            if (saved.Length > 0)
            {
                if (!string.Equals(saved, fp, StringComparison.OrdinalIgnoreCase))
                {
                    pinError = $"电脑端证书指纹与上次不一致，可能有人冒充（{host}）。若确实重装过 Easy4K，可清除 App 数据后重连。";
                    return false;
                }
                return true;
            }
            SavePinnedFingerprint?.Invoke(host, fp);
            return true;
        }

        var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, cert, _, _) => Accept(cert));
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.None,   // 让系统协商最高版本（TLS 1.3 优先，其次 1.2）
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, ct);
            return ssl;
        }
        catch (AuthenticationException)
        {
            try { ssl.Dispose(); } catch { }
            throw new AuthenticationException(string.IsNullOrEmpty(pinError)
                ? "无法建立加密连接：电脑端版本过旧或该端口不是 Easy4K 远程控制"
                : pinError);
        }
    }

    /// <summary>连接 → 握手（hello，必要时校验安全密钥）。返回 hello_ack。</summary>
    public async Task<JsonElement> ConnectAsync(string host, int port, string clientName,
        Func<Task<string?>>? requestKey = null, CancellationToken ct = default)
    {
        await DisconnectAsync();

        _manualClose = false;
        port = port is > 0 and <= 65535 ? port : DefaultPort;
        lock (_helloLock)
        {
            _helloAck = null;
            _helloTcs = null;
        }

        Step?.Invoke($"正在连接 {host}:{port} …");
        _client = new TcpClient { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await _client.ConnectAsync(host, port, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"连接超时：{host}:{port} 无响应。请确认电脑端已「启动远程控制」，且防火墙放行 {port} 端口。");
            }
        }

        var stream = _client.GetStream();
        // 全链路加密：先做 TLS 握手并校验电脑端证书指纹，之后的协议都在密文里跑
        Step?.Invoke("正在建立加密通道 …");
        Stream secure = await ConnectTlsAsync(stream, host, ct);
        _reader = new StreamReader(secure, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
        _writer = new StreamWriter(secure, new UTF8Encoding(false), 1 << 16, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        IsConnected = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));

        Step?.Invoke("已建立连接，正在握手 …");
        // 服务端在连接建立时就会主动发 hello_ack / auth_required，
        // 这里等它，而不是再发一个 hello（未授权前发 hello 会被服务端当成非法指令直接断开）。
        var ack = await WaitForHelloAckAsync(8000, ct);
        RemoteVersion = GetStr(ack, "version");

        if (GetBool(ack, "authRequired"))
        {
            Step?.Invoke("对方已启用安全密钥，等待输入 …");
            var key = requestKey is null ? null : await requestKey();
            if (string.IsNullOrWhiteSpace(key))
                throw new UnauthorizedAccessException("对方已启用安全密钥，未输入密钥，已取消连接");

            Step?.Invoke("正在校验安全密钥 …");
            var auth = await RequestAsync("auth", new JsonObject { ["key"] = key }, 10000, ct);
            if (GetStr(auth, "type") == "auth_failed")
            {
                var msg = GetStr(auth, "message");
                throw new UnauthorizedAccessException(string.IsNullOrEmpty(msg) ? "安全密钥错误" : msg);
            }
        }

        // 已经授权，这时候再发 hello 才是合法的（服务端会记下客户端名称，用于「客户端已连接：xxx」）
        if (!string.IsNullOrWhiteSpace(clientName))
        {
            try { await RequestAsync("hello", new JsonObject { ["client"] = clientName }, 5000, ct); }
            catch { }
        }

        return ack;
    }

    /// <summary>等连接建立后服务端主动推来的 hello_ack（不带 id）。</summary>
    private Task<JsonElement> WaitForHelloAckAsync(int timeoutMs, CancellationToken ct)
    {
        TaskCompletionSource<JsonElement> tcs;
        lock (_helloLock)
        {
            if (_helloAck is { } ready) return Task.FromResult(ready);
            tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _helloTcs = tcs;
        }

        return WaitCore();

        async Task<JsonElement> WaitCore()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            var delay = Task.Delay(Timeout.Infinite, timeout.Token);
            var done = await Task.WhenAny(tcs.Task, delay);
            if (done == tcs.Task) return await tcs.Task;
            throw new TimeoutException("握手超时：电脑端没有发来 hello_ack");
        }
    }

    private void StoreHelloAck(JsonElement root)
    {
        TaskCompletionSource<JsonElement>? waiter;
        lock (_helloLock)
        {
            _helloAck = root;
            waiter = _helloTcs;
            _helloTcs = null;
        }
        waiter?.TrySetResult(root);
    }

    /// <summary>发一条请求并等带同一个 id 的响应；超时抛 TimeoutException。</summary>
    public async Task<JsonElement> RequestAsync(string type, JsonObject? payload = null, int timeoutMs = 10000,
        CancellationToken ct = default)
    {
        if (!IsConnected || _writer is null) throw new InvalidOperationException("尚未连接");

        var id = Interlocked.Increment(ref _reqId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            var node = payload is null ? new JsonObject() : (JsonObject)payload.DeepClone();
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

    /// <summary>发一条不需要等待回应的消息（如 upload_chunk / stop）。</summary>
    public async Task SendAsync(JsonObject payload, CancellationToken ct = default)
    {
        if (!IsConnected || _writer is null) throw new InvalidOperationException("尚未连接");
        await _writeLock.WaitAsync(ct);
        try { await _writer.WriteLineAsync(payload.ToJsonString()); }
        finally { _writeLock.Release(); }
    }

    public async Task DisconnectAsync()
    {
        var wasConnected = IsConnected;
        IsConnected = false;
        _manualClose = true;
        try { _cts?.Cancel(); } catch { }
        try { _client?.Close(); } catch { }
        try { if (_readTask is not null) await Task.WhenAny(_readTask, Task.Delay(1200)); } catch { }

        foreach (var kv in _pending) kv.Value.TrySetException(new IOException("连接已断开"));
        _pending.Clear();

        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        _reader = null;
        _writer = null;
        _client = null;
        _cts?.Dispose();
        _cts = null;
        _readTask = null;

        if (wasConnected) Disconnected?.Invoke("已断开连接");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _writeLock.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var reason = "连接已断开";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _reader!.ReadLineAsync(ct);
                if (line is null) { reason = "远端已关闭连接"; break; }
                LastReceivedAt = System.Environment.TickCount64;
                if (line.Length == 0) continue;

                JsonElement root;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    root = doc.RootElement.Clone();
                }
                catch (JsonException) { continue; }

                if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id)
                    && _pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(root);
                    continue;
                }

                var type = GetStr(root, "type");
                if (type == "hello_ack")
                {
                    StoreHelloAck(root);
                    continue;
                }
                if (type == "auth_required") continue;

                if (type == "preview")
                {
                    var b64 = GetStr(root, "data");
                    if (b64.Length > 0)
                    {
                        try { PreviewReceived?.Invoke(Convert.FromBase64String(b64)); } catch { }
                    }
                    continue;
                }

                Message?.Invoke(root);
            }
        }
        catch (OperationCanceledException) { reason = "已断开连接"; }
        catch (Exception ex) { reason = $"连接中断：{ex.Message}"; }
        finally
        {
            IsConnected = false;
            foreach (var kv in _pending) kv.Value.TrySetException(new IOException(reason));
            _pending.Clear();
            if (!_manualClose) Disconnected?.Invoke(reason);
        }
    }

    // ===================== JSON 小工具 =====================

    // JsonElement.TryGetProperty 在非 Object（Undefined / Null / Array / 数字）时会直接抛
    // InvalidOperationException，服务端少下发一个字段就足以让 UI 线程闪退。
    // 所有取值入口统一先检查 ValueKind。
    private static bool Has(JsonElement e, string key, out JsonElement v)
    {
        v = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out v);
    }

    public static string GetStr(JsonElement e, string key) =>
        Has(e, key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static bool GetBool(JsonElement e, string key, bool fallback = false) =>
        Has(e, key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback
            }
            : fallback;

    public static int GetInt(JsonElement e, string key, int fallback = 0) =>
        Has(e, key, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    public static long GetLong(JsonElement e, string key, long fallback = 0) =>
        Has(e, key, out var v) && v.TryGetInt64(out var l) ? l : fallback;

    public static double GetDbl(JsonElement e, string key, double fallback = 0) =>
        Has(e, key, out var v) && v.TryGetDouble(out var d) ? d : fallback;

    public static JsonElement Obj(JsonElement e, string key) =>
        Has(e, key, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
}
