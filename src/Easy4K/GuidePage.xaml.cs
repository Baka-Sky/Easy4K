using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Easy4K;

/// <summary>
/// 开发版的新手引导页：复刻 ClassIsland 的观感 —— 半透明遮罩按目标控件「挖洞」，
/// 洞外面套强调色描边 + 四角括号 + 循环扩散的脉冲，旁边跟一个带小尖角的说明气泡。
/// ClassIsland 用 Avalonia 的 AdornerLayer + TeachingTip 实现，这里用 WinUI 的
/// 几何遮罩（EvenOdd 圆角洞）+ Composition 动画复刻同样的视觉效果。
/// </summary>
public sealed partial class GuidePage : Page
{
    private sealed record Step(FrameworkElement Target, string Title, string Body);

    private const double HolePadding = 8;
    private const double HoleRadius = 12;
    private const double BubbleGap = 16;
    private const double BubbleMaxWidth = 340;
    private const double TailSize = 14;

    private readonly List<Step> _steps = [];
    private int _index;
    private int _renderedIndex = -1;
    private bool _active;
    private bool _laying;
    private Rect _hole = Rect.Empty;
    private Size _bubbleSize;

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

        Unloaded += (_, _) => StopAnimations();
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        _index = 0;
        _active = true;
        _renderedIndex = -1;
        _hole = Rect.Empty;
        GuideLayer.Visibility = Visibility.Visible;
        FadeInLayer();
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
        StopAnimations();
    }

    /// <summary>
    /// 更新遮罩、描边和气泡。LayoutUpdated 里调用时只在该变的时候改布局，
    /// 并且用 <see cref="_laying"/> 防止「改布局 → LayoutUpdated → 再改布局」的死循环。
    /// </summary>
    private void LayoutGuide(bool force)
    {
        if (_laying || _index < 0 || _index >= _steps.Count)
        {
            return;
        }

        _laying = true;
        try
        {
            var step = _steps[_index];
            var hole = Inflate(GetBounds(step.Target), HolePadding);

            if (force || _renderedIndex != _index)
            {
                _renderedIndex = _index;
                BubbleTitle.Text = step.Title;
                BubbleBody.Text = step.Body;
                StepText.Text = $"{_index + 1} / {_steps.Count}";
                PrevButton.IsEnabled = _index > 0;
                NextButton.Content = _index == _steps.Count - 1 ? "完成" : "下一步";
                Bubble.Measure(new Size(BubbleMaxWidth, double.PositiveInfinity));
                _bubbleSize = Bubble.DesiredSize;
            }

            if (force || hole != _hole)
            {
                _hole = hole;
                ApplyMask(hole);
                ApplyRing(hole);
                PlaceBubble(hole, _bubbleSize);
                AnimateRingIn();
            }
        }
        finally
        {
            _laying = false;
        }
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

    // 整页矩形 + 圆角的目标矩形，EvenOdd 填充规则让目标处变成透明的洞。
    private void ApplyMask(Rect hole)
    {
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry { Rect = new Rect(0, 0, Root.ActualWidth, Root.ActualHeight) });
        group.Children.Add(RoundedRect(hole, HoleRadius));
        MaskPath.Data = group;
    }

    private static PathGeometry RoundedRect(Rect rect, double radius)
    {
        var r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
        var figure = new PathFigure
        {
            StartPoint = new Point(rect.X + r, rect.Y),
            IsClosed = true,
            IsFilled = true,
        };

        figure.Segments.Add(new LineSegment { Point = new Point(rect.Right - r, rect.Y) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(rect.Right, rect.Y + r),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
        });
        figure.Segments.Add(new LineSegment { Point = new Point(rect.Right, rect.Bottom - r) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(rect.Right - r, rect.Bottom),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
        });
        figure.Segments.Add(new LineSegment { Point = new Point(rect.X + r, rect.Bottom) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(rect.X, rect.Bottom - r),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
        });
        figure.Segments.Add(new LineSegment { Point = new Point(rect.X, rect.Y + r) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(rect.X + r, rect.Y),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void ApplyRing(Rect hole)
    {
        RingHost.Margin = new Thickness(hole.X, hole.Y, 0, 0);
        RingHost.Width = hole.Width;
        RingHost.Height = hole.Height;
    }

    private void PlaceBubble(Rect hole, Size size)
    {
        var maxX = Math.Max(8, Root.ActualWidth - size.Width - 8);
        var x = Math.Clamp(hole.X + (hole.Width / 2) - (size.Width / 2), 8, maxX);

        var below = hole.Bottom + BubbleGap;
        var above = hole.Y - BubbleGap - size.Height;
        var isBelow = below + size.Height <= Root.ActualHeight - 8;
        var y = isBelow ? below : Math.Max(8, above);

        Canvas.SetLeft(Bubble, x);
        Canvas.SetTop(Bubble, y);

        // 小尖角贴在气泡靠近目标的一侧
        Canvas.SetLeft(BubbleTail, x + (size.Width / 2) - (TailSize / 2));
        Canvas.SetTop(BubbleTail, isBelow ? y - (TailSize / 2) : y + size.Height - (TailSize / 2));
    }

    private void FadeInLayer()
    {
        var visual = ElementCompositionPreview.GetElementVisual(GuideLayer);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = TimeSpan.FromMilliseconds(260);
        visual.StartAnimation("Opacity", fade);
    }

    // ClassIsland 的高亮入场：描边从放大 1.14 缩回 1，同时淡入；随后第二圈描边每 1.75s 扩散一次。
    private void AnimateRingIn()
    {
        var visual = ElementCompositionPreview.GetElementVisual(RingHost);
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

        visual.CenterPoint = new Vector3((float)(RingHost.Width / 2), (float)(RingHost.Height / 2), 0f);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(1.14f, 1.14f, 1f), ease);
        scale.InsertKeyFrame(1f, Vector3.One);
        scale.Duration = TimeSpan.FromMilliseconds(750);
        visual.StartAnimation("Scale", scale);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f, ease);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = TimeSpan.FromMilliseconds(450);
        visual.StartAnimation("Opacity", fade);

        var loopVisual = ElementCompositionPreview.GetElementVisual(RingLoop);
        loopVisual.CenterPoint = visual.CenterPoint;
        loopVisual.StopAnimation("Scale");
        loopVisual.StopAnimation("Opacity");

        var loopScale = compositor.CreateVector3KeyFrameAnimation();
        loopScale.InsertKeyFrame(0f, new Vector3(1.1f, 1.1f, 1f));
        loopScale.InsertKeyFrame(0.43f, Vector3.One, ease);
        loopScale.InsertKeyFrame(1f, Vector3.One);
        loopScale.Duration = TimeSpan.FromSeconds(1.75);
        loopScale.DelayTime = TimeSpan.FromSeconds(1.5);
        loopScale.IterationBehavior = AnimationIterationBehavior.Forever;

        var loopFade = compositor.CreateScalarKeyFrameAnimation();
        loopFade.InsertKeyFrame(0f, 0f);
        loopFade.InsertKeyFrame(0.43f, 0.75f, ease);
        loopFade.InsertKeyFrame(1f, 0f);
        loopFade.Duration = TimeSpan.FromSeconds(1.75);
        loopFade.DelayTime = TimeSpan.FromSeconds(1.5);
        loopFade.IterationBehavior = AnimationIterationBehavior.Forever;

        loopVisual.StartAnimation("Scale", loopScale);
        loopVisual.StartAnimation("Opacity", loopFade);
    }

    private void StopAnimations()
    {
        ElementCompositionPreview.GetElementVisual(RingHost).StopAnimation("Scale");
        ElementCompositionPreview.GetElementVisual(RingHost).StopAnimation("Opacity");
        var loop = ElementCompositionPreview.GetElementVisual(RingLoop);
        loop.StopAnimation("Scale");
        loop.StopAnimation("Opacity");
    }
}
