using Easy4K.Services;
using Easy4K.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Easy4K;

/// <summary>设置页：由 NavigationView 顶栏/侧栏最右侧的齿轮进入。
/// 原来菜单栏（文件 / 工具 / 设置 / 帮助）的全部功能收敛到这里。</summary>
public sealed partial class SettingsPage : Page
{
    private MainViewModel Vm => App.Services;

    /// <summary>程序回填控件时抑制事件（避免构造期误触发主题/语言切换）</summary>
    private bool _syncing;

    public SettingsPage()
    {
        InitializeComponent();
        SyncCurrentValues();
    }

    /// <summary>把主题与语言控件对齐当前配置。</summary>
    private void SyncCurrentValues()
    {
        _syncing = true;
        switch (Vm.SavedTheme)
        {
            case "light": ThemeLightRb.IsChecked = true; break;
            case "dark": ThemeDarkRb.IsChecked = true; break;
            case "acrylic": ThemeAcrylicRb.IsChecked = true; break;
            case "image": ThemeImageRb.IsChecked = true; break;
            default: ThemeSystemRb.IsChecked = true; break;
        }
        _syncing = false;
        UpdateBgImageRowVisibility();
    }

    // ===================== 外观 =====================

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var theme = sender == ThemeLightRb ? "light"
            : sender == ThemeDarkRb ? "dark"
            : sender == ThemeAcrylicRb ? "acrylic"
            : sender == ThemeImageRb ? "image"
            : "system";
        UpdateBgImageRowVisibility();
        App.MainWindow?.ApplyThemeFromSettings(theme);
    }

    /// <summary>只有选中"图片背景"主题才显示背景图与浓度选择。</summary>
    private void UpdateBgImageRowVisibility()
    {
        var on = ThemeImageRb.IsChecked == true;
        BgImageRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        BgOpacityRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        BgImageHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>选择背景图（图片背景主题）：选完写入配置，窗口立即换图。</summary>
    private async void OnBrowseBgImage(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        Vm.BackgroundImage = file.Path; // 立即持久化；窗口监听属性变化即时换图
    }

    // ===================== 文件与目录 =====================

    private async void OnOpenVideo(object sender, RoutedEventArgs e) => await App.MainWindow.OpenVideoAsync();

    private async void OnChooseTemp(object sender, RoutedEventArgs e) => await App.MainWindow.ChooseTempFolderAsync();

    private async void OnChooseOutput(object sender, RoutedEventArgs e) => await App.MainWindow.ChooseOutputFolderAsync();

    // ===================== 工具与诊断 =====================

    private async void OnCheckEnv(object sender, RoutedEventArgs e)
        => await App.MainWindow.ShowInfoDialogAsync("环境检测", Vm.CheckEnvironment());

    private async void OnCheckGpu(object sender, RoutedEventArgs e)
        => await App.MainWindow.ShowInfoDialogAsync("显卡检测", Vm.CheckGpu());

    private async void OnCleanTemp(object sender, RoutedEventArgs e) => await App.MainWindow.CleanTempAsync();

    // ===================== 其它 =====================

    /// <summary>保存当前设置为默认：写盘后只留一行日志，窗口上看不到 → 通知条确认结果并给打开配置文件目录的入口。</summary>
    private void OnSaveDefaults(object sender, RoutedEventArgs e)
    {
        Vm.SaveSettings();
        Vm.Logger.Info("设置已保存");
        App.MainWindow?.ShowNotice("已保存为默认设置", "模型、倍率与勾选步骤已写入 appsettings.json，下次启动自动恢复。",
            "打开配置文件目录", () => App.MainWindow?.OpenConfigFolder());
    }

    private async void OnHelp(object sender, RoutedEventArgs e) => await App.MainWindow.ShowHelpAsync();

    private async void OnShortcuts(object sender, RoutedEventArgs e) => await App.MainWindow.ShowShortcutsAsync();

    private async void OnAbout(object sender, RoutedEventArgs e) => await App.MainWindow.ShowAboutAsync();

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Exit();
}
