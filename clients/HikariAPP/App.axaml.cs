using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HikariAPP2.ViewModels;
using HikariAPP2.Views;

namespace HikariAPP2;

/// <summary>HikariAPP2 · Sixth-Sense（第六感）：Easy4K 远程控制客户端。</summary>
public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var core = RemoteCore.Instance;

        // Android 会重建 Activity，必须用 MainViewFactory 而不是缓存同一个 MainView
        if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => new MainView { DataContext = core };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView { DataContext = core };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Avalonia 12 要求应用级 Android Application 单独声明。</summary>
[Application(Icon = "@mipmap/ic_launcher", UsesCleartextTraffic = true)]
public class AndroidApp : AvaloniaAndroidApplication<App>
{
    // 必须是 public：JNI 激活托管实例时（TypeManager.Activate）只会找公共构造函数，
    // 用 protected 会出现 "No implementation found for n_onCreate" 的启动闪退。
    public AndroidApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();
        CrashLog.Install();
    }
}

/// <summary>把未捕获异常写到 应用外部文件目录/crash.log，闪退后可以取回来看原因。</summary>
public static class CrashLog
{
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception);
        // 注意：不要设置 e.Handled = true。
        // 一旦把托管异常标记为已处理，JNI 边界会当它没发生，Activity 的 onCreate 就“正常返回”，
        // 系统随即抛 SuperNotCalledException，真正的原因反而被吞掉。
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Write(e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception);
            e.SetObserved();
        };

        // Avalonia 的 UI 线程异常（比如解析服务端消息时出错）默认会把整个进程带走，
        // 这里只做兜底：记录后继续运行，避免“点一下就开始闪退”。
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Write(e.Exception);
                e.Handled = true;
            };
        }
        catch { }
    }

    public static void Write(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var dir = Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
            if (dir is null) return;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"\n==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\n{ex}\n");
        }
        catch { }
    }
}
