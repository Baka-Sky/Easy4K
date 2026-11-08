using System;
using Easy4K.Controls.WelcomeHero;
using Microsoft.UI.Dispatching;
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
    private double _appliedHeight;
    private readonly DispatcherQueueTimer _resizeTimer;

    public IntroPage()
    {
        InitializeComponent();

        _resizeTimer = DispatcherQueue.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(180);
        _resizeTimer.IsRepeating = false;
        _resizeTimer.Tick += (_, _) => Fit(play: false);

        Loaded += (_, _) => Fit(play: true);
        Host.SizeChanged += OnHostSizeChanged;
        Unloaded += (_, _) => _resizeTimer.Stop();
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 窗口尺寸变化后按新高度重建（Settle 短动画），不再重播整段入场。
        if (!_fitted || Math.Abs(e.NewSize.Height - _appliedHeight) < 40)
        {
            return;
        }

        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void Fit(bool play)
    {
        var height = Host.ActualHeight;
        if (height < 200 || (_fitted && Math.Abs(height - _appliedHeight) < 1))
        {
            return;
        }

        _fitted = true;
        _appliedHeight = height;

        // WelcomeHero 默认 320 高（PowerToys 页面卡片的高度），这里改成整页高度：
        // 渐变穹顶才会铺满背景，卡片也会按新的高度重新排布。
        Hero.Height = height;
        Hero.Playback = play ? WelcomeHeroPlayback.Intro : WelcomeHeroPlayback.Settle;
        Rehost();
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        Hero.Playback = WelcomeHeroPlayback.Intro;
        Rehost();
    }

    // 移出可视树 → Unloaded 释放场景；下一帧放回 → Loaded 重建并按 Playback 重播。
    private void Rehost()
    {
        Host.Children.Clear();
        DispatcherQueue.TryEnqueue(() => Host.Children.Add(Hero));
    }
}
