using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Easy4K;

/// <summary>开发版的开场页：点「播放入场」重播一次 Composition 入场动画（移植自 PowerToys WelcomeHero）。</summary>
public sealed partial class IntroPage : Page
{
    public IntroPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Hero.Play();   // 进页面先自动播一次
    }

    private void OnPlay(object sender, RoutedEventArgs e) => Hero.Play();
}
