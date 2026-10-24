using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;

namespace Easy4K;

/// <summary>启动画面：显示 Assets/Splash.png，底部状态条显示「正在检查必要组件」。
/// 无边框、固定尺寸、不可缩放；Win11 由 DWM 补圆角（Win10 自动降级为直角）。</summary>
public sealed partial class SplashWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    public SplashWindow()
    {
        InitializeComponent();
        Title = "Easy4K";   // 任务栏/Alt+Tab 显示用（窗口本身无标题栏）

        // 无边框窗口：隐藏标题栏与系统边框，固定大小
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        TryRoundCorners(hwnd);
        CenterAt(NativeMethods.GetDpiForWindow(hwnd));

        // 4K 原图按显示尺寸解码，避免启动时整张解码拖慢
        _ = LoadImageAsync();
    }

    /// <summary>窗口按启动图比例（16:10）以 1280×800 逻辑像素居中显示。</summary>
    private void CenterAt(int dpi)
    {
        try
        {
            var scale = dpi / 96.0;
            var w = (int)(1280 * scale);
            var h = (int)(800 * scale);

            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                work.X + (work.Width - w) / 2,
                work.Y + (work.Height - h) / 2,
                w, h));
        }
        catch { /* 取不到显示器信息时保持默认尺寸 */ }
    }

    /// <summary>Win11 圆角：DWM_WINDOW_CORNER_PREFERENCE = ROUND（Win10 调用失败忽略）。</summary>
    private static void TryRoundCorners(IntPtr hwnd)
    {
        try
        {
            var value = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref value, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private async Task LoadImageAsync()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Splash.png");
            if (!File.Exists(path)) return;

            var bmp = new BitmapImage
            {
                DecodePixelWidth = 1280,   // 窗口宽 1280 逻辑像素（高 DPI 下画面仍清晰）
                DecodePixelType = DecodePixelType.Logical
            };
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                await bmp.SetSourceAsync(fs.AsRandomAccessStream());

            SplashImage.Source = bmp;
        }
        catch { /* 图片读取失败时保留纯色底，不影响启动检测 */ }
    }
}
