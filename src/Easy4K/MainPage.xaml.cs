using System.IO;
using Easy4K.Services;
using Easy4K.Services.CommandBuilders;
using Easy4K.Services.RemoteControl;
using Easy4K.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Easy4K;

/// <summary>主页 code-behind：路径选择、处理按钮。菜单栏已上移到 MainWindow。</summary>
public sealed partial class MainPage : Page
{
    private MainViewModel Vm => App.Services;

    /// <summary>上次已触发检测的输入视频路径（手填时避免同一路径重复检测）</summary>
    private string _lastVideoPath = "";

    /// <summary>帧文件夹参数弹窗是否已打开（防止重复弹出）</summary>
    private bool _frameParamsDialogOpen;

    /// <summary>按当前时段生成问候语：凌晨 / 早上 / 上午 / 中午 / 下午 / 傍晚 / 晚上好 + 当前 Windows 用户名。</summary>
    private static string BuildGreeting()
    {
        var period = DateTime.Now.Hour switch
        {
            >= 0 and < 5 => "凌晨好",
            >= 5 and < 9 => "早上好",
            >= 9 and < 11 => "上午好",
            >= 11 and < 13 => "中午好",
            >= 13 and < 17 => "下午好",
            >= 17 and < 19 => "傍晚好",
            _ => "晚上好"
        };

        var user = "";
        try { user = Environment.UserName?.Trim() ?? ""; } catch { }

        return string.IsNullOrEmpty(user)
            ? $"{period}，您今天想做些什么？"
            : $"{period}，{user}。您今天想做些什么？";
    }

    public MainPage()
    {
        InitializeComponent();

        GreetingText.Text = BuildGreeting();

        // 单文件「视频 HDR」只在 HDR 可用时出现：AMD 显卡、核显、非 RTX 的 N 卡一律不显示这一项
        // （NVEncC 的 NGX TrueHDR 需要 NVIDIA RTX 20/30/40/50 系列，摆出来也点不了）
        if (!Vm.IsHdrEnabled && SfKindCombo.Items.Count > 2)
        {
            SfKindCombo.Items.RemoveAt(2);
            if (Vm.SfKindIndex > 1) Vm.SfKindIndex = 0;
        }

        // 模型属性变化兜底：即使下拉框 SelectionChanged 未触发，也刷新倍率锁定
        Loaded += (_, _) =>
        {
            Vm.PropertyChanged += OnVmPropertyChanged;
            // 回到主页时重读孤儿队列：任务可能已被后台守护进程取走执行，或被执行进程移除
            Vm.ReloadOrphanTasks();
        };
        Unloaded += (_, _) => Vm.PropertyChanged -= OnVmPropertyChanged;
        // 初始填充「种类」与「模型」下拉框。
        // 「种类」必须对齐到配置里保存的补帧模型所属的族，否则选了 PyTorch 轨（RIFE / DRBA / GMFSS /
        // GIMM-VFI）重启后会掉回 NCNN 族，而回落的选中值还会把 DefaultIfModel 静默覆盖掉。
        var ifFamily = VfiCatalog.FamilyIndexOf(Vm.IfModel);
        // 显卡拦截的兜底：配置里存的是 PyTorch 轨模型（依赖 CUDA / cupy，只有 N 卡能跑）但本机不可用
        // （A 卡 / 核显 / 没装 python-torch），启动时直接退回 NCNN-RIFE，否则还原出来的选择一跑就崩。
        // 下拉框的事件此时被抑制，走不到 OnIfEngineSelectionChanged 里的那层拦截，所以这里单独判一次。
        if (EnforceGpuGating && VfiCatalog.IsTorchFamily(ifFamily) && !Vm.VfiTorchUsable)
            ifFamily = 0;
        _suppressIfEngineEvent = true;
        IfEngineCombo.ItemsSource = Vm.IfEngines.ToList();
        IfEngineCombo.SelectedIndex = ifFamily;
        Vm.IfEngineIndex = ifFamily;
        _suppressIfEngineEvent = false;
        ReloadIfModelCombo();

        // 超分「算法」下拉框：对齐到配置里保存的模型所属算法（「模型」下拉框由 Vm.SrModels 绑定自动跟随）
        _suppressSrEngineEvent = true;
        SrEngineCombo.ItemsSource = Vm.SrEngines.ToList();
        SrEngineCombo.SelectedIndex = Vm.SrEngineIndex;
        _suppressSrEngineEvent = false;
    }

    /// <summary>程序回填超分「算法」下拉框时抑制 SelectionChanged</summary>
    private bool _suppressSrEngineEvent;

    /// <summary>切换超分算法：VM 会顺带把倍率夹到该算法支持的档位并重建模型列表，
    /// 「模型」下拉框是绑定 Vm.SrModels，会自己跟着刷新。</summary>
    private void OnSrEngineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSrEngineEvent) return;
        var idx = SrEngineCombo.SelectedIndex;
        if (idx < 0) return;
        Vm.SwitchSrEngine(idx);
    }

    /// <summary>程序回填「种类」下拉框时抑制 SelectionChanged</summary>
    private bool _suppressIfEngineEvent;

    /// <summary>是否启用显卡拦截：非 NVIDIA 显卡选 PyTorch 轨（RIFE / DRBA / GMFSS / GIMM-VFI）
    /// 的种类或模型时弹窗提示并回退到 NCNN-RIFE。这些族依赖 CUDA / cupy，A 卡与核显跑不了。</summary>
    private const bool EnforceGpuGating = true;

    /// <summary>切换补帧种类（模型族）：PyTorch 那几个族依赖 CUDA/cupy，非 N 卡弹窗并退回 NCNN-RIFE。</summary>
    private async void OnIfEngineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressIfEngineEvent) return;
        var idx = IfEngineCombo.SelectedIndex;
        if (idx < 0) return;
        // 只有 NCNN-RIFE（索引 0）走 Vulkan；其余族都是 PyTorch 轨，需要 NVIDIA
        if (EnforceGpuGating && VfiCatalog.IsTorchFamily(idx) && !Vm.VfiTorchUsable)
        {
            await WarnTorchEngineNeedsNvidiaAsync();
            return;
        }
        Vm.IfEngineIndex = idx;
        ReloadIfModelCombo();
    }

    private async Task WarnTorchEngineNeedsNvidiaAsync()
    {
        _suppressIfEngineEvent = true;
        try
        {
            IfEngineCombo.SelectedIndex = 0;
            Vm.IfEngineIndex = 0;
        }
        finally { _suppressIfEngineEvent = false; }
        ReloadIfModelCombo();

        var dlg = new ContentDialog
        {
            Title = "这个补帧种类需要 NVIDIA 显卡",
            Content = "RIFE / DRBA / GMFSS / GIMM-VFI 上游只有 PyTorch 实现，依赖 CUDA / cupy，"
                    + "AMD 显卡与核显跑不了。\n\n"
                    + "Bro 去买老黄的 RTX 显卡就可以开启本补帧种类了哦！\n\n"
                    + "已退回 NCNN-RIFE（Vulkan，全显卡通用）。",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        await dlg.ShowLocalizedAsync();
    }

    /// <summary>程序回填单文件音频精度时抑制联动（避免自己触发自己的提示）</summary>
    private bool _sfAudioPrecisionSyncing;

    /// <summary>VM 属性兜底：模型变化 → 刷新倍率锁定。</summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Vm.IfModel))
            Vm.RefreshIfMultiplierLock(Vm.IfModel);
    }

    /// <summary>单文件「精度」下拉框：CPU 处理模式下不能选 fp16（FP16 权重包只能在 GPU 上跑）。
    /// 用控件事件而不是 VM 属性变更，确保用户手点一定能弹提示。</summary>
    private async void OnSfPrecisionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await Task.Yield(); // 等 x:Bind 把 SelectedItem 写进 VM 再判断
        if (_sfAudioPrecisionSyncing) return;
        if (!Vm.UseCpuProcessing) return;
        if (!string.Equals(Vm.SfAudioPrecision, "fp16", StringComparison.OrdinalIgnoreCase)) return;

        SetSfAudioPrecision("fp32");
        var dlg = new ContentDialog
        {
            Title = "CPU 处理模式下不能用 FP16",
            Content = "FP16 权重包只能在 GPU 上推理，已经开启「使用 CPU 处理所有模型」，所以单文件 AudioSR 也只能用 FP32。\n\n" +
                      "本次已把精度改回 fp32；想用 fp16 请先在高级页关掉 CPU 处理模式。",
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot
        };
        await dlg.ShowLocalizedAsync();
    }

    /// <summary>带抑制标记地改单文件音频精度（防止再触发一次提示）</summary>
    private void SetSfAudioPrecision(string value)
    {
        _sfAudioPrecisionSyncing = true;
        try { Vm.SfAudioPrecision = value; }
        finally { _sfAudioPrecisionSyncing = false; }
    }

    // ===================== 补帧模型选择（重写：整体重建下拉框，避开 ComboBox 绑定联动 0x80070490 闪退） =====================

    /// <summary>整体重建模型下拉框：先解绑事件、清空 ItemsSource/SelectedItem，再赋新列表，最后设合法选中值。
    /// 每次都是"从零开始"，ComboBox 内部不会残留旧选中状态，展开下拉时不再崩溃。</summary>
    private void ReloadIfModelCombo()
    {
        var models = Vm.GetIfModels();

        IfModelCombo.SelectionChanged -= OnIfModelSelectionChanged;
        IfModelCombo.ItemsSource = null;
        IfModelCombo.SelectedItem = null;
        IfModelCombo.ItemsSource = models.ToList();

        // 选中规则：优先保留当前已选模型（切页重建时用户的选择不丢）；
        // 其次配置里的默认模型；列表为空则保持占位符
        string selected = "";
        if (models.Count > 0)
        {
            selected = models.Contains(Vm.IfModel) ? Vm.IfModel
                : models.Contains(Vm.DefaultIfModel) ? Vm.DefaultIfModel
                : models[0];
        }
        IfModelCombo.SelectedItem = selected;
        Vm.IfModel = selected;
        Vm.RefreshIfMultiplierLock(selected); // 重建后显式刷新倍率锁定（v2/v3 老模型禁用倍率）
        IfModelCombo.SelectionChanged += OnIfModelSelectionChanged;
    }

    private void OnIfModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressIfModelEvent) return;
        var m = IfModelCombo.SelectedItem is string s ? s : "";
        // 兜底：PyTorch 轨版本（RIFE / DRBA / GMFSS / GIMM-VFI）依赖 CUDA/cupy，只有 NVIDIA 能跑。
        // 正常情况下「种类」那一级已经拦过，这里是防止别处直接改选中值绕过。
        if (EnforceGpuGating && VfiCatalog.IsTorchModel(m) && !Vm.VfiTorchUsable)
        {
            _ = WarnTorchModelNeedsNvidiaAsync(m);
            return;
        }
        if (m != Vm.IfModel) Vm.IfModel = m;
        Vm.RefreshIfMultiplierLock(m); // 切换模型后显式刷新倍率锁定
    }

    /// <summary>程序回填下拉框时抑制 SelectionChanged（弹窗回退会改选中值）</summary>
    private bool _suppressIfModelEvent;

    /// <summary>选了 PyTorch 轨版本但显卡不是 NVIDIA → 退回 NCNN-RIFE 并弹窗说明。</summary>
    private async Task WarnTorchModelNeedsNvidiaAsync(string tried)
    {
        var fallback = Vm.IfModel;
        if (string.IsNullOrEmpty(fallback) || VfiCatalog.IsTorchModel(fallback))
            fallback = Vm.GetIfModels().FirstOrDefault(x => !VfiCatalog.IsTorchModel(x)) ?? "";

        _suppressIfModelEvent = true;
        try
        {
            IfModelCombo.SelectedItem = fallback;
            Vm.IfModel = fallback;
            Vm.RefreshIfMultiplierLock(fallback);
        }
        finally { _suppressIfModelEvent = false; }

        var dlg = new ContentDialog
        {
            Title = "这个模型需要 NVIDIA 显卡",
            Content = $"{tried} 上游只有 PyTorch 版实现，依赖 CUDA / cupy，AMD 显卡与核显跑不了。\n\n"
                    + "Bro 去买老黄的 RTX 显卡就可以开启本补帧模型了哦！\n\n"
                    + "AMD / Intel 显卡请继续用 NCNN-RIFE 种类下的模型。\n"
                    + $"已回退到：{fallback}",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        await dlg.ShowLocalizedAsync();
    }

    // ===================== 文件 / 目录选择 =====================

    private async void OnBrowseInput(object sender, RoutedEventArgs e) => await PickVideoAsync();

    /// <summary>清除输入视频：清空后不会自动重选，用底部通知条告知结果并提供"重新选择视频"入口。</summary>
    private void OnClearInputVideo(object sender, RoutedEventArgs e)
    {
        Vm.ClearInputVideo();
        App.MainWindow?.ShowNotice("已清除输入视频", "检测结果与可用输出已同步清空，选好新视频即可再次开始处理。",
            "重新选择视频", async () => await PickVideoAsync());
    }

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
        // 目录校验与帧数统计都在 ImportFrameFolderAsync 内部后台执行（大目录同步统计会卡界面）
        if (Directory.Exists(path))
        {
            var prev = Vm.ExternalFramesDir;
            await Vm.ImportFrameFolderAsync(path);
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

            var result = await dialog.ShowLocalizedAsync();
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

    private async void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        // 遥控远端：输出目录在对方电脑上
        if (RemoteSession.IsActive)
        {
            var picked = await App.MainWindow.PickRemotePathAsync(folder: true, kind: "video", start: Vm.OutputRoot);
            if (!string.IsNullOrWhiteSpace(picked)) Vm.SetOutputRoot(picked); // 属性变化会自动下发到远端
            return;
        }
        await PickFolderAsync(p => Vm.SetOutputRoot(p));
    }

    private async void OnExtractAudio(object sender, RoutedEventArgs e)
    {
        await Vm.ExtractAudioAsync();
        // 提取结果只写日志，主页看不到 → 通知条告知成功/失败，成功时给一个直达临时目录的入口
        if (System.IO.File.Exists(Vm.ExtractedAudioPath))
            App.MainWindow?.ShowNotice("音频提取完成", $"已保存到 {Vm.ExtractedAudioPath}",
                "打开临时目录", () => App.MainWindow?.OpenTempFolder());
        else
            App.MainWindow?.ShowNotice("音频提取失败", "失败原因见「进行中」页的日志与 ffmpeg 输出。");
    }

    private async void OnImportFrameFolder(object sender, RoutedEventArgs e) => await ImportFrameFolderAsync();

    // ===================== 单文件超分（图片 / 音频） =====================

    /// <summary>选择单文件输入：按当前类型给不同的扩展名过滤</summary>
    private async void OnBrowseSfInput(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        if (Vm.SfIsImage)
        {
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".bmp");
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        }
        else if (Vm.SfIsVideoHdr)
        {
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".mkv");
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".avi");
            picker.FileTypeFilter.Add(".ts");
            picker.FileTypeFilter.Add(".m2ts");
            picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        }
        else
        {
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".flac");
            picker.FileTypeFilter.Add(".m4a");
            picker.FileTypeFilter.Add(".aac");
            picker.FileTypeFilter.Add(".ogg");
            picker.FileTypeFilter.Add(".wma");
            picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        }
        var file = await picker.PickSingleFileAsync();
        if (file is not null) Vm.SfInput = file.Path;
    }

    private void OnClearSfInput(object sender, RoutedEventArgs e) => Vm.SfInput = "";

    /// <summary>开始单文件超分：跑完只把结果汇总到卡片状态与通知条，详细输出在「进行中」页日志</summary>
    private async void OnStartSingleFile(object sender, RoutedEventArgs e)
    {
        await Vm.StartSingleFileAsync();
        if (Vm.SfStatus.StartsWith("完成：", StringComparison.Ordinal))
            App.MainWindow?.ShowNotice("单文件超分完成", Vm.SfStatus[3..]);
        else if (!string.IsNullOrEmpty(Vm.SfStatus))
            App.MainWindow?.ShowNotice("单文件超分未完成", Vm.SfStatus);
    }

    private void OnStopSingleFile(object sender, RoutedEventArgs e) => Vm.StopSingleFile();

    private async Task ImportFrameFolderAsync()
    {
        // 遥控远端：帧文件夹在对方电脑上，去对方的磁盘里挑（校验由远端做）
        if (RemoteSession.IsActive)
        {
            var remote = await App.MainWindow.PickRemotePathAsync(folder: true, kind: "video", start: Vm.ExternalFramesDir);
            if (string.IsNullOrWhiteSpace(remote)) return;
            var prevRemote = Vm.ExternalFramesDir;
            await Vm.ImportFrameFolderAsync(remote); // 内部转发到远端
            if (Vm.HasExternalFrames && Vm.ExternalFramesDir != prevRemote)
                await ShowFrameParamsDialogAsync();
            return;
        }

        var picker = new FolderPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.ViewMode = PickerViewMode.List;
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            var prev = Vm.ExternalFramesDir;
            await Vm.ImportFrameFolderAsync(folder.Path);
            if (Vm.HasExternalFrames && Vm.ExternalFramesDir != prev)
                await ShowFrameParamsDialogAsync();
        }
    }

    /// <summary>清除帧文件夹：流程会退回"先拆帧"，用通知条说明并提供重新导入入口。</summary>
    private void OnClearFrameFolder(object sender, RoutedEventArgs e)
    {
        Vm.ClearExternalFrames();
        App.MainWindow?.ShowNotice("已清除帧文件夹", "流程恢复为从输入视频拆帧，请确认输入视频已选择。",
            "重新导入帧文件夹", async () => await ImportFrameFolderAsync());
    }

    /// <summary>打开视频选择框是否已弹出（防止快捷键连击叠出多个选择框，关一个还剩一个）</summary>
    private bool _pickVideoOpen;

    private async Task PickVideoAsync()
    {
        if (_pickVideoOpen) return;
        _pickVideoOpen = true;
        try
        {
            // 遥控远端：去对方电脑的磁盘里挑（本机文件选择器看不到对方的文件）
            if (RemoteSession.IsActive)
            {
                var remote = await App.MainWindow.PickRemotePathAsync(folder: false, kind: "video", start: null);
                if (!string.IsNullOrWhiteSpace(remote))
                {
                    _lastVideoPath = remote;
                    await Vm.SetInputVideoAsync(remote);
                }
                return;
            }

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
        finally { _pickVideoOpen = false; }
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
        // 遥控远端：临时目录在对方电脑上，缓存判定也由对方做（本机 CheckTempCache 看不到远端目录）
        if (RemoteSession.IsActive)
        {
            await PickRemoteTempFolderAsync();
            return;
        }

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
            var r = await dlg.ShowLocalizedAsync();
            if (r == ContentDialogResult.Primary)
            {
                Vm.AcceptTempRoot(folder.Path); // 清空旧缓存并采用该目录
                return;
            }
            if (r == ContentDialogResult.Secondary) continue; // 重新选文件夹
            return; // 取消本次实例
        }
    }

    /// <summary>远端临时目录选择：选完让远端自己判缓存，有旧缓存则弹与本机一致的三选一。</summary>
    private async Task PickRemoteTempFolderAsync()
    {
        while (true)
        {
            var picked = await App.MainWindow.PickRemotePathAsync(folder: true, kind: "video", start: Vm.TempRoot);
            if (string.IsNullOrWhiteSpace(picked)) return;

            var (status, source) = await RemoteSession.Current!.SetTempRootAsync(picked);
            // 远端判定无缓存 / 缓存就是当前视频的 → 直接用
            if (status is not "mismatch") return;

            if (await ConfirmOverwriteRemoteCacheAsync(source, picked)) return;
        }
    }

    /// <summary>「覆盖老缓存」→ 让远端清空它自己的临时目录。返回 true 表示已处理完（调用方应结束流程）。</summary>
    private async Task<bool> ConfirmOverwriteRemoteCacheAsync(string source, string tempRoot)
    {
        var dlg = new ContentDialog
        {
            Title = "检测到旧缓存文件",
            Content = $"远端电脑的临时目录（{tempRoot}）缓存来源是：{source}，与当前输入视频不符。\n\n如何处理？",
            PrimaryButtonText = "覆盖老缓存",
            SecondaryButtonText = "更换至其他文件夹",
            CloseButtonText = "取消本次实例",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        var r = await dlg.ShowLocalizedAsync();
        if (r == ContentDialogResult.Primary)
        {
            try { await RemoteSession.Current!.CleanTempAsync(); }
            catch (Exception ex) { App.MainWindow?.ShowNotice("清理远端临时目录失败", ex.Message); }
            return true;
        }
        if (r == ContentDialogResult.Secondary) return false; // 回外层继续换文件夹
        return true;
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
        if (IfModelCombo.SelectedItem is string m && m != Vm.IfModel)
            Vm.IfModel = m;
        if (Vm.HasExternalFrames && (Vm.Video is null || !Vm.Video.IsValid))
        {
            await ShowFrameParamsDialogAsync();
            if (Vm.Video is null || !Vm.Video.IsValid) return; // 未确认参数，不启动
        }
        // 启动前确认（顺序在 VM 内编排）：先临时目录冲突弹窗引导，再 CPU 最终警告；任一被阻断则不启动
        if (!await Vm.ConfirmStartAsync()) return;

        // 处理前测试：输入有效且未勾选"跳过测试"时，先用 1 秒测试视频按当前勾选流程验证能否正常跑通。
        // 通过/被跳过 → 自动清理测试残留后开始正式处理；未通过 → 弹报错并取消本次处理。
        bool inputReady;
        if (Vm.Video is null || !Vm.Video.IsValid) inputReady = false;
        else if (!Vm.HasExternalFrames && !System.IO.File.Exists(Vm.InputVideo)) inputReady = false;
        else inputReady = true;

        if (inputReady && !Vm.SkipStartupSelfTest)
        {
            var (outcome, reason) = await Vm.RunPreProcessTestAsync();
            if (outcome == MainViewModel.PreProcessTestOutcome.Failed)
            {
                await ShowPreTestFailedAsync(reason);
                return; // 处理前测试未通过 → 弹报错后取消正式处理
            }
            if (outcome == MainViewModel.PreProcessTestOutcome.Canceled)
            {
                Vm.Logger.Warn("已取消本次处理（未运行正式流程）");
                return; // 测试被"停止"取消 → 取消本次处理
            }
            // Passed / Skipped → 继续正式处理
        }

        await Vm.StartAsync();
    }

    /// <summary>处理前测试未通过时的报错弹窗：提示原因并取消本次处理，关闭后回主页。</summary>
    private async Task ShowPreTestFailedAsync(string? reason)
    {
        Vm.Logger.Error("处理前测试未通过，已取消本次正式处理");
        var dlg = new ContentDialog
        {
            Title = "处理前测试未通过",
            Content = "正式处理开始前，软件先用 1 秒测试视频按你当前勾选的流程验证，但未能正常跑通，已取消本次正式处理。\n\n"
                      + (string.IsNullOrWhiteSpace(reason) ? "" : $"原因：{reason}\n\n")
                      + "请查看「进行中」页的命令输出与日志定位问题（例如所选模型不兼容当前显卡、工具未就绪），调整后再试。",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = App.MainWindow.WindowContentRoot
        };
        await dlg.ShowLocalizedAsync();
        App.MainWindow.HideNavAndGoHome(); // 失败后留在"进行中"页无意义，关窗即回主页
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
        if (await first.ShowLocalizedAsync() != ContentDialogResult.Primary) return;

        var second = new ContentDialog
        {
            Title = "再次确认",
            Content = "再次确认：即将删除临时目录中的全部中间产物（帧、音频、视频缓存），此操作不可撤销。",
            PrimaryButtonText = "确认清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close, // 第二次默认取消更安全
            XamlRoot = XamlRoot
        };
        if (await second.ShowLocalizedAsync() != ContentDialogResult.Primary) return;

        var msg = await Vm.CleanTempInAsync(Vm.TempRoot); // 后台清理，主页进度条显示进度
        Vm.RefreshCacheBlock(); // cache.json 已删除 → 解除"缓存与当前视频不符"的启动阻断
        App.MainWindow?.ShowNotice(
            msg.Contains("无法删除") ? "清理完成，但有项目未能删除" : "临时文件已清理",
            msg, "打开临时目录", () => App.MainWindow?.OpenTempFolder());
    }

    // ===================== 孤儿执行任务 =====================

    /// <summary>把当前这套设置加成一条孤儿执行任务：先选启动时间，再落盘并拉起后台守护进程。
    /// 未选输入时按钮本就是禁用的，这里再兜一层并说明原因（与「开始处理」同一套文案）。</summary>
    private async void OnAddOrphanTask(object sender, RoutedEventArgs e)
    {
        if (!Vm.CanStart)
        {
            var reason = string.IsNullOrWhiteSpace(Vm.CanStartReason)
                ? "请先点「浏览」选择输入视频（帧文件夹则点「导入帧文件夹」）。"
                : Vm.CanStartReason;
            var blocked = new ContentDialog
            {
                Title = "还不能添加孤儿执行任务",
                Content = reason,
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            await blocked.ShowLocalizedAsync();
            return;
        }

        // 三列滚轮（日 / 月 / 年）。WinUI 的 DatePicker 模板把「月」那格写成 TextAlignment="Left"、
        // 还带 9px 左内边距，月列又是 132 星宽（年/日只有 78 星），中文里那个「10月」就明显偏左，
        // 看着跟年/日对不齐。模板改不了，只能等控件加载完把三列宽度拉平、把月那格改成居中。
        var datePicker = new DatePicker { Date = DateTimeOffset.Now, HorizontalAlignment = HorizontalAlignment.Stretch };
        datePicker.Loaded += (_, _) => AlignDatePickerColumns(datePicker);
        var timePicker = new TimePicker
        {
            ClockIdentifier = "24HourClock",
            SelectedTime = NextQuarterHour(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "到点自动启动 Easy4K，按当前选项处理这支视频，跑完自动关闭；期间可继续排队其它任务。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(datePicker);
        panel.Children.Add(timePicker);

        var dlg = new ContentDialog
        {
            Title = "选择孤儿模式启动时间",
            Content = panel,
            PrimaryButtonText = "添加任务",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dlg.ShowLocalizedAsync() != ContentDialogResult.Primary) return;

        var when = datePicker.Date.Date + timePicker.Time;
        var today = DateTime.Now;

        // 只能排在今年：跨年的孤儿任务没人盯着，很容易变成「明年才响」的幽灵任务。
        if (when.Year < today.Year)
        {
            App.MainWindow?.ShowNotice("今夕是何年?",
                $"现在是 {today:yyyy} 年，孤儿任务只能排在 {today:yyyy} 年内，不能往回排到 {when:yyyy} 年。");
            return;
        }
        if (when.Year > today.Year)
        {
            App.MainWindow?.ShowNotice("并非永远哦",
                $"孤儿任务只能排在 {today:yyyy} 年内，不能排到 {when:yyyy} 年。");
            return;
        }
        if (when <= today)
        {
            App.MainWindow?.ShowNotice("启动时间已过去", "请选一个还没到的时间点，任务才能排进队列。");
            return;
        }

        // 与已排队任务的临时目录在同一块盘上时问一句：启动前要不要先清掉上一次的产物。
        // 两个任务虽然串行，但共用同一批中间文件，不清理就可能把上一个视频的帧当成自己的续跑。
        var conflict = Vm.OrphanTasks
            .Where(t => SameDrive(t.Settings.TempRoot, Vm.TempRoot))
            .OrderBy(t => t.RunAt)
            .FirstOrDefault();

        // 二次确认：点「添加任务」会真的拉起一个无界面的后台进程（Easy4K.Orphan.exe），
        // 用户需要知道它是什么、怎么取消，避免以为是普通提醒。
        var videoName = Vm.HasExternalFrames
            ? Path.GetFileName(Vm.ExternalFramesDir.TrimEnd('\\', '/'))
            : Path.GetFileName(Vm.InputVideo);
        var content = "点「确认添加」后，Easy4K 会创建一个静默后台进程（Easy4K.Orphan.exe：没有窗口、不在任务栏显示）。\n\n"
                    + $"启动时间：{when:yyyy-MM-dd HH:mm}\n"
                    + $"到点会自动启动 Easy4K 处理「{videoName}」，跑完再自动关闭。\n\n"
                    + "任务是一个接一个执行的：前一个没跑完，这个会等它结束再开始，不会同时开两个。\n\n"
                    + "期间可以继续排其它任务；不想执行了就在任务列表里点「删除」——最后一个任务删掉时，这个后台进程也会自动退出。";
        if (conflict is not null)
        {
            content += $"\n\n注意：已排队的「{conflict.DisplayName}」（{conflict.RunAt:yyyy-MM-dd HH:mm}）跟这条任务用的是同一块盘上的临时目录：\n"
                     + $"{Vm.TempRoot}\n"
                     + "两次执行会共用同一批中间产物。如果两次填的就是同一个临时目录，建议启动前先清一次，"
                     + "否则上一次没跑完的帧会被当成这次的续跑结果。要不要在这条任务启动时先强制清理？";
        }

        var confirm = new ContentDialog
        {
            Title = "确认添加孤儿执行任务",
            Content = content,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (conflict is null)
        {
            confirm.PrimaryButtonText = "确认添加";
            confirm.CloseButtonText = "取消";
        }
        else
        {
            confirm.PrimaryButtonText = "启动前强制清理";
            confirm.SecondaryButtonText = "不清理，直接启动";
            confirm.CloseButtonText = "取消";
        }

        var result = await confirm.ShowLocalizedAsync();
        if (result == ContentDialogResult.None) return; // 取消

        var cleanTemp = conflict is null || result == ContentDialogResult.Primary;
        var task = Vm.AddOrphanTask(when, cleanTemp);
        App.MainWindow?.ShowNotice("已添加孤儿执行任务",
            $"{task.RunAt:yyyy-MM-dd HH:mm:ss} 将自动启动 Easy4K 处理「{task.DisplayName}」。当前有 {Vm.OrphanTasks.Count} 个孤儿执行任务。");
    }

    /// <summary>两个目录是否在同一块磁盘上（盘符相同）。路径为空或非法时按「不同盘」处理，不打扰用户。</summary>
    private static bool SameDrive(string a, string b)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            var ra = Path.GetPathRoot(Path.GetFullPath(a));
            var rb = Path.GetPathRoot(Path.GetFullPath(b));
            return !string.IsNullOrEmpty(ra) && ra.Equals(rb, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>DatePicker 的内联模板把三列设成 78*/132*/78*，「月」那格还写死左对齐 + 9px 左内边距，
    /// 中文的「10月」因此不居中。这里等控件加载完直接改模板实例：三列等宽、月那格居中。</summary>
    private static void AlignDatePickerColumns(DatePicker picker)
    {
        if (FindByName<Grid>(picker, "FlyoutButtonContentGrid") is { } grid
            && grid.ColumnDefinitions.Count >= 5)
        {
            // 列顺序：日 / 间隔 / 月 / 间隔 / 年
            grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[4].Width = new GridLength(1, GridUnitType.Star);
        }

        if (FindByName<TextBlock>(picker, "MonthTextBlock") is { } month)
        {
            month.TextAlignment = TextAlignment.Center;
            month.Margin = new Thickness(0);
            month.Padding = new Thickness(0, 3, 0, 6); // 与年/日同样的 DatePickerFlyoutPresenterItemPadding
        }
    }

    /// <summary>在可视树里按名字找控件（模板块里的元素没有公开字段，只能这样拿）</summary>
    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit && hit.Name == name) return hit;
            if (FindByName<T>(child, name) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>删除一条孤儿任务（按钮 Tag 里带的就是任务 id）</summary>
    private void OnRemoveOrphanTask(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var task = Vm.OrphanTasks.FirstOrDefault(t => t.Id == id);
        if (task is null) return;
        Vm.RemoveOrphanTask(task);
        App.MainWindow?.ShowNotice("已删除孤儿执行任务",
            $"「{task.DisplayName}」不会再自动执行。当前有 {Vm.OrphanTasks.Count} 个孤儿执行任务。");
    }

    /// <summary>时间选择器的默认值：下一个 15 分钟整点（避免默认值就已经是过去时间）</summary>
    private static TimeSpan NextQuarterHour()
    {
        var now = DateTime.Now;
        var next = now.AddMinutes(15 - now.Minute % 15).AddSeconds(-now.Second);
        return next.TimeOfDay;
    }
}
