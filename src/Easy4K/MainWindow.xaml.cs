using System.Diagnostics;
using Easy4K.Models;
using Easy4K.Services;
using Easy4K.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Easy4K;

/// <summary>应用窗口：自定义标题栏 + 全局菜单栏 + 主页/进行中导航 + 纯进度条。</summary>
public sealed partial class MainWindow : Window
{
    private MainViewModel Vm => App.Services;
    private bool _allowClose;
    /// <summary>老缓存弹窗是否已打开（防止重复触发时叠弹多个）</summary>
    private bool _cacheDialogOpen;
    /// <summary>临时目录旧文件提示弹窗连续弹出次数（用户一直点取消/不处理时逐次累加，超过 5 次转"你怎么不听"警告）</summary>
    private int _cacheWarnCount;
    /// <summary>亚克力主题材质（作为独立主题选项，与普通主题互斥）</summary>
    private DesktopAcrylicBackdrop? _acrylic;
    /// <summary>遥测同意弹窗是否已触发（Loaded 可能多次触发，避免重复弹窗）</summary>
    private bool _telemetryAsked;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 标题追加版本号（版本号来自 config 文件 appsettings.json 的 Version）
        var title = $"Easy4K v{Vm.Version}";
        Title = title;
        TitleTextBlock.Text = title;

        // 窗口按 16:9 比例设置初始尺寸（1280x720，按 DPI 缩放）
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        AppWindow.Resize(new() { Width = (int)(1280 * scale), Height = (int)(720 * scale) });

        // 处理中关闭窗口 → 警告并确认，确认后强杀工具进程
        AppWindow.Closing += OnAppWindowClosing;

        RootFrame.Navigate(typeof(MainPage));

        // 处理开始 → 显示导航按钮 + 自动切到"进行中"页
        Vm.ProcessingStarted += () =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ProgressNavItem.Visibility = Visibility.Visible;
                NavigateToProgress();
            });
        };

        // 处理结束 → 弹完成窗体（窗口级 XamlRoot，页面切换/卸载不影响）+ 隐藏导航按钮 + 切回主页
        Vm.ProcessingCompleted += r =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ProgressNavItem.Visibility = Visibility.Collapsed;
                NavigateToHome();
                // 正式处理完成 → 自动生成 HTML 报告（选项/命令/测试帧/中间帧抽帧），随后弹完成窗体
                Vm.TryGenerateReport(r.OutputPath, r.StepsText, r.Elapsed);
                _ = ShowCompletionDialogAsync(r);
            });
        };

        // 纯进度条实时更新（进行页隐藏，主页可见）
        // 只接受有总帧数的进度事件；拆帧/超分预览轮询事件(Total=0)只用于刷新预览图，
        // 若也写入进度条，会被每 200ms 一次的轮询事件归零导致进度条抽搐
        Vm.ProgressChanged += p =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (p.Total > 0) ThinProgressBar.Value = p.Percent;
            });
        };

        // 自动测试结束 → 回主页并展示结果
        Vm.AutoTestFinished += ok => DispatcherQueue.TryEnqueue(() =>
        {
            ProgressNavItem.Visibility = Visibility.Collapsed;
            ThinProgressBar.Visibility = Visibility.Collapsed;
            NavigateToHome();
            Vm.ProgressDetail = ok ? "自动测试全部通过，总结见日志" : "自动测试结束（有失败项），详见日志/总结文件";
            Vm.Logger.Info(ok ? "自动测试全部通过" : "自动测试结束（存在失败项），请查看 Res\\autotest_summary.txt 与失败保留目录");
        });

        // 清理临时文件：主页进度条显示清理进度（不定进度转圈，避免 0→100→0 跳变抽搐；不切换进行中页）
        Vm.PropertyChanged += (s, e) => DispatcherQueue.TryEnqueue(() =>
        {
            if (e.PropertyName == nameof(Vm.IsCleaning))
            {
                ThinProgressBar.IsIndeterminate = Vm.IsCleaning; // 清理用不定进度
                ThinProgressBar.Visibility = (Vm.IsCleaning || Vm.IsProcessing) ? Visibility.Visible : Visibility.Collapsed;
                if (!Vm.IsCleaning && !Vm.IsProcessing)
                    ThinProgressBar.IsIndeterminate = false; // 恢复定值进度供处理使用
            }
            else if (e.PropertyName == nameof(Vm.BackgroundImage) && Vm.SavedTheme == "image")
            {
                ApplyBackdropImage(); // 图片背景主题下换图立即生效
            }
            else if (e.PropertyName == nameof(Vm.BackdropAcrylicPercent) && Vm.SavedTheme == "image")
            {
                ApplyBackdropOpacity(); // 拖动浓度条立即生效
            }
        });

        // 临时目录有旧文件（缓存来自其他视频/残留帧）→ 弹窗引导（重新选择临时目录 / 清理缓存 / 取消）
        // 启动按钮不再因缓存不符置灰：用户每次点"开始处理"都会再次弹窗，直到清理或更换目录；
        // 愤怒计数仅在"点开始处理"触发时累计，按下第 5 次仍不处理时改为"你怎么不听"的警告文案；
        // 选视频等其它途径触发的一律保持普通弹窗，不累计。
        Vm.TempCacheMismatchDetected += fromStart => DispatcherQueue.TryEnqueue(async () =>
        {
            if (_cacheDialogOpen) return;
            _cacheDialogOpen = true;
            if (fromStart) _cacheWarnCount++; // 仅点开始处理累计愤怒计数
            try
            {
                var angry = fromStart && _cacheWarnCount >= 5; // 点开始处理第 5 次起上警告
                var dlg = new ContentDialog
                {
                    Title = angry ? "你为什么不听呢" : "临时目录有旧文件",
                    Content = angry
                        ? "欸朋友，我跟你说话你怎么不听呢？都说了临时目录有旧文件会导致处理中断，你为什么不听呢？\n\n" +
                          "请重新选择临时目录，或先清理缓存！"
                        : "当前临时目录中的缓存来自其他视频，直接处理会被旧缓存误导导致中断。\n\n请重新选择临时目录，或先清理缓存。",
                    PrimaryButtonText = "重新选择临时目录",
                    SecondaryButtonText = "清理缓存",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = RootFrame.XamlRoot
                };
                var r = await dlg.ShowLocalizedAsync();
                if (r == ContentDialogResult.Primary)
                {
                    await PickTempFolderAsync();
                }
                else if (r == ContentDialogResult.Secondary)
                {
                    // 清理缓存（双重警告确认后执行，含 cache.json 一并删除）
                    await ConfirmCleanTempAsync();
                }
                // 重新评估：已清理/已更换到匹配或无缓存目录 → 放行并重置警告计数；
                // 仍不匹配（含用户点取消/更换时取消选择器）→ 计数继续累加，下次点击启动再弹
                Vm.RefreshCacheBlock();
                var resolved = !Vm.CacheBlocked;
                if (resolved) _cacheWarnCount = 0;
                Vm.DialogResult(resolved); // 唤醒启动确认流程：true 继续（可能弹 CPU 警告），false 阻断启动
            }
            finally
            {
                _cacheDialogOpen = false;
            }
        });

        // 启动前 CPU 最终警告（ConfirmStartAsync 在临时目录冲突通过后触发）
        Vm.CpuFinalWarningRequired += () => DispatcherQueue.TryEnqueue(async () =>
        {
            var dlg = new ContentDialog
            {
                Title = "不建议开启 CPU 处理",
                Content = "当前已开启 CPU 处理模式，所有模型将使用 CPU 推理，处理速度会大幅下降（可能比 GPU 慢 10 倍以上）。\n\n确定要以 CPU 模式启动处理吗？",
                PrimaryButtonText = "继续处理",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootFrame.XamlRoot
            };
            Vm.DialogResult(await dlg.ShowLocalizedAsync() == ContentDialogResult.Primary);
        });

        // 涡轮模式因临时目录在机械盘未生效 → 明确弹窗告知（不静默关闭），并提供直接切换临时目录的入口
        Vm.TurboDisabledRequired += reason => DispatcherQueue.TryEnqueue(async () =>
        {
            var dlg = new ContentDialog
            {
                Title = "涡轮模式未生效",
                Content = new ScrollViewer
                {
                    MaxHeight = 420,
                    Content = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Text = reason }
                },
                PrimaryButtonText = "切换硬盘",
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootFrame.XamlRoot
            };
            if (await dlg.ShowLocalizedAsync() == ContentDialogResult.Primary)
                await ChooseTempFolderAsync();
        });

        // 开始处理被前置校验拦截（如目录不可写）→ 弹窗告知原因，避免主页看不到日志而表现为"点了没反应"
        Vm.StartFailed += msg => DispatcherQueue.TryEnqueue(async () =>
        {
            var dlg = new ContentDialog
            {
                Title = "无法开始处理",
                Content = msg,
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootFrame.XamlRoot
            };
            await dlg.ShowLocalizedAsync();
        });

        // 启动时恢复上次保存的主题（light/dark/system/acrylic/image）
        switch (Vm.SavedTheme)
        {
            case "light": SetTheme(ElementTheme.Light); break;
            case "dark": SetTheme(ElementTheme.Dark); break;
            case "acrylic": SetAcrylicTheme(); break;
            case "image": SetImageBackdropTheme(); break;
        }

        // 导航：默认选中第 1 项（主页）；随窗口宽度在顶栏/左侧最小化之间自适应
        if (RootNav.MenuItems.Count > 0) RootNav.SelectedItem = RootNav.MenuItems[0];
        RootNav.SizeChanged += (_, e) => UpdatePaneDisplayMode(e.NewSize.Width);
        UpdatePaneDisplayMode(RootNav.ActualWidth);

        // 多语言：界面加载完/切页/切语言时重新本地化（语言在设置页切换）
        // 注意：Frame.Navigated 触发时新页面的可视树往往还没构建，直接遍历会什么都扫不到
        //（表现为"切到另一个页面就变回中文"），所以再挂一次该页面的 Loaded。
        Loc.LanguageChanged += () => DispatcherQueue.TryEnqueue(ApplyLocalization);
        RootFrame.Navigated += (_, e) =>
        {
            if (e.Content is FrameworkElement page)
                page.Loaded += (s, _) =>
                {
                    if (s is DependencyObject loaded) Loc.LocalizeTree(loaded);
                };
            DispatcherQueue.TryEnqueue(ApplyLocalization);
        };
        DispatcherQueue.TryEnqueue(ApplyLocalization);

        // 启动后异步检查更新（服务器版本高于 config 里的本地版本时弹窗提示）
        _ = CheckUpdateAsync();

        // 首次进入主界面：先询问是否同意上传遥测数据（仅第一次启动；已有选择则静默跳过）
        RootFrame.Loaded += async (_, _) =>
        {
            if (_telemetryAsked) return;
            _telemetryAsked = true;
            try
            {
                await Task.Delay(400); // 等界面呈现稳定后再弹，避免与启动自检抢焦点
                await AskTelemetryConsentAsync();
            }
            catch (Exception ex)
            {
                Vm.Logger.Warn($"遥测同意询问失败（忽略）: {ex.Message}");
            }
        };
    }

    // ===================== 匿名遥测同意（仅首次启动询问一次） =====================

    /// <summary>首次进入主界面时询问是否同意上传遥测数据；同意/拒绝都只问这一次（结果写进 appsettings.json）。</summary>
    private async Task AskTelemetryConsentAsync()
    {
        if (!Vm.NeedsTelemetryConsent) return; // 已选择过 → 不再询问

        var dlg = new ContentDialog
        {
            Title = "是否同意上传遥测数据",
            Content =
                "为了解 Easy4K 的实际使用情况、改进软件，我们希望收集少量匿名信息并上传到官方服务器。\n\n" +
                "会收集：\n" +
                "· 软件启动时间\n" +
                "· 本次开启的功能（拆分 / 超分 / 补帧 / 帧去重 / 音频超分等）\n" +
                "· Easy4K 版本号\n" +
                "· Windows 系统版本\n" +
                "· 所在地区（仅国家/地区，取自系统区域设置）\n\n" +
                "不会收集：视频或音频内容、文件名、文件路径、电脑用户名、账号等任何可识别到个人的信息，也不会收集 IP 定位。\n\n" +
                "数据仅用于统计功能使用情况与系统兼容性，帮助定位问题、改进软件，不用于其他用途。\n\n" +
                "同意后，每次点击「开始处理」时会自动上传一条。\n\n" +
                "本提示只在首次启动时出现一次：选择「不同意」同样不会影响任何功能。",
            PrimaryButtonText = "同意",
            CloseButtonText = "不同意",
            DefaultButton = ContentDialogButton.Close, // 默认落在「不同意」，避免误触上传
            XamlRoot = RootFrame.XamlRoot
        };
        Vm.SetTelemetryConsent(await dlg.ShowLocalizedAsync() == ContentDialogResult.Primary);
    }

    // ===================== 多语言 =====================

    /// <summary>把窗口内（含导航栏、当前页面）的中文文案替换成当前语言译文。语言在设置页切换。</summary>
    private void ApplyLocalization()
    {
        Loc.LocalizeNavView(RootNav); // 顶栏导航项：窄窗口下不在可视树里，按数据翻译
        if (Content is DependencyObject root) Loc.LocalizeTree(root);
    }

    // ===================== 导航 =====================

    public void NavigateToHome()
    {
        if (RootFrame.CurrentSourcePageType != typeof(MainPage))
            RootFrame.Navigate(typeof(MainPage));
        SyncNavSelection("ez"); // 主页 = EZ Mode，导航高亮同步回来
        // 未启动处理时不显示进度条；处理中手动回主页才显示全局进度
        ThinProgressBar.Visibility = Vm.IsProcessing ? Visibility.Visible : Visibility.Collapsed;
    }

    public void NavigateToProgress()
    {
        ProgressNavItem.Visibility = Visibility.Visible; // 「进行中」只在处理期间存在，进页面前确保可见
        if (RootFrame.CurrentSourcePageType != typeof(ProgressPage))
            RootFrame.Navigate(typeof(ProgressPage));
        SyncNavSelection("progress"); // 导航高亮移到「进行中」
        ThinProgressBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>处理前测试未通过（已取消正式处理）后调用：隐藏「进行中」导航项并回主页。</summary>
    public void HideNavAndGoHome()
    {
        ProgressNavItem.Visibility = Visibility.Collapsed;
        ThinProgressBar.Visibility = Visibility.Collapsed;
        NavigateToHome();
    }

    /// <summary>主窗口内容根（供弹窗指定 XamlRoot，避免页面卸载后引用失效）。</summary>
    public Microsoft.UI.Xaml.XamlRoot WindowContentRoot => RootFrame.XamlRoot;

    // ===================== 自适应导航（主页 / 高级 / 进行中 / 齿轮设置） =====================

    /// <summary>程序设置导航选中项时抑制 SelectionChanged，避免与页面导航互相触发。</summary>
    private bool _suppressNavChange;

    /// <summary>参考 WinUI 导航指南：宽窗口用顶栏（Top），窄窗口收成左侧最小化（LeftMinimal）。</summary>
    private void UpdatePaneDisplayMode(double width)
    {
        var mode = width >= 1000 ? NavigationViewPaneDisplayMode.Top : NavigationViewPaneDisplayMode.LeftMinimal;
        if (RootNav.PaneDisplayMode != mode) RootNav.PaneDisplayMode = mode;
    }

    /// <summary>顶栏切换：主页 ↔ 高级 ↔ 进行中。</summary>
    private void OnRootNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavChange) return;
        if (args.SelectedItem is not NavigationViewItem item) return;

        var tag = item.Tag as string;
        if (tag == "ez" && RootFrame.CurrentSourcePageType != typeof(MainPage))
            RootFrame.Navigate(typeof(MainPage));
        else if (tag == "adv" && RootFrame.CurrentSourcePageType != typeof(AdvancedPage))
            RootFrame.Navigate(typeof(AdvancedPage));
        else if (tag == "progress" && RootFrame.CurrentSourcePageType != typeof(ProgressPage))
            RootFrame.Navigate(typeof(ProgressPage));

        ThinProgressBar.Visibility = Vm.IsProcessing && RootFrame.CurrentSourcePageType != typeof(ProgressPage)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>齿轮（设置项）不在 MenuItems 里，只能通过 ItemInvoked 捕获。</summary>
    private void OnRootNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (!args.IsSettingsInvoked) return;
        if (RootFrame.CurrentSourcePageType != typeof(SettingsPage))
            RootFrame.Navigate(typeof(SettingsPage));
        ThinProgressBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>把导航选中项同步到指定项（返回主页等场景调用）。</summary>
    private void SyncNavSelection(string tag)
    {
        _suppressNavChange = true;
        foreach (var obj in RootNav.MenuItems)
            if (obj is NavigationViewItem item && (item.Tag as string) == tag)
                RootNav.SelectedItem = item;
        _suppressNavChange = false;
    }

    // ===================== 完成弹窗 =====================

    /// <summary>弹"当前任务已完成"窗体：绿勾 + 产出路径 + 步骤 + 耗时 + 是否清理冗余文件。
    /// 用窗口级 XamlRoot（RootFrame.XamlRoot，不随页面导航卸载失效），处理完成自动切回主页后仍能正常弹出。
    /// 清理在后台线程执行（大量中间文件递归删除会冻结 UI，之前点"是"卡死就是这原因）。</summary>
    private async Task ShowCompletionDialogAsync(ProcessingResult r)
    {
        var check = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 48,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 80, 200, 100))
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
        panel.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                check,
                new TextBlock { Text = "当前任务已完成!", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center }
            }
        });
        panel.Children.Add(new TextBlock { Text = $"文件产出在：{r.OutputPath}", TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        panel.Children.Add(new TextBlock { Text = $"进行步骤：{r.StepsText}", TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        panel.Children.Add(new TextBlock { Text = $"总耗时：{FormatElapsed(r.Elapsed)}", FontSize = 13 });
        panel.Children.Add(new TextBlock { Text = "是否清理冗余文件?", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 });

        var dlg = new ContentDialog
        {
            Title = "完成",
            Content = panel,
            PrimaryButtonText = "是",
            SecondaryButtonText = "否",
            CloseButtonText = "取消",
            XamlRoot = RootFrame.XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dlg.ShowLocalizedAsync();
        if (result == ContentDialogResult.Primary)
        {
            // 后台清理（主页顶部进度条显示清理进度，不阻塞 UI）
            await Vm.CleanTempInAsync(Vm.TempRoot);
        }
    }

    private static string FormatElapsed(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}小时{t.Minutes}分{t.Seconds}秒"
           : t.TotalMinutes >= 1 ? $"{t.Minutes}分{t.Seconds}秒"
           : $"{t.Seconds}秒";

    // ===================== 关闭拦截 =====================

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        // 已确认关闭（包括无处理任务的正常关闭）直接放行
        if (_allowClose) return;

        args.Cancel = true; // 先阻止关闭，弹窗确认后再决定
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        // 没在处理 → 直接关闭
        if (!Vm.IsProcessing)
        {
            _allowClose = true;
            Close();
            return;
        }

        var dlg = new ContentDialog
        {
            Title = "正在处理中",
            Content = "还有处理任务正在进行中，关闭将强制终止 FFmpeg / RealESRGAN / RIFE 等进程并丢弃未完成结果。确定关闭吗？",
            PrimaryButtonText = "关闭并终止",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootFrame.XamlRoot
        };
        var result = await dlg.ShowLocalizedAsync();
        if (result != ContentDialogResult.Primary) return; // 取消 → 继续处理

        // 确认关闭：停止任务 + 强杀工具进程（防止残留占用 GPU/文件）
        Vm.Stop();
        Vm.KillToolProcesses();
        Vm.Logger.Warn("已强制终止处理进程，正在退出...");
        _allowClose = true;
        Close();
    }

    // ===================== 文件 / 目录选择 =====================

    private async void OnOpenVideoMenu(object sender, RoutedEventArgs e) => await PickVideoAsync();
    private async void OnChooseTempMenu(object sender, RoutedEventArgs e) => await PickTempFolderAsync();
    private async void OnChooseOutputMenu(object sender, RoutedEventArgs e) => await PickFolderAsync(p => Vm.SetOutputRoot(p));
    private void OnExitMenu(object sender, RoutedEventArgs e) => Application.Current.Exit();

    private async Task PickVideoAsync()
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mkv");
        picker.FileTypeFilter.Add(".avi");
        picker.FileTypeFilter.Add(".mov");
        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await Vm.SetInputVideoAsync(file.Path);
    }

    private async Task PickFolderAsync(Action<string> apply)
    {
        var picker = new FolderPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.ViewMode = PickerViewMode.List;
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) apply(folder.Path);
    }

    /// <summary>选择临时目录：检测 cache.json——来源视频与当前视频不符则弹三选一，否则直接接受</summary>
    private async Task PickTempFolderAsync()
    {
        var picker = new FolderPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.ViewMode = PickerViewMode.List;

        while (true)
        {
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            var (status, source) = Vm.CheckTempCache(folder.Path);
            if (status is ViewModels.MainViewModel.TempCacheStatus.None or ViewModels.MainViewModel.TempCacheStatus.Match)
            {
                Vm.SetTempRoot(folder.Path);
                return;
            }

            var dlg = new ContentDialog
            {
                Title = "检测到旧缓存文件",
                Content = $"该文件夹的缓存来源是：{source}，与当前输入视频不符。\n\n如何处理？",
                PrimaryButtonText = "覆盖老缓存",
                SecondaryButtonText = "更换至其他文件夹",
                CloseButtonText = "取消本次实例",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootFrame.XamlRoot
            };
            var r = await dlg.ShowLocalizedAsync();
            if (r == ContentDialogResult.Primary)
            {
                Vm.AcceptTempRoot(folder.Path);
                return;
            }
            if (r == ContentDialogResult.Secondary) continue;
            return;
        }
    }

    private void InitializePicker(object picker)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);
    }

    // ===================== 供设置页调用的公开入口 =====================

    public async Task OpenVideoAsync() => await PickVideoAsync();

    public async Task ChooseTempFolderAsync() => await PickTempFolderAsync();

    public async Task ChooseOutputFolderAsync() => await PickFolderAsync(p => Vm.SetOutputRoot(p));

    /// <summary>选择 HTML 报告保存目录（供「高级」页调用）</summary>
    public async Task ChooseReportFolderAsync() => await PickFolderAsync(p => Vm.ReportDir = p);

    public async Task CleanTempAsync() => await ConfirmCleanTempAsync();

    /// <summary>底部通知条当前挂起的动作（用户点"动作按钮"时执行；重开/关闭即清空）</summary>
    private Action? _noticeAction;

    /// <summary>弹一条非目标式通知条（窗口底部居中，不遮挡内容、不打断操作）。
    /// 给"点了有动作但界面无反馈"的按钮用：actionText 非空时带一个动作按钮（如"重新选择视频""打开临时目录"），
    /// 点击后执行 action 回到触发处继续操作；不传 actionText 则只有"知道了"。</summary>
    public void ShowNotice(string title, string message, string? actionText = null, Action? action = null)
    {
        NoticeTip.Title = title;
        NoticeTip.Subtitle = message;
        NoticeTip.ActionButtonContent = actionText; // null → 不显示动作按钮
        _noticeAction = actionText is null ? null : action;

        // 已显示时只替换内容（同一时刻只留最新一条），避免先关后开同帧内弹不出来
        if (!NoticeTip.IsOpen) NoticeTip.IsOpen = true;
    }

    private void OnNoticeTipAction(TeachingTip sender, object args)
    {
        var action = _noticeAction;
        _noticeAction = null;
        sender.IsOpen = false;
        action?.Invoke();
    }

    private void OnNoticeTipClosed(TeachingTip sender, TeachingTipClosedEventArgs args) => _noticeAction = null;

    /// <summary>用资源管理器打开目录（通知条动作按钮用）。</summary>
    private static void OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path)) return;
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
        }
        catch { /* 打不开目录不影响主流程 */ }
    }

    /// <summary>打开 appsettings.json 所在目录（可写根目录）。</summary>
    public void OpenConfigFolder() => OpenFolder(AppPaths.WritableRoot);

    /// <summary>打开临时目录（提取音频等动作的查看入口）。</summary>
    public void OpenTempFolder() => OpenFolder(Vm.TempRoot);

    public Task ShowInfoDialogAsync(string title, string content)
    {
        ShowDialog(title, content);
        return Task.CompletedTask;
    }

    public Task ShowHelpAsync()
    {
        ShowDialog("使用说明", HelpText);
        return Task.CompletedTask;
    }

    public Task ShowShortcutsAsync()
    {
        ShowDialog("快捷键", ShortcutsText);
        return Task.CompletedTask;
    }

    public Task ShowAboutAsync()
    {
        ShowDialog($"关于 Easy4K v{Vm.Version}", AboutText);
        return Task.CompletedTask;
    }

    /// <summary>按设置页选中的主题串应用主题（light / dark / system / acrylic / image）。</summary>
    public void ApplyThemeFromSettings(string theme)
    {
        switch (theme)
        {
            case "light": SetTheme(ElementTheme.Light); break;
            case "dark": SetTheme(ElementTheme.Dark); break;
            case "acrylic": SetAcrylicTheme(); break;
            case "image": SetImageBackdropTheme(); break;
            default: SetTheme(ElementTheme.Default); break;
        }
    }

    /// <summary>清理临时文件：双重警告确认，全部确认才执行，结果走日志。
    /// 处理进行中禁止清理（主页按钮已禁用，此处兜底拦截菜单等入口）。</summary>
    private async Task ConfirmCleanTempAsync()
    {
        if (Vm.IsProcessing)
        {
            Vm.Logger.Warn("处理进行中，无法清理临时文件");
            return;
        }

        var first = new ContentDialog
        {
            Title = "清理临时文件",
            Content = "确定要删除临时目录中的帧和中间产物吗？删除后无法恢复。",
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootFrame.XamlRoot
        };
        if (await first.ShowLocalizedAsync() != ContentDialogResult.Primary) return;

        var second = new ContentDialog
        {
            Title = "再次确认",
            Content = "再次确认：即将删除临时目录中的全部中间产物（帧、音频、视频缓存），此操作不可撤销。",
            PrimaryButtonText = "确认清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close, // 第二次默认取消更安全
            XamlRoot = RootFrame.XamlRoot
        };
        if (await second.ShowLocalizedAsync() != ContentDialogResult.Primary) return;

        var msg = await Vm.CleanTempInAsync(Vm.TempRoot); // 后台清理，主页进度条显示进度
        // 清理后重新评估：cache.json 已删除则解除启动阻断
        Vm.RefreshCacheBlock();
        ShowNotice(msg.Contains("无法删除") ? "清理完成，但有项目未能删除" : "临时文件已清理",
            msg, "打开临时目录", OpenTempFolder);
    }

    /// <summary>使用说明正文</summary>
    private static string HelpText =>
        "1. 选择输入视频\n2. 自动检测分辨率/帧率\n3. 勾选要执行的处理（超分/补帧/合并/音频/HDR）\n" +
        "4. 选择模型与倍率（超分倍率会自动过滤可用模型）\n5. 点击开始处理\n\n" +
        "处理流程：拆帧 → 帧去重 → 超分 → 补帧 → 合并视频 → SDR→HDR → 音频超分 → 嵌入音频\n\n" +
        "【高级】页可开启两项额外能力（默认关闭）：\n" +
        "· 帧去重：超分/补帧前剔除与上一帧重复的画面，处理完按索引表回填，成品时长、帧率与音频不变；\n" +
        "· 音频超分：AudioSR 把音轨升到 48kHz 高带宽，勾选「将原音频合并进新视频」时替换视频音轨，\n" +
        "  未勾选则单独输出一份 48kHz WAV。";

    /// <summary>快捷键正文</summary>
    private static string ShortcutsText =>
        "Ctrl+O       打开视频\nCtrl+S        开始处理\nCtrl+Shift+S  停止处理\n" +
        "Ctrl+L        清空日志\nF1            帮助\nAlt+F4        退出";

    /// <summary>关于正文（含免责声明）</summary>
    private string AboutText =>
        $"Easy4K v{Vm.Version} - 一键视频超分补帧工具\n\n基于 WinUI 3 / Windows App SDK\n" +
        "Real-ESRGAN-ncnn-Vulkan / RIFE-ncnn-Vulkan / Offical RIFE (PyTorch) / AudioSR (ONNX) /\n" +
        "NVEncC / FFmpeg\n\n" +
        "补帧引擎：NCNN（Vulkan 全 GPU）/ Offical（官方 PyTorch pkl 模型，NVIDIA CUDA 自动加速）\n" +
        "附加能力（「高级」页，默认关闭）：帧去重（自研四阶级联判决：像素差+分块局部 / dHash / QR 分解 /\n" +
        "Farnebäck 稠密光流）/ 音频超分（AudioSR，48kHz 高带宽重建，FP16 性能模式与 FP32 完美模式）\n\n" +
        "========== 免责声明 ==========\n\n" +
        "1. 本软件以\"现状\"（AS-IS）提供，开发者不对其正确性、可靠性、完整性及适用性作任何明示或暗示保证。\n\n" +
        "2. 使用本软件及所调用第三方工具（FFmpeg/Real-ESRGAN/RIFE/AudioSR/NVEncC）产生的一切后果，包括但不限于：\n" +
        "   处理结果错误、文件损坏或丢失、硬件故障或损坏、系统崩溃或不稳定、数据泄露、时间与经济损失，\n" +
        "   均由使用者自行承担，开发者概不负责。\n\n" +
        "3. 软件运行时会调用系统全部 CPU/GPU 资源，可能导致设备高负载、发热、降频甚至崩溃，请自行评估风险。\n\n" +
        "4. 输出的视频/音频内容之版权、合法性与用途由使用者自行负责，请勿用于任何非法用途或侵犯他人权益的场景。\n\n" +
        "5. 第三方工具受其各自许可证约束（FFmpeg: LGPL/GPL；Real-ESRGAN: BSD-3；RIFE: 见其项目许可；\n" +
        "   AudioSR 及其 ONNX 权重: MIT；onnxruntime: MIT；NVEncC: MIT 等），使用前请自行查阅并遵守。\n\n" +
        "6. 开发者不承诺修复任何缺陷，不提供任何形式的售后服务与技术支持。\n\n" +
        "7. 使用本软件即表示已阅读并同意以上全部条款；不同意请立即停止使用并删除本软件。";

    private async void ShowDialog(string title, string content)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            // 长文本（如环境/显卡检测结果）用可滚动区域展示，等宽字体便于对齐
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    Text = content,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12
                }
            },
            CloseButtonText = "关闭",
            XamlRoot = RootFrame.XamlRoot
        };
        await dlg.ShowLocalizedAsync();
    }

    /// <summary>普通主题：移除亚克力材质，恢复纯色主题背景，并持久化主题选择。</summary>
    private void SetTheme(ElementTheme theme)
    {
        SystemBackdrop = null; // 亚克力是独立主题选项，切回普通主题时移除
        HideBackdropLayers();  // 图片背景层只在图片主题显示
        SetNavPaneTransparent(false);
        RootFrame.RequestedTheme = theme;
        Vm.SetThemeMode(theme switch
        {
            ElementTheme.Light => "light",
            ElementTheme.Dark => "dark",
            _ => "system"
        });
        Vm.Logger.Info($"主题切换为: {theme}");
    }

    /// <summary>亚克力主题：半透明毛玻璃背景，颜色/透明度跟随系统主题，并持久化主题选择。</summary>
    private void SetAcrylicTheme()
    {
        _acrylic ??= new DesktopAcrylicBackdrop();
        SystemBackdrop = _acrylic;
        HideBackdropLayers();
        SetNavPaneTransparent(false);
        // 亚克力颜色跟随系统主题，内容也切回跟随系统保持一致
        RootFrame.RequestedTheme = ElementTheme.Default;
        Vm.SetThemeMode("acrylic");
        // 系统关闭"透明度效果"时所有亚克力/云母材质都会回退为纯色背景
        if (!new Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled)
            Vm.Logger.Warn("系统已关闭「透明度效果」，亚克力回退为纯色背景。请到 设置→个性化→颜色→透明度效果 开启后重新选择亚克力。");
        Vm.Logger.Info("主题切换为: 亚克力");
    }

    /// <summary>图片背景主题：在亚克力之上再铺一层背景图，图片上覆盖应用内亚克力（模糊+着色）。
    /// 内容固定用深色主题，保证图片再怎么亮，卡片与文字都有足够对比度。</summary>
    private void SetImageBackdropTheme()
    {
        // 未选图时整窗只剩这层系统亚克力，不会出现"空背景"
        _acrylic ??= new DesktopAcrylicBackdrop();
        SystemBackdrop = _acrylic;
        RootFrame.RequestedTheme = ElementTheme.Dark;
        SetNavPaneTransparent(true); // 去掉 NavigationView 自带的内容层灰色底，否则导航栏下方会多出一块灰面板
        Vm.SetThemeMode("image");
        ApplyBackdropOpacity();
        ApplyBackdropImage();
        Vm.Logger.Info(string.IsNullOrWhiteSpace(Vm.BackgroundImage)
            ? "主题切换为: 图片背景（尚未选择背景图，当前等同亚克力）"
            : $"主题切换为: 图片背景（{Vm.BackgroundImage}）");
    }

    /// <summary>刷新背景图层：图片存在才显示图片（不存在则只有亚克力遮罩），遮罩始终显示保证文字可读。
    /// 路径是边输入边同步的，读不到就当没选图，不刷日志。</summary>
    private void ApplyBackdropImage()
    {
        var path = Vm.BackgroundImage;
        var loaded = false;
        if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
        {
            try
            {
                BackdropImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
                loaded = true;
            }
            catch (Exception ex)
            {
                Vm.Logger.Warn($"背景图片加载失败: {ex.Message}");
            }
        }

        BackdropImage.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
        BackdropOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>图片背景主题下把 NavigationView 自带的内容层/窗格底色改成透明。
    /// 官方模板在顶部模式下会给内容区铺一层 LayerFillColorDefaultBrush（半透明灰），
    /// 叠在背景图上就是导航栏下方那块"多余灰面板"；清掉后图片才能整片透出来。
    /// 只在图片主题生效，其它主题保持 WinUI 默认观感。</summary>
    private void SetNavPaneTransparent(bool on)
    {
        string[] keys =
        {
            "NavigationViewContentBackground",
            "NavigationViewTopPaneBackground",
            "NavigationViewDefaultPaneBackground",
            "NavigationViewExpandedPaneBackground"
        };
        foreach (var key in keys)
        {
            if (on) RootNav.Resources[key] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            else RootNav.Resources.Remove(key);
        }
    }

    /// <summary>把"亚克力浓度"拖动条的值（0-100）应用到覆盖层的 AcrylicBrush：
    /// TintOpacity = 浓度，FallbackColor 用同浓度 alpha（系统关闭透明度效果时按纯色半透明着色）。</summary>
    private void ApplyBackdropOpacity()
    {
        if (BackdropOverlay.Background is not AcrylicBrush brush) return;
        var percent = Math.Clamp(Vm.BackdropAcrylicPercent, 0, 100);
        brush.TintOpacity = percent / 100.0;
        brush.FallbackColor = Windows.UI.Color.FromArgb((byte)(percent * 255 / 100), 0x1C, 0x1C, 0x1C);
    }

    /// <summary>隐藏图片背景层（切到其它主题时调用）。</summary>
    private void HideBackdropLayers()
    {
        BackdropImage.Visibility = Visibility.Collapsed;
        BackdropOverlay.Visibility = Visibility.Collapsed;
    }

    // ===================== 版本检查 / 更新提示 =====================

    private const string UpdateBase = "https://update.baka233.top/Easy/";
    private const string UpdateVersionUrl = UpdateBase + "Version.txt";
    private const string UpdateLogUrl = UpdateBase + "Log.txt";
    private const string UpdateAppUrl = UpdateBase + "App.txt";

    /// <summary>启动后异步检查更新：服务器版本高于本地版本（config 的 Version）时弹窗提示。</summary>
    private async Task CheckUpdateAsync()
    {
        try
        {
            // 自测/自动化模式不弹更新提示，避免弹窗卡住自动化流程
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
                return;

            var server = await FetchTextAsync(UpdateVersionUrl);
            if (string.IsNullOrWhiteSpace(server)) return; // 网络失败/无版本信息 → 静默

            if (!IsNewerVersion(server, Vm.Version)) return; // 本地已是最新

            var log = await FetchTextAsync(UpdateLogUrl) ?? "（无法获取更新日志）";
            await ShowUpdateDialogAsync(server, log);
        }
        catch (Exception ex)
        {
            Vm.Logger.Warn($"检查更新失败: {ex.Message}");
        }
    }

    /// <summary>获取远程文本内容（5 秒超时，失败返回 null，不抛异常）</summary>
    private static async Task<string?> FetchTextAsync(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return (await client.GetStringAsync(url)).Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>版本号比较：server 高于 local 返回 true（支持 x.y.z 数字比较，解析失败按字符串比较）</summary>
    private static bool IsNewerVersion(string server, string local)
    {
        if (System.Version.TryParse(server, out var s) && System.Version.TryParse(local, out var l))
            return s > l;
        return string.Compare(server, local, StringComparison.Ordinal) > 0;
    }

    /// <summary>更新弹窗：多行只读滚动日志 + 是/否。点"是"→ 打开 App.txt 里的更新链接。</summary>
    private async Task ShowUpdateDialogAsync(string serverVersion, string log)
    {
        // 日志区用 ScrollViewer + TextBlock（与检测弹窗一致）：多行完整显示、可滚动、可选中复制
        var logBox = new ScrollViewer
        {
            MaxHeight = 300,
            MinWidth = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = log,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12
            }
        };

        var dlg = new ContentDialog
        {
            Title = "发现新版本",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"当前版本: v{Vm.Version}\n目标版本: v{serverVersion}\n\n以下是新版本更新信息:",
                        TextWrapping = TextWrapping.Wrap
                    },
                    logBox
                }
            },
            PrimaryButtonText = "是",
            CloseButtonText = "否",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootFrame.XamlRoot
        };

        if (await dlg.ShowLocalizedAsync() != ContentDialogResult.Primary) return;

        // 是 → 获取更新链接并用默认浏览器打开
        var url = await FetchTextAsync(UpdateAppUrl);
        if (string.IsNullOrWhiteSpace(url))
        {
            Vm.Logger.Warn("未能获取更新链接");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Vm.Logger.Warn($"打开更新链接失败: {ex.Message}");
        }
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetDpiForWindow(System.IntPtr hwnd);
}
