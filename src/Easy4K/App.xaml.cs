using System.IO;
using Easy4K.Models;
using Easy4K.Services;
using Easy4K.ViewModels;
using Microsoft.UI.Xaml;

namespace Easy4K;

/// <summary>应用程序入口。在此装配所有服务并创建主窗口。</summary>
public partial class App : Application
{
    private Window? _window;

    // 简易服务定位器：MainPage 通过 App.Services 访问 ViewModel
    public static MainViewModel Services { get; private set; } = null!;
    public static MainWindow MainWindow { get; private set; } = null!;

    /// <summary>启动崩溃日志路径（exe 旁）</summary>
    public static string CrashLogPath => Path.Combine(AppContext.BaseDirectory, "crash.log");

    public App()
    {
        // 先装最基础的兜底：AppDomain 级未处理异常落盘
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            File.AppendAllText(CrashLogPath, $"[AppDomain UnhandledException @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{e.ExceptionObject}\n\n");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        { File.AppendAllText(CrashLogPath, $"[TaskScheduler Unobserved @ {DateTime.Now}]\n{e.Exception}\n\n"); e.SetObserved(); };

        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            File.AppendAllText(CrashLogPath, $"[InitializeComponent @ {DateTime.Now}]\n{ex}\n\n");
            throw;
        }

        // InitializeComponent 之后再订阅 WinUI XAML 层的未处理异常
        Microsoft.UI.Xaml.Application.Current.UnhandledException += (_, e) =>
        { File.AppendAllText(CrashLogPath, $"[Xaml Unhandled @ {DateTime.Now}]\n{e.Exception}\n\n"); e.Handled = true; };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            LaunchInternal();
        }
        catch (Exception ex)
        {
            File.AppendAllText(CrashLogPath, $"[OnLaunched @ {DateTime.Now}]\n{ex}\n\n");
            // 启动期失败也尝试显示一个简易错误对话框（无 XAML 依赖）
            try { Microsoft.UI.Xaml.Controls.ContentDialog? _ = null; } catch { }
        }
    }

    private void LaunchInternal()
    {
        // 装配服务链
        var settingsSvc = new SettingsService();
        var (app, toolCfg) = settingsSvc.Load();
        var tools = ToolPathResolver.Resolve(app, toolCfg);

        var logger = new Logger();
        var gpu = new GpuDetector();
        var env = new EnvironmentDetector(tools, gpu);
        var videoDet = new VideoInfoDetector(tools);
        var runner = new ProcessRunner(logger);
        var orch = new ProcessingOrchestrator(runner, logger);

        Services = new MainViewModel(logger, settingsSvc, tools, env, videoDet, orch, runner, app, toolCfg);

        MainWindow = new MainWindow();
        _window = MainWindow;
        _window.Activate();

        // 软件内自测模式: Easy4K.exe --selftest <inputVideo> <reportPath> [stages]
        // stages 位掩码(可选, 默认15): 1=拆帧 2=超分 4=补帧 8=合并 16=音频
        var cmdLine = Environment.GetCommandLineArgs();
        for (int i = 0; i < cmdLine.Length; i++)
        {
            if (cmdLine[i].Equals("--selftest", StringComparison.OrdinalIgnoreCase) && i + 2 < cmdLine.Length)
            {
                var video = cmdLine[i + 1];
                var report = cmdLine[i + 2];
                int stages = 15;
                if (i + 3 < cmdLine.Length && int.TryParse(cmdLine[i + 3], out var s))
                    stages = s;
                _ = RunSelfTestDelayedAsync(video, report, stages);
                break;
            }
        }
    }

    /// <summary>延迟启动自测，等窗口/页面加载完成，确保 UI 线程就绪。</summary>
    private static async Task RunSelfTestDelayedAsync(string video, string report, int stages)
    {
        try
        {
            await Task.Delay(1500);
            await Services.RunSelfTestAsync(video, report, stages);
        }
        catch (Exception ex)
        {
            File.AppendAllText(CrashLogPath, $"[SelfTest @ {DateTime.Now}]\n{ex}\n\n");
        }
    }
}

