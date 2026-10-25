using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Easy4K;

/// <summary>关于页面：由设置页进入，展示品牌信息、项目说明、版权许可与赞助。</summary>
public sealed partial class AboutPage : Page
{
    /// <summary>标题右侧的版本号（取程序集版本，与标题栏一致）</summary>
    public string Version => App.Services.Version;

    /// <summary>当前 logo 对应的主题（避免重复解码同一张图）</summary>
    private bool _logoDark;

    /// <summary>logo 解码请求序号：主题刚解析出来时会连续发两次请求（先 Default 再真实主题），
    /// 两张图解码完成顺序不定，不加序号会被旧结果覆盖 → 黑白图片偶发反色。</summary>
    private int _logoRequest;

    public AboutPage()
    {
        InitializeComponent();

        var text = App.MainWindow?.AboutText ?? "";
        var copyrightMarker = "========== 版权信息 ==========";
        var copyrightIndex = text.IndexOf(copyrightMarker, StringComparison.Ordinal);
        var body = copyrightIndex >= 0 ? text[..copyrightIndex].TrimEnd() : text;
        var firstBreak = body.IndexOf('\n');
        if (firstBreak >= 0) body = body[(firstBreak + 1)..].TrimStart();
        AboutTextBlock.Text = body.Replace("\n\n", "\n");
        CopyrightTextBlock.Text =
            $"Copyright © 2025-{DateTime.Now.Year} Wells,Jiang. All rights reserved.\n" +
            "Easy4K 源代码依据 GPL-3.0 发布。\n" +
            "Easy4K 名称、图标及 SFDP 帧去重算法的版权归 Wells,Jiang 所有；Siamese NET AI 模型依据 CC BY-NC 4.0 开源；第三方组件依各自许可使用。";

        // 右上角 logo：浅色用黑版、深色用白版。构造期先发起解码，主题解析出来后再确认一次
        ApplyLogo();
        ActualThemeChanged += (_, _) => ApplyLogo();
        Loaded += (_, _) => ApplyLogo();

        // 赞助二维码（固定图片，进页面就解码好）
        _ = LoadSponsorQrAsync();
    }

    private async Task LoadSponsorQrAsync()
    {
        SponsorQr1.Source = await LoadImageAsync("SponsorQr1.png", 440);
        SponsorQr2.Source = await LoadImageAsync("SponsorQr2.jpg", 440);
    }

    /// <summary>从 Assets 读图并按显示尺寸解码（4K / 大图直接缩，避免整张解码）。</summary>
    private static async Task<BitmapImage?> LoadImageAsync(string file, int decodeWidth)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
            if (!File.Exists(path)) return null;

            var bmp = new BitmapImage
            {
                DecodePixelWidth = decodeWidth,
                DecodePixelType = DecodePixelType.Logical
            };
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await bmp.SetSourceAsync(fs.AsRandomAccessStream());
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateToSettings();

    /// <summary>按当前主题换 logo（黑版/白版）。解码与 <see cref="AdvancedPage"/> 的卡片插图同一套路：
    /// 指定 DecodePixelWidth 让 4K 原图直接缩到显示尺寸，避免首次进入时内存暴涨、图片迟到。</summary>
    private async void ApplyLogo()
    {
        var dark = CurrentThemeIsDark();
        if (dark == _logoDark && AboutLogo.Source is not null) return;
        _logoDark = dark;

        var seq = ++_logoRequest;
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

            // 期间可能又因为主题解析发起了新请求，过期结果直接丢弃，否则黑白会反
            if (seq != _logoRequest) return;

            AboutLogo.Source = bmp;
            AboutLogo.Visibility = Visibility.Visible;
        }
        catch
        {
            if (seq == _logoRequest) AboutLogo.Visibility = Visibility.Collapsed;
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
