using Easy4K.Models;
using Easy4K.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace Easy4K;

/// <summary>处理进行中页面：总进度 + 实时预览 + 命令输出 + CPU/GPU 压力。</summary>
public sealed partial class ProgressPage : Page
{
    private MainViewModel Vm => App.Services;
    private string _lastPreviewPath = "";    // 右：筛选帧
    /// <summary>中图（对比帧）当前显示的路径，用于去重时不重复解码</summary>
    private string _lastComparePath = "";
    /// <summary>左图（判决帧）当前显示的路径</summary>
    private string _lastVerdictPath = "";
    /// <summary>三联图各自的解码序号：慢解码完成后若已有更新的帧，直接丢弃，避免旧帧覆盖新帧</summary>
    private int _mainSeq;
    private int _compareSeq;
    private int _verdictSeq;
    /// <summary>是否处于"判决帧 + 对比帧 + 筛选帧"三联布局（仅帧去重阶段）</summary>
    private bool _compareMode;
    /// <summary>三联图各自的上次解码时间（分别节流：共用同一条时间戳会让后加载的图被跳掉）</summary>
    private long _lastMainTick;
    private long _lastCompareTick;
    private long _lastVerdictTick;
    /// <summary>预览解码宽度上限：4K 帧整幅解码既慢又占内存，预览框用不到这个分辨率</summary>
    private const int PreviewDecodeWidth = 960;
    /// <summary>是否已订阅 ViewModel/Logger 事件（Loaded 可能多次触发，重复订阅会让每条日志/进度出现两次）</summary>
    private bool _subscribed;

    public ProgressPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 回填订阅前已产生的日志（重新启动时"开始处理/命令"等开头几行可能在订阅前写入，避免丢失）
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var entry in Vm.Logger.LogEntries)
                sb.AppendLine(entry.LogText);
            CommandLogBox.Text = sb.ToString().TrimEnd('\r', '\n');
            if (CommandLogBox.Text.Length > 0) DispatcherQueue.TryEnqueue(ScrollLogToBottom);
        }
        catch { }
        if (!_subscribed)
        {
            Vm.ProgressChanged += OnProgress;
            Vm.CleanRequested += OnCleanRequested;
            Vm.Logger.EntryAdded += OnLogEntryAdded;
            Vm.Logger.Cleared += OnLogCleared;
            Vm.PropertyChanged += OnVmPropertyChanged;
            _subscribed = true;
        }
        SelfTestSkipBtn.Visibility = Vm.IsStartupSelfTest ? Visibility.Visible : Visibility.Collapsed;
        UpdatePauseButton();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        Vm.ProgressChanged -= OnProgress;
        Vm.CleanRequested -= OnCleanRequested;
        Vm.Logger.EntryAdded -= OnLogEntryAdded;
        Vm.Logger.Cleared -= OnLogCleared;
        Vm.PropertyChanged -= OnVmPropertyChanged;
        _subscribed = false;
    }

    /// <summary>日志被清空（含 MainPage 的 Ctrl+L）→ 同步清空本页日志框，避免"日志已清但界面还留着"</summary>
    private void OnLogCleared()
    {
        DispatcherQueue.TryEnqueue(() => CommandLogBox.Text = "");
    }

    /// <summary>清空预览并让在途解码作废（同时释放 Image 对帧文件的引用，方便清理临时文件）。</summary>
    private void ClearPreview()
    {
        PreviewImage.Source = null;
        _lastPreviewPath = "";
        _mainSeq++;
        _compareSeq++;
        _verdictSeq++;
        SetCompareMode(false);
        PreviewHint.Visibility = Visibility.Visible;
    }

    /// <summary>清理临时文件时清空预览图，释放帧文件句柄（否则文件被锁删不掉）</summary>
    private void OnCleanRequested()
    {
        DispatcherQueue.TryEnqueue(ClearPreview);
    }

    /// <summary>暂停状态变化（确认挂起/恢复）→ 更新按钮文案。
    /// 预览阻断（合并/HDR/音频阶段）→ 清空当前预览图并释放帧文件句柄。</summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Vm.IsPaused) || e.PropertyName == nameof(Vm.IsProcessing))
            UpdatePauseButton();
        if (e.PropertyName == nameof(Vm.ShowPreview))
            UpdatePreviewVisibility();
        if (e.PropertyName == nameof(Vm.PreviewBlocked) && Vm.PreviewBlocked)
        {
            // 进入不产帧的阶段：清空旧画面，避免误导（同时释放 Image 对帧文件的引用）
            ClearPreview();
        }
        if (e.PropertyName == nameof(Vm.IsStartupSelfTest))
            SelfTestSkipBtn.Visibility = Vm.IsStartupSelfTest ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>预览开关变化时更新提示文字可见性（PreviewImage 的可见性由 x:Bind 控制）。</summary>
    private void UpdatePreviewVisibility()
    {
        if (!Vm.ShowPreview)
        {
            PreviewHint.Visibility = Visibility.Visible;
        }
        else
        {
            PreviewHint.Visibility = string.IsNullOrEmpty(_lastPreviewPath) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdatePauseButton()
    {
        PauseBtn.Content = Vm.IsPaused ? "继续" : "暂停";
        PauseBtn.IsEnabled = Vm.IsProcessing;
    }

    /// <summary>新日志直接追加到 TextBox 文本（不闪烁），自动滚底，超上限裁剪最早行</summary>
    private void OnLogEntryAdded(LogEntry entry)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var newText = CommandLogBox.Text.Length > 0
                    ? CommandLogBox.Text + "\r\n" + entry.LogText
                    : entry.LogText;

                const int maxLines = 200;
                var lines = newText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                if (lines.Length > maxLines)
                    newText = string.Join("\r\n", lines, lines.Length - maxLines, maxLines);

                CommandLogBox.Text = newText;
                // 布局更新后滚到底部（只读 TextBox 用内部 ScrollViewer 滚动）
                DispatcherQueue.TryEnqueue(ScrollLogToBottom);
            }
            catch { }
        });
    }

    /// <summary>把命令日志滚动到底部</summary>
    private void ScrollLogToBottom()
    {
        try
        {
            var scroller = FindScrollViewer(CommandLogBox);
            if (scroller is not null)
                scroller.ChangeView(null, scroller.ScrollableHeight, null, disableAnimation: true);
        }
        catch { }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var nested = FindScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void OnProgress(ProcessProgress p)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // 关闭图片预览时不再加载帧
            if (!Vm.ShowPreview) return;

            // 帧去重阶段：左「判决帧」+ 中「对比帧」+ 右「筛选帧」三联（一进入去重就摆好布局）；其他阶段只有单图
            if (p.FrameCompareMode != _compareMode) SetCompareMode(p.FrameCompareMode);

            if (p.FrameCompareMode)
            {
                // 标签带上帧号：左右两图停在哪儿一目了然，中间与右侧则会一路前移
                VerdictLabel.Text = p.VerdictFrameIndex > 0 ? $"判决帧 · 第 {p.VerdictFrameIndex} 帧" : "判决帧";
                CompareLabel.Text = p.CompareFrameIndex > 0 ? $"对比帧 · 第 {p.CompareFrameIndex} 帧" : "对比帧";
                PreviewCaption.Text = p.Current > 0 ? $"筛选帧 · 第 {p.Current} 帧" : "筛选帧";
            }

            // 左：判决帧（只在判出重复时更新，其余时间保持不动）
            if (p.FrameCompareMode && !string.IsNullOrEmpty(p.VerdictFramePath) && p.VerdictFramePath != _lastVerdictPath)
            {
                _lastVerdictPath = p.VerdictFramePath;
                LoadPreviewAsync(p.VerdictFramePath, PreviewTarget.Verdict);
            }
            // 中：对比帧（与筛选帧作比较的那一帧，随判决逐帧前移）
            if (p.FrameCompareMode && !string.IsNullOrEmpty(p.CompareFramePath) && p.CompareFramePath != _lastComparePath)
            {
                _lastComparePath = p.CompareFramePath;
                LoadPreviewAsync(p.CompareFramePath, PreviewTarget.Compare);
            }
            // 右：筛选帧（当前正在判决的帧）
            if (!string.IsNullOrEmpty(p.LatestFramePath) && p.LatestFramePath != _lastPreviewPath)
            {
                _lastPreviewPath = p.LatestFramePath;
                LoadPreviewAsync(p.LatestFramePath, PreviewTarget.Main);
            }
        });
    }

    /// <summary>预览目标：判决帧（左）/ 对比帧（中）/ 筛选帧（右）。</summary>
    private enum PreviewTarget { Verdict, Compare, Main }

    /// <summary>切换「单图 / 三联对比」布局（三联仅用于帧去重）：判决帧、对比帧各占三分之一。</summary>
    private void SetCompareMode(bool on)
    {
        _compareMode = on;
        var star = new GridLength(1, GridUnitType.Star);
        var none = new GridLength(0);
        VerdictColumn.Width = on ? star : none;
        CompareColumn.Width = on ? star : none;
        VerdictPane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ComparePane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PreviewCaption.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on)
        {
            VerdictImage.Source = null;   // 退出三联时释放左、中两张图
            CompareImage.Source = null;
            _lastVerdictPath = "";
            _lastComparePath = "";
            VerdictLabel.Text = "判决帧";
            CompareLabel.Text = "对比帧";
            PreviewCaption.Text = "筛选帧";
        }
    }

    /// <summary>加载预览帧。要点：
    /// ① 读文件在后台线程（Task.Run），UI 线程不碰磁盘 IO；
    /// ② 期间不持有帧文件句柄（先整幅读进内存），否则帧去重搬移这些 PNG 时会撞"文件被占用"；
    /// ③ 解码限制宽度（4K 帧整幅解码既慢又吃内存，预览框用不到）；
    /// ④ 三联图各自节流 + 过期丢弃：慢解码不会覆盖新帧，也不会互相挤掉。</summary>
    private async void LoadPreviewAsync(string path, PreviewTarget target)
    {
        var seq = target switch
        {
            PreviewTarget.Verdict => ++_verdictSeq,
            PreviewTarget.Compare => ++_compareSeq,
            _ => ++_mainSeq
        };
        try
        {
            var now = Environment.TickCount64;
            if (target == PreviewTarget.Verdict)
            {
                if (now - _lastVerdictTick < 100) return;
                _lastVerdictTick = now;
            }
            else if (target == PreviewTarget.Compare)
            {
                if (now - _lastCompareTick < 100) return;
                _lastCompareTick = now;
            }
            else
            {
                if (now - _lastMainTick < 100) return;
                _lastMainTick = now;
            }

            byte[] bytes;
            try { bytes = await Task.Run(() => File.ReadAllBytes(path)); }
            catch { return; }   // 帧可能刚被搬走或正在写入

            // 注意：BitmapImage 是 UI 对象（DependencyObject），必须在 UI 线程创建、在 UI 线程调 SetSourceAsync，
            // 放到后台线程会直接挂不上图。它本身是异步解码（解码在内部后台线程完成），不会阻塞界面。
            var bmp = new BitmapImage { DecodePixelWidth = PreviewDecodeWidth };
            using (var ms = new InMemoryRandomAccessStream())
            {
                await ms.WriteAsync(bytes.AsBuffer());
                ms.Seek(0);
                await bmp.SetSourceAsync(ms);
            }

            if (!Vm.ShowPreview) return;
            switch (target)
            {
                case PreviewTarget.Verdict:
                    if (seq != _verdictSeq || !_compareMode) return;
                    VerdictImage.Source = bmp;
                    break;
                case PreviewTarget.Compare:
                    if (seq != _compareSeq || !_compareMode) return;
                    CompareImage.Source = bmp;
                    break;
                default:
                    if (seq != _mainSeq) return;
                    PreviewImage.Source = bmp;
                    PreviewHint.Visibility = Visibility.Collapsed;
                    break;
            }
        }
        catch (Exception ex)
        {
            // 预览失败不影响处理流程（帧可能刚好被搬走/删除）；出问题时便于从调试输出定位
            System.Diagnostics.Debug.WriteLine($"[Preview] 帧加载失败 {path}: {ex.Message}");
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => Vm.Stop();

    /// <summary>复制日志：剪贴板操作没有可见反馈 → 通知条确认条数，并给一个顺手清空的入口。</summary>
    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        var count = Vm.Logs.Count;
        Vm.CopyLog();
        if (count > 0)
            App.MainWindow?.ShowNotice("日志已复制到剪贴板", $"共 {count} 条，可直接粘贴到问题反馈里。",
                "清空日志", () => { Vm.ClearLog(); CommandLogBox.Text = ""; });
        else
            App.MainWindow?.ShowNotice("日志为空", "当前没有可复制的内容。");
    }

    private void OnClearLog(object sender, RoutedEventArgs e) { Vm.ClearLog(); CommandLogBox.Text = ""; }

    /// <summary>跳过处理前测试（仅测试阶段可见）：停止测试，但随后仍会开始正式处理。</summary>
    private void OnSkipSelfTest(object sender, RoutedEventArgs e)
    {
        Vm.RequestSkipPreTest();
        App.MainWindow?.ShowNotice("已请求跳过测试", "当前测试会在几秒内停止，随后直接按当前勾选开始正式处理。");
    }

    /// <summary>暂停/继续切换（立即挂起或恢复当前工具进程）</summary>
    private void OnPauseToggle(object sender, RoutedEventArgs e)
    {
        if (Vm.IsPaused) Vm.ResumeProcessing(); else Vm.PauseProcessing();
    }
}
