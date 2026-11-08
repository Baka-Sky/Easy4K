using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Easy4K.Controls;

/// <summary>Easy4K 开场动画（移植自 PowerToys 的 WelcomeHero）：
/// 工具卡片从纵深里"曲速"飞入 → Logo 砸落 → 震屏 + 白闪 + 冲击波环 → 进入常驻视差。
/// 全部用 Composition 程序化绘制，没有位图序列，也不走 XAML Storyboard。</summary>
public sealed partial class IntroHero : UserControl
{
    // —— 尺寸（DIP） ——
    private const float TileSize = 56f;
    private const float TileCorner = 12f;
    private const float Spacing = 76f;
    private const float LogoSize = 92f;
    private const float ApexBottomInset = 76f;

    // —— 时间线（毫秒） ——
    private const float FlightMs = 1100f;
    private const float StaggerMs = 12f;
    private const float SlamStartMs = 760f;
    private const float SlamMs = 240f;
    private const float ImpactMs = SlamStartMs + SlamMs;
    private const float ShakeMs = 300f;
    private const float FlashMs = 560f;
    private const float RingMs = 900f;

    // —— 纵深 ——
    private const float WarpDepth = 3200f;
    private const float PerspectiveDistance = 900f;
    private const float MinDivisor = 0.3f;
    private const float SlamScale = 2.6f;
    private const float ShakeAmplitude = 3f;

    private readonly UISettings _uiSettings = new();
    private readonly List<Tile> _tiles = [];

    private Compositor? _compositor;
    private ContainerVisual? _root;
    private ContainerVisual? _shakeRig;
    private ContainerVisual? _tiltRig;
    private ContainerVisual? _stage;
    private ContainerVisual? _tilesLayer;
    private SpriteVisual? _logo;
    private SpriteVisual? _flash;
    private ShapeVisual? _ring;
    private CompositionEllipseGeometry? _ringGeometry;
    private CompositionSpriteShape? _ringShape;
    private LoadedImageSurface? _logoSurface;
    private CompositionEasingFunction? _easeWarp;
    private CompositionEasingFunction? _easeOutCubic;
    private CompositionEasingFunction? _easeInQuad;
    private CompositionEasingFunction? _easeOutExpo;

    private Vector2 _viewport;
    private Vector2 _apex;
    private float _funnelCenter;
    private bool _built;
    private bool _playing;

    public IntroHero()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    /// <summary>播放（或重播）整段入场。控件还没加载时会先记下，加载完再播。</summary>
    public void Play()
    {
        if (!_built)
        {
            _playing = true;
            return;
        }

        _playing = true;
        if (!_uiSettings.AnimationsEnabled)
        {
            ShowFinalState();
            return;
        }

        ResetToStart();
        RunTimeline();
    }

    // ===================== 生命周期 =====================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureBuilt();
        if (_playing) Play();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_root is not null)
        {
            try { ElementCompositionPreview.SetElementChildVisual(SceneHost, null!); } catch { }
        }

        foreach (var t in _tiles)
        {
            t.Body?.Dispose();
            t.Shape?.Dispose();
        }

        _tiles.Clear();
        _logoSurface?.Dispose();
        _logoSurface = null;
        _root?.Dispose();
        _root = null;
        _built = false;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_built) return;
        if (Math.Abs(e.NewSize.Width - _viewport.X) < 1 && Math.Abs(e.NewSize.Height - _viewport.Y) < 1) return;

        // 尺寸变了就重建（槽位是按视口算出来的）
        TearDownScene();
        EnsureBuilt();
        if (_playing) Play();
    }

    // ===================== 场景搭建 =====================

    private void EnsureBuilt()
    {
        if (_built || ActualWidth <= 0 || ActualHeight <= 0) return;

        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _viewport = new Vector2((float)ActualWidth, (float)ActualHeight);
        _apex = new Vector2(_viewport.X / 2f, _viewport.Y - ApexBottomInset);
        _funnelCenter = _apex.Y / 2f;

        _easeWarp = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.12f, 0.78f), new Vector2(0.16f, 1f));
        _easeOutCubic = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 0.61f), new Vector2(0.36f, 1f));
        _easeInQuad = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.55f, 0.085f), new Vector2(0.68f, 0.53f));
        _easeOutExpo = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 0.84f), new Vector2(0.44f, 1f));

        _root = _compositor.CreateContainerVisual();
        _shakeRig = _compositor.CreateContainerVisual();
        _tiltRig = _compositor.CreateContainerVisual();
        _stage = _compositor.CreateContainerVisual();
        _tilesLayer = _compositor.CreateContainerVisual();

        // 透视：让 stage 里 z 不为 0 的子元素自动被投影（这就是"曲速"的来源）
        _stage.TransformMatrix = Matrix4x4.CreateTranslation(0f, _funnelCenter, 0f)
            * new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, -1f / PerspectiveDistance, 0, 0, 0, 1)
            * Matrix4x4.CreateTranslation(0f, -_funnelCenter, 0f);

        _root.Children.InsertAtTop(_shakeRig);
        _shakeRig.Children.InsertAtTop(_tiltRig);
        _tiltRig.Children.InsertAtTop(_stage);
        _stage.Children.InsertAtTop(_tilesLayer);
        _stage.Children.InsertAtTop(BuildRing());
        _stage.Children.InsertAtTop(BuildFlash());
        _stage.Children.InsertAtTop(BuildLogo());
        BuildTiles();

        ElementCompositionPreview.SetElementChildVisual(SceneHost, _root);
        _built = true;
    }

    private SpriteVisual BuildLogo()
    {
        _logo = _compositor!.CreateSpriteVisual();
        _logo.Size = new Vector2(LogoSize, LogoSize);
        _logo.Offset = new Vector3(_apex.X - (LogoSize / 2f), _apex.Y - (LogoSize / 2f), 0f);

        try
        {
            _logoSurface = LoadedImageSurface.StartLoadFromUri(new Uri("ms-appx:///Assets/logo.png"));
            var brush = _compositor.CreateSurfaceBrush(_logoSurface);
            brush.Stretch = CompositionStretch.Uniform;
            _logo.Brush = brush;
        }
        catch
        {
            var fallback = _compositor.CreateColorBrush(Color.FromArgb(255, 0x2F, 0x8C, 0xD8));
            _logo.Brush = fallback;
        }

        return _logo;
    }

    private SpriteVisual BuildFlash()
    {
        _flash = _compositor!.CreateSpriteVisual();
        _flash.Size = _viewport;
        _flash.Offset = Vector3.Zero;
        _flash.Brush = _compositor.CreateColorBrush(Color.FromArgb(255, 255, 255, 255));
        _flash.Opacity = 0f;
        _flash.CenterPoint = new Vector3(_viewport.X / 2f, _apex.Y, 0f);
        return _flash;
    }

    private ShapeVisual BuildRing()
    {
        var ring = _compositor!.CreateShapeVisual();
        ring.Size = _viewport;
        _ringGeometry = _compositor.CreateEllipseGeometry();
        _ringGeometry.Center = _apex;
        _ringGeometry.Radius = new Vector2(LogoSize / 2f, LogoSize / 2f);
        _ringShape = _compositor.CreateSpriteShape(_ringGeometry);
        _ringShape.StrokeBrush = _compositor.CreateColorBrush(Color.FromArgb(255, 0x45, 0xB6, 0xF0));
        _ringShape.StrokeThickness = 3f;
        _ringShape.FillBrush = null;
        ring.Shapes.Add(_ringShape);
        ring.Opacity = 0f;
        _ring = ring;
        return ring;
    }

    private void BuildTiles()
    {
        var half = (float)Math.Floor(_viewport.X / 2f / Spacing) - 1f;
        var maxColumns = Math.Max(3, (int)half);
        var fill = _compositor!.CreateLinearGradientBrush();
        fill.StartPoint = Vector2.Zero;
        fill.EndPoint = new Vector2(0f, TileSize);
        fill.ColorStops.Add(_compositor.CreateColorGradientStop(0f, Color.FromArgb(255, 0x3A, 0x3F, 0x4A)));
        fill.ColorStops.Add(_compositor.CreateColorGradientStop(1f, Color.FromArgb(255, 0x22, 0x26, 0x2D)));
        var stroke = _compositor.CreateColorBrush(Color.FromArgb(140, 0x8F, 0x9A, 0xAA));

        var index = 0;
        for (var column = -maxColumns; column <= maxColumns; column++)
        {
            var halfStep = Math.Abs(column) / 2f;
            for (var row = column == 0 ? 1 : 0; ; row++)
            {
                var offset = new Vector2(column * Spacing, -Spacing * (row + halfStep));
                var y = _apex.Y + offset.Y;
                var opacity = EdgeOpacity(y);
                if (opacity <= 0.05f || index > 90) break;

                var body = _compositor.CreateContainerVisual();
                body.Offset = new Vector3(_apex.X + offset.X, y, 0f);

                var shape = _compositor.CreateShapeVisual();
                shape.Size = new Vector2(TileSize, TileSize);
                shape.Offset = new Vector3(-TileSize / 2f, -TileSize / 2f, 0f);

                var geo = _compositor.CreateRoundedRectangleGeometry();
                geo.Size = new Vector2(TileSize, TileSize);
                geo.CornerRadius = new Vector2(TileCorner, TileCorner);
                var sp = _compositor.CreateSpriteShape(geo);
                sp.FillBrush = fill;
                sp.StrokeBrush = stroke;
                sp.StrokeThickness = 1f;
                shape.Shapes.Add(sp);

                body.Children.InsertAtTop(shape);
                body.Opacity = opacity;
                _tilesLayer!.Children.InsertAtTop(body);

                _tiles.Add(new Tile(body, shape, offset, opacity));
                index++;
            }
        }
    }

    // ===================== 时间线 =====================

    private void ResetToStart()
    {
        foreach (var tile in _tiles)
        {
            tile.Body!.StopAnimation("Offset");
            tile.Body.StopAnimation("Opacity");
            tile.Body.Offset = new Vector3(_apex.X + tile.Offset.X, _apex.Y + tile.Offset.Y, -WarpDepth);
            tile.Body.Opacity = 0f;
            tile.Body.Scale = Vector3.One;
        }

        if (_logo is not null)
        {
            _logo.StopAnimation("Scale");
            _logo.StopAnimation("Opacity");
            _logo.Scale = new Vector3(SlamScale, SlamScale, 1f);
            _logo.Opacity = 0f;
        }

        if (_shakeRig is not null)
        {
            _shakeRig.StopAnimation("Offset");
            _shakeRig.Offset = Vector3.Zero;
        }

        if (_flash is not null)
        {
            _flash.StopAnimation("Opacity");
            _flash.StopAnimation("Scale");
            _flash.Opacity = 0f;
            _flash.Scale = Vector3.One;
        }

        if (_ring is not null)
        {
            _ring.StopAnimation("Opacity");
            _ring.Opacity = 0f;
        }
    }

    private void RunTimeline()
    {
        if (_compositor is null || _tilesLayer is null) return;

        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            var delay = i * StaggerMs;
            var from = new Vector3(_apex.X + tile.Offset.X, _apex.Y + tile.Offset.Y, -WarpDepth);
            var to = new Vector3(_apex.X + tile.Offset.X, _apex.Y + tile.Offset.Y, 0f);

            var fly = _compositor.CreateVector3KeyFrameAnimation();
            fly.Duration = TimeSpan.FromMilliseconds(FlightMs);
            fly.DelayTime = TimeSpan.FromMilliseconds(delay);
            fly.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            fly.InsertKeyFrame(0f, from);
            fly.InsertKeyFrame(1f, to, _easeWarp);
            tile.Body!.StartAnimation("Offset", fly);
            fly.Dispose();

            var fade = _compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(180);
            fade.DelayTime = TimeSpan.FromMilliseconds(delay);
            fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, tile.Opacity, _easeOutCubic);
            tile.Body.StartAnimation("Opacity", fade);
            fade.Dispose();
        }

        // Logo 砸落
        if (_logo is not null)
        {
            var logoFade = _compositor.CreateScalarKeyFrameAnimation();
            logoFade.Duration = TimeSpan.FromMilliseconds(90);
            logoFade.DelayTime = TimeSpan.FromMilliseconds(SlamStartMs);
            logoFade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            logoFade.InsertKeyFrame(0f, 0f);
            logoFade.InsertKeyFrame(1f, 1f);
            _logo.StartAnimation("Opacity", logoFade);
            logoFade.Dispose();

            var slam = _compositor.CreateVector3KeyFrameAnimation();
            slam.Duration = TimeSpan.FromMilliseconds(SlamMs);
            slam.DelayTime = TimeSpan.FromMilliseconds(SlamStartMs);
            slam.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            slam.InsertKeyFrame(0f, new Vector3(SlamScale, SlamScale, 1f));
            slam.InsertKeyFrame(1f, Vector3.One, _easeInQuad);
            _logo.StartAnimation("Scale", slam);
            slam.Dispose();
        }

        // 震屏
        if (_shakeRig is not null)
        {
            const float k = ShakeAmplitude;
            var shake = _compositor.CreateVector3KeyFrameAnimation();
            shake.Duration = TimeSpan.FromMilliseconds(ShakeMs);
            shake.DelayTime = TimeSpan.FromMilliseconds(ImpactMs);
            shake.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            shake.InsertKeyFrame(0f, Vector3.Zero);
            shake.InsertKeyFrame(0.12f, new Vector3(k, -k * 0.7f, 0f));
            shake.InsertKeyFrame(0.32f, new Vector3(-k * 0.75f, k * 0.45f, 0f));
            shake.InsertKeyFrame(0.58f, new Vector3(k * 0.35f, -k * 0.2f, 0f));
            shake.InsertKeyFrame(1f, Vector3.Zero, _easeOutCubic);
            _shakeRig.StartAnimation("Offset", shake);
            shake.Dispose();
        }

        // 白闪
        if (_flash is not null)
        {
            var flash = _compositor.CreateScalarKeyFrameAnimation();
            flash.Duration = TimeSpan.FromMilliseconds(FlashMs);
            flash.DelayTime = TimeSpan.FromMilliseconds(ImpactMs);
            flash.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            flash.InsertKeyFrame(0f, 0f);
            flash.InsertKeyFrame(0.1f, 1f, _easeOutCubic);
            flash.InsertKeyFrame(1f, 0f, _easeInQuad);
            _flash.StartAnimation("Opacity", flash);
            flash.Dispose();

            var grow = _compositor.CreateVector3KeyFrameAnimation();
            grow.Duration = TimeSpan.FromMilliseconds(FlashMs);
            grow.DelayTime = TimeSpan.FromMilliseconds(ImpactMs);
            grow.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            grow.InsertKeyFrame(0f, new Vector3(0.3f, 0.3f, 1f));
            grow.InsertKeyFrame(1f, new Vector3(2.3f, 2.3f, 1f), _easeOutExpo);
            _flash.StartAnimation("Scale", grow);
            grow.Dispose();
        }

        // 冲击波环
        if (_ring is not null && _ringGeometry is not null && _ringShape is not null)
        {
            var start = LogoSize / 2f;
            var end = (float)Math.Sqrt((_viewport.X * _viewport.X) + (_viewport.Y * _viewport.Y)) / 2f + 120f;

            var radius = _compositor.CreateVector2KeyFrameAnimation();
            radius.Duration = TimeSpan.FromMilliseconds(RingMs);
            radius.DelayTime = TimeSpan.FromMilliseconds(ImpactMs);
            radius.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            radius.InsertKeyFrame(0f, new Vector2(start, start));
            radius.InsertKeyFrame(1f, new Vector2(end, end), _easeOutCubic);
            _ringGeometry.StartAnimation("Radius", radius);
            radius.Dispose();

            var ringFade = _compositor.CreateScalarKeyFrameAnimation();
            ringFade.Duration = TimeSpan.FromMilliseconds(RingMs);
            ringFade.DelayTime = TimeSpan.FromMilliseconds(ImpactMs);
            ringFade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            ringFade.InsertKeyFrame(0f, 0f);
            ringFade.InsertKeyFrame(0.06f, 0.42f);
            ringFade.InsertKeyFrame(1f, 0f, _easeOutCubic);
            _ring.StartAnimation("Opacity", ringFade);
            ringFade.Dispose();
        }
    }

    private void ShowFinalState()
    {
        if (!_built) return;
        foreach (var tile in _tiles)
        {
            tile.Body!.Offset = new Vector3(_apex.X + tile.Offset.X, _apex.Y + tile.Offset.Y, 0f);
            tile.Body.Opacity = tile.Opacity;
            tile.Body.Scale = Vector3.One;
        }

        if (_logo is not null)
        {
            _logo.Scale = Vector3.One;
            _logo.Opacity = 1f;
        }

        if (_flash is not null) _flash.Opacity = 0f;
        if (_ring is not null) _ring.Opacity = 0f;
        if (_shakeRig is not null) _shakeRig.Offset = Vector3.Zero;
    }

    // ===================== 指针交互 =====================

    private void OnPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_built || _tiltRig is null) return;
        var p = e.GetCurrentPoint(SceneHost).Position;
        var dx = ((float)p.X - _apex.X) / Math.Max(_apex.X, 1f);
        var dy = ((float)p.Y - _apex.Y) / Math.Max(_funnelCenter, 1f);
        dx = Math.Clamp(dx, -1f, 1f);
        dy = Math.Clamp(dy, -1f, 1f);

        // 轻轻倾斜：绕 Y 轴随水平指针、绕 X 轴随垂直指针（5° 以内）
        _tiltRig.RotationAxis = Vector3.Normalize(new Vector3(-dy, dx, 1f));
        _tiltRig.RotationAngleInDegrees = 5f * (float)Math.Sqrt((dx * dx) + (dy * dy));
        _tiltRig.CenterPoint = new Vector3(_apex.X, _apex.Y, 0f);

        // 指针附近放大（docking 式），120px 半径内最多 1.26 倍
        const float radius = 120f;
        foreach (var tile in _tiles)
        {
            var tx = _apex.X + tile.Offset.X;
            var ty = _apex.Y + tile.Offset.Y;
            var d = Math.Min(Vector2.Distance(new Vector2((float)p.X, (float)p.Y), new Vector2(tx, ty)) / radius, 1f);
            var falloff = (1f - (d * d)) * (1f - (d * d));
            var scale = 1f + (0.26f * falloff);
            tile.Body!.Scale = new Vector3(scale, scale, 1f);
        }
    }

    private void OnPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_tiltRig is not null)
        {
            _tiltRig.RotationAngleInDegrees = 0f;
            _tiltRig.RotationAxis = Vector3.UnitZ;
        }

        foreach (var tile in _tiles)
            if (tile.Body is not null) tile.Body.Scale = Vector3.One;
    }

    // ===================== 工具 =====================

    private void TearDownScene()
    {
        foreach (var t in _tiles)
        {
            t.Body?.Dispose();
            t.Shape?.Dispose();
        }

        _tiles.Clear();
        _logoSurface?.Dispose();
        _logoSurface = null;
        try { ElementCompositionPreview.SetElementChildVisual(SceneHost, null!); } catch { }
        _root?.Dispose();
        _root = null;
        _built = false;
    }

    /// <summary>越靠近顶部越淡，让漏斗看起来没有尽头。</summary>
    private float EdgeOpacity(float y)
    {
        var t = Math.Clamp((y + (TileSize * 0.45f)) / (TileSize * 1.2f), 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    private sealed record Tile(ContainerVisual? Body, ShapeVisual? Shape, Vector2 Offset, float Opacity);
}
