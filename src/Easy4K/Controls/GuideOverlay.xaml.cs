using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Easy4K.Controls;

/// <summary>
/// 通用的新手引导覆盖层：遮罩按目标控件「挖洞」，洞外面套强调色描边 + 四角括号 +
/// 循环扩散的脉冲，旁边跟一个带小尖角的说明气泡（观感对照 ClassIsland 的 TutorialHighlightControl）。
/// 宿主只负责调用 <see cref="ShowStep"/>，步骤切换通过 <see cref="StepRequested"/> 交给宿主决定。
/// </summary>
public sealed partial class GuideOverlay : UserControl
{
    private const double HolePadding = 8;
    private const double HoleRadius = 12;
    private const double BubbleGap = 16;
    private const double BubbleMaxWidth = 440;
    private const double TailSize = 14;

    private bool _active;
    private bool _laying;
    private Rect _hole = Rect.Empty;
    private Size _bubbleSize;

    public GuideOverlay()
    {
        InitializeComponent();

        SizeChanged += (_, _) =>
        {
            if (_active)
            {
                Relayout();
            }
        };

        LayoutUpdated += (_, _) =>
        {
            if (_active)
            {
                Relayout();
            }
        };
    }

    /// <summary>上一步 / 下一步 / 跳过时触发：传入要显示的步骤序号，-1 表示关闭。</summary>
    public event EventHandler<int>? StepRequested;

    public bool IsActive => _active;

    public void ShowStep(FrameworkElement? target, string title, string body, int index, int count)
    {
        _index = index;

        if (!_active)
        {
            _active = true;
            _hole = Rect.Empty;
            Visibility = Visibility.Visible;
            FadeIn();
        }

        UpdateText(title, body, index, count);
        ApplyTarget(target);
    }

    /// <summary>只更新气泡文字（切页时先换文案，再等新页面布局完成后定位）。</summary>
    public void UpdateText(string title, string body, int index, int count)
    {
        _index = index;
        BubbleTitle.Text = title;
        BubbleBody.Text = body;
        StepText.Text = index >= 0 ? $"{index + 1} / {count}" : string.Empty;
        PrevButton.IsEnabled = index > 0;
        NextButton.Content = index >= count - 1 ? "完成" : "下一步";
        Bubble.Measure(new Size(BubbleMaxWidth, double.PositiveInfinity));
        _bubbleSize = Bubble.DesiredSize;

        if (_hole != Rect.Empty)
        {
            PlaceBubble(_hole, _bubbleSize);
        }
    }

    public void Close()
    {
        _active = false;
        Visibility = Visibility.Collapsed;
        StopAnimations();
    }

    private void Relayout()
    {
        if (_laying || !_active)
        {
            return;
        }

        _laying = true;
        try
        {
            if (_target is not null)
            {
                var hole = Inflate(GetBounds(_target), HolePadding);
                if (hole != _hole)
                {
                    ApplyTarget(_target);
                }
            }
        }
        finally
        {
            _laying = false;
        }
    }

    private FrameworkElement? _target;

    private void ApplyTarget(FrameworkElement? target)
    {
        _target = target;

        var hole = target is null
            ? new Rect(0, 0, ActualWidth, ActualHeight)
            : Inflate(GetBounds(target), HolePadding);

        _hole = hole;
        ApplyMask(hole);
        ApplyRing(hole);
        PlaceBubble(hole, _bubbleSize);
        AnimateRingIn();
    }

    private Rect GetBounds(FrameworkElement target)
    {
        var width = target.ActualWidth;
        var height = target.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return new Rect(0, 0, ActualWidth, ActualHeight);
        }

        return target.TransformToVisual(this).TransformBounds(new Rect(0, 0, width, height));
    }

    private static Rect Inflate(Rect rect, double amount) => new(
        Math.Max(0, rect.X - amount),
        Math.Max(0, rect.Y - amount),
        rect.Width + (amount * 2),
        rect.Height + (amount * 2));

    private void ApplyMask(Rect hole)
    {
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) });
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
        figure.Segments.Add(Arc(new Point(rect.Right, rect.Y + r), r));
        figure.Segments.Add(new LineSegment { Point = new Point(rect.Right, rect.Bottom - r) });
        figure.Segments.Add(Arc(new Point(rect.Right - r, rect.Bottom), r));
        figure.Segments.Add(new LineSegment { Point = new Point(rect.X + r, rect.Bottom) });
        figure.Segments.Add(Arc(new Point(rect.X, rect.Bottom - r), r));
        figure.Segments.Add(new LineSegment { Point = new Point(rect.X, rect.Y + r) });
        figure.Segments.Add(Arc(new Point(rect.X + r, rect.Y), r));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static ArcSegment Arc(Point point, double radius) => new()
    {
        Point = point,
        Size = new Size(radius, radius),
        SweepDirection = SweepDirection.Clockwise,
    };

    private void ApplyRing(Rect hole)
    {
        RingHost.Margin = new Thickness(hole.X, hole.Y, 0, 0);
        RingHost.Width = hole.Width;
        RingHost.Height = hole.Height;
    }

    private void PlaceBubble(Rect hole, Size size)
    {
        var maxX = Math.Max(8, ActualWidth - size.Width - 8);
        var x = Math.Clamp(hole.X + (hole.Width / 2) - (size.Width / 2), 8, maxX);

        var below = hole.Bottom + BubbleGap;
        var above = hole.Y - BubbleGap - size.Height;
        var isBelow = below + size.Height <= ActualHeight - 8;
        var y = isBelow ? below : Math.Max(8, above);

        Canvas.SetLeft(Bubble, x);
        Canvas.SetTop(Bubble, y);
        Canvas.SetLeft(BubbleTail, x + (size.Width / 2) - (TailSize / 2));
        Canvas.SetTop(BubbleTail, isBelow ? y - (TailSize / 2) : y + size.Height - (TailSize / 2));
    }

    private void FadeIn()
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = TimeSpan.FromMilliseconds(240);
        visual.StartAnimation("Opacity", fade);
    }

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
        var root = ElementCompositionPreview.GetElementVisual(RingHost);
        root.StopAnimation("Scale");
        root.StopAnimation("Opacity");
        var loop = ElementCompositionPreview.GetElementVisual(RingLoop);
        loop.StopAnimation("Scale");
        loop.StopAnimation("Opacity");
    }

    private void OnPrev(object sender, RoutedEventArgs e) => StepRequested?.Invoke(this, _index - 1);

    private void OnNext(object sender, RoutedEventArgs e) => StepRequested?.Invoke(this, _index + 1);

    private void OnSkip(object sender, RoutedEventArgs e) => StepRequested?.Invoke(this, -1);

    private int _index;
}
