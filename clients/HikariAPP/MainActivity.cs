using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;
using HikariAPP2.ViewModels;

[assembly: Android.App.UsesPermission(Android.Manifest.Permission.Internet)]
[assembly: Android.App.UsesPermission(Android.Manifest.Permission.AccessNetworkState)]
[assembly: Android.App.UsesPermission(Android.Manifest.Permission.WakeLock)]
[assembly: Android.App.UsesPermission(Android.Manifest.Permission.AccessWifiState)]
[assembly: Android.App.UsesPermission(Android.Manifest.Permission.PostNotifications)]
[assembly: Android.App.UsesPermission(Android.Manifest.Permission.ForegroundService)]
[assembly: Android.App.UsesPermission(Android.Manifest.Permission.ForegroundServiceDataSync)]
[assembly: Android.App.UsesPermission(Android.Manifest.Permission.WriteExternalStorage, MaxSdkVersion = 28)]

namespace HikariAPP2;

/// <summary>HikariAPP2 的 Android 入口。布局与旧版保持一致，仅 UI 框架换成 Avalonia。</summary>
[Activity(
    Label = "HikariAPP",
    Theme = "@style/AppTheme.NoActionBar",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize |
                           ConfigChanges.UiMode | ConfigChanges.KeyboardHidden)]
public class MainActivity : AvaloniaMainActivity
{
    private bool _attached;
    private bool _autoRan;

    protected override void OnResume()
    {
        base.OnResume();

        if (!_attached)
        {
            _attached = true;
            RemoteCore.Instance.Attach(ApplicationContext!);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
                !Services.AndroidPlatform.HasNotificationPermission())
            {
                try
                {
                    RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, 1001);
                }
                catch { }
            }
        }

        if (!_autoRan && Intent?.GetStringExtra("hikari_autotest") == "1")
        {
            _autoRan = true;
            RemoteCore.Instance.BeginAutoTest(
                Intent.GetStringExtra("host") ?? "",
                Intent.GetStringExtra("key") ?? "",
                Intent.GetStringExtra("file") ?? "",
                Intent.GetStringExtra("stats") == "1");
        }

        RemoteCore.Instance.OnUiResumed();
    }

    public override void OnBackPressed()
    {
        if (RemoteCore.Instance.HandleBack()) return;
        base.OnBackPressed();
    }
}
