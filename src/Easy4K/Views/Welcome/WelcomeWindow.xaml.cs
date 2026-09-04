using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Easy4K.Models;
using Easy4K.Services;
using Easy4K.ViewModels;

namespace Easy4K.Views.Welcome;

/// <summary>Easy4K 首次运行设置向导（无边框 Mica，固定 800x600，7 步，页内圆形导航按钮）。
/// 欢迎 → 协议声明 → 硬件声明 → 配置软件 → 处理报告配置 → 选择默认主题 → 完成。
/// 打开期间静默循环播放 Res\welcomemusic.wav；完成时把所有选择持久化并触发 Completed。</summary>
public sealed partial class WelcomeWindow : Window
{
    /// <summary>向导完成（App 订阅后启动正式主窗口）</summary>
    public event Action? Completed;

    private readonly MainViewModel _vm;
    private readonly AppSettings _app;
    private readonly SettingsService _settings;
    private readonly ToolPathConfig _toolCfg;
    private List<Grid> _steps = new();
    private int _step;
    private Windows.Media.Playback.MediaPlayer? _music;
    private bool _finished;
    private Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop? _welcomeAcrylic;

    public WelcomeWindow(MainViewModel vm, SettingsService settings, AppSettings app, ToolPathConfig toolCfg)
    {
        _vm = vm;
        _settings = settings;
        _app = app;
        _toolCfg = toolCfg;
        InitializeComponent();

        // 无边框：扩展内容到标题栏，自定义标题栏为拖拽区（同主窗口）
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")); } catch { }
        // 背景主题由第 6 步四选（浅色/深色/亚克力/跟随系统）决定，回填后由 ApplyOobeTheme 应用

        // 固定 800x600 居中（与 ClassIsland WelcomeWindow 一致），不可缩放
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is not null)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = Easy4K.NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        var w = (int)(800 * scale);
        var h = (int)(600 * scale);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        CenterOnScreen(hwnd, w, h);

        _steps = new List<Grid> { StepWelcome, StepLicense, StepHardware, StepConfig, StepReport, StepTheme, StepFinish };

        RootLayout.Loaded += OnRootLoaded;
        Closed += (_, _) =>
        {
            StopMusic();
            // 向导是首次运行的唯一窗口：未点"完成"就关闭（右上角 X）→ 直接退出
            if (!_finished) Application.Current.Exit();
        };
        ShowStep(0);
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        await LoadLogoAsync();

        // 只读 TextBox 呈现协议/硬件/报告说明（不弹窗）
        LicenseBox.Text = BuildLicenseText();
        HardwareBox.Text = BuildHardwareText();
        ReportDesc.Text = BuildReportDescText();
        VersionText.Text = $"v{_vm.Version}";

        // 回填已保存的默认配置
        CfgSplitCb.IsChecked = _app.DefaultSplitFrames;
        CfgSrCb.IsChecked = _app.DefaultSuperResolution;
        CfgIfCb.IsChecked = _app.DefaultInterpolation;
        CfgMergeCb.IsChecked = _app.DefaultMergeVideo;
        CfgAudioCb.IsChecked = _app.DefaultMergeAudio;
        CfgStartupTestTs.IsOn = _app.StartupSelfTest;
        ReportEnabledTs.IsOn = _app.ReportEnabled;
        CfgReportTs.IsOn = _app.ReportEnabled; // 与第 5 步同源；切换时互相同步
        ReportAutoOpenTs.IsOn = _app.ReportAutoOpen;
        ReportDirBox.Text = string.IsNullOrWhiteSpace(_app.ReportDir) ? "Reports" : _app.ReportDir;
        switch (_app.Theme)
        {
            case "light": ThemeLightRb.IsChecked = true; break;
            case "dark": ThemeDarkRb.IsChecked = true; break;
            case "acrylic": ThemeAcrylicRb.IsChecked = true; break;
            default: ThemeSystemRb.IsChecked = true; break;
        }

        try
        {
            HardwareDetected.Text = _vm.CheckGpu().Replace("\n", "  |  ");
        }
        catch { }

        // 欢迎内容直接可见；配置页勾选与主界面一致（回填后再挂事件避免干扰恢复）
        HookStepLinkage();
        HookReportToggleSync();

        // 静默循环播放欢迎音乐
        await PlayMusicLoopAsync();
    }

    /// <summary>第 4/5 步两个"生成配置文件(HTML报告)"开关互相同步（XAML 事件改代码挂接，规避解析期崩溃）。</summary>
    private void HookReportToggleSync()
    {
        CfgReportTs.Toggled += OnReportToggleChanged;
        ReportEnabledTs.Toggled += OnReportToggleChanged;
    }

    /// <summary>与主界面一致的强制勾选联动（MainViewModel 同规则）：
    /// 勾选 超分/补帧/合并/音频 → 强制勾选拆帧；取消 拆帧 → 取消全部依赖项。</summary>
    private void HookStepLinkage()
    {
        // 依赖项勾选 → 强制勾选拆帧
        foreach (var cb in new[] { CfgSrCb, CfgIfCb, CfgMergeCb, CfgAudioCb })
            cb.Checked += (_, _) =>
            {
                if (CfgSplitCb.IsChecked != true) CfgSplitCb.IsChecked = true;
            };
        // 取消拆帧 → 取消全部依赖项（与主界面 OnSplitFramesChanged 一致）
        CfgSplitCb.Unchecked += (_, _) =>
        {
            CfgSrCb.IsChecked = false;
            CfgIfCb.IsChecked = false;
            CfgMergeCb.IsChecked = false;
            CfgAudioCb.IsChecked = false;
        };
    }

    private async Task LoadLogoAsync()
    {
        try
        {
            var logoPath = Path.Combine(AppContext.BaseDirectory, "Res", "logo.png");
            if (!File.Exists(logoPath)) logoPath = Path.Combine(MainViewModel.ResDir, "logo.png");
            if (!File.Exists(logoPath)) return;
            using var fs = new FileStream(logoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bmp.SetSourceAsync(fs.AsRandomAccessStream());
            TitleLogo.Source = bmp;
            WelcomeLogo.Source = bmp;
        }
        catch
        {
            // logo 仅是装饰，加载失败静默
        }
    }

    private async Task PlayMusicLoopAsync()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Res", "welcomemusic.wav"),
                Path.Combine(MainViewModel.ResDir, "welcomemusic.wav"),
                Path.Combine(AppContext.BaseDirectory, "welcomemusic.wav"),
                @"I:\Easy4K\welcomemusic.wav"
            };
            var file = candidates.FirstOrDefault(File.Exists);
            if (file is null) return;
            var sf = await StorageFile.GetFileFromPathAsync(file);
            _music = new Windows.Media.Playback.MediaPlayer
            {
                Source = Windows.Media.Core.MediaSource.CreateFromStorageFile(sf),
                IsLoopingEnabled = true,
                Volume = 0.6
            };
            _music.Play();
        }
        catch
        {
            // 音乐仅是氛围，加载失败静默跳过
        }
    }

    private void StopMusic()
    {
        try { _music?.Pause(); _music?.Dispose(); } catch { }
        _music = null;
    }

    // ===================== 导航 =====================

    private void ShowStep(int index)
    {
        if (index < 0 || index >= _steps.Count) return;
        _step = index;
        for (int i = 0; i < _steps.Count; i++)
            _steps[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        UpdateNextEnabled();
        if (index == _steps.Count - 1)
            BuildFinishSummary();
    }

    private void UpdateNextEnabled()
    {
        switch (_step)
        {
            case 1:
                LicenseNextBtn.IsEnabled = AgreeLicenseCb.IsChecked == true && AgreePrivacyCb.IsChecked == true;
                break;
            case 2:
                HardwareNextBtn.IsEnabled = AgreeHardwareCb.IsChecked == true;
                break;
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void OnAgreeChanged(object sender, RoutedEventArgs e) => UpdateNextEnabled();

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == 1 && (AgreeLicenseCb.IsChecked != true || AgreePrivacyCb.IsChecked != true))
        {
            _vm.Logger.Warn("请先勾选同意全部协议条款后再继续");
            return;
        }
        if (_step == 2 && AgreeHardwareCb.IsChecked != true)
        {
            _vm.Logger.Warn("请确认已了解硬件要求后再继续");
            return;
        }
        ShowStep(_step + 1);
    }

    private void OnBrowseReportDir(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        _ = PickAsync(picker);
    }

    private async Task PickAsync(FolderPicker picker)
    {
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ReportDirBox.Text = folder.Path;
    }

    // ===================== 主题（浅色/深色/亚克力/跟随系统）与报告开关同步 =====================

    /// <summary>第 4/5 步的"生成配置文件(HTML报告)"开关互相同步。</summary>
    private void OnReportToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts) return;
        var other = ReferenceEquals(ts, CfgReportTs) ? ReportEnabledTs : CfgReportTs;
        if (other.IsOn != ts.IsOn) other.IsOn = ts.IsOn;
    }

    /// <summary>把四选主题即时应用到向导窗口：浅色/深色/跟随系统为纯色主题，亚克力加半透明毛玻璃。</summary>
    private void OnThemeChanged(object sender, RoutedEventArgs e) => ApplyOobeTheme();

    private void ApplyOobeTheme()
    {
        try
        {
            if (ThemeAcrylicRb.IsChecked == true)
            {
                _welcomeAcrylic ??= new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
                SystemBackdrop = _welcomeAcrylic;
                RootLayout.RequestedTheme = ElementTheme.Default; // 亚克力颜色跟随系统
            }
            else
            {
                SystemBackdrop = null;
                RootLayout.RequestedTheme = ThemeLightRb.IsChecked == true ? ElementTheme.Light
                    : ThemeDarkRb.IsChecked == true ? ElementTheme.Dark : ElementTheme.Default;
            }
        }
        catch
        {
            // 亚克力不可用（系统关闭透明效果等）时退化为纯色跟随系统
            SystemBackdrop = null;
            RootLayout.RequestedTheme = ElementTheme.Default;
        }
    }

    private string SelectedThemeName => ThemeLightRb.IsChecked == true ? "浅色"
        : ThemeDarkRb.IsChecked == true ? "深色"
        : ThemeAcrylicRb.IsChecked == true ? "亚克力" : "跟随系统";

    // ===================== 完成 =====================

    private void BuildFinishSummary()
    {
        var parts = new List<string>();
        if (CfgSplitCb.IsChecked == true) parts.Add("拆帧");
        if (CfgSrCb.IsChecked == true) parts.Add("超分");
        if (CfgIfCb.IsChecked == true) parts.Add("补帧");
        if (CfgMergeCb.IsChecked == true) parts.Add("合并");
        if (CfgAudioCb.IsChecked == true) parts.Add("音频");
        var steps = parts.Count == 0 ? "无（仅视频信息检测）" : string.Join(" → ", parts);

        FinishSummary.Text =
            $"· 默认处理步骤：{steps}\n" +
            $"· 启动自检：{(CfgStartupTestTs.IsOn ? "每次启动先跑测试视频" : "关闭")}\n" +
            $"· 配置文件(HTML报告)：{(ReportEnabledTs.IsOn ? "生成" : "不生成")}，保存目录：{ReportDirBox.Text}\n" +
            $"· 默认主题：{SelectedThemeName}\n\n点击下方按钮启动 Easy4K，即可开始正式使用。";
    }

    private void OnFinishLaunch(object sender, RoutedEventArgs e)
    {
        if (_finished) return;
        _finished = true;

        var reportDir = string.IsNullOrWhiteSpace(ReportDirBox.Text) ? "Reports" : ReportDirBox.Text.Trim();
        var theme = SelectedThemeName switch
        {
            "浅色" => "light",
            "深色" => "dark",
            "亚克力" => "acrylic",
            _ => "system"
        };

        _vm.ApplyWelcomeConfig(
            split: CfgSplitCb.IsChecked == true,
            sr: CfgSrCb.IsChecked == true,
            interpolation: CfgIfCb.IsChecked == true,
            merge: CfgMergeCb.IsChecked == true,
            mergeAudio: CfgAudioCb.IsChecked == true,
            hdr: false,
            startupSelfTest: CfgStartupTestTs.IsOn,
            reportDir: reportDir,
            reportEnabled: ReportEnabledTs.IsOn,
            reportAutoOpen: ReportAutoOpenTs.IsOn,
            theme: theme);
        _vm.SetThemeMode(theme);

        StopMusic();
        Completed?.Invoke();
        Close();
    }

    // ===================== 文本内容（协议/硬件/报告说明，Textbox 展示不弹窗） =====================

    private static string BuildLicenseText() =>
        """
        【Easy4K 使用协议】
        1. 本软件为"视频超分补帧"工具，基于 WinUI 3 / Windows App SDK 开发。
        2. 使用本软件需自备显卡（Vulkan）与 FFmpeg / Real-ESRGAN / RIFE / NVEncC 等第三方工具。
        3. 处理过程中会大量占用 CPU/GPU，可能导致发热、降频甚至崩溃，请自行评估风险。
        4. 请勿将本软件用于任何非法用途；输出内容的版权与合规性由使用者负责。

        【开源许可】
        Easy4K 调用以下开源/第三方组件：
        - FFmpeg (LGPL/GPL)：视频解码/编码/合成
        - Real-ESRGAN (BSD-3-Clause)：AI 超分
        - RIFE / rife-ncnn-vulkan：AI 补帧
        - NVEncC (MIT)：RTX 显卡 SDR→HDR 硬件编码
        各组件版权归其作者所有，请遵守其各自许可证。

        【隐私与免责声明】
        1. 本软件不会收集或上传任何个人数据，所有视频与设置仅保存在本机。
        2. 本软件按"现状"提供，开发者不对处理结果的正确性、完整性及可用性作任何保证。
        3. 因使用本软件导致的数据损坏、硬件故障或任何直接/间接损失，开发者概不负责。
        4. 不同意以上条款请勿使用本软件。
        """;

    private static string BuildHardwareText() =>
        """
        【硬件最低要求】
        1. 系统：Windows 10 1809+ / Windows 11
        2. 显卡：支持 Vulkan 的 NVIDIA / AMD / Intel GPU（NVIDIA 实测最稳）
        3. 内存：建议 8GB 以上（超分/补帧会吃大量内存）
        4. 显存：建议 4GB+；超分 4K 建议 6GB+
        5. SDR→HDR：仅 NVIDIA RTX 20/30/40/50 系列 + NVEncC 可用
        6. CPU 模式：无独显时可选，速度慢，仅应急用

        【注意】
        - 显卡驱动过旧可能导致设备丢失/崩溃，请保持最新驱动。
        - AMD 显卡偶发 ncnn-vulkan 停滞属已知现象，可降低线程或升级驱动缓解。
        - 低显存设备运行大模型失败时，软件会自动降线程重试。
        """;

    private static string BuildReportDescText() =>
        """
        报告内容（每次处理完成自动生成，纯本地 HTML，不联网）：
        · 本次开启的选项（超分模型/倍率、补帧引擎/模型、各开关等）
        · 调用的构建代码 / 外部命令
        · 测试帧与输出中间帧（随机抽取若干张内嵌展示）
        · 输入/输出文件、执行步骤、总耗时

        生成位置：下方目录（可点"浏览..."修改）。生成后自动用默认浏览器打开（可在上方关闭）。
        """;

    private void CenterOnScreen(IntPtr hwnd, int width, int height)
    {
        try
        {
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            var x = area.WorkArea.X + (area.WorkArea.Width - width) / 2;
            var y = area.WorkArea.Y + (area.WorkArea.Height - height) / 2;
            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
        catch { }
    }
}