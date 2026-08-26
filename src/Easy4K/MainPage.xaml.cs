using System.IO;
using Easy4K.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Easy4K;

/// <summary>主页 code-behind：路径选择、处理按钮、快捷键。菜单栏已上移到 MainWindow。</summary>
public sealed partial class MainPage : Page
{
    private MainViewModel Vm => App.Services;

    /// <summary>上次已触发检测的输入视频路径（手填时避免同一路径重复检测）</summary>
    private string _lastVideoPath = "";

    /// <summary>帧文件夹参数弹窗是否已打开（防止重复弹出）</summary>
    private bool _frameParamsDialogOpen;

    /// <summary>上次确认的线程数（调到 1:8:8 及以上弹窗，拒绝则回退到该值）</summary>
    private int _lastConfirmedThreads;

    /// <summary>会话级：高线程警告只弹一次（确认/拒绝后均不再弹）</summary>
    private static bool _threadWarnShown;

    /// <summary>警告弹窗是否已打开（防止拖动期间重复弹）</summary>
    private bool _threadDialogOpen;

    public MainPage()
    {
        InitializeComponent();
        _lastConfirmedThreads = Vm.ThreadCount;
        // 初始填充模型下拉框（引擎变化后也走 ReloadIfModelCombo，由 OnIfEngineSelectionChanged 触发）
        ReloadIfModelCombo();
    }

    // ===================== 补帧模型选择（重写：整体重建下拉框，避开 ComboBox 绑定联动 0x80070490 闪退） =====================

    /// <summary>引擎（种类）切换 → 用新引擎的模型列表整体重建模型下拉框：
    /// 先解绑事件、清空 ItemsSource/SelectedItem，再赋新列表，最后设合法选中值。
    /// 每次都是"从零开始"，ComboBox 内部不会残留旧选中状态，展开下拉时不再崩溃。</summary>
    private void ReloadIfModelCombo()
    {
        var models = Vm.GetIfModelsForEngine(Vm.IfEngine);

        IfModelCombo.SelectionChanged -= OnIfModelSelectionChanged;
        IfModelCombo.ItemsSource = null;
        IfModelCombo.SelectedItem = null;
        IfModelCombo.ItemsSource = models.ToList();

        // 选中规则：优先保留当前已选模型（切页重建时用户的选择不丢）；
        // 其次 NCNN 优先配置里的默认模型；列表为空则保持占位符
        string selected = "";
        if (models.Count > 0)
        {
            selected = models.Contains(Vm.IfModel) ? Vm.IfModel
                : Vm.IfEngine == "NCNN" && models.Contains(Vm.DefaultIfModel) ? Vm.DefaultIfModel
                : models[0];
        }
        IfModelCombo.SelectedItem = selected;
        Vm.IfModel = selected;
        Vm.RefreshIfMultiplierLock(selected); // 重建后显式刷新倍率锁定（v2/v3 老模型禁用倍率）
        IfModelCombo.SelectionChanged += OnIfModelSelectionChanged;
    }

    private void OnIfEngineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // x:Bind 初始化也会触发一次；控件未就绪时跳过
        if (IfModelCombo is null) return;
        // 时序兜底：x:Bind TwoWay 回写可能晚于 SelectionChanged，
        // 先手动同步引擎，确保 ReloadIfModelCombo 用新引擎的模型列表重建
        if (IfEngineCombo.SelectedItem is string eng && eng != Vm.IfEngine)
            Vm.IfEngine = eng;
        ReloadIfModelCombo();
    }

    private void OnIfModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var m = IfModelCombo.SelectedItem is string s ? s : "";
        if (m != Vm.IfModel) Vm.IfModel = m;
        Vm.RefreshIfMultiplierLock(m); // 切换模型后显式刷新倍率锁定
    }

    /// <summary>种类旁的 Info 图标点击 → 详细讲解 NCNN / Offical 及兼容性</summary>
    private async void OnIfEngineInfoClick(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "模型种类：NCNN 与 Offical",
            Content = new TextBlock
            {
                Text = "NCNN：基于 ncnn 框架 + Vulkan 的 rife-ncnn-vulkan 补帧。" +
                       "GPU 加速、速度快，支持线程调节（-j）、安全帧率、GPU 错误自动降级重试等选项。" +
                       "模型为 ncnn 格式（flownet.param/.bin）。\n\n" +
                       "Offical：官方 RIFE PyTorch 模型（pkl 文件），使用软件随附的 Python + PyTorch 运行，" +
                       "包含官方全部模型（v4.6 / v4.8 / v4.9 / v4.15 / v4.18 / v4.22 / v4.26 / v4.26_heavy / 2.3 / rpr_v7_2.3）。" +
                       "画质更接近官方实现，但速度取决于 CPU/GPU 与 Python 性能；同样支持线程数调节、安全帧率与降低画质(-u, FP16)。\n\n" +
                       "兼容性说明：线程滑块、安全帧率、降低画质(-u) 在 NCNN 与 Offical 下均生效，" +
                       "其余流程（拆帧/超分/合并/音频/HDR）不受影响。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                MaxWidth = 480,
            },
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        };
        await dlg.ShowAsync();
    }

    /// <summary>线程滑块变化：调到 1:8:8（8）及以上时弹窗确认（会话内只弹一次），
    /// 弹窗前释放滑块指针捕获（相当于自动松开鼠标左键，避免弹窗期间滑块继续被拖动），拒绝则回退到上次确认值。</summary>
    private async void OnThreadSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var newVal = (int)e.NewValue;
        if (newVal >= 8 && newVal > _lastConfirmedThreads)
        {
            if (_threadWarnShown)
            {
                // 已警告过一次，不再弹窗，直接放行
                _lastConfirmedThreads = newVal;
                return;
            }
            if (_threadDialogOpen) return;
            _threadDialogOpen = true;

            // 松开鼠标左键：向系统发送真实 LEFTUP 事件，终止滑块拖动（ReleasePointerCaptures 不足以停止）
            ReleaseMouseLeftButton();
            if (sender is Slider slider) slider.ReleasePointerCaptures();

            var dlg = new ContentDialog
            {
                Title = "高线程警告",
                Content = $"将线程设置为 1:{newVal}:{newVal} 可能会导致 Vulkan 设备丢失或显卡内存溢出，您确定？",
                PrimaryButtonText = "是",
                CloseButtonText = "否",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
            var result = await dlg.ShowAsync();
            _threadDialogOpen = false;
            _threadWarnShown = true; // 本次会话不再重复弹出

            if (result != ContentDialogResult.Primary)
            {
                // 拒绝 → 回退到上次确认值（TwoWay 绑定会同步更新滑块）
                Vm.ThreadCount = _lastConfirmedThreads;
                return;
            }
        }
        _lastConfirmedThreads = newVal;
    }

    /// <summary>模拟系统鼠标左键抬起：终止正在进行的滑块拖动（弹窗拦截拖动时调用）。</summary>
    private static void ReleaseMouseLeftButton()
    {
        var input = new NativeInput
        {
            type = InputMouse,
            mi = new NativeMouseInput { dwFlags = MouseEventFLeftUp }
        };
        SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<NativeInput>());
    }

    private const uint InputMouse = 0;
    private const uint MouseEventFLeftUp = 0x0004;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public System.IntPtr dwExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint type;
        public NativeMouseInput mi;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, NativeInput[] pInputs, int cbSize);

    // ===================== 文件 / 目录选择 =====================

    private async void OnBrowseInput(object sender, RoutedEventArgs e) => await PickVideoAsync();
    private void OnClearInputVideo(object sender, RoutedEventArgs e) => Vm.ClearInputVideo();

    /// <summary>手填输入视频路径：路径变化时触发检测（空/无效路径只清空检测状态）。
    /// InputVideo 已由 TwoWay 绑定实时更新，这里只检测不写回，避免输入被打断。</summary>
    private async void OnInputVideoTextChanged(object sender, TextChangedEventArgs e)
    {
        var path = ((TextBox)sender).Text.Trim();
        if (path == _lastVideoPath) return;
        _lastVideoPath = path;
        await Vm.DetectVideoAsync(path);
    }

    /// <summary>手填帧文件夹路径：目录存在且含 PNG 帧才导入，输入过程中不误报</summary>
    private async void OnFrameFolderTextChanged(object sender, TextChangedEventArgs e)
    {
        var path = ((TextBox)sender).Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            Vm.ClearExternalFrames();
            return;
        }
        if (Directory.Exists(path) && Directory.GetFiles(path, "*.png").Length > 0)
        {
            var prev = Vm.ExternalFramesDir;
            Vm.ImportFrameFolder(path);
            if (Vm.HasExternalFrames && Vm.ExternalFramesDir != prev)
                await ShowFrameParamsDialogAsync();
        }
    }

    /// <summary>使用帧文件夹时弹出窗口填写参数：分辨率宽高 / 图片张数 / 原视频帧率。
    /// 无输入视频文件时，用这些参数替代 FFprobe 检测，供超分/补帧/合并使用。</summary>
    private async Task ShowFrameParamsDialogAsync()
    {
        if (_frameParamsDialogOpen) return;
        if (!string.IsNullOrEmpty(Vm.InputVideo)) return; // 已有真实视频，直接用其检测参数
        _frameParamsDialogOpen = true;
        try
        {
            // 图片张数预填：读取帧文件夹实际 PNG 数量
            var count = 0;
            var dir = Vm.ExternalFramesDir;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                count = Directory.GetFiles(dir, "*.png").Length;

            var widthBox = new TextBox { Header = "分辨率宽度", PlaceholderText = "如 1920" };
            var heightBox = new TextBox { Header = "分辨率高度", PlaceholderText = "如 1080" };
            var framesBox = new TextBox { Header = "图片张数", Text = count > 0 ? count.ToString() : "", PlaceholderText = "帧数" };
            var fpsBox = new TextBox { Header = "原视频帧率", PlaceholderText = "如 24 / 60 / 23.976" };

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(widthBox);
            panel.Children.Add(heightBox);
            panel.Children.Add(framesBox);
            panel.Children.Add(fpsBox);

            var dialog = new ContentDialog
            {
                Title = "帧文件夹参数",
                Content = panel,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false, // 参数未填完整时"确定"禁用
                XamlRoot = XamlRoot
            };

            // TextBox.TextChanged 每次按键即触发，实时刷新"确定"可用性。
            // （不用 NumberBox：其 Value 要失焦/回车才提交，输入过程中不会亮起确定）
            bool TryNum(TextBox b, out double v) => double.TryParse(b.Text.Trim(), out v);
            bool ParamsValid() => TryNum(widthBox, out var w) && w >= 1
                && TryNum(heightBox, out var h) && h >= 1
                && TryNum(framesBox, out var f) && f >= 1
                && TryNum(fpsBox, out var fps) && fps > 0;
            void UpdatePrimary() => dialog.IsPrimaryButtonEnabled = ParamsValid();
            widthBox.TextChanged += (_, _) => UpdatePrimary();
            heightBox.TextChanged += (_, _) => UpdatePrimary();
            framesBox.TextChanged += (_, _) => UpdatePrimary();
            fpsBox.TextChanged += (_, _) => UpdatePrimary();

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            // 确定按钮仅在参数有效时可点，此处直接应用
            TryNum(widthBox, out var wv); TryNum(heightBox, out var hv);
            TryNum(framesBox, out var fv); TryNum(fpsBox, out var fpsv);
            Vm.SetManualVideoInfo((int)Math.Round(wv), (int)Math.Round(hv), (long)Math.Round(fv), fpsv);
        }
        finally
        {
            _frameParamsDialogOpen = false;
        }
    }
    private async void OnBrowseTemp(object sender, RoutedEventArgs e) => await PickTempFolderAsync();
    private async void OnBrowseOutput(object sender, RoutedEventArgs e) => await PickFolderAsync(p => Vm.SetOutputRoot(p));

    private async void OnExtractAudio(object sender, RoutedEventArgs e) => await Vm.ExtractAudioAsync();

    private async void OnImportFrameFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.ViewMode = PickerViewMode.List;
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            var prev = Vm.ExternalFramesDir;
            Vm.ImportFrameFolder(folder.Path);
            if (Vm.HasExternalFrames && Vm.ExternalFramesDir != prev)
                await ShowFrameParamsDialogAsync();
        }
    }

    private void OnClearFrameFolder(object sender, RoutedEventArgs e) => Vm.ClearExternalFrames();

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
        if (file is not null)
        {
            // 先记录路径，避免绑定刷新触发 TextChanged 导致重复检测
            _lastVideoPath = file.Path;
            await Vm.SetInputVideoAsync(file.Path);
        }
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
            if (folder is null) return; // 用户取消选择

            var (status, source) = Vm.CheckTempCache(folder.Path);
            // 无缓存 或 缓存来源与当前视频一致 → 直接接受（处理时会覆盖旧帧）
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
                XamlRoot = XamlRoot
            };
            var r = await dlg.ShowAsync();
            if (r == ContentDialogResult.Primary)
            {
                Vm.AcceptTempRoot(folder.Path); // 清空旧缓存并采用该目录
                return;
            }
            if (r == ContentDialogResult.Secondary) continue; // 重新选文件夹
            return; // 取消本次实例
        }
    }

    private void InitializePicker(object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);
    }

    // ===================== 按钮 =====================

    /// <summary>启动处理：帧文件夹模式下参数未设置时先弹出参数窗口，确认后再启动</summary>
    private async Task StartWithFrameParamsAsync()
    {
        // 启动前强制把下拉框当前选中值同步到 VM，防御 UI/VM 不同步（"选了 A 实际跑 B"）
        if (IfEngineCombo.SelectedItem is string eng && eng != Vm.IfEngine)
            Vm.IfEngine = eng;
        if (IfModelCombo.SelectedItem is string m && m != Vm.IfModel)
            Vm.IfModel = m;
        if (Vm.HasExternalFrames && (Vm.Video is null || !Vm.Video.IsValid))
        {
            await ShowFrameParamsDialogAsync();
            if (Vm.Video is null || !Vm.Video.IsValid) return; // 未确认参数，不启动
        }
        await Vm.StartAsync();
    }

    private async void OnStart(object sender, RoutedEventArgs e) => await StartWithFrameParamsAsync();
    private void OnStop(object sender, RoutedEventArgs e) => Vm.Stop();
    private async void OnCleanTemp(object sender, RoutedEventArgs e) => await ConfirmCleanTempAsync();
    private void OnClearLog(object sender, RoutedEventArgs e) => Vm.ClearLog();
    private void OnCopyLog(object sender, RoutedEventArgs e) => Vm.CopyLog();

    /// <summary>清理临时文件：双重警告确认，全部确认才执行，结果走日志</summary>
    private async Task ConfirmCleanTempAsync()
    {
        var first = new ContentDialog
        {
            Title = "清理临时文件",
            Content = "确定要删除临时目录中的帧和中间产物吗？删除后无法恢复。",
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await first.ShowAsync() != ContentDialogResult.Primary) return;

        var second = new ContentDialog
        {
            Title = "再次确认",
            Content = "再次确认：即将删除临时目录中的全部中间产物（帧、音频、视频缓存），此操作不可撤销。",
            PrimaryButtonText = "确认清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close, // 第二次默认取消更安全
            XamlRoot = XamlRoot
        };
        if (await second.ShowAsync() != ContentDialogResult.Primary) return;

        await Vm.CleanTempInAsync(Vm.TempRoot); // 后台清理，主页进度条显示进度
    }

    // ===================== 快捷键 =====================

    private async void OnAcceleratorOpen(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await PickVideoAsync(); }
    private async void OnAcceleratorStart(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await StartWithFrameParamsAsync(); }
    private void OnAcceleratorStop(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; Vm.Stop(); }
    private void OnAcceleratorClearLog(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; Vm.ClearLog(); }
    private void OnAcceleratorHelp(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = ShowHelpAsync();
    }

    private async Task ShowHelpAsync()
    {
        var dlg = new ContentDialog
        {
            Title = "使用说明",
            Content = "1. 选择输入视频\n2. 自动检测分辨率/帧率\n3. 勾选要执行的处理（超分/补帧/合并/音频/HDR）\n" +
                      "4. 选择模型与倍率\n5. 点击开始处理\n\n处理流程：拆帧 → 超分 → 补帧 → 合并 → 嵌入音频 → HDR",
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        await dlg.ShowAsync();
    }
}
