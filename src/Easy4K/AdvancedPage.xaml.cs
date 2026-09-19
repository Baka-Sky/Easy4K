using Easy4K.Services;
using Easy4K.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Easy4K;

/// <summary>高级模式页：主界面「高级性能选项」整块移到这里。
/// 顶部 SelectorBar 在 标准模式 / 高级模式 之间切换（高亮由控件自带的动画滑动）。</summary>
public sealed partial class AdvancedPage : Page
{
    private MainViewModel Vm => App.Services;

    /// <summary>上次确认的线程数（调到 1:8:8 及以上弹窗，拒绝则回退到该值）</summary>
    private int _lastConfirmedThreads;

    /// <summary>会话级：高线程警告只弹一次（确认/拒绝后均不再弹）</summary>
    private static bool _threadWarnShown;

    /// <summary>警告弹窗是否已打开（防止拖动期间重复弹）</summary>
    private bool _threadDialogOpen;

    /// <summary>CPU 模式确认弹窗是否已打开（防止重复弹）</summary>
    private bool _cpuDialogOpen;

    /// <summary>构造/恢复阶段标志：从 config/VM 恢复 CPU 勾选时跳过弹窗（进页面、切模式都不弹），仅用户手动勾选才弹。</summary>
    private bool _cpuDialogFromLoad;

    public AdvancedPage()
    {
        _cpuDialogFromLoad = true;
        InitializeComponent();
        _cpuDialogFromLoad = false;

        // CPU 勾选状态手动同步自 VM（x:Bind 初始化时机不可靠，会让"读取 config 恢复勾选"误触发弹窗）
        _cpuDialogFromLoad = true;
        CpuProcessingCb.IsChecked = Vm.UseCpuProcessing;
        _cpuDialogFromLoad = false;

        _lastConfirmedThreads = Vm.ThreadCount;

        // 帧去重卡片右下角插图：浅色主题用黑版、深色主题用白版（主题变化时换图）
        Loaded += (_, _) => ApplyDedupArt();
        ActualThemeChanged += (_, _) => ApplyDedupArt();

        // 去重模式单选：程序回填时不写回，避免构造期覆盖配置
        _dedupSyncing = true;
        DedupPerfRb.IsChecked = Vm.DedupMode != "uhd";
        DedupUhdRb.IsChecked = Vm.DedupMode == "uhd";
        _dedupSyncing = false;

        Loaded += (_, _) =>
        {
            Vm.PropertyChanged += OnVmPropertyChanged;
            UpdateCpuDependentCheckBoxes();
        };
        Unloaded += (_, _) => Vm.PropertyChanged -= OnVmPropertyChanged;
    }

    /// <summary>选择 HTML 报告保存目录（与主页的临时目录选择同一套选择器）。</summary>
    private async void OnBrowseReportDir(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not null) await App.MainWindow.ChooseReportFolderAsync();
    }

    /// <summary>帧去重卡片右下角插图：浅色主题用黑版、深色主题用白版；找不到文件就保持隐藏（仅装饰）。
    /// 注意必须 await 解码完成再释放文件流——之前 fire-and-forget + using 会让流在解码中途被关掉，
    /// 表现为"第一次打开有概率不显示"。</summary>
    private async void ApplyDedupArt()
    {
        try
        {
            var file = ActualTheme == ElementTheme.Light ? "DedupArt_black.png" : "DedupArt_white.png";
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
            if (!File.Exists(path))
            {
                DedupArt.Visibility = Visibility.Collapsed;
                return;
            }
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                await bmp.SetSourceAsync(fs.AsRandomAccessStream());
            DedupArt.Source = bmp;
            DedupArt.Visibility = Visibility.Visible;
        }
        catch
        {
            DedupArt.Visibility = Visibility.Collapsed;
        }
    }

    // ===================== 帧去重 =====================

    /// <summary>程序回填去重模式单选时抑制事件</summary>
    private bool _dedupSyncing;

    private void OnDedupModeChecked(object sender, RoutedEventArgs e)
    {
        if (_dedupSyncing) return;
        Vm.DedupMode = DedupUhdRb.IsChecked == true ? "uhd" : "performance";
    }

    /// <summary>ⓘ 提示：去重两种模式的差别、判决阶数与阈值。</summary>
    private async void OnDedupInfoClick(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = "帧去重：两种模式",
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Text =
                        "用途：在超分/补帧前剔除与上一帧重复的画面，处理完再按索引表回填。重复帧本来就不需要重新算一遍，" +
                        "所以省下的是实打实的超分/补帧耗时；回填后成品时长、帧率、音频与原片完全一致。\n\n" +
                        "【Performance Mode 性能模式】\n" +
                        "两阶级联判决，速度最快：\n" +
                        "  第 1 阶 像素差粗筛：灰度化后算平均绝对差 MAD 与变化像素比例，两者都低于阈值才进入下一阶。\n" +
                        "  第 2 阶 dHash 细筛：缩到 9×8 灰度做水平差分，生成 64 位感知哈希，汉明距离 ≤ 5 判为重复。\n" +
                        "适用：动画（一拍二/三）、批量预处理。绝大多数素材用这个模式就够了。\n\n" +
                        "【UHD Mode 完美模式】\n" +
                        "在性能模式基础上再加两道精判（论文的四阶级联），用于老片修复、高价值素材：\n" +
                        "  第 3 阶 QR 分解精判：图像按 8×8 分块，每块做 QR 分解取 R 矩阵对角线当特征向量，" +
                        "再算全部块的 Minkowski 距离（p=2）并归一化成「特征值的 RMS 灰阶差」，大于阈值 1.0 灰阶判为不同" +
                        "——比哈希更细，能看出\"哈希相同但结构已经变了\"的情况。\n" +
                        "    （论文原作把距离除以特征值个数 K·B，1080p 下面是 25.9 万，结果被压到 1e-3 量级、" +
                        "阈值 0.05 等于形同虚设，故改为除以 √(K·B)：量纲就是灰阶差，与分辨率无关、阈值可实测标定。）\n" +
                        "  第 4 阶 Farnebäck 稠密光流终判：逐像素算出运动矢量，统计平均位移 MeanFlow 与方差 VarFlow，" +
                        "只有 MeanFlow ≤ 0.5 且 VarFlow ≤ 0.1 才算真重复——把\"整体平移/局部运动\"的运动帧救回来，避免误删。\n" +
                        "代价是耗时明显增加（光流是逐像素多项式拟合+金字塔迭代），建议只对确有价值的素材开启。\n\n" +
                        "【时序平滑（两种模式都生效）】\n" +
                        "孤立单帧被判为重复时撤销删除（连续重复长度不足 3 帧不作数），避免动画刻意的 1 帧顿帧/闪烁被吃掉。\n\n" +
                        "注意：判为重复的帧不是真的删除，而是不进超分/补帧，合并前会原样补回，因此不会改变成品的播放时序。"
                }
            },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        await dlg.ShowLocalizedAsync();
    }

    /// <summary>CPU 处理模式开启/关闭时，禁用/启用安全帧率、降低画质、GPU加速三个选项。</summary>
    private void UpdateCpuDependentCheckBoxes()
    {
        var enabled = !Vm.UseCpuProcessing;
        SafeFrameRateCb.IsEnabled = enabled;
        LowerQualityCb.IsEnabled = enabled;
        GpuAccelerationCb.IsEnabled = enabled;
    }

    /// <summary>VM 属性兜底：CPU 处理模式变化时同步勾选状态与联动禁用。</summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Vm.UseCpuProcessing)) return;

        UpdateCpuDependentCheckBoxes();
        // 程序侧同步勾选状态（如用户拒绝弹窗回退取消勾选时联动；程序设值不弹窗）
        if (CpuProcessingCb.IsChecked != Vm.UseCpuProcessing)
            CpuProcessingCb.IsChecked = Vm.UseCpuProcessing;
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
                XamlRoot = XamlRoot
            };
            var result = await dlg.ShowLocalizedAsync();
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

    /// <summary>勾选"使用CPU处理所有模型"时弹窗警告不建议开启；拒绝则回退取消勾选。
    /// _cpuDialogFromLoad=true（构造/恢复阶段）时不弹窗，仅用户手动勾选才弹。</summary>
    private async void OnUseCpuProcessingChecked(object sender, RoutedEventArgs e)
    {
        if (_cpuDialogFromLoad) return; // 构造/恢复阶段不弹窗
        if (_cpuDialogOpen) return;
        _cpuDialogOpen = true;
        try
        {
            var dlg = new ContentDialog
            {
                Title = "不建议开启 CPU 处理",
                Content = "开启后所有模型（超分/补帧）将使用 CPU 推理，处理速度会大幅下降（可能比 GPU 慢 10 倍以上）。\n\n" +
                          "仅在显卡不可用（驱动故障/设备丢失）或不稳定时才建议开启。确定要继续吗？",
                PrimaryButtonText = "继续开启",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close, // 默认取消更安全
                XamlRoot = XamlRoot
            };
            var result = await dlg.ShowLocalizedAsync();
            if (result == ContentDialogResult.Primary)
            {
                // 确认开启 → 写回 VM（触发联动：取消勾选安全帧率/降低画质/GPU加速并禁用，保存 config）
                Vm.UseCpuProcessing = true;
            }
            else
            {
                // 拒绝 → 回退取消勾选（VM 同步关闭，UI 经 PropertyChanged 同步取消勾选）
                Vm.UseCpuProcessing = false;
            }
        }
        finally
        {
            _cpuDialogOpen = false;
        }
    }

    /// <summary>用户取消勾选"使用CPU处理"→ 写回 VM 关闭（触发联动：恢复三个选项可用，保存 config）。</summary>
    private void OnUseCpuProcessingUnchecked(object sender, RoutedEventArgs e)
    {
        if (_cpuDialogFromLoad) return; // 构造/恢复阶段程序赋值不处理
        if (Vm.UseCpuProcessing) Vm.UseCpuProcessing = false;
    }
}
