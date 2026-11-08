using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using AndroidX.Core.Content;

namespace HikariAPP2.Services;

/// <summary>Android 侧能力：通知 / 前台保活 / 唤醒锁 / 下载到手机 / 内外网判断。</summary>
public static class AndroidPlatform
{
    /// <summary>只用一个通道 / 一个 id：进度和完成提醒在同一条通知上就地更新，不会出现两条。</summary>
    public const string StatusChannel = "easy4k_status";
    public const int StatusNotificationId = 4001;

    private static PowerManager.WakeLock? _wakeLock;
    private static WifiManager.WifiLock? _wifiLock;

    public static Context AppContext { get; private set; } = null!;

    public static void Init(Context context)
    {
        AppContext = context.ApplicationContext ?? context;
        EnsureChannels();
        // 旧版本用过「保持连接 / 处理完成提醒」两个通道，清掉免得通知设置里留一堆没用的
        try
        {
            var mgr = (NotificationManager?)AppContext.GetSystemService(Context.NotificationService);
            mgr?.DeleteNotificationChannel("easy4k_running");
            mgr?.DeleteNotificationChannel("easy4k_done");
        }
        catch { }
    }

    // ===================== 通知 =====================

    public static void EnsureChannels()
    {
        if (AppContext is null) return;
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var mgr = (NotificationManager?)AppContext.GetSystemService(Context.NotificationService);
        if (mgr is null) return;

        if (mgr.GetNotificationChannel(StatusChannel) is null)
        {
            var ch = new NotificationChannel(StatusChannel, "处理进度与完成提醒", NotificationImportance.High)
            {
                Description = "显示 Easy4K 处理进度，完成后在同一条通知上提醒"
            };
            ch.EnableVibration(true);
            ch.SetShowBadge(false);
            mgr.CreateNotificationChannel(ch);
        }
    }

    public static bool HasNotificationPermission()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu) return true;
        return ContextCompat.CheckSelfPermission(AppContext, Android.Manifest.Permission.PostNotifications)
               == Permission.Granted;
    }

    /// <summary>构建唯一的那条通知：alert=false 是处理中的静默进度，true 是完成提醒（响一声/震动）。</summary>
    public static Notification BuildStatusNotification(string title, string text, bool alert)
    {
        EnsureChannels();
        var intent = new Intent(AppContext, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        var pending = PendingIntent.GetActivity(AppContext, 0, intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var b = new Notification.Builder(AppContext, StatusChannel)
            .SetSmallIcon(Resource.Drawable.ic_stat_easy4k)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetStyle(new Notification.BigTextStyle().BigText(text))
            .SetVisibility(NotificationVisibility.Public)
            .SetContentIntent(pending);

        if (alert)
        {
            b.SetCategory(Notification.CategoryMessage)
             .SetOngoing(false)
             .SetAutoCancel(true)
             .SetOnlyAlertOnce(false)
             .SetDefaults(NotificationDefaults.Sound | NotificationDefaults.Vibrate | NotificationDefaults.Lights)
             .SetVibrate(new long[] { 0, 400, 200, 400 });
        }
        else
        {
            // 进度更新不要每次都响（OnlyAlertOnce + 无额外 defaults）
            b.SetOngoing(true)
             .SetOnlyAlertOnce(true)
             .SetDefaults(0);
        }

        return b.Build()!;
    }

    /// <summary>处理结束：把同一条通知的内容就地改成完成状态，然后脱离前台服务（可下拉清除）。</summary>
    public static void NotifyDone(string title, string text)
    {
        if (AppContext is null || !HasNotificationPermission()) return;
        try
        {
            var i = new Intent(AppContext, typeof(KeepAliveService));
            i.PutExtra("text", text);
            i.PutExtra("title", title);
            i.PutExtra("done", true);
            ContextCompat.StartForegroundService(AppContext, i);
        }
        catch
        {
            // 服务起不来时至少把通知内容改掉
            try
            {
                var mgr = (NotificationManager?)AppContext.GetSystemService(Context.NotificationService);
                mgr?.Notify(StatusNotificationId, BuildStatusNotification(title, text, alert: true));
            }
            catch { }
        }
    }

    /// <summary>收掉完成提醒（用户回到 App 时调用）。</summary>
    public static void CancelDone()
    {
        try
        {
            var mgr = (NotificationManager?)AppContext?.GetSystemService(Context.NotificationService);
            mgr?.Cancel(StatusNotificationId);
        }
        catch { }
    }

    public static void StartKeepAlive(string text, string? title = null)
    {
        try
        {
            var ctx = AppContext;
            if (ctx is null) return;
            var i = new Intent(ctx, typeof(KeepAliveService));
            i.PutExtra("text", text);
            if (!string.IsNullOrWhiteSpace(title)) i.PutExtra("title", title);
            ContextCompat.StartForegroundService(ctx, i);
        }
        catch { }
    }

    public static void StopKeepAlive()
    {
        try
        {
            var ctx = AppContext;
            if (ctx is null) return;
            ctx.StopService(new Intent(ctx, typeof(KeepAliveService)));
        }
        catch { }
    }

    // ===================== 会话期唤醒锁（切后台/熄屏也不断连） =====================

    public static void AcquireSessionLocks()
    {
        try
        {
            if (_wakeLock is null)
            {
                var pm = (PowerManager?)AppContext.GetSystemService(Context.PowerService);
                if (pm is not null)
                {
                    _wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "easy4k:session");
                    _wakeLock!.SetReferenceCounted(false);
                    _wakeLock.Acquire();
                }
            }
        }
        catch { }

        try
        {
            if (_wifiLock is null)
            {
                var wm = (WifiManager?)AppContext.GetSystemService(Context.WifiService);
                if (wm is not null)
                {
                    _wifiLock = wm.CreateWifiLock(WifiMode.FullHighPerf, "easy4k:session");
                    if (_wifiLock is not null)
                    {
                        _wifiLock.SetReferenceCounted(false);
                        _wifiLock.Acquire();
                    }
                }
            }
        }
        catch { }
    }

    public static void ReleaseSessionLocks()
    {
        try { if (_wakeLock?.IsHeld == true) _wakeLock.Release(); } catch { }
        _wakeLock = null;
        try { if (_wifiLock?.IsHeld == true) _wifiLock.Release(); } catch { }
        _wifiLock = null;
    }

    // ===================== 成品下载到手机「下载」目录 =====================

    /// <summary>打开手机下载目录里的输出流：API 29+ 走 MediaStore，低版本直接写公共下载目录。</summary>
    public static Stream? OpenDownloadSink(string name)
    {
        try
        {
            var ctx = AppContext;
            if (ctx is null) return null;
            var resolver = ctx.ContentResolver;
            if (resolver is null) return null;

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                var values = new ContentValues();
                values.Put(MediaStore.MediaColumns.DisplayName, name);
                values.Put(MediaStore.MediaColumns.MimeType, MimeOf(name));
                values.Put(MediaStore.MediaColumns.IsPending, 1);

                var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values);
                if (uri is null) return null;
                var os = resolver.OpenOutputStream(uri);
                return os is null ? null : new MediaStoreSink(os, resolver, uri);
            }

#pragma warning disable CA1422
            var dir = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads);
#pragma warning restore CA1422
            if (dir is null) return null;
            if (!dir.Exists()) dir.Mkdirs();
            return new FileStream(Path.Combine(dir.AbsolutePath, name), FileMode.Create, FileAccess.Write);
        }
        catch
        {
            return null;
        }
    }

    private sealed class MediaStoreSink : Stream
    {
        private readonly Stream _inner;
        private readonly ContentResolver _resolver;
        private readonly Android.Net.Uri _uri;
        private bool _closed;

        public MediaStoreSink(Stream inner, ContentResolver resolver, Android.Net.Uri uri)
        {
            _inner = inner;
            _resolver = resolver;
            _uri = uri;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => _inner.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => _inner.WriteAsync(buffer, ct);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing || _closed) return;
            _closed = true;
            try { _inner.Dispose(); } catch { }
            try
            {
                var done = new ContentValues();
                done.Put(MediaStore.MediaColumns.IsPending, 0);
                _resolver.Update(_uri, done, null, null);
            }
            catch { }
        }
    }

    public static string MimeOf(string name)
    {
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "mp4" or "m4v" => "video/mp4",
            "mkv" => "video/x-matroska",
            "mov" => "video/quicktime",
            "avi" => "video/x-msvideo",
            "webm" => "video/webm",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            "wav" => "audio/wav",
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            _ => "application/octet-stream"
        };
    }

    // ===================== 设备名 / 内外网判断 =====================

    public static string DeviceDisplayName()
    {
        try
        {
            var n = Settings.Global.GetString(AppContext.ContentResolver, "device_name");
            if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
        }
        catch { }

        try
        {
            var name = $"{Build.Manufacturer} {Build.Model}".Trim();
            if (name.Length > 0) return name;
        }
        catch { }

        return "Android 手机";
    }

    /// <summary>电脑是否与手机在同一网段：同网段 → 内网（默认全开预览与占用）。</summary>
    public static bool DetectLan(string serverHost)
    {
        try
        {
            var addresses = System.Net.Dns.GetHostAddresses(serverHost);
            var server = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (server is null) return false;

            var serverInt = IpToLong(server.GetAddressBytes());
            foreach (var nif in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nif.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nif.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                var props = nif.GetIPProperties();
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    if (System.Net.IPAddress.IsLoopback(ua.Address)) continue;

                    var prefix = ua.PrefixLength <= 0 ? 24 : ua.PrefixLength;
                    var mask = prefix <= 0 ? 0L : (-1L << (32 - prefix)) & 0xFFFFFFFFL;
                    if ((IpToLong(ua.Address.GetAddressBytes()) & mask) == (serverInt & mask))
                        return true;
                }
            }

            return false;
        }
        catch
        {
            return true; // 判断不了时按内网处理（外网提示只是流量保护）
        }
    }

    private static long IpToLong(byte[] bytes)
    {
        long v = 0;
        foreach (var b in bytes) v = (v << 8) | (long)(b & 0xFF);
        return v & 0xFFFFFFFFL;
    }
}

/// <summary>连接期间常驻的前台服务：不做业务，只保证进程不被冻结，完成提醒才送得到。</summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class KeepAliveService : Service
{
    /// <summary>完成通知已经 detach（通知留在通知栏，服务退出）：OnDestroy 不能再 Remove 掉它。</summary>
    private bool _detached;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        AndroidPlatform.EnsureChannels();
        var title = intent?.GetStringExtra("title");
        if (string.IsNullOrWhiteSpace(title)) title = "HikariAPP";
        var text = intent?.GetStringExtra("text") ?? "已连接电脑，保持后台运行";
        var done = intent?.GetBooleanExtra("done", false) == true;

        // 同一个 id 就地更新：进度 → 完成，通知栏始终只有一条
        var notification = AndroidPlatform.BuildStatusNotification(title!, text, alert: done);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(AndroidPlatform.StatusNotificationId, notification, ForegroundService.TypeDataSync);
        else
#pragma warning disable CA1422
            StartForeground(AndroidPlatform.StatusNotificationId, notification);
#pragma warning restore CA1422

        if (done)
        {
            // 处理已结束：把通知从前台服务上摘下来，服务退出但通知留在通知栏（可下拉清除）
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                {
                    StopForeground(StopForegroundFlags.Detach);
                    _detached = true;
                }
            }
            catch { }
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        try
        {
            if (!_detached)
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                    StopForeground(StopForegroundFlags.Remove);
                else
#pragma warning disable CA1422
                    StopForeground(true);
#pragma warning restore CA1422
            }
        }
        catch { }
        base.OnDestroy();
    }
}
