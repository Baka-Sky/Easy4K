using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Easy4K;

/// <summary>关于页面：由设置页进入，展示版本、组件与免责声明。
/// 正文直接复用 <see cref="MainWindow.AboutText"/>，与旧「关于」弹窗保持同一份内容。</summary>
public sealed partial class AboutPage : Page
{
    /// <summary>标题右侧的版本号（取程序集版本，与标题栏一致）</summary>
    public string Version => App.Services.Version;

    /// <summary>当前 logo 对应的主题（避免重复解码同一张图）</summary>
    private bool _logoDark;

    public AboutPage()
    {
        InitializeComponent();

        var text = App.MainWindow?.AboutText ?? "";
        // AboutText 首行是「Easy4K vX - 一键视频超分补帧工具」，页面上方已单独显示，正文去掉这行；
        // 段落间原本是空行（\n\n），页面上按行高收紧后显得间隔过大，压成单换行
        var firstBreak = text.IndexOf('\n');
        var body = firstBreak >= 0 ? text[(firstBreak + 1)..].TrimStart() : text;
        AboutTextBlock.Text = body.Replace("\n\n", "\n");

        // 右上角 logo：浅色用黑版、深色用白版。构造期先发起解码，主题解析出来后再确认一次
        ApplyLogo();
        ActualThemeChanged += (_, _) => ApplyLogo();
        Loaded += (_, _) => ApplyLogo();
    }

    private void OnBack(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateToSettings();

    /// <summary>按当前主题换 logo（黑版/白版）。解码与 <see cref="AdvancedPage"/> 的卡片插图同一套路：
    /// 指定 DecodePixelWidth 让 4K 原图直接缩到显示尺寸，避免首次进入时内存暴涨、图片迟到。</summary>
    private async void ApplyLogo()
    {
        var dark = CurrentThemeIsDark();
        if (dark == _logoDark && AboutLogo.Source is not null) return;
        _logoDark = dark;

        var path = Path.Combine(AppContext.BaseDirectory, "Assets",
            dark ? "AboutLogo_white.png" : "AboutLogo_black.png");

        try
        {
            if (!File.Exists(path))
            {
                AboutLogo.Visibility = Visibility.Collapsed;
                return;
            }

            var bmp = new BitmapImage
            {
                DecodePixelWidth = 360,   // 显示 180 逻辑像素，留 2 倍余量
                DecodePixelType = DecodePixelType.Logical
            };
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                await bmp.SetSourceAsync(fs.AsRandomAccessStream());

            AboutLogo.Source = bmp;
            AboutLogo.Visibility = Visibility.Visible;
        }
        catch
        {
            AboutLogo.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>当前是否深色主题（深色用白版 logo，浅色用黑版）。
    /// 首次进入时页面的 ActualTheme 可能还是 Default，逐级回退到窗口根元素、系统主题。</summary>
    private bool CurrentThemeIsDark()
    {
        var theme = ActualTheme;
        if (theme == ElementTheme.Default && App.MainWindow?.Content is FrameworkElement root)
            theme = root.ActualTheme;
        if (theme == ElementTheme.Default)
            theme = Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? ElementTheme.Dark : ElementTheme.Light;
        return theme == ElementTheme.Dark;
    }
}
