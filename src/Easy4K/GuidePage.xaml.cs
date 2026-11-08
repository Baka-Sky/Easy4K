using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Easy4K;

/// <summary>
/// 开发版的新手引导页：复刻 ClassIsland 的做法 —— 半透明遮罩按目标控件「挖洞」，
/// 目标外面套一圈高亮描边，旁边跟一个说明气泡，支持上一步 / 下一步 / 跳过。
/// ClassIsland 用的是 Avalonia 的 AdornerLayer + TeachingTip，这里用 WinUI 的
/// 几何遮罩（EvenOdd）+ 手动定位气泡实现同样的观感。
/// </summary>
public sealed partial class GuidePage : Page
{
    private sealed record Step(FrameworkElement Target, string Title, string Body);

    private const double HolePadding = 8;
    private const double BubbleGap = 14;

    private readonly List<Step> _steps = [];
    private Storyboard? _pulse;
    private int _index;
    private bool _active;
    private Rect _hole;

    public GuidePage()
    {
        InitializeComponent();

        _steps.Add(new(TargetPick, "选择要处理的视频", "点这里挑选素材，也支持把文件直接拖进来批量加入队列。"));
        _steps.Add(new(TargetQuality, "挑选画质档位", "4K 超分、RIFE 补帧、HDR 映射都在这里配，预设一键套用。"));
        _steps.Add(new(TargetStart, "开始处理", "点一下就开始；进度、日志和报错都会同步到手机端。"));
        _steps.Add(new(TargetRemote, "手机远程控制", "手机连上同一台电脑，就能选片、看进度、下载成品。"));

        Root.SizeChanged += (_, _) =>
        {
            if (_active)
            {
                LayoutGuide(force: true);
            }
        };

        // 跟随目标控件的位置变化（窗口缩放、页面布局变化）重新定位遮罩与气泡。
        LayoutUpdated += (_, _) =>
        {
            if (_active)
            {
                LayoutGuide(force: false);
            }
        };

        Unloaded += (_, _) => StopPulse();
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        _index = 0;
        _active = true;
        _hole = Rect.Empty;
        GuideLayer.Visibility = Visibility.Visible;
        StartPulse();
        LayoutGuide(force: true);
    }

    private void OnPrev(object sender, RoutedEventArgs e)
    {
        if (_index > 0)
        {
            _index--;
            LayoutGuide(force: true);
        }
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_index < _steps.Count - 1)
        {
            _index++;
            LayoutGuide(force: true);
        }
        else
        {
            Close();
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Close();

    private void Close()
    {
        _active = false;
        GuideLayer.Visibility = Visibility.Collapsed;
        StopPulse();
    }

    private void LayoutGuide(bool force)
    {
        if (_index < 0 || _index >= _steps.Count)
        {
            return;
        }

        var step = _steps[_index];
        var hole = Inflate(GetBounds(step.Target), HolePadding);
        if (force || hole != _hole)
        {
            _hole = hole;
            ApplyMask(hole);
            ApplyRing(hole);
        }

        BubbleTitle.Text = step.Title;
        BubbleBody.Text = step.Body;
        StepText.Text = $"{_index + 1} / {_steps.Count}";
        PrevButton.IsEnabled = _index > 0;
        NextButton.Content = _index == _steps.Count - 1 ? "完成" : "下一步";

        PlaceBubble(hole);
    }

    private Rect GetBounds(FrameworkElement target)
    {
        var width = target.ActualWidth;
        var height = target.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return new Rect(0, 0, Root.ActualWidth, Root.ActualHeight);
        }

        return target.TransformToVisual(Root).TransformBounds(new Rect(0, 0, width, height));
    }

    private static Rect Inflate(Rect rect, double amount) => new(
        Math.Max(0, rect.X - amount),
        Math.Max(0, rect.Y - amount),
        rect.Width + (amount * 2),
        rect.Height + (amount * 2));

    // 整页矩形 + 目标矩形，EvenOdd 填充规则让目标处变成透明的洞。
    private void ApplyMask(Rect hole)
    {
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry { Rect = new Rect(0, 0, Root.ActualWidth, Root.ActualHeight) });
        group.Children.Add(new RectangleGeometry { Rect = hole });
        MaskPath.Data = group;
    }

    private void ApplyRing(Rect hole)
    {
        HighlightRing.Margin = new Thickness(hole.X, hole.Y, 0, 0);
        HighlightRing.Width = hole.Width;
        HighlightRing.Height = hole.Height;
        HighlightRing.HorizontalAlignment = HorizontalAlignment.Left;
        HighlightRing.VerticalAlignment = VerticalAlignment.Top;
    }

    private void PlaceBubble(Rect hole)
    {
        Bubble.Measure(new Size(340, double.PositiveInfinity));
        var size = Bubble.DesiredSize;
        var maxX = Math.Max(8, Root.ActualWidth - size.Width - 8);

        var x = Math.Clamp(hole.X + (hole.Width / 2) - (size.Width / 2), 8, maxX);
        var below = hole.Bottom + BubbleGap;
        var above = hole.Y - BubbleGap - size.Height;
        var y = below + size.Height <= Root.ActualHeight - 8 ? below : Math.Max(8, above);

        Bubble.Margin = new Thickness(x, y, 0, 0);
        Bubble.HorizontalAlignment = HorizontalAlignment.Left;
        Bubble.VerticalAlignment = VerticalAlignment.Top;
    }

    private void StartPulse()
    {
        if (_pulse is not null)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            From = 0.45,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };

        Storyboard.SetTarget(animation, HighlightRing);
        Storyboard.SetTargetProperty(animation, "Opacity");

        _pulse = new Storyboard();
        _pulse.Children.Add(animation);
        _pulse.Begin();
    }

    private void StopPulse()
    {
        _pulse?.Stop();
        _pulse = null;
        HighlightRing.Opacity = 1;
    }
}
