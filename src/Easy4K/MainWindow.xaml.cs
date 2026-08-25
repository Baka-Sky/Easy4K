using System.Diagnostics;
using Easy4K.Models;
using Easy4K.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Easy4K;

/// <summary>应用窗口：自定义标题栏 + 全局菜单栏 + 主页/进行中导航 + 纯进度条。</summary>
public sealed partial class MainWindow : Window
{
    private MainViewModel Vm => App.Services;
    private bool _allowClose;
    /// <summary>老缓存弹窗是否已打开（防止重复触发时叠弹多个）</summary>
    private bool _cacheDialogOpen;
    /// <summary>亚克力主题材质（作为独立主题选项，与普通主题互斥）</summary>
    private DesktopAcrylicBackdrop? _acrylic;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 标题追加版本号（版本号来自 config 文件 appsettings.json 的 Version）
        var title = $"Easy4K v{Vm.Version}";
        Title = title;
        TitleTextBlock.Text = title;

        // 窗口按 16:9 比例设置初始尺寸（1280x720，按 DPI 缩放）
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        AppWindow.Resize(new() { Width = (int)(1280 * scale), Height = (int)(720 * scale) });

        // 处理中关闭窗口 → 警告并确认，确认后强杀工具进程
        AppWindow.Closing += OnAppWindowClosing;

        RootFrame.Navigate(typeof(MainPage));

        // 处理开始 → 显示导航按钮 + 自动切到"进行中"页
        Vm.ProcessingStarted += () =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                NavButtons.Visibility = Visibility.Visible;
                CleanTempMenuItem.IsEnabled = false; // 处理中禁止清理（菜单入口同样禁用）
                NavigateToProgress();
            });
        };

        // 处理结束 → 隐藏导航按钮 + 切回主页
        Vm.ProcessingCompleted += _ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                NavButtons.Visibility = Visibility.Collapsed;
                CleanTempMenuItem.IsEnabled = true; // 处理结束恢复清理菜单
                NavigateToHome();
            });
        };

        // 纯进度条实时更新（进行页隐藏，主页可见）
        // 只接受有总帧数的进度事件；拆帧/超分预览轮询事件(Total=0)只用于刷新预览图，
        // 若也写入进度条，会被每 200ms 一次的轮询事件归零导致进度条抽搐
        Vm.ProgressChanged += p =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (p.Total > 0) ThinProgressBar.Value = p.Percent;
            });
        };

        // 清理临时文件：主页进度条显示清理进度（不定进度转圈，避免 0→100→0 跳变抽搐；不切换进行中页）
        Vm.PropertyChanged += (s, e) => DispatcherQueue.TryEnqueue(() =>
        {
            if (e.PropertyName == nameof(Vm.IsCleaning))
            {
                ThinProgressBar.IsIndeterminate = Vm.IsCleaning; // 清理用不定进度
                ThinProgressBar.Visibility = (Vm.IsCleaning || Vm.IsProcessing) ? Visibility.Visible : Visibility.Collapsed;
                if (!Vm.IsCleaning && !Vm.IsProcessing)
                    ThinProgressBar.IsIndeterminate = false; // 恢复定值进度供处理使用
            }
        });

        // 临时目录缓存与当前视频不符 → 弹窗引导（可重新选择临时目录 / 清理缓存 / 取消）
        // 取消 → 阻断启动（启动按钮禁用），再次用快捷键/按钮启动仍会重新弹窗，直到清理缓存或换目录
        Vm.TempCacheMismatchDetected += () => DispatcherQueue.TryEnqueue(async () =>
        {
            if (_cacheDialogOpen) return;
            _cacheDialogOpen = true;
            try
            {
                var dlg = new ContentDialog
                {
                    Title = "临时目录缓存不符",
                    Content = "当前临时目录中的缓存来自其他视频，直接处理会被旧缓存误导。\n\n请重新选择临时目录，或先清理缓存。",
                    PrimaryButtonText = "重新选择临时目录",
                    SecondaryButtonText = "清理缓存",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = RootFrame.XamlRoot
                };
                var r = await dlg.ShowAsync();
                if (r == ContentDialogResult.Primary)
                {
                    await PickTempFolderAsync();
                }
                else if (r == ContentDialogResult.Secondary)
                {
                    // 清理缓存（双重警告确认后执行，含 cache.json 一并删除）
                    await ConfirmCleanTempAsync();
                }
                // 任何结果后重新评估阻断状态：
                // 换了匹配/无缓存的目录或已清理 → 放行；仍不匹配（含用户点取消）→ 保持阻断禁用启动
                Vm.RefreshCacheBlock();
            }
            finally
            {
                _cacheDialogOpen = false;
            }
        });

        // 启动时恢复上次保存的主题（light/dark/system/acrylic）
        switch (Vm.SavedTheme)
        {
            case "light": SetTheme(ElementTheme.Light); break;
            case "dark": SetTheme(ElementTheme.Dark); break;
            case "acrylic": SetAcrylicTheme(); break;
        }

        // 启动后异步检查更新（服务器版本高于 config 里的本地版本时弹窗提示）
        _ = CheckUpdateAsync();
    }

    // ===================== 导航 =====================

    public void NavigateToHome()
    {
        if (RootFrame.CurrentSourcePageType != typeof(MainPage))
            RootFrame.Navigate(typeof(MainPage));
        // 未启动处理时不显示进度条；处理中手动回主页才显示全局进度
        ThinProgressBar.Visibility = Vm.IsProcessing ? Visibility.Visible : Visibility.Collapsed;
    }

    public void NavigateToProgress()
    {
        if (RootFrame.CurrentSourcePageType != typeof(ProgressPage))
            RootFrame.Navigate(typeof(ProgressPage));
        ThinProgressBar.Visibility = Visibility.Collapsed;
    }

    private void OnNavHome(object sender, RoutedEventArgs e) => NavigateToHome();
    private void OnNavProgress(object sender, RoutedEventArgs e) => NavigateToProgress();

    // ===================== 关闭拦截 =====================

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        // 已确认关闭（包括无处理任务的正常关闭）直接放行
        if (_allowClose) return;

        args.Cancel = true; // 先阻止关闭，弹窗确认后再决定
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        // 没在处理 → 直接关闭
        if (!Vm.IsProcessing)
        {
            _allowClose = true;
            Close();
            return;
        }

        var dlg = new ContentDialog
        {
            Title = "正在处理中",
            Content = "还有处理任务正在进行中，关闭将强制终止 FFmpeg / RealESRGAN / RIFE 等进程并丢弃未完成结果。确定关闭吗？",
            PrimaryButtonText = "关闭并终止",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootFrame.XamlRoot
        };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return; // 取消 → 继续处理

        // 确认关闭：停止任务 + 强杀工具进程（防止残留占用 GPU/文件）
        Vm.Stop();
        Vm.KillToolProcesses();
        Vm.Logger.Warn("已强制终止处理进程，正在退出...");
        _allowClose = true;
        Close();
    }

    // ===================== 文件 / 目录选择 =====================

    private async void OnOpenVideoMenu(object sender, RoutedEventArgs e) => await PickVideoAsync();
    private async void OnChooseTempMenu(object sender, RoutedEventArgs e) => await PickTempFolderAsync();
    private async void OnChooseOutputMenu(object sender, RoutedEventArgs e) => await PickFolderAsync(p => Vm.SetOutputRoot(p));
    private void OnExitMenu(object sender, RoutedEventArgs e) => Application.Current.Exit();

    private async Task PickVideoAsync()
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mkv");
        picker.FileTypeFilter.Add(".avi");
        picker.FileTypeFilter.Add(".mov");
        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await Vm.SetInputVideoAsync(file.Path);
    }

    private async Task PickFolderAsync(Action<string> apply)
    {
        var picker = new FolderPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.ViewMode = PickerViewMode.List;
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) apply(folder.Path);
    }

    /// <summary>选择临时目录：检测 cache.json——来源视频与当前视频不符则弹三选一，否则直接接受</summary>
    private async Task PickTempFolderAsync()
    {
        var picker = new FolderPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.ViewMode = PickerViewMode.List;

        while (true)
        {
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            var (status, source) = Vm.CheckTempCache(folder.Path);
            if (status is ViewModels.MainViewModel.TempCacheStatus.None or ViewModels.MainViewModel.TempCacheStatus.Match)
            {
                Vm.SetTempRoot(folder.Path);
                return;
            }

            var dlg = new ContentDialog
            {
                Title = "检测到旧缓存文件",
                Content = $"该文件夹的缓存来源是：{source}，与当前输入视频不符。\n\n如何处理？",
                PrimaryButtonText = "覆盖老缓存",
                SecondaryButtonText = "更换至其他文件夹",
                CloseButtonText = "取消本次实例",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootFrame.XamlRoot
            };
            var r = await dlg.ShowAsync();
            if (r == ContentDialogResult.Primary)
            {
                Vm.AcceptTempRoot(folder.Path);
                return;
            }
            if (r == ContentDialogResult.Secondary) continue;
            return;
        }
    }

    private void InitializePicker(object picker)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);
    }

    // ===================== 工具 / 设置 / 帮助 =====================

    private void OnCheckEnvMenu(object sender, RoutedEventArgs e) => ShowDialog("环境检测", Vm.CheckEnvironment());
    private void OnCheckGpuMenu(object sender, RoutedEventArgs e) => ShowDialog("显卡检测", Vm.CheckGpu());
    private async void OnCleanTempMenu(object sender, RoutedEventArgs e) => await ConfirmCleanTempAsync();

    /// <summary>清理临时文件：双重警告确认，全部确认才执行，结果走日志。
    /// 处理进行中禁止清理（主页按钮已禁用，此处兜底拦截菜单等入口）。</summary>
    private async Task ConfirmCleanTempAsync()
    {
        if (Vm.IsProcessing)
        {
            Vm.Logger.Warn("处理进行中，无法清理临时文件");
            return;
        }

        var first = new ContentDialog
        {
            Title = "清理临时文件",
            Content = "确定要删除临时目录中的帧和中间产物吗？删除后无法恢复。",
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootFrame.XamlRoot
        };
        if (await first.ShowAsync() != ContentDialogResult.Primary) return;

        var second = new ContentDialog
        {
            Title = "再次确认",
            Content = "再次确认：即将删除临时目录中的全部中间产物（帧、音频、视频缓存），此操作不可撤销。",
            PrimaryButtonText = "确认清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close, // 第二次默认取消更安全
            XamlRoot = RootFrame.XamlRoot
        };
        if (await second.ShowAsync() != ContentDialogResult.Primary) return;

        await Vm.CleanTempInAsync(Vm.TempRoot); // 后台清理，主页进度条显示进度
        // 清理后重新评估：cache.json 已删除则解除启动阻断
        Vm.RefreshCacheBlock();
    }

    private void OnThemeLight(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Light);
    private void OnThemeDark(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Dark);
    private void OnThemeSystem(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Default);
    private void OnThemeAcrylic(object sender, RoutedEventArgs e) => SetAcrylicTheme();
    private void OnSaveSettingsMenu(object sender, RoutedEventArgs e) { Vm.SaveSettings(); Vm.Logger.Info("设置已保存"); }

    private void OnHelpMenu(object sender, RoutedEventArgs e) => ShowDialog("使用说明",
        "1. 选择输入视频\n2. 自动检测分辨率/帧率\n3. 勾选要执行的处理（超分/补帧/合并/音频/HDR）\n" +
        "4. 选择模型与倍率（超分倍率会自动过滤可用模型）\n5. 点击开始处理\n\n" +
        "处理流程：拆帧 → 超分 → 补帧 → 合并 → 嵌入音频 → HDR 转换");

    private void OnShortcutsMenu(object sender, RoutedEventArgs e) => ShowDialog("快捷键",
        "Ctrl+O       打开视频\nCtrl+S        开始处理\nCtrl+Shift+S  停止处理\n" +
        "Ctrl+L        清空日志\nF1            帮助\nAlt+F4        退出");

    private void OnAboutMenu(object sender, RoutedEventArgs e) => ShowDialog($"关于 Easy4K v{Vm.Version}",
        $"Easy4K v{Vm.Version} - 一键视频超分补帧工具\n\n基于 WinUI 3 / Windows App SDK\n" +
        "Real-ESRGAN-ncnn-Vulkan / RIFE-ncnn-Vulkan / Offical RIFE (PyTorch) / NVEncC / FFmpeg\n\n" +
        "补帧引擎：NCNN（Vulkan 全 GPU）/ Offical（官方 PyTorch pkl 模型，NVIDIA CUDA 自动加速）\n\n" +
        "========== 免责声明 ==========\n\n" +
        "1. 本软件以\"现状\"（AS-IS）提供，开发者不对其正确性、可靠性、完整性及适用性作任何明示或暗示保证。\n\n" +
        "2. 使用本软件及所调用第三方工具（FFmpeg/Real-ESRGAN/RIFE/NVEncC）产生的一切后果，包括但不限于：\n" +
        "   处理结果错误、文件损坏或丢失、硬件故障或损坏、系统崩溃或不稳定、数据泄露、时间与经济损失，\n" +
        "   均由使用者自行承担，开发者概不负责。\n\n" +
        "3. 软件运行时会调用系统全部 CPU/GPU 资源，可能导致设备高负载、发热、降频甚至崩溃，请自行评估风险。\n\n" +
        "4. 输出的视频内容之版权、合法性与用途由使用者自行负责，请勿用于任何非法用途或侵犯他人权益的场景。\n\n" +
        "5. 第三方工具受其各自许可证约束（FFmpeg: LGPL/GPL；Real-ESRGAN: BSD-3；RIFE: 见其项目许可；\n" +
        "   NVEncC: MIT 等），使用前请自行查阅并遵守。\n\n" +
        "6. 开发者不承诺修复任何缺陷，不提供任何形式的售后服务与技术支持。\n\n" +
        "7. 使用本软件即表示已阅读并同意以上全部条款；不同意请立即停止使用并删除本软件。");

    private async void ShowDialog(string title, string content)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            // 长文本（如环境/显卡检测结果）用可滚动区域展示，等宽字体便于对齐
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    Text = content,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12
                }
            },
            CloseButtonText = "关闭",
            XamlRoot = RootFrame.XamlRoot
        };
        await dlg.ShowAsync();
    }

    /// <summary>普通主题：移除亚克力材质，恢复纯色主题背景，并持久化主题选择。</summary>
    private void SetTheme(ElementTheme theme)
    {
        SystemBackdrop = null; // 亚克力是独立主题选项，切回普通主题时移除
        RootFrame.RequestedTheme = theme;
        Vm.SetThemeMode(theme switch
        {
            ElementTheme.Light => "light",
            ElementTheme.Dark => "dark",
            _ => "system"
        });
        Vm.Logger.Info($"主题切换为: {theme}");
    }

    /// <summary>亚克力主题：半透明毛玻璃背景，颜色/透明度跟随系统主题，并持久化主题选择。</summary>
    private void SetAcrylicTheme()
    {
        _acrylic ??= new DesktopAcrylicBackdrop();
        SystemBackdrop = _acrylic;
        // 亚克力颜色跟随系统主题，内容也切回跟随系统保持一致
        RootFrame.RequestedTheme = ElementTheme.Default;
        Vm.SetThemeMode("acrylic");
        // 系统关闭"透明度效果"时所有亚克力/云母材质都会回退为纯色背景
        if (!new Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled)
            Vm.Logger.Warn("系统已关闭「透明度效果」，亚克力回退为纯色背景。请到 设置→个性化→颜色→透明度效果 开启后重新选择亚克力。");
        Vm.Logger.Info("主题切换为: 亚克力");
    }

    // ===================== 版本检查 / 更新提示 =====================

    private const string UpdateBase = "https://update.baka233.top/Easy/";
    private const string UpdateVersionUrl = UpdateBase + "Version.txt";
    private const string UpdateLogUrl = UpdateBase + "Log.txt";
    private const string UpdateAppUrl = UpdateBase + "App.txt";

    /// <summary>启动后异步检查更新：服务器版本高于本地版本（config 的 Version）时弹窗提示。</summary>
    private async Task CheckUpdateAsync()
    {
        try
        {
            // 自测/自动化模式不弹更新提示，避免弹窗卡住自动化流程
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
                return;

            var server = await FetchTextAsync(UpdateVersionUrl);
            if (string.IsNullOrWhiteSpace(server)) return; // 网络失败/无版本信息 → 静默

            if (!IsNewerVersion(server, Vm.Version)) return; // 本地已是最新

            var log = await FetchTextAsync(UpdateLogUrl) ?? "（无法获取更新日志）";
            await ShowUpdateDialogAsync(server, log);
        }
        catch (Exception ex)
        {
            Vm.Logger.Warn($"检查更新失败: {ex.Message}");
        }
    }

    /// <summary>获取远程文本内容（5 秒超时，失败返回 null，不抛异常）</summary>
    private static async Task<string?> FetchTextAsync(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return (await client.GetStringAsync(url)).Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>版本号比较：server 高于 local 返回 true（支持 x.y.z 数字比较，解析失败按字符串比较）</summary>
    private static bool IsNewerVersion(string server, string local)
    {
        if (System.Version.TryParse(server, out var s) && System.Version.TryParse(local, out var l))
            return s > l;
        return string.Compare(server, local, StringComparison.Ordinal) > 0;
    }

    /// <summary>更新弹窗：多行只读滚动日志 + 是/否。点"是"→ 打开 App.txt 里的更新链接。</summary>
    private async Task ShowUpdateDialogAsync(string serverVersion, string log)
    {
        // 日志区用 ScrollViewer + TextBlock（与检测弹窗一致）：多行完整显示、可滚动、可选中复制
        var logBox = new ScrollViewer
        {
            MaxHeight = 300,
            MinWidth = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = log,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12
            }
        };

        var dlg = new ContentDialog
        {
            Title = "发现新版本",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"当前版本: v{Vm.Version}\n目标版本: v{serverVersion}\n\n以下是新版本更新信息:",
                        TextWrapping = TextWrapping.Wrap
                    },
                    logBox
                }
            },
            PrimaryButtonText = "是",
            CloseButtonText = "否",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootFrame.XamlRoot
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        // 是 → 获取更新链接并用默认浏览器打开
        var url = await FetchTextAsync(UpdateAppUrl);
        if (string.IsNullOrWhiteSpace(url))
        {
            Vm.Logger.Warn("未能获取更新链接");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Vm.Logger.Warn($"打开更新链接失败: {ex.Message}");
        }
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetDpiForWindow(System.IntPtr hwnd);
}
