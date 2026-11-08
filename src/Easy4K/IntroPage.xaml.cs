using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Easy4K;

/// <summary>
/// 开发版的开场页：承载直接搬运自 PowerToys 的 WelcomeHero（MIT，见 Controls/WelcomeHero 下的来源注释）。
/// 「播放入场」通过把控件移出再放回可视树，触发它自身的 Unloaded / Loaded 重建并重播。
/// </summary>
public sealed partial class IntroPage : Page
{
    private bool _fitted;

    public IntroPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_fitted || Host.ActualHeight < 200)
        {
            return;
        }

        _fitted = true;

        // WelcomeHero 默认 320 高（PowerToys 页面卡片的高度），这里改成整页高度，
        // 让它自带的渐变穹顶铺满背景，而不是只占中间一条。
        Hero.Height = Host.ActualHeight;
        Rehost();
    }

    private void OnPlay(object sender, RoutedEventArgs e) => Rehost();

    // 移出可视树 → Unloaded 释放场景；下一帧放回 → Loaded 重建并按 Playback 重播。
    private void Rehost()
    {
        Host.Children.Clear();
        DispatcherQueue.TryEnqueue(() => Host.Children.Add(Hero));
    }
}
