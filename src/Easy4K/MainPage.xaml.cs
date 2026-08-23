using Easy4K.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Easy4K;

/// <summary>主页 code-behind：路径选择、处理按钮、快捷键。菜单栏已上移到 MainWindow。</summary>
public sealed partial class MainPage : Page
{
    private MainViewModel Vm => App.Services;

    public MainPage()
    {
        InitializeComponent();
    }

    // ===================== 文件 / 目录选择 =====================

    private async void OnBrowseInput(object sender, RoutedEventArgs e) => await PickVideoAsync();
    private async void OnBrowseTemp(object sender, RoutedEventArgs e) => await PickFolderAsync(p => Vm.SetTempRoot(p));
    private async void OnBrowseOutput(object sender, RoutedEventArgs e) => await PickFolderAsync(p => Vm.SetOutputRoot(p));

    private async void OnExtractAudio(object sender, RoutedEventArgs e) => await Vm.ExtractAudioAsync();

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
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);
    }

    // ===================== 按钮 =====================

    private async void OnStart(object sender, RoutedEventArgs e) => await Vm.StartAsync();
    private void OnStop(object sender, RoutedEventArgs e) => Vm.Stop();
    private async void OnCleanTemp(object sender, RoutedEventArgs e) => await ConfirmCleanTempAsync();
    private void OnClearLog(object sender, RoutedEventArgs e) => Vm.ClearLog();
    private void OnCopyLog(object sender, RoutedEventArgs e) => Vm.CopyLog();

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
            XamlRoot = XamlRoot
        };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var msg = Vm.CleanTemp();
        var doneDlg = new ContentDialog
        {
            Title = "清理完成",
            Content = msg,
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        await doneDlg.ShowAsync();
    }

    private async void OnSuperThreadChecked(object sender, RoutedEventArgs e)
    {
        // 确保两个 ContentDialog 都在 XamlRoot 可用时弹出
        if (XamlRoot is null) return;

        // 第一次警告
        var dlg1 = new ContentDialog
        {
            Title = "警告：超级多线程",
            Content = "开启超级多线程将调用 CPU 全部物理核心和 GPU 全部计算单元。\n\n系统可能出现短暂卡顿，这是正常现象。\n\n是否继续开启？",
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dlg1.ShowAsync() != ContentDialogResult.Primary)
        {
            SuperThreadCheck.IsChecked = false;
            return;
        }

        // 第二次警告
        var dlg2 = new ContentDialog
        {
            Title = "再次确认：超级多线程",
            Content = "此模式下显卡和 CPU 将满载运行，可能影响其他正在运行的程序。\n\n如遇崩溃，请关闭此选项或开启安全帧率。\n\n确认开启？",
            PrimaryButtonText = "确认开启",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dlg2.ShowAsync() != ContentDialogResult.Primary)
        {
            SuperThreadCheck.IsChecked = false;
        }
    }

    // ===================== 快捷键 =====================

    private async void OnAcceleratorOpen(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await PickVideoAsync(); }
    private async void OnAcceleratorStart(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await Vm.StartAsync(); }
    private void OnAcceleratorStop(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; Vm.Stop(); }
    private void OnAcceleratorClearLog(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; Vm.ClearLog(); }
    private void OnAcceleratorHelp(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = ShowHelpAsync();
    }

    private async Task ShowHelpAsync()
    {
        var dlg = new ContentDialog
        {
            Title = "使用说明",
            Content = "1. 选择输入视频\n2. 自动检测分辨率/帧率\n3. 勾选要执行的处理（超分/补帧/合并/音频/HDR）\n" +
                      "4. 选择模型与倍率\n5. 点击开始处理\n\n处理流程：拆帧 → 超分 → 补帧 → 合并 → 嵌入音频 → HDR",
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        await dlg.ShowAsync();
    }
}
