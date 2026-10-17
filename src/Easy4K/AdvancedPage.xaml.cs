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

        // 卡片右下角插图（帧去重 / 音频超分）：浅色主题用黑版、深色主题用白版（主题变化时换图）。
        // 构造期就发起解码（与页面构建/布局重叠，不等 Loaded），否则图片会在页面已经显示出来之后才"跳"出来。
        ApplyCardArt();
        ActualThemeChanged += (_, _) => ApplyCardArt();

        // 涡轮模式的磁盘检查：手动切换时触发；另外每次启动也检查一遍已开启的涡轮
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => _turboUiReady = true);
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(WarnTurboHddOnStartup);

        // 块大小滑块：绑定回填期间抑制事件，页面加载完成后再放行（避免构造期弹警告）
        _turboBlockSyncing = true;
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => _turboBlockSyncing = false);

        // 去重模式单选：程序回填时不写回，避免构造期覆盖配置
        _dedupSyncing = true;
        DedupPerfRb.IsChecked = Vm.DedupMode != "uhd";
        DedupUhdRb.IsChecked = Vm.DedupMode == "uhd";
        _dedupSyncing = false;

        // 音频超分精度单选：同样在程序回填时抑制事件
        SyncAudioSrFromVm();

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

    /// <summary>两张卡片右下角插图（帧去重 / 音频超分）统一按主题换图：浅色用黑版、深色用白版。
    /// 两张并行加载，谁先解完谁先显示（原来串行 await，第二张要等第一张解完才开始）。</summary>
    private void ApplyCardArt()
        => _ = Task.WhenAll(
            LoadThemeArtAsync(DedupArt, "DedupArt"),
            LoadThemeArtAsync(AudioSrArt, "AudioSrArt"));

    /// <summary>已解码的主题插图缓存（键 = 文件绝对路径）：同一张只解一次，
    /// 反复进页面、切主题都不用再解码。null 表示文件缺失。</summary>
    private static readonly Dictionary<string, Microsoft.UI.Xaml.Media.Imaging.BitmapImage?> ArtCache = new();

    /// <summary>给指定插图加载当前主题对应的那张（文件不存在就保持隐藏，仅装饰用）。
    /// 三个坑：
    /// ① 原图是 3840×2160 的 4K PNG，只显示 150px 宽。按原始尺寸解码一次要几十 MB 内存、几百毫秒，
    ///    正是"高级设置里图片延迟显示"的根因 —— 必须用 DecodePixelWidth 让解码器直接缩到显示尺寸。
    /// ② 必须 await 解码完成再释放文件流——fire-and-forget + using 会让流在解码中途被关掉，
    ///    表现为"第一次打开有概率不显示"。
    /// ③ 解码结果按路径缓存，避免每次进页面都重来一遍。</summary>
    private async Task LoadThemeArtAsync(Image target, string baseName)
    {
        var file = $"{baseName}_{(ActualTheme == ElementTheme.Light ? "black" : "white")}.png";
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);

        if (!ArtCache.TryGetValue(path, out var bmp))
        {
            try
            {
                if (!File.Exists(path))
                {
                    ArtCache[path] = null;
                }
                else
                {
                    // 150 逻辑像素显示，留 2 倍余量给高 DPI 屏；4K 原图直接按这个尺寸解码
                    var b = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
                    {
                        DecodePixelWidth = 300,
                        DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical
                    };
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        await b.SetSourceAsync(fs.AsRandomAccessStream());
                    ArtCache[path] = b;
                    bmp = b;
                }
            }
            catch
            {
                ArtCache[path] = null;
                bmp = null;
            }
        }

        if (bmp is null)
        {
            target.Visibility = Visibility.Collapsed;
            return;
        }
        target.Source = bmp;
        target.Visibility = Visibility.Visible;
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
                        "  第 1 阶 像素差粗筛：灰度化后先算全局平均绝对差 MAD 与变化像素比例（整幅画面都变了就直接保留）；" +
                        "再按块看局部——细块（约 1/32 边长）里任何一块的平均差超阈值、或粗块（4×4 细块）超阈值，都判为不同。\n" +
                        "    为什么必须分块：1080p 里一个只占画面 0.05% 的元素在动，全局 MAD 只有 0.02~0.1、变化像素占比不到 0.01%，" +
                        "会被全局平均彻底抹平（8×8 的 dHash 也看不出），于是这段真实运动会被误判成重复帧删掉、回填成定格。" +
                        "实测：全局几乎无变化的帧对里有 23% 存在块内平均差高达 24 的真实局部运动，而真正雷同的帧对块内平均差中位仅 0.7。\n" +
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

    // ===================== 音频超分（AudioSR） =====================

    /// <summary>程序回填音频超分精度单选时抑制事件</summary>
    private bool _audioSrSyncing;

    /// <summary>FP16 权重包是否可用（缺失时 FP16 单选置灰）</summary>
    private bool AudioSrFp16Available => Vm.Tools.AudioSrFp16Exists;

    /// <summary>按 VM 回填音频超分单选与提示（程序赋值，不触发写回）</summary>
    private void SyncAudioSrFromVm()
    {
        // 恢复配置时可能残留「CPU 模式 + FP16」的非法组合，先纠正再回填单选
        if (Vm.UseCpuProcessing && Vm.AudioSrPrecision == "fp16") Vm.AudioSrPrecision = "fp32";
        _audioSrSyncing = true;
        AudioSrFp16Rb.IsEnabled = AudioSrFp16Available;
        AudioSrFp16Rb.IsChecked = Vm.AudioSrPrecision == "fp16";
        AudioSrFp32Rb.IsChecked = Vm.AudioSrPrecision != "fp16";
        _audioSrSyncing = false;
        UpdateAudioSrHint();
    }

    /// <summary>卡片底部动态提示：脚本/权重包缺失时给出可执行说明。</summary>
    private void UpdateAudioSrHint()
    {
        AudioSrFp16Rb.IsEnabled = AudioSrFp16Available;
        if (!Vm.Tools.AudioSrScriptExists)
            AudioSrHint.Text = "未找到 Tools\\AudioSR\\audiosr_onnx.py，音频超分无法启用。";
        else if (!AudioSrFp16Available)
            AudioSrHint.Text = "未检测到 FP16 权重包（Tools\\AudioSR\\models-fp16），该选项不可用；请放入权重包或改用 FP32。";
        else if (Vm.UseCpuProcessing)
            AudioSrHint.Text = "已开启 CPU 处理模式：AudioSR 只能用 FP32 高精度（FP16 权重包需要 GPU，选了会自动改回 FP32）。";
        else
            AudioSrHint.Text = "精度跟随你的选择，后端降级不会改档：N 卡按 CUDA → DirectML → CPU 逐级尝试，其它显卡按 DirectML → CPU；三级都起不来才会报错。";
    }

    /// <summary>把音频超分精度强制回退到 FP32（单选与 VM 同步，且不触发写回事件）。</summary>
    private void RevertAudioSrToFp32()
    {
        _audioSrSyncing = true;
        AudioSrFp32Rb.IsChecked = true;
        _audioSrSyncing = false;
        if (Vm.AudioSrPrecision != "fp32") Vm.AudioSrPrecision = "fp32";
        UpdateAudioSrHint();
    }

    /// <summary>CPU 处理模式下选 FP16：FP16 权重包只能在 GPU 上推理，弹窗说明并改回 FP32。</summary>
    private async Task ShowAudioSrFp16BlockedAsync()
    {
        var dlg = new ContentDialog
        {
            Title = "CPU 处理模式下不能用 FP16",
            Content = "FP16 权重包只能在 GPU 上推理，已经开启「使用 CPU 处理所有模型」，所以音频超分精度只能是 FP32。\n\n" +
                      "本次已把精度改回 FP32 高精度；想用 FP16 请先关掉 CPU 处理模式。",
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot
        };
        await dlg.ShowLocalizedAsync();
    }

    /// <summary>精度单选：选 FP16 时校验权重包与 CPU 处理模式。</summary>
    private async void OnAudioSrPrecisionChecked(object sender, RoutedEventArgs e)
    {
        if (_audioSrSyncing) return;
        var wantFp16 = AudioSrFp16Rb.IsChecked == true;

        if (wantFp16 && Vm.UseCpuProcessing)
        {
            RevertAudioSrToFp32();
            await ShowAudioSrFp16BlockedAsync();
            return;
        }

        if (wantFp16 && !AudioSrFp16Available)
        {
            RevertAudioSrToFp32();
            return;
        }

        Vm.AudioSrPrecision = wantFp16 ? "fp16" : "fp32";
        UpdateAudioSrHint();
    }

    /// <summary>页面初始化回填开关状态时也会触发 Toggled，要等绑定完成后再允许弹磁盘提示</summary>
    private bool _turboUiReady;

    /// <summary>程序回填块大小输入框时抑制写回。初值必须为 true：InitializeComponent()
    /// 在构造函数体之前执行，XAML/绑定回填会在构造期就触发 ValueChanged。</summary>
    private bool _turboBlockSyncing = true;

    /// <summary>块大小越界警告本次运行只提示一次（拖动滑块不会反复打扰）。
    /// 用 static：语义是"每次启动最多提醒一次"，重进页面也不再重复弹。</summary>
    private static bool _turboBlockWarned;

    /// <summary>本次启动是否已就"涡轮 + 机械盘"提醒过。用 static = 每次启动提醒一次：
    /// 以前这条只在用户手动切换涡轮时才检查，配置里记住的涡轮开启状态在启动后不会再有提醒。</summary>
    private static bool _turboDiskWarnedThisLaunch;

    /// <summary>块大小调整：写回配置；明显越界时弹窗警告（本次运行只提醒一次）。</summary>
    private async void OnTurboBlockChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_turboBlockSyncing) return;

        // 页面尚未进入可视树时 XamlRoot 为 null，此时显示 ContentDialog 会抛异常
        if (XamlRoot is null) return;

        // 写回由 TwoWay 绑定负责（与「处理线程」滑块同一套），这里只做越界提醒
        var value = (int)Math.Round(e.NewValue);

        if (_turboBlockWarned) return;

        string? title = null;
        string? body = null;
        if (value < 64)
        {
            title = "涡轮模式：块太小";
            body = $"当前每块 {value} 帧。\n\n" +
                   "超分与补帧工具每处理一个块都要重新加载一次模型，还要按块多复制一轮帧文件，块太小时这些开销会超过并行带来的收益。\n\n" +
                   "建议 64 帧以上（默认 240）。";
        }
        else if (value > 600)
        {
            title = "涡轮模式：块太大";
            body = $"当前每块 {value} 帧。\n\n" +
                   "块太大时 CPU 与 GPU 重叠的时间会变短，涡轮模式的收益会变小；实测开了帧去重时 296 帧素材上块 240 比块 64 慢 17%。\n\n" +
                   "建议 64 ~ 600 帧。";
        }
        if (title is null) return;

        _turboBlockWarned = true;
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Text = body }
            },
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot
        };
        await dlg.ShowLocalizedAsync();
    }

    /// <summary>涡轮 + 机械盘 的统一警告弹窗（选「切换硬盘」返回 Primary）。</summary>
    private async Task<ContentDialogResult> ShowTurboHddWarningAsync()
    {
        var dlg = new ContentDialog
        {
            Title = "涡轮模式：检测到机械硬盘",
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Text =
                        "涡轮模式会让拆帧、去重判决、回填、超分、补帧同时读写临时目录，机械硬盘的随机读写能力撑不住这种吞吐，" +
                        "开启后很可能比普通模式更慢。\n\n" +
                        $"当前临时目录：\n{Vm.TempRoot}\n\n" +
                        "该目录位于机械硬盘（HDD）。建议先改到固态硬盘（SSD / NVMe）再开启。"
                }
            },
            PrimaryButtonText = "切换硬盘",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        return await dlg.ShowLocalizedAsync();
    }

    /// <summary>勾选涡轮模式时检查临时目录所在磁盘：机械盘撑不住多路并发读写，弹窗确认，选「否」自动关闭。</summary>
    private async void OnTurboToggled(object sender, RoutedEventArgs e)
    {
        if (!_turboUiReady) return;
        if (!TurboSwitch.IsOn) return;   // 关闭时不需要检查

        var kind = await Task.Run(() => StorageMediaDetector.DetectForPath(Vm.TempRoot));
        if (kind != StorageMediaDetector.MediaKind.Hdd) return;

        _turboDiskWarnedThisLaunch = true;
        var result = await ShowTurboHddWarningAsync();

        // 机械盘上不允许开启涡轮：两个按钮都不保持开启状态
        TurboSwitch.IsOn = false;
        Vm.TurboMode = false;

        // 选「切换硬盘」直接去挑一个固态盘上的目录，换完可以再开启涡轮
        if (result == ContentDialogResult.Primary && App.MainWindow is not null)
            await App.MainWindow.ChooseTempFolderAsync();
    }

    /// <summary>每次启动提醒一次：配置里记住的"涡轮已开启"也要检查临时目录磁盘。
    /// 以前这条只在用户手动切换涡轮时才检查，重启后涡轮仍是开的却再也不提醒了。</summary>
    private async void WarnTurboHddOnStartup()
    {
        if (_turboDiskWarnedThisLaunch || !Vm.TurboMode) return;
        _turboDiskWarnedThisLaunch = true;
        try
        {
            var kind = await Task.Run(() => StorageMediaDetector.DetectForPath(Vm.TempRoot));
            if (kind != StorageMediaDetector.MediaKind.Hdd) return;

            var result = await ShowTurboHddWarningAsync();
            TurboSwitch.IsOn = false;
            Vm.TurboMode = false;
            if (result == ContentDialogResult.Primary && App.MainWindow is not null)
                await App.MainWindow.ChooseTempFolderAsync();
        }
        catch { }
    }

    /// <summary>ⓘ 提示：AudioSR 是什么、两档精度的差别、为什么 FP16 不能用 CPU。</summary>
    private async void OnAudioSrInfoClick(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = "音频超分：两档精度",
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Text =
                        "用途：把低码率/带宽受限的音轨升采样成 48kHz 高带宽音频（AudioSR，扩散模型 + 声码器重建高频）。" +
                        "开启后音频不再由 ffmpeg 重采样，而是交给 AudioSR 负责。产物去向跟随主页的「将原音频合并进新视频」：" +
                        "勾选 → 超分结果替换视频音轨（视频时长、帧率、画面完全不变，不会出现音画对不上）；" +
                        "未勾选 → 不动视频，把超分结果单独输出成一份 48kHz WAV（此时只跑音频超分也能直接开始处理）。" +
                        "输入视频必须有音频流才生效。\n\n" +
                        "【处理链路】\n" +
                        "提取音轨 → 转 48kHz WAV → 分窗 DDIM(50 步, CFG 3.5) 推理 → " +
                        "mel/波形低频段回填（低音用回原音频，保证不跑调）→ 写出 48kHz 24bit WAV → " +
                        "按主页勾选决定去向：嵌入视频轨（PCM 24bit 48kHz）或单独输出该 WAV。\n\n" +
                        "【FP16 低精度性能模式】\n" +
                        "权重以半精度存放（1.26 GiB），体积约为 FP32 的一半、显存占用更小，实测速度比 FP32 快约 10%~20%，" +
                        "官方实测相对 FP32 的保真度为 59.4 dB SI-SDR（差异在 0.5 峰值下最大 0.0007，基本听不出）。\n" +
                        "官方建议 fp16 跑在显卡上：CPU 执行器支持的 fp16 算子极少，已有的那些往往也更慢。" +
                        "本软件不限制组合，精度严格跟随你的选择，不随后端降级自动改档。\n\n" +
                        "【FP32 高精度完美模式】\n" +
                        "官方原始精度（2.51 GiB），画质基准，CPU 与 GPU 都能跑。\n" +
                        "追求最高还原度、或显卡不支持 DirectML 时选这个。\n\n" +
                        "【后端选择与回退】\n" +
                        "N 卡按 CUDA → DirectML → CPU 逐级尝试，其它显卡按 DirectML → CPU；每级起不来才降到下一级，" +
                        "精度档不受影响。三级都起不来时才报错。\n\n" +
                        "【失败与回退】\n" +
                        "任何一步失败（权重包缺失、推理报错）都只影响音频：本次会自动回退成原音轨 + ffmpeg 重采样，" +
                        "视频处理结果照常产出，不会让整条流水线失败。"
                }
            },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        await dlg.ShowLocalizedAsync();
    }

    /// <summary>涡轮模式：把 CPU 侧与 GPU 侧的工作按块拆开同时开工。</summary>
    private async void OnTurboInfoClick(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = "涡轮模式：让所有阶段同时开工",
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Text =
                        "平时处理是「一段一段排队」：拆帧全部跑完才轮到超分，超分跑完才轮到补帧 —— 在不碰显卡的时段里，GPU 其实是空转的。\n\n" +
                        "【涡轮模式做了什么】\n" +
                        "把帧序列切成若干块，两条 GPU 流水同时推进：超分 worker 处理第 N+1 块时，补帧 worker 正在处理第 N 块，" +
                        "两个外部进程各占一份显存、真正并发；CPU 侧负责把下一块的帧拷进独立目录（外部工具只吃目录，没法只喂指定几帧），" +
                        "开了帧去重时，去重判决（dHash / QR / 光流，纯 CPU 运算）也放在这一侧按块流式跑，与 GPU 重叠。\n" +
                        "两条流水之间用容量 2 的有界队列衔接，队列满了上游会自动等一等（背压），不会把磁盘和显存同时吃爆。\n" +
                        "两条流水都按「处理线程」滑块设定的线程数跑（刻意不做「各分一半」：单个进程用满线程比两个进程各半高效得多，" +
                        "实测各分一半反而慢 24%）。\n\n" +
                        "【与普通模式的结果一致性】\n" +
                        "块边界的末帧在 RIFE 里没有「下一帧」，直接按块跑会退化成定格；这里会把下一块的首帧借来当「下一帧」，" +
                        "多算一份插值丢掉不用，因此块边界与整段处理完全一致。实测同一素材（296 帧 360p）：" +
                        "不开帧去重、性能模式去重、完美模式去重三组，涡轮（块 64 与块 240 两种切法）与串行的超分帧、补帧帧、" +
                        "回填展开帧逐帧 MD5 全部相同，成品时长、帧率、音频对齐与普通模式一致。\n\n" +
                        "【单卡的真实收益】\n" +
                        "超分与补帧都是 GPU 密集阶段，只有一块显卡时同时跑会互相抢算力，GPU 那部分总时长基本不变；" +
                        "涡轮真正能省下的是 CPU 侧的活（主要是帧去重判决）。实测（RX 580，296 帧 360p，线程 8）：\n" +
                        "· 不开帧去重：串行 58.6s / 涡轮 56.5s（块 64）→ 1.04×\n" +
                        "· 完美模式去重（判决 19s）：串行 82.8s / 涡轮 70.7s（块 64）→ 1.17×\n" +
                        "· 同样完美模式去重、块放大到 240：84.8s —— 块太大重叠变粗，反而更慢\n" +
                        "上限就是「1 + 去重耗时 ÷ 其余总耗时」：去重越慢（完美模式）、素材越短，比例越高；" +
                        "反过来一盘 7000 帧 1080p 的素材，去重只占百分之几，提速也就百分之几。\n" +
                        "结论：单卡上收益上限在 1.2× 上下，结果与普通模式逐帧一致，代价是多占一份临时盘空间。\n\n" +
                        "【当前不参与并行的部分】\n" +
                        "官方 PyTorch 补帧引擎（入参结构与 NCNN 不同）仍按逐阶段串行处理，日志里会写明「当前组合暂不支持块级流水」；" +
                        "音频链路不在流水线内。\n\n" +
                        "【为什么要求固态临时目录】\n" +
                        "块级流水会同时读写多个块目录：一边在读上一块的输入、一边在写超分结果、一边在写补帧结果。机械硬盘的随机读写会成为" +
                        "整条流水线的瓶颈，甚至拖到比不开涡轮还慢；固态盘上这些读写才能真正重叠。\n\n" +
                        "【保留的能力】\n" +
                        "任一块失败或产物数量与判决表对不上都会自动回退逐阶段串行重跑，本次任务不会被做废；去重帧搬不动时会改判为保留，" +
                        "保证判决表、磁盘与超分产物三者一致；安全帧率、显存降级重试照常生效。关闭涡轮即回到与原来完全一致的处理方式。"
                }
            },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        await dlg.ShowLocalizedAsync();
    }

    /// <summary>CPU 处理模式开启/关闭时，禁用/启用安全帧率、降低画质、GPU加速、硬件编码 CQ/QP 这些依赖显卡的选项。
    /// CPU 模式下合并一律走 libx265（连硬件编码器都不挑），留着 CQ/QP 可勾只会让人以为设了却没用。</summary>
    private void UpdateCpuDependentCheckBoxes()
    {
        var enabled = !Vm.UseCpuProcessing;
        SafeFrameRateCb.IsEnabled = enabled;
        LowerQualityCb.IsEnabled = enabled;
        GpuAccelerationCb.IsEnabled = enabled;

        // CPU 模式下合并一律走 libx265，CQ/QP 完全不生效：先把两个开关弹回未勾选（写回配置），
        // 再连同右侧数值一起置灰，避免「勾着却是灰的」这种看着别扭的状态。
        if (!enabled && !string.IsNullOrEmpty(Vm.HwQualityMode)) Vm.HwQualityMode = "";
        HwCqCb.IsEnabled = enabled;
        HwQpCb.IsEnabled = enabled;
        HwQualityBox.IsEnabled = enabled;
        // 标签与说明也一起变灰（CheckBox 的文字由主题自动变灰，这两个 TextBlock 不会）
        HwQualityLabel.Opacity = enabled ? 1.0 : 0.35;
        HwQualityHint.Opacity = enabled ? 0.6 : 0.25;
    }

    /// <summary>VM 属性兜底：CPU 处理模式变化时同步勾选状态与联动禁用。</summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Vm.AudioSrPrecision))
        {
            // 精度被程序侧改动（如权重包缺失回退）→ 单选跟着走
            if ((AudioSrFp16Rb.IsChecked == true) != (Vm.AudioSrPrecision == "fp16")) SyncAudioSrFromVm();
            else UpdateAudioSrHint();
            return;
        }
        if (e.PropertyName != nameof(Vm.UseCpuProcessing)) return;

        UpdateCpuDependentCheckBoxes();
        // 程序侧同步勾选状态（如用户拒绝弹窗回退取消勾选时联动；程序设值不弹窗）
        if (CpuProcessingCb.IsChecked != Vm.UseCpuProcessing)
            CpuProcessingCb.IsChecked = Vm.UseCpuProcessing;

        // CPU 处理模式与 FP16 互斥：程序侧变化（恢复配置、手机遥控）也要把精度拉回 FP32
        if (Vm.UseCpuProcessing && Vm.AudioSrPrecision == "fp16") RevertAudioSrToFp32();

        UpdateAudioSrHint();
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
                var hadFp16 = Vm.AudioSrPrecision == "fp16";
                Vm.UseCpuProcessing = true;
                // CPU 模式与 FP16 互斥：原来选了 FP16 就说明一下为什么被改回 FP32
                if (hadFp16) await ShowAudioSrFp16BlockedAsync();
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
