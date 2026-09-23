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

        // 卡片右下角插图（帧去重 / 音频超分）：浅色主题用黑版、深色主题用白版（主题变化时换图）
        Loaded += (_, _) => ApplyCardArt();
        ActualThemeChanged += (_, _) => ApplyCardArt();

        // 涡轮模式的磁盘检查只在用户手动切换时触发：等首屏绑定回填完成后再放行
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => _turboUiReady = true);

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

    /// <summary>两张卡片右下角插图（帧去重 / 音频超分）统一按主题换图：浅色用黑版、深色用白版。</summary>
    private async void ApplyCardArt()
    {
        await LoadThemeArtAsync(DedupArt, "DedupArt");
        await LoadThemeArtAsync(AudioSrArt, "AudioSrArt");
    }

    /// <summary>给指定插图加载当前主题对应的那张（文件不存在就保持隐藏，仅装饰用）。
    /// 注意必须 await 解码完成再释放文件流——之前 fire-and-forget + using 会让流在解码中途被关掉，
    /// 表现为"第一次打开有概率不显示"。</summary>
    private async Task LoadThemeArtAsync(Image target, string baseName)
    {
        try
        {
            var file = $"{baseName}_{(ActualTheme == ElementTheme.Light ? "black" : "white")}.png";
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
            if (!File.Exists(path))
            {
                target.Visibility = Visibility.Collapsed;
                return;
            }
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                await bmp.SetSourceAsync(fs.AsRandomAccessStream());
            target.Source = bmp;
            target.Visibility = Visibility.Visible;
        }
        catch
        {
            target.Visibility = Visibility.Collapsed;
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

    /// <summary>FP16 冲突弹窗是否已打开（防止重复弹）</summary>
    private bool _audioSrDialogOpen;

    /// <summary>FP16 权重包是否可用（缺失时 FP16 单选置灰）</summary>
    private bool AudioSrFp16Available => Vm.Tools.AudioSrFp16Exists;

    /// <summary>按 VM 回填音频超分单选与提示（程序赋值，不触发写回）</summary>
    private void SyncAudioSrFromVm()
    {
        _audioSrSyncing = true;
        AudioSrFp16Rb.IsEnabled = AudioSrFp16Available;
        AudioSrFp16Rb.IsChecked = Vm.AudioSrPrecision == "fp16";
        AudioSrFp32Rb.IsChecked = Vm.AudioSrPrecision != "fp16";
        _audioSrSyncing = false;
        UpdateAudioSrHint();
    }

    /// <summary>卡片底部动态提示：脚本/权重包缺失、或 FP16 撞上 CPU 处理模式时给出可执行说明。</summary>
    private void UpdateAudioSrHint()
    {
        AudioSrFp16Rb.IsEnabled = AudioSrFp16Available;
        if (!Vm.Tools.AudioSrScriptExists)
            AudioSrHint.Text = "未找到 Tools\\AudioSR\\audiosr_onnx.py，音频超分无法启用。";
        else if (!AudioSrFp16Available)
            AudioSrHint.Text = "未检测到 FP16 权重包（Tools\\AudioSR\\models-fp16），该选项不可用；请放入权重包或改用 FP32。";
        else if (Vm.UseCpuProcessing)
            AudioSrHint.Text = "已开启 CPU 处理模式：FP16 在 CPU 上算子又少又慢，此状态下禁止选择 FP16，精度已锁定在 FP32。";
        else
            AudioSrHint.Text = "FP16 必须跑在显卡上（DirectML），若显卡不可用脚本会直接报错，不会退回 CPU；追求画质请用 FP32。";
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

    /// <summary>精度单选：选 FP16 时校验权重包与 CPU 处理模式；FP16 与 CPU 互斥，冲突时弹窗告知并禁止开启 FP16。</summary>
    private async void OnAudioSrPrecisionChecked(object sender, RoutedEventArgs e)
    {
        if (_audioSrSyncing) return;
        var wantFp16 = AudioSrFp16Rb.IsChecked == true;

        if (wantFp16 && !AudioSrFp16Available)
        {
            RevertAudioSrToFp32();
            return;
        }

        // 核心规则：FP16 严禁与 CPU 处理模式共用（CPU 的 fp16 算子又少又慢，官方明确不建议）
        if (wantFp16 && Vm.UseCpuProcessing)
        {
            if (_audioSrDialogOpen) return;
            _audioSrDialogOpen = true;
            try
            {
                var dlg = new ContentDialog
                {
                    Title = "FP16 不能与 CPU 处理模式共用",
                    Content = "FP16 低精度性能模式必须跑在显卡上。CPU 的 fp16 算子覆盖极少，官方明确不建议，" +
                              "实测会明显变慢且精度更差。\n\n" +
                              "当前已勾选「使用CPU处理所有模型」，因此禁止开启 FP16。\n\n" +
                              "要用 FP16：请先取消「使用CPU处理所有模型」；\n" +
                              "要继续用 CPU：请选 FP32 高精度完美模式。",
                    PrimaryButtonText = "改用 FP32",
                    CloseButtonText = "知道了",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                await dlg.ShowLocalizedAsync();
            }
            finally { _audioSrDialogOpen = false; }

            // 不论用户点哪个按钮都不允许 FP16 生效
            RevertAudioSrToFp32();
            return;
        }

        Vm.AudioSrPrecision = wantFp16 ? "fp16" : "fp32";
        UpdateAudioSrHint();
    }

    /// <summary>页面初始化回填开关状态时也会触发 Toggled，要等绑定完成后再允许弹磁盘提示</summary>
    private bool _turboUiReady;

    /// <summary>程序回填块大小输入框时抑制写回</summary>
    private bool _turboBlockSyncing;

    /// <summary>上一次已提示过的越界块大小（同一档位不重复弹窗，避免拖动时连弹）</summary>
    private int _lastWarnedBlock = -1;

    /// <summary>块大小调整：写回配置；明显越界时直接弹窗警告（同一档位只提醒一次）。</summary>
    private async void OnTurboBlockChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_turboBlockSyncing) return;

        var value = (int)Math.Round(e.NewValue);
        value = Math.Clamp(value, 32, 2000);
        Vm.TurboBlockFrames = value;

        if (value == _lastWarnedBlock) return;

        string? title = null;
        string? body = null;
        if (value < 120)
        {
            title = "涡轮模式：块太小";
            body = $"当前每块 {value} 帧。\n\n" +
                   "超分与补帧工具每处理一个块都要重新加载一次模型，块太小时加载开销可能超过并行带来的收益。\n\n" +
                   "建议 120 帧以上（默认 240）。";
        }
        else if (value > 600)
        {
            title = "涡轮模式：块太大";
            body = $"当前每块 {value} 帧。\n\n" +
                   "块太大时 CPU 与 GPU 重叠的时间会变短，涡轮模式的收益会变小；超过 1500 帧基本退化成串行处理。\n\n" +
                   "建议 120 ~ 600 帧。";
        }
        if (title is null) return;

        _lastWarnedBlock = value;
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

    /// <summary>勾选涡轮模式时检查临时目录所在磁盘：机械盘撑不住多路并发读写，弹窗确认，选「否」自动关闭。</summary>
    private async void OnTurboToggled(object sender, RoutedEventArgs e)
    {
        if (!_turboUiReady) return;
        if (!TurboSwitch.IsOn) return;   // 关闭时不需要检查

        var kind = await Task.Run(() => StorageMediaDetector.DetectForPath(Vm.TempRoot));
        if (kind != StorageMediaDetector.MediaKind.Hdd) return;

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
            PrimaryButtonText = "仍要开启",
            CloseButtonText = "否",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dlg.ShowLocalizedAsync() != ContentDialogResult.Primary)
        {
            TurboSwitch.IsOn = false;
            Vm.TurboMode = false;   // 选「否」自动关闭涡轮模式
        }
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
                        "提取音轨 → 转 48kHz WAV → 频谱判定真实带宽 → 官方算法低通 → 分窗 DDIM(50 步, CFG 3.5) 推理 → " +
                        "mel/波形低频段回填（低音用回原音频，保证不跑调）→ 写出 48kHz 24bit WAV → " +
                        "按主页勾选决定去向：嵌入视频轨（PCM 24bit 48kHz）或单独输出该 WAV。\n\n" +
                        "【FP16 低精度性能模式】\n" +
                        "权重以半精度存放（1.26 GiB），体积约为 FP32 的一半、显存占用更小，实测速度比 FP32 快约 10%~20%，" +
                        "官方实测相对 FP32 的保真度为 59.4 dB SI-SDR（差异在 0.5 峰值下最大 0.0007，基本听不出）。\n" +
                        "严禁使用 CPU：CPU 执行器支持的 fp16 算子极少，且已有的那些往往更慢，官方直接写明「CPU 请用 FP32 包」。" +
                        "因此只要勾选了「使用CPU处理所有模型」，这里就禁止选 FP16；如果显卡不可用，脚本会直接报错退出，不会偷偷退回 CPU。\n\n" +
                        "【FP32 高精度完美模式】\n" +
                        "官方原始精度（2.51 GiB），画质基准，CPU 与 GPU 都能跑；显卡不可用时自动回退 CPU 兜底（只是慢）。\n" +
                        "追求最高还原度、或显卡不支持 DirectML 时选这个。\n\n" +
                        "【失败与回退】\n" +
                        "任何一步失败（权重包缺失、显卡不可用、推理报错）都只影响音频：本次会自动回退成原音轨 + ffmpeg 重采样，" +
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
                        "平时处理是「一段一段排队」：拆帧全部跑完才轮到超分，超分跑完才轮到补帧 —— 在拆帧、判决、回填、合并这些" +
                        "不碰显卡的时段里，GPU 其实是空转的。\n\n" +
                        "【涡轮模式做了什么】\n" +
                        "把帧序列切成若干块，让两条流水线同时推进：\n" +
                        "· CPU 侧：拆帧 → 逐帧判决（帧去重）→ 回填展开\n" +
                        "· GPU 侧：超分 → 补帧\n" +
                        "两侧之间用有界队列衔接：GPU 侧处理第 N 块时，CPU 侧已经在判第 N+1 块，队列满了上游会自动等一等" +
                        "（背压），不会把内存吃爆。音频超分则作为独立链路全程并行，不与视频抢同一个队列。\n\n" +
                        "【为什么要求固态临时目录】\n" +
                        "同时开工意味着同一时刻会有多路读写：拆帧在写 PNG、超分在读 PNG 并写新 PNG、补帧再读写一轮、" +
                        "合并同时在编码。机械硬盘的随机读写会成为整条流水线的瓶颈，甚至拖到比不开涡轮还慢；\n" +
                        "固态盘上这些读写能真正重叠，收益才明显。\n\n" +
                        "【保留的能力】\n" +
                        "断点续传按块判断（块目录帧数够就跳过）、安全帧率与降级重试照常生效、缺陷帧被占用时仍会改判为保留，" +
                        "成片时长、帧率与音频对齐与普通模式完全一致。\n\n" +
                        "【什么时候别开】\n" +
                        "临时目录在机械盘或网络盘、显存吃紧（同时驻留超分与补帧进程）、或素材很短（并行还没热起来就结束了）。" +
                        "关闭涡轮即回到与原来完全一致的处理方式。"
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
        if (e.PropertyName == nameof(Vm.AudioSrPrecision))
        {
            // 精度被程序侧改动（如启动时的 FP16/CPU 冲突纠正）→ 单选跟着走
            if ((AudioSrFp16Rb.IsChecked == true) != (Vm.AudioSrPrecision == "fp16")) SyncAudioSrFromVm();
            else UpdateAudioSrHint();
            return;
        }
        if (e.PropertyName != nameof(Vm.UseCpuProcessing)) return;

        UpdateCpuDependentCheckBoxes();
        // 程序侧同步勾选状态（如用户拒绝弹窗回退取消勾选时联动；程序设值不弹窗）
        if (CpuProcessingCb.IsChecked != Vm.UseCpuProcessing)
            CpuProcessingCb.IsChecked = Vm.UseCpuProcessing;

        // CPU 与 FP16 互斥：CPU 被打开而当前是 FP16 时，强制切回 FP32（绝不让非法组合生效）
        if (Vm.UseCpuProcessing && Vm.AudioSrPrecision == "fp16") RevertAudioSrToFp32();
        else UpdateAudioSrHint();
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
                          "仅在显卡不可用（驱动故障/设备丢失）或不稳定时才建议开启。确定要继续吗？"
                          + (Vm.AudioSrPrecision == "fp16"
                              ? "\n\n注意：音频超分的 FP16 模式不能与 CPU 共用（CPU 的 fp16 算子又少又慢），"
                                + "开启 CPU 后会把音频超分自动切回 FP32 高精度完美模式。"
                              : ""),
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
                // FP16 与 CPU 互斥：一并把精度切回 FP32，避免出现非法组合
                if (Vm.AudioSrPrecision == "fp16") RevertAudioSrToFp32();
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
