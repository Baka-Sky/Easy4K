﻿// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Ported from Microsoft PowerToys (https://github.com/microsoft/PowerToys)
// Original: src/settings-ui/Settings.UI/SettingsXAML/Controls/WelcomeHero/WelcomeHero.xaml.cs
// License: MIT (Copyright (c) Microsoft Corporation). See THIRD-PARTY-NOTICES in the repo for details.
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Easy4K.Controls.WelcomeHero
{
    /// <summary>
    /// The animated hero of the OOBE Welcome page: every PowerToys tool warps in out of the depth, the logo slams
    /// down, and the tools settle into a constellation that reacts to the pointer. Hovering a tool shows its name,
    /// clicking it opens its page.
    /// </summary>
    [SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The scene is created on Loaded and disposed on Unloaded.")]
    public sealed partial class WelcomeHero : UserControl
    {
        private const float LabelGap = 6f;

        // Text reveal, in milliseconds.
        private const float IntroRevealStartMs = WelcomeHeroScene.TextRevealStartMs;
        private const float IntroRevealStaggerMs = 90f;
        private const float IntroRevealMs = 700f;
        private const float SettleRevealStartMs = 80f;
        private const float SettleRevealStaggerMs = 50f;
        private const float SettleRevealMs = 450f;
        private const float RevealDistance = 24f;

        // 入场动画整段时长（飞入 + 砸落 + 穹顶/光环/待机动效启动）之后再放开指针互动。
        private const float IntroInteractionDelayMs = 2200f;

        private static readonly TimeSpan AssetLoadTimeout = TimeSpan.FromMilliseconds(1500);

        private readonly List<LoadedImageSurface> _iconSurfaces = [];
        private readonly UISettings _uiSettings = new();

        private WelcomeHeroLayout _layout;
        private WelcomeHeroScene _scene;
        private LoadedImageSurface _logoSurface;
        private DispatcherQueueTimer _loadTimeout;
        private DispatcherQueueTimer _introTimer;
        private InputCursor _handCursor;
        private WelcomeHeroPlayback _activePlayback;
        private int _pendingLoads;
        private bool _started;
        /// <summary>入场动画是否已结束（结束前忽略鼠标悬停/点击，避免动画被指针特效打断）。</summary>
        private bool _introDone;
        private int _hoveredTile = -1;
        private bool _hoveringLogo;
        private bool _pointerOver;
        private bool _watchingAnimationsSetting;
        private Vector2 _lastPointer;

        public WelcomeHero()
        {
            InitializeComponent();
            Height = WelcomeHeroLayout.DefaultHeroHeight;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
            ActualThemeChanged += OnActualThemeChanged;
        }

        /// <summary>
        /// Raised with the OOBE navigation tag of a tool when its tile is clicked.
        /// </summary>
        public event EventHandler<string> ModuleInvoked;

        /// <summary>
        /// Raised when the intro actually starts playing (not when it is skipped or replaced by a static hero).
        /// </summary>
        public event EventHandler IntroStarted;

        /// <summary>
        /// Gets or sets how the hero enters the screen. Set this before the control is loaded.
        /// </summary>
        public WelcomeHeroPlayback Playback { get; set; } = WelcomeHeroPlayback.Intro;

        /// <summary>
        /// Gets the page elements (title, description, ...) that rise into view in sync with the hero. Populate before the control is loaded.
        /// </summary>
        public IList<UIElement> RevealTargets { get; } = [];

        /// <summary>
        /// 星座的纵向偏移（DIP，负数向上）。默认居中；全屏欢迎页用它把整组方块稍微上移，给底部文案留空间。
        /// </summary>
        public double ContentOffsetY { get; set; }

        /// <summary>
        /// Gets a value indicating whether Windows animations are turned on.
        /// </summary>
        public bool AnimationsEnabled => _uiSettings.AnimationsEnabled;

        private int IconTileCount => Math.Min(WelcomeHeroModules.All.Count, _scene?.TileCount ?? 0);

        private WelcomeHeroMotion CurrentMotion => _activePlayback == WelcomeHeroPlayback.Static || !_pointerOver
            ? WelcomeHeroMotion.None
            : WelcomeHeroMotion.Live((float)SceneHost.ActualWidth);

        private static Color GetResourceColor(string key, Color fallback) =>
            Application.Current.Resources.TryGetValue(key, out var value) && value is Color color ? color : fallback;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_scene is not null)
            {
                return;
            }

            _activePlayback = AnimationsEnabled ? Playback : WelcomeHeroPlayback.Static;

            // Easy4K 的外观尺寸跟随页面拉伸，但星座本身按 PowerToys 的设计高度（320）排版再垂直居中，
            // 这样最大化时不会因为槽位变多而变得又密又小。
            _layout = new WelcomeHeroLayout(WelcomeHeroLayout.DefaultHeroHeight);
            WatchAnimationsSetting(true);

            foreach (var target in RevealTargets)
            {
                ElementCompositionPreview.SetIsTranslationEnabled(target, true);
                if (_activePlayback != WelcomeHeroPlayback.Static)
                {
                    ElementCompositionPreview.GetElementVisual(target).Opacity = 0f;
                }
            }

            var scale = (float)(XamlRoot?.RasterizationScale ?? 1.0);
            var iconPixels = Math.Ceiling(WelcomeHeroLayout.IconSize * WelcomeHeroScene.MaxMagnification * scale);
            var logoPixels = Math.Ceiling(WelcomeHeroLayout.LogoSize * 1.2f * scale);

            foreach (var module in WelcomeHeroModules.All)
            {
                _iconSurfaces.Add(LoadSurface(module.AssetName, iconPixels));
            }

            _logoSurface = LoadSurface(WelcomeHeroModules.LogoAssetName, logoPixels);

            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _scene = new WelcomeHeroScene(compositor, _layout, _iconSurfaces, _logoSurface, ResolvePalette());
            _scene.SetViewport(new Vector2((float)SceneHost.ActualWidth, (float)SceneHost.ActualHeight), scale, (float)ContentOffsetY);
            ElementCompositionPreview.SetElementChildVisual(SceneHost, _scene.Root);

            if (_activePlayback == WelcomeHeroPlayback.Static)
            {
                _scene.ShowFinalState();
                _started = true;
                _introDone = true;
                return;
            }

            _scene.HideAll();

            // Start once every icon is decoded, so nothing pops in mid-flight; never wait forever though.
            _loadTimeout = DispatcherQueue.CreateTimer();
            _loadTimeout.Interval = AssetLoadTimeout;
            _loadTimeout.IsRepeating = false;
            _loadTimeout.Tick += (_, _) => Start();
            _loadTimeout.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            WatchAnimationsSetting(false);
            DisposeScene();
            ShowRevealTargets();
        }

        /// <summary>
        /// 重新播放：清掉当前合成场景与贴图，再按 Loaded 的流程重建并播放一次入场。
        /// 供全屏欢迎页重复播放使用（不依赖移出/放回可视树）。
        /// </summary>
        public void Restart()
        {
            if (!IsLoaded)
            {
                return;
            }

            WatchAnimationsSetting(false);
            DisposeScene();
            OnLoaded(this, new RoutedEventArgs());
        }

        private void DisposeScene()
        {
            _loadTimeout?.Stop();
            _loadTimeout = null;
            _introTimer?.Stop();
            _introTimer = null;

            ElementCompositionPreview.SetElementChildVisual(SceneHost, null);
            _scene?.Dispose();
            _scene = null;

            foreach (var surface in _iconSurfaces)
            {
                surface.LoadCompleted -= OnSurfaceLoadCompleted;
                surface.Dispose();
            }

            _iconSurfaces.Clear();
            if (_logoSurface is not null)
            {
                _logoSurface.LoadCompleted -= OnSurfaceLoadCompleted;
                _logoSurface.Dispose();
                _logoSurface = null;
            }

            _pendingLoads = 0;
            _started = false;
            _introDone = false;
            _hoveredTile = -1;
            _hoveringLogo = false;
            _pointerOver = false;
            ProtectedCursor = null;
        }

        private void WatchAnimationsSetting(bool watch)
        {
            if (watch == _watchingAnimationsSetting
                || !ApiInformation.IsEventPresent(typeof(UISettings).FullName, nameof(UISettings.AnimationsEnabledChanged)))
            {
                return;
            }

            if (watch)
            {
                _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
            }
            else
            {
                _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
            }

            _watchingAnimationsSetting = watch;
        }

        private void OnAnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
        {
            // Raised on a background thread.
            DispatcherQueue.TryEnqueue(ApplyAnimationsSetting);
        }

        private void ApplyAnimationsSetting()
        {
            if (_scene is null)
            {
                return;
            }

            var enabled = AnimationsEnabled;
            if (!enabled && _activePlayback != WelcomeHeroPlayback.Static)
            {
                // Animations were turned off mid-session: drop all motion, including whatever is still in flight.
                _activePlayback = WelcomeHeroPlayback.Static;
                _loadTimeout?.Stop();
                _introTimer?.Stop();
                _introTimer = null;
                _started = true;
                _introDone = true;
                _scene.DisablePointerEffects();
                _scene.ShowFinalState();
                ShowRevealTargets();
                RefreshHoverLabel();
            }
            else if (enabled && _activePlayback == WelcomeHeroPlayback.Static)
            {
                _activePlayback = WelcomeHeroPlayback.Settle;
                _scene.EnablePointerEffects(ElementCompositionPreview.GetPointerPositionPropertySet(SceneHost));
                _scene.ResumeIdle();
                _scene.SetPointerActive(_pointerOver);
            }
        }

        private void ShowRevealTargets()
        {
            foreach (var target in RevealTargets)
            {
                var visual = ElementCompositionPreview.GetElementVisual(target);
                visual.StopAnimation("Opacity");
                visual.StopAnimation("Translation");
                visual.Opacity = 1f;
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
            }
        }

        private LoadedImageSurface LoadSurface(string assetName, double pixels)
        {
            var surface = LoadedImageSurface.StartLoadFromUri(new Uri(WelcomeHeroModules.GetAssetUri(assetName)), new Size(pixels, pixels));
            surface.LoadCompleted += OnSurfaceLoadCompleted;
            _pendingLoads++;
            return surface;
        }

        private void OnSurfaceLoadCompleted(LoadedImageSurface sender, LoadedImageSourceLoadCompletedEventArgs args)
        {
            sender.LoadCompleted -= OnSurfaceLoadCompleted;
            if (--_pendingLoads <= 0)
            {
                Start();
            }
        }

        private void Start()
        {
            if (_started || _scene is null)
            {
                return;
            }

            _started = true;
            _loadTimeout?.Stop();

            if (_activePlayback == WelcomeHeroPlayback.Intro)
            {
                _scene.PlayIntro();
                RevealContent(IntroRevealStartMs, IntroRevealStaggerMs, IntroRevealMs);
                IntroStarted?.Invoke(this, EventArgs.Empty);
                BeginInteractionLock();
            }
            else
            {
                _scene.PlaySettle();
                RevealContent(SettleRevealStartMs, SettleRevealStaggerMs, SettleRevealMs);
                AllowPointerEffects();
            }
        }

        /// <summary>
        /// 入场动画期间屏蔽指针互动（否则鼠标一动方块就会歪、会放大），
        /// 等整段动画（飞入 + 砸落 + 冲击波）结束后再开启倾斜/放大。
        /// </summary>
        private void BeginInteractionLock()
        {
            _introDone = false;
            _introTimer?.Stop();
            _introTimer = DispatcherQueue.CreateTimer();
            _introTimer.Interval = TimeSpan.FromMilliseconds(IntroInteractionDelayMs);
            _introTimer.IsRepeating = false;
            _introTimer.Tick += (_, _) => AllowPointerEffects();
            _introTimer.Start();
        }

        /// <summary>动画结束后才把指针特效接上，并同步当前指针是否在画面上。</summary>
        private void AllowPointerEffects()
        {
            _introTimer?.Stop();
            _introTimer = null;

            if (_scene is null || _activePlayback == WelcomeHeroPlayback.Static)
            {
                return;
            }

            _introDone = true;
            _scene.EnablePointerEffects(ElementCompositionPreview.GetPointerPositionPropertySet(SceneHost));
            if (_pointerOver)
            {
                _scene.SetPointerActive(true);
            }
        }

        private void RevealContent(float startMs, float staggerMs, float durationMs)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            var easeOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
            var linear = compositor.CreateLinearEasingFunction();

            for (var i = 0; i < RevealTargets.Count; i++)
            {
                var target = RevealTargets[i];
                var delay = TimeSpan.FromMilliseconds(startMs + (i * staggerMs));
                var duration = TimeSpan.FromMilliseconds(durationMs);

                var visual = ElementCompositionPreview.GetElementVisual(target);

                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, 0f, linear);
                fade.InsertKeyFrame(1f, 1f, easeOut);
                fade.Duration = duration;
                fade.DelayTime = delay;
                fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                visual.StartAnimation("Opacity", fade);

                var rise = compositor.CreateVector3KeyFrameAnimation();
                rise.InsertKeyFrame(0f, new Vector3(0f, RevealDistance, 0f), linear);
                rise.InsertKeyFrame(1f, Vector3.Zero, easeOut);
                rise.Duration = duration;
                rise.DelayTime = delay;
                rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                visual.StartAnimation("Translation", rise);
            }
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _scene?.SetViewport(new Vector2((float)SceneHost.ActualWidth, (float)SceneHost.ActualHeight), (float)(XamlRoot?.RasterizationScale ?? 1.0), (float)ContentOffsetY);
            HideLabel();
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            _scene?.ApplyPalette(ResolvePalette(), animate: _activePlayback != WelcomeHeroPlayback.Static);
        }

        private WelcomeHeroPalette ResolvePalette()
        {
            if (new AccessibilitySettings().HighContrast)
            {
                return WelcomeHeroPalette.HighContrast(
                    GetResourceColor("SystemColorWindowTextColor", Microsoft.UI.Colors.White),
                    GetResourceColor("SystemColorHighlightColor", Microsoft.UI.Colors.Cyan));
            }

            return ActualTheme == ElementTheme.Light ? WelcomeHeroPalette.Light : WelcomeHeroPalette.Dark;
        }

        private void SceneHost_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _pointerOver = true;
            if (!_introDone)
            {
                return;
            }

            if (_activePlayback != WelcomeHeroPlayback.Static)
            {
                _scene?.SetPointerActive(true);
            }

            UpdateHover(e);
        }

        private void SceneHost_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_introDone)
            {
                return;
            }

            UpdateHover(e);
        }

        private void SceneHost_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _pointerOver = false;
            if (!_introDone)
            {
                return;
            }

            if (_activePlayback != WelcomeHeroPlayback.Static)
            {
                _scene?.SetPointerActive(false);
            }

            SetHover(-1, false);
        }

        private void SceneHost_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_scene is null || !_introDone)
            {
                return;
            }

            var (tile, logo) = HitTest(e.GetPosition(SceneHost));
            if (tile >= 0)
            {
                ModuleInvoked?.Invoke(this, WelcomeHeroModules.All[tile].NavigationTag);
            }
            else if (logo && _started && _activePlayback != WelcomeHeroPlayback.Static)
            {
                // Easter egg: power it on again.
                // 先把指针特效彻底摘掉（倾斜/放大是合成表达式，光屏蔽事件不够），再播入场，
                // 否则鼠标一动手感还是会被打乱。
                _scene.DisablePointerEffects();
                _activePlayback = WelcomeHeroPlayback.Intro;
                _scene.PlayIntro();
                BeginInteractionLock();
            }
        }

        private void UpdateHover(PointerRoutedEventArgs e)
        {
            if (_scene is null)
            {
                return;
            }

            _lastPointer = ToApex(e.GetCurrentPoint(SceneHost).Position);
            var (tile, logo) = HitTest(_lastPointer);
            SetHover(tile, logo);
        }

        private void RefreshHoverLabel()
        {
            if (_hoveredTile >= 0)
            {
                ShowLabel(_hoveredTile);
            }
        }

        private Vector2 ToApex(Point position) => new Vector2((float)position.X, (float)position.Y) - _scene.Apex;

        private (int Tile, bool Logo) HitTest(Point position) => HitTest(ToApex(position));

        // Hit test what is on screen: while the hero is live, tiles are magnified, tilted and bob.
        private (int Tile, bool Logo) HitTest(Vector2 fromApex)
        {
            var motion = CurrentMotion;
            var tile = _layout.HitTestSlot(fromApex, IconTileCount, motion);
            return (tile, tile < 0 && _layout.HitTestLogo(fromApex, motion));
        }

        private void SetHover(int tile, bool logo)
        {
            if (tile == _hoveredTile && logo == _hoveringLogo)
            {
                return;
            }

            _hoveredTile = tile;
            _hoveringLogo = logo;
            _scene?.SetHighlightedTile(tile);

            if (tile >= 0 || logo)
            {
                _handCursor ??= InputSystemCursor.Create(InputSystemCursorShape.Hand);
                ProtectedCursor = _handCursor;
            }
            else
            {
                ProtectedCursor = null;
            }

            if (tile >= 0)
            {
                ShowLabel(tile);
            }
            else
            {
                HideLabel();
            }
        }

        private void ShowLabel(int tile)
        {
            var module = WelcomeHeroModules.All[tile];
            var name = module.NameResourceKey;
            LabelText.Text = string.IsNullOrEmpty(name) ? module.AssetName : name;
            LabelPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelSize = LabelPill.DesiredSize;

            var center = _scene.Apex + _layout.GetSlotCenter(tile, _lastPointer, CurrentMotion);
            var reach = (WelcomeHeroLayout.TileSize / 2f * WelcomeHeroScene.MaxMagnification) + LabelGap;
            var left = Math.Clamp(center.X - ((float)labelSize.Width / 2f), 4f, Math.Max(4f, (float)ActualWidth - (float)labelSize.Width - 4f));
            var top = center.Y - reach - (float)labelSize.Height;
            var above = top >= 2f;
            if (!above)
            {
                top = center.Y + reach;
            }

            var wasVisible = LabelPill.Opacity > 0;
            Canvas.SetLeft(LabelPill, left);
            Canvas.SetTop(LabelPill, top);
            if (!wasVisible)
            {
                // Start slightly towards the tile, then rise into place.
                LabelPill.TranslationTransition = null;
                LabelPill.Translation = new Vector3(0f, above ? 6f : -6f, 0f);
                LabelPill.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
            }

            LabelPill.Translation = Vector3.Zero;
            LabelPill.Opacity = 1;
        }

        private void HideLabel()
        {
            LabelPill.Opacity = 0;
        }
    }
}
