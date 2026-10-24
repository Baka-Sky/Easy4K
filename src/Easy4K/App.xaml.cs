using System.IO;
using Easy4K.Models;
using Easy4K.Services;
using Easy4K.ViewModels;
using Easy4K.Views.Welcome;
using Microsoft.UI.Xaml;

namespace Easy4K;

/// <summary>应用程序入口。在此装配所有服务并创建主窗口。</summary>
public partial class App : Application
{
    private Window? _window;

    // 简易服务定位器：MainPage 通过 App.Services 访问 ViewModel
    public static MainViewModel Services { get; private set; } = null!;
    public static MainWindow MainWindow { get; private set; } = null!;

    /// <summary>启动崩溃日志路径（可写根目录：便携版为 exe 旁，MSIX 安装版为 %LOCALAPPDATA%\Easy4K）</summary>
    public static string CrashLogPath => Path.Combine(AppPaths.WritableRoot, "crash.log");

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
        {
            try { File.AppendAllText(CrashLogPath, $"[Xaml Unhandled @ {DateTime.Now}]\n{e.Exception}\n\n"); } catch { }
            e.Handled = true;
        };

        // x:Bind 绑定失败追踪（Debug 构建下提供精确绑定信息，用于定位"找不到元素"类崩溃）
        var dbg = Microsoft.UI.Xaml.Application.Current.DebugSettings;
        dbg.IsBindingTracingEnabled = true;
        dbg.BindingFailed += (_, e) =>
        {
            try { File.AppendAllText(CrashLogPath, $"[BindingFailed @ {DateTime.Now}] {e.Message}\n\n"); } catch { }
        };
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

        // 孤儿执行（Easy4K.exe --orphan-run <任务id>，由守护进程到点拉起）：
        // 必须在构建 ViewModel 之前处理——用任务里的选项快照替代 appsettings.json，
        // 并禁止任何回写，否则会把用户后来改过的设置覆盖掉。
        // Take 会顺手把任务从 orphans.json 移除：即使本次处理中途崩了也不会被守护进程反复重跑。
        var orphanTask = TakeOrphanTaskFromCli();

        var (app, toolCfg) = settingsSvc.Load();
        if (orphanTask is not null)
        {
            app = orphanTask.Settings;
            settingsSvc.SuppressWrites = true;
        }
        // 多语言：功能暂时隐藏，固定简体中文（词表 Loc/strings.json 与 Loc 服务都保留，
        // 需要恢复"按配置切换语言"时把下面一行换回 Loc.Set(app.Language, notify: false) 即可）
        Loc.Set("zh-CN", notify: false);
        var tools = ToolPathResolver.Resolve(app, toolCfg);

        var logger = new Logger();
        var gpu = new GpuDetector();
        var env = new EnvironmentDetector(tools, gpu);
        var videoDet = new VideoInfoDetector(tools);
        var runner = new ProcessRunner(logger);
        var orch = new ProcessingOrchestrator(runner, logger);

        Services = new MainViewModel(logger, settingsSvc, tools, env, videoDet, orch, runner, app, toolCfg);

        // 软件内自测命令行模式（不弹 OOBE、不自动跑处理前测试/自动测试）
        var cmdLine = Environment.GetCommandLineArgs();
        var selftestCli = false;
        var autoTestCli = false;
        var autoTestFull = false;
        var autoTestApi = false;
        var dedupOnly = false;
        var remoteCli = false;
        for (int i = 0; i < cmdLine.Length; i++)
        {
            // 远程控制直启: Easy4K.exe --remote（跳过 OOBE，直接进入等待客户端连接）
            if (cmdLine[i].Equals("--remote", StringComparison.OrdinalIgnoreCase))
            {
                remoteCli = true;
                continue;
            }

            if (cmdLine[i].Equals("--selftest", StringComparison.OrdinalIgnoreCase) && i + 2 < cmdLine.Length)
            {
                selftestCli = true;
                var video = cmdLine[i + 1];
                var report = cmdLine[i + 2];
                int stages = 15;
                if (i + 3 < cmdLine.Length && int.TryParse(cmdLine[i + 3], out var s))
                    stages = s;
                _ = RunSelfTestDelayedAsync(video, report, stages);
                break;
            }

            // 清理自测模式: Easy4K.exe --selftest-clean <tempDir> <reportPath>
            if (cmdLine[i].Equals("--selftest-clean", StringComparison.OrdinalIgnoreCase) && i + 2 < cmdLine.Length)
            {
                var dir = cmdLine[i + 1];
                var report = cmdLine[i + 2];
                _ = RunSelfCleanAsync(dir, report);
                break;
            }

            // GUI 全面自动测试: Easy4K.exe --autotest-all [full]（全模型×2；full 时全部模型×2~5 全矩阵）
            if (cmdLine[i].Equals("--autotest-all", StringComparison.OrdinalIgnoreCase))
            {
                autoTestCli = true;
                if (i + 1 < cmdLine.Length
                    && (cmdLine[i + 1].Equals("full", StringComparison.OrdinalIgnoreCase)
                        || cmdLine[i + 1].Equals("matrix", StringComparison.OrdinalIgnoreCase)))
                    autoTestFull = true;
                break;
            }

            // API 模式全组合自动测试（同孤儿模式：隐藏窗口后台跑，跑完把报告写桌面并退出进程）
            // Easy4K.exe --autotest-api [full]
            if (cmdLine[i].Equals("--autotest-api", StringComparison.OrdinalIgnoreCase)
                || cmdLine[i].Equals("--autotest-headless", StringComparison.OrdinalIgnoreCase))
            {
                autoTestCli = true;
                autoTestApi = true;
                if (i + 1 < cmdLine.Length
                    && (cmdLine[i + 1].Equals("full", StringComparison.OrdinalIgnoreCase)
                        || cmdLine[i + 1].Equals("matrix", StringComparison.OrdinalIgnoreCase)))
                    autoTestFull = true;
                break;
            }

            // 帧去重专项测试: Easy4K.exe --dedup-test（不加载超分模型，补帧只用最轻 lite，几十秒跑完）
            if (cmdLine[i].Equals("--dedup-test", StringComparison.OrdinalIgnoreCase))
            {
                autoTestCli = true;
                dedupOnly = true;
                break;
            }
        }

        // 首次运行：先展示 OOBE 设置向导（完成后保存配置并衔接正式主界面）
        // 孤儿执行也算命令行直启，不能被向导拦下（快照里已把 SetupCompleted 置真，这里再兜一层）
        if (!selftestCli && !autoTestCli && !remoteCli && orphanTask is null && !app.SetupCompleted)
        {
            var wizard = new WelcomeWindow(Services, settingsSvc, app, toolCfg);
            wizard.Completed += () =>
            {
                OpenMainWindow();
            };
            _window = wizard;
            wizard.Activate();
            return;
        }

        OpenMainWindow();
        // 孤儿执行：界面起来后立刻按任务里的选项开跑，跑完自动退出进程
        if (orphanTask is not null)
            _ = RunOrphanTaskDelayedAsync(orphanTask);
        if (autoTestCli)
            _ = RunAutoTestDelayedAsync(autoTestFull, dedupOnly, autoTestApi);
        // 远程连接模式：--remote 命令行直启，或用户开了「每次启动自动进入远程连接模式」
        if (remoteCli || Services.AutoRemoteOnStartup)
            _ = MainWindow.StartRemoteControlAsync();
    }

    /// <summary>命令行里是否带 --orphan-run &lt;任务id&gt;。带的话取走该任务（并从队列移除）返回，否则返回 null。</summary>
    private static OrphanTask? TakeOrphanTaskFromCli()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--orphan-run", StringComparison.OrdinalIgnoreCase))
                return new OrphanTaskStore().Take(args[i + 1]);
        }
        return null;
    }

    /// <summary>延迟到主窗口/页面加载完成后再跑孤儿任务（等 UI 线程就绪，与自测同款）。</summary>
    private static async Task RunOrphanTaskDelayedAsync(OrphanTask task)
    {
        try
        {
            await Task.Delay(1500);
            await Services.RunOrphanTaskAsync(task);
        }
        catch (Exception ex)
        {
            File.AppendAllText(CrashLogPath, $"[OrphanRun @ {DateTime.Now}]\n{ex}\n\n");
            Environment.Exit(1); // 兜底：异常也不能让进程留在这里不退出
        }
    }

    /// <summary>创建并显示正式主窗口。
    /// 处理前测试不再在启动时执行：只在用户点击"开始处理"且未勾选"跳过测试"时，用 1 秒测试视频验证勾选流程。</summary>
    private void OpenMainWindow()
    {
        if (_window is MainWindow) return; // 已打开
        MainWindow = new MainWindow();
        _window = MainWindow;
        MainWindow.Activate();
    }

    /// <summary>延迟到主窗口/页面加载完成后再跑 GUI 自动测试（--autotest-all / --dedup-test / --autotest-api 专用）。
    /// apiMode = 同孤儿模式：隐藏窗口后台跑，报告写桌面，跑完自动退出进程。</summary>
    private static async Task RunAutoTestDelayedAsync(bool fullMatrix, bool dedupOnly = false, bool apiMode = false)
    {
        try
        {
            await Task.Delay(1500);
            if (apiMode)
            {
                try { MainWindow?.AppWindow.Hide(); } catch { }
            }
            await Services.RunAutoTestAllAsync(fullMatrix, dedupOnly, apiMode);
        }
        catch (Exception ex)
        {
            File.AppendAllText(CrashLogPath, $"[AutoTest @ {DateTime.Now}]\n{ex}\n\n");
        }
        finally
        {
            if (apiMode)
            {
                try { Application.Current.Exit(); } catch { }
            }
        }
    }

    /// <summary>自测清理：延迟执行清理逻辑，把结果写报告文件后退出。</summary>
    private static async Task RunSelfCleanAsync(string dir, string report)
    {
        try
        {
            await Task.Delay(1500);
            var result = await Services.CleanTempInAsync(dir);
            var gone = !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any();
            await File.WriteAllTextAsync(report,
                $"RESULT: {result}\r\nDIR_EMPTY_OR_GONE: {gone}\r\nDONE");
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(report, $"EXCEPTION: {ex}");
        }
        Application.Current.Exit();
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

