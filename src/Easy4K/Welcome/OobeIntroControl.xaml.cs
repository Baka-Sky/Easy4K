using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;

namespace Easy4K.Welcome;

/// <summary>
/// Easy4K OOBE 开场动画 —— 100% 复刻 ClassIsland 的 OobeIntroAnimationControl：
/// 一排 32x32 方块（间距 4）逐个自下浮入 → 逐块绕 X 轴翻转退场、同格字母反向翻入 →
/// 字母间距收拢为品牌名 "Easy4K"。所有时长/延迟/缓动按原版公式移植。
/// 原版 count=11（ClassIsland）总时长约 1.73s，这里 count=6，把 durationMs 从 500 缩到 360
/// 使整体节奏与原版一致。
/// </summary>
public sealed partial class OobeIntroControl : UserControl
{
    /// <summary>动画结束（原版在收拢动画开始时立刻触发，不等待收拢完成）</summary>
    public event EventHandler? AnimationEnd;

    private const string Brand = "Easy4K";
    private const int DurationMs = 360;      // 原版 500，按 6/11 字母数缩放对齐总时长
    private const int SpecialIndex = 3;      // 原版 i==6 带蓝色拉伸闪光（这里对应 "y"）
    private const double BlockPivotY = 12.0 / 32.0;   // 原版 RenderTransformOrigin="0,12"：绕 X 轴翻转的轴线在方块顶部下方 12px
    private readonly List<Border> _blocks = new();
    private readonly List<TextBlock> _letters = new();
    private bool _started;

    public OobeIntroControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = PlayAsync();
    }

    private async Task PlayAsync()
    {
        if (_started) return;
        _started = true;
        try
        {
            BuildCells();
            var count = _blocks.Count;

            // 原版延迟公式：delay_i = sin(((i+2)/(count+2)) * π/2) * durationMs / count（逐字累计起播）
            var starts = new double[count];
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                var delay = Math.Sin((1.0 * (i + 2) / (count + 2)) * (Math.PI / 2)) * DurationMs / count;
                starts[i] = sum;
                sum += delay;
            }

            var sb = new Storyboard();

            // 0) 整体：0→1 淡入 + 1.25→1.0 缩放，3s，缓动 (0,0.83)(0.17,0.90)
            sb.Children.Add(SplineAnim(this, "Opacity", 0, 1, 3000, (0, 0.83), (0.17, 0.90)));
            sb.Children.Add(SplineAnim(RootScale, "ScaleX", 1.25, 1.0, 3000, (0, 0.83), (0.17, 0.90)));
            sb.Children.Add(SplineAnim(RootScale, "ScaleY", 1.25, 1.0, 3000, (0, 0.83), (0.17, 0.90)));

            var baseBrush = GetStrongFillBrush();
            double lastEnd = 0;

            for (int i = 0; i < count; i++)
            {
                var timeMs = DelayOf(i) * 9;                 // 原版用 delay*9 参与时长计算
                var start = TimeSpan.FromMilliseconds(starts[i]);
                var block = _blocks[i];
                var letter = _letters[i];
                var willHide = i == SpecialIndex;

                // 1) 方块：浮入（Y 50→0 / 透明度 0→1）→ 沿水平轴纵向压扁（ScaleY 1→0）退场
                // 原版是 Avalonia Rotate3DTransform.AngleX 0→90°，其 Depth=0 属正交投影，
                // 投影后视觉 = 绕轴 scaleY=cosθ 压缩成一条线（无 3D 透视），故用 ScaleY 复刻。
                var riseDur = timeMs * 1.5 + 750;
                var flipStart = timeMs + 750;
                AddKeyFrames(sb, block, "(UIElement.RenderTransform).(TransformGroup.Children)[0].(TranslateTransform.Y)", start,
                    new (double, double, KeySpline?)[] { (0, 50, null), (timeMs, 0, Spl((0.25, 1), (0.5, 1))) }, riseDur);
                AddKeyFrames(sb, block, "Opacity", start,
                    new (double, double, KeySpline?)[] { (0, 0, null), (timeMs, 1, Spl((0.25, 1), (0.5, 1))), (timeMs + 1, willHide ? 0 : 1, null), (riseDur, 0, null) }, riseDur);
                AddKeyFrames(sb, block, "(UIElement.RenderTransform).(TransformGroup.Children)[1].(ScaleTransform.ScaleY)", start,
                    new (double, double, KeySpline?)[] { (flipStart, 1, null), (riseDur, 0, Spl((0.32, 0), (0.67, 0))) }, riseDur);

                // 2) 字母：纵向压扁的"反向翻转"入场（ScaleY 0→1）+ 淡入（原版 BuildAnimation2：AngleX -90→0）
                var letterStart = timeMs * 1.5 + 750;
                var letterDur = timeMs * 2 + 750;
                AddKeyFrames(sb, letter, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", start,
                    new (double, double, KeySpline?)[] { (letterStart, 0, null), (letterDur, 1, Spl((0.33, 1), (0.68, 1))) }, letterDur);
                AddKeyFrames(sb, letter, "Opacity", start,
                    new (double, double, KeySpline?)[] { (letterStart, 0, null), (letterDur, 1, Spl((0.33, 1), (0.68, 1))) }, letterDur);

                if (i == SpecialIndex)
                {
                    // 特殊块：+307ms 起 0.25s 蓝色伸缩闪光（原版 .sp-anim）
                    var spStart = start + TimeSpan.FromMilliseconds(307);
                    AddKeyFrames(sb, block, "(UIElement.RenderTransform).(TransformGroup.Children)[1].(ScaleTransform.ScaleX)", spStart,
                        new (double, double, KeySpline?)[] { (0, 1, null), (250, 2.17, Spl((0.76, 0), (0.24, 1))) }, 250);
                    var colorFrames = new ColorAnimationUsingKeyFrames { BeginTime = spStart, Duration = TimeSpan.FromMilliseconds(250) };
                    colorFrames.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = TimeSpan.Zero, Value = BaseBlockColor() });
                    colorFrames.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = TimeSpan.FromMilliseconds(250), Value = Color.FromArgb(255, 0, 191, 255) });
                    Storyboard.SetTarget(colorFrames, block);
                    Storyboard.SetTargetProperty(colorFrames, "(Border.Background).(SolidColorBrush.Color)");
                    sb.Children.Add(colorFrames);
                }

                if (i == count - 3)   // 原版等第 count-3 个字母动画完成即开始收拢
                    lastEnd = starts[i] + letterDur;
            }

            sb.Begin();
            await Task.Delay(TimeSpan.FromMilliseconds(lastEnd + 40));

            // 3) 收拢拼字：间距 4→0 / MinWidth 32→0，0.7s，缓动 (0.33,1)(0.68,1)；随后立刻触发 AnimationEnd
            var collapse = new Storyboard();
            collapse.Children.Add(SplineAnim(Texts, "Spacing", 4, 0, 700, (0.33, 1), (0.68, 1)));
            foreach (var l in _letters)
                collapse.Children.Add(SplineAnim(l, "MinWidth", 32, 0, 700, (0.33, 1), (0.68, 1)));
            collapse.Begin();
            AnimationEnd?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 动画仅是开场效果，任何异常都不能让欢迎页卡在不可交互状态
            AnimationEnd?.Invoke(this, EventArgs.Empty);
        }
    }

    private double DelayOf(int i)
        => Math.Sin((1.0 * (i + 2) / (Brand.Length + 2)) * (Math.PI / 2)) * DurationMs / Brand.Length;

    private void BuildCells()
    {
        for (int i = 0; i < Brand.Length; i++)
        {
            // 方块：32x32、间距 4、初始 Y=50 & 透明（结构与原版一致：上方方块行、下方文字行同坐标）
            var block = new Border
            {
                Width = 32,
                Height = 32,
                Background = new SolidColorBrush(BaseBlockColor()),
                Opacity = 0,
                IsHitTestVisible = false,
                // 原版方块 RenderTransformOrigin="0,12"（绝对像素）→ 压扁/翻退的轴线在距顶 12px 处
                RenderTransformOrigin = new Point(0.5, 12.0 / 32.0),
                RenderTransform = new TransformGroup
                {
                    Children =
                    {
                        new TranslateTransform { Y = 50 },
                        new ScaleTransform()
                    }
                }
            };
            Blocks.Children.Add(block);
            _blocks.Add(block);

            // 字母：FontSize 32、FontWeight Medium、MinWidth 32、初始透明
            var letter = new TextBlock
            {
                Text = Brand[i].ToString(),
                FontSize = 32,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                MinWidth = 32,
                Opacity = 0,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                // 初始 ScaleY=0（横向一条线，透明不可见），翻转时 0→1 撑开，轴心在字母中心
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform { ScaleY = 0 }
            };
            Texts.Children.Add(letter);
            _letters.Add(letter);
        }
    }

    private static Color BaseBlockColor()
        => Application.Current.RequestedTheme == ApplicationTheme.Dark
            ? Color.FromArgb(255, 190, 190, 190)
            : Color.FromArgb(255, 96, 96, 96);

    private static Brush GetStrongFillBrush()
        => Application.Current.RequestedTheme == ApplicationTheme.Dark
            ? new SolidColorBrush(Color.FromArgb(255, 200, 200, 200))
            : new SolidColorBrush(Color.FromArgb(255, 60, 60, 60));

    private static KeySpline Spl((double x1, double y1) p1, (double x2, double y2) p2)
        => new() { ControlPoint1 = new Point(p1.x1, p1.y1), ControlPoint2 = new Point(p2.x2, p2.y2) };

    /// <summary>带关键帧缓动曲线的 Double 动画（KeyTime 用毫秒时刻，Duration 对齐最后帧）</summary>
    private static DoubleAnimationUsingKeyFrames SplineAnim(DependencyObject target, string path, double from, double to, double durationMs, (double, double) p1, (double, double) p2)
    {
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(durationMs) };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        anim.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(durationMs),
            Value = to,
            KeySpline = Spl(p1, p2)
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, path);
        return anim;
    }

    /// <summary>多关键帧动画（time 单位 ms；spline 为空表示线性）</summary>
    private static void AddKeyFrames(Storyboard sb, DependencyObject target, string path, TimeSpan begin,
        (double time, double value, KeySpline? spline)[] frames, double durationMs)
    {
        var anim = new DoubleAnimationUsingKeyFrames { BeginTime = begin, Duration = TimeSpan.FromMilliseconds(durationMs) };
        foreach (var (time, value, spline) in frames)
        {
            DoubleKeyFrame kf = spline is null
                ? new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(time), Value = value }
                : new SplineDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(time), Value = value, KeySpline = spline };
            anim.KeyFrames.Add(kf);
        }
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, path);
        sb.Children.Add(anim);
    }
}