using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Easy4K.LuminolEngine.RemoteControl;

/// <summary>远程控制的全链路加密（TLS 1.3 / 1.2）：
///  · 服务端首次启动时生成一张自签证书并保存在配置目录，之后一直复用；
///  · 客户端连接时做「指纹固定」（TOFU）：第一次连上记下证书 SHA-256，之后指纹变了就拒绝连接，
///    防止局域网里有人冒充服务端做中间人；
///  · 加密层之下仍是原来的「一行一条 JSON」协议，业务代码不用改。</summary>
public static class RemoteTls
{
    private const string CertFileName = "remote-tls.pfx";
    private const string CertPassword = "easy4k-remote";
    private static readonly object _sync = new();
    private static X509Certificate2? _cert;

    public static X509Certificate2 ServerCertificate
    {
        get
        {
            lock (_sync) return _cert ??= LoadOrCreate();
        }
    }

    /// <summary>服务端证书指纹（SHA-256，十六进制），用于界面展示 / 排障。</summary>
    public static string ServerFingerprint =>
        Convert.ToHexString(SHA256.HashData(ServerCertificate.RawData));

    private static X509Certificate2 LoadOrCreate()
    {
        var path = Path.Combine(AppPaths.WritableRoot, CertFileName);
        try
        {
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                var loaded = X509CertificateLoader.LoadPkcs12(bytes, CertPassword);
                if (loaded.NotAfter > DateTime.Now.AddDays(7)) return loaded;
            }
        }
        catch { }

        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest("CN=Easy4K Remote", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));   // serverAuth
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        var pfx = cert.Export(X509ContentType.Pfx, CertPassword);
        try { File.WriteAllBytes(path, pfx); } catch { }
        return X509CertificateLoader.LoadPkcs12(pfx, CertPassword);
    }

    /// <summary>服务端：把已接受的 TCP 连接升级成加密流。</summary>
    public static async Task<SslStream> AcceptAsync(NetworkStream stream, CancellationToken ct)
    {
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = ServerCertificate,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, ct);
        return ssl;
    }

    /// <summary>客户端：建立加密连接并校验服务端证书指纹。</summary>
    public static async Task<SslStream> ConnectAsync(NetworkStream stream, string host, CancellationToken ct)
    {
        var pinError = "";
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false,
            (_, cert, _, _) => ValidatePinned(host, cert, out pinError));

        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, ct);
            return ssl;
        }
        catch (AuthenticationException)
        {
            try { ssl.Dispose(); } catch { }
            throw new AuthenticationException(string.IsNullOrEmpty(pinError)
                ? "无法建立加密连接：TLS 握手失败（对方可能不是 Easy4K，或版本过旧）"
                : pinError);
        }
    }

    // ===================== 指纹固定（TOFU） =====================

    private static readonly object _pinSync = new();
    private static string PinFile => Path.Combine(AppPaths.WritableRoot, "remote-tls-pins.json");

    public static bool ValidatePinned(string host, X509Certificate? cert, out string error)
    {
        error = "";
        if (cert is null) { error = "对方没有提供证书"; return false; }
        var fp = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

        lock (_pinSync)
        {
            var map = LoadPins();
            if (map.TryGetValue(host, out var saved))
            {
                if (!string.Equals(saved, fp, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"证书指纹与上次连接不一致，可能有人在冒充服务端（{host}）。\n上次：{saved}\n本次：{fp}";
                    return false;
                }
                return true;
            }

            map[host] = fp;
            SavePins(map);
            return true;
        }
    }

    private static Dictionary<string, string> LoadPins()
    {
        try
        {
            if (File.Exists(PinFile))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PinFile))
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SavePins(Dictionary<string, string> map)
    {
        try { File.WriteAllText(PinFile, JsonSerializer.Serialize(map)); } catch { }
    }
}
