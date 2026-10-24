using System.IO;
using System.Runtime.InteropServices;
using Easy4K.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;

namespace Easy4K;

/// <summary>启动画面：显示 Assets/Splash.png（整窗铺满）。
/// 无边框、固定尺寸、不可缩放；用 SetWindowRgn 把窗口本身裁成圆角
/// （无边框窗口上 DWM 圆角不可靠，直接裁窗口区域在各系统上都生效）。</summary>
public sealed partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Title = "Easy4K";   // 任务栏/Alt+Tab 显示用（窗口本身无标题栏）

        // 保留系统边框（DWM 圆角/阴影依赖它）但不要标题栏 → 四角是抗锯齿圆角，也没有系统按钮
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        _ = CenterAt(NativeMethods.GetDpiForWindow(hwnd));

        // 4K 原图按显示尺寸解码，避免启动时整张解码拖慢
        _ = LoadImageAsync();
    }

    /// <summary>右上角关闭：直接退出程序（启动阶段还没进主界面）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        try { Application.Current.Exit(); }
        catch { Close(); }
    }

    /// <summary>启动检测失败：直接在启动画面上弹窗（不进主界面）；关闭后由调用方退出程序。
    /// 检测可能在窗口完成首帧之前就返回，这里先等 XamlRoot 就绪，否则弹窗会直接抛异常。</summary>
    public async Task ShowStartupProblemAsync(string message)
    {
        try
        {
            for (var i = 0; i < 60 && Content?.XamlRoot is null; i++)
                await Task.Delay(50);
            if (Content?.XamlRoot is null) return;

            var dlg = new ContentDialog
            {
                Title = "必要文件缺失",
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                },
                CloseButtonText = "退出",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
                RequestedTheme = ElementTheme.Dark
            };
            await dlg.ShowLocalizedAsync();
        }
        catch { /* 弹窗失败也由调用方退出 */ }
    }

    /// <summary>窗口按屏幕工作区面积的约 10%（线性约 31.6%）居中显示，保持启动图 16:10 比例。
    /// 返回物理像素尺寸（供圆角区域裁切用）。</summary>
    private (int w, int h) CenterAt(int dpi)
    {
        try
        {
            var scale = dpi / 96.0;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;

            // 目标面积 = 工作区 10%，换算成线性尺寸后按 16:10 收进工作区
            var factor = Math.Sqrt(0.1);
            var w = work.Width / scale * factor;
            var h = w * 10.0 / 16.0;
            var maxH = work.Height / scale * factor;
            if (h > maxH)
            {
                h = maxH;
                w = h * 16.0 / 10.0;
            }

            _logicalWidth = Math.Max(320, (int)Math.Round(w));
            var physW = (int)Math.Round(w * scale);
            var physH = (int)Math.Round(h * scale);

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                work.X + (work.Width - physW) / 2,
                work.Y + (work.Height - physH) / 2,
                physW, physH));

            return (physW, physH);
        }
        catch
        {
            // 取不到显示器信息时用 AppWindow 当前尺寸
            var s = AppWindow.ClientSize;
            return (s.Width, s.Height);
        }
    }

    /// <summary>窗口逻辑宽度（供图片按显示尺寸解码）</summary>
    private int _logicalWidth = 800;

    private async Task LoadImageAsync()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Splash.png");
            if (!File.Exists(path)) return;

            var bmp = new BitmapImage
            {
                DecodePixelWidth = _logicalWidth,   // 按实际窗口宽度解码（高 DPI 下仍清晰）
                DecodePixelType = DecodePixelType.Logical
            };
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                await bmp.SetSourceAsync(fs.AsRandomAccessStream());

            SplashImage.Source = bmp;
        }
        catch { /* 图片读取失败时保留纯色底，不影响启动检测 */ }
    }
}
