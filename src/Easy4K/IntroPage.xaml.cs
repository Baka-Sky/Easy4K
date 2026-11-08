using System;
using Easy4K.Controls.WelcomeHero;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Easy4K;

/// <summary>
/// 开发版的开场页：承载直接搬运自 PowerToys 的 WelcomeHero（MIT，见 Controls/WelcomeHero 下的来源注释）。
/// Hero 撑满页面高度（背景铺满），星座本身按设计尺寸排版并垂直居中；
/// 「播放入场」通过把控件移出再放回可视树，触发它自身的 Unloaded / Loaded 重建并重播。
/// </summary>
public sealed partial class IntroPage : Page
{
    public IntroPage()
    {
        InitializeComponent();

        Loaded += (_, _) => FitToPage();
        Host.SizeChanged += (_, _) => FitToPage();
    }

    // 高度跟着页面走，但不需要重建场景：WelcomeHero 会按设计尺寸重新居中星座。
    private void FitToPage()
    {
        var height = Host.ActualHeight;
        if (height > 200 && Math.Abs(height - Hero.Height) > 1)
        {
            Hero.Height = height;
        }
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
