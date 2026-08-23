using Easy4K.Models;
using Easy4K.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

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
                NavigateToProgress();
            });
        };

        // 处理结束 → 隐藏导航按钮 + 切回主页
        Vm.ProcessingCompleted += _ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                NavButtons.Visibility = Visibility.Collapsed;
                NavigateToHome();
            });
        };

        // 纯进度条实时更新（进行页隐藏，主页可见）
        Vm.ProgressChanged += p =>
        {
            DispatcherQueue.TryEnqueue(() => ThinProgressBar.Value = p.Percent);
        };
    }

    // ===================== 导航 =====================

    public void NavigateToHome()
    {
        if (RootFrame.CurrentSourcePageType != typeof(MainPage))
            RootFrame.Navigate(typeof(MainPage));
        ThinProgressBar.Visibility = Visibility.Visible;
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
    private async void OnChooseTempMenu(object sender, RoutedEventArgs e) => await PickFolderAsync(p => Vm.SetTempRoot(p));
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

    private void InitializePicker(object picker)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);
    }

    // ===================== 工具 / 设置 / 帮助 =====================

    private void OnCheckEnvMenu(object sender, RoutedEventArgs e) => Vm.CheckEnvironment();
    private void OnCheckGpuMenu(object sender, RoutedEventArgs e) => Vm.CheckGpu();
    private async void OnCleanTempMenu(object sender, RoutedEventArgs e) => await ConfirmCleanTempAsync();

    /// <summary>清理临时文件：先确认，清理后弹结果窗口</summary>
    private async Task ConfirmCleanTempAsync()
    {
        var dlg = new ContentDialog
        {
            Title = "清理临时文件",
            Content = "确定要删除临时目录中的帧和中间产物吗？删除后无法恢复。",
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootFrame.XamlRoot
        };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var msg = Vm.CleanTemp();
        var doneDlg = new ContentDialog
        {
            Title = "清理完成",
            Content = msg,
            CloseButtonText = "关闭",
            XamlRoot = RootFrame.XamlRoot
        };
        await doneDlg.ShowAsync();
    }

    private void OnThemeLight(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Light);
    private void OnThemeDark(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Dark);
    private void OnThemeSystem(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Default);
    private void OnSaveSettingsMenu(object sender, RoutedEventArgs e) { Vm.SaveSettings(); Vm.Logger.Info("设置已保存"); }

    private void OnHelpMenu(object sender, RoutedEventArgs e) => ShowDialog("使用说明",
        "1. 选择输入视频\n2. 自动检测分辨率/帧率\n3. 勾选要执行的处理（超分/补帧/合并/音频/HDR）\n" +
        "4. 选择模型与倍率（超分倍率会自动过滤可用模型）\n5. 点击开始处理\n\n" +
        "处理流程：拆帧 → 超分 → 补帧 → 合并 → 嵌入音频 → HDR 转换");

    private void OnShortcutsMenu(object sender, RoutedEventArgs e) => ShowDialog("快捷键",
        "Ctrl+O       打开视频\nCtrl+S        开始处理\nCtrl+Shift+S  停止处理\n" +
        "Ctrl+L        清空日志\nF1            帮助\nAlt+F4        退出");

    private void OnAboutMenu(object sender, RoutedEventArgs e) => ShowDialog("关于 Easy4K",
        "Easy4K - 一键视频超分补帧工具\n\n基于 WinUI 3 / Windows App SDK 2.4\n" +
        "Real-ESRGAN-ncnn-Vulkan / RIFE-ncnn-Vulkan / NVEncC / FFmpeg");

    private async void ShowDialog(string title, string content)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "关闭",
            XamlRoot = RootFrame.XamlRoot
        };
        await dlg.ShowAsync();
    }

    private void SetTheme(ElementTheme theme)
    {
        RootFrame.RequestedTheme = theme;
        Vm.Logger.Info($"主题切换为: {theme}");
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetDpiForWindow(System.IntPtr hwnd);
}
