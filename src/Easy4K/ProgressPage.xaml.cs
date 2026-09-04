using Easy4K.Models;
using Easy4K.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Easy4K;

/// <summary>处理进行中页面：总进度 + 实时预览 + 命令输出 + CPU/GPU 压力。</summary>
public sealed partial class ProgressPage : Page
{
    private MainViewModel Vm => App.Services;
    private string _lastPreviewPath = "";
    private int _previewSeq;

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
        Vm.ProgressChanged += OnProgress;
        Vm.CleanRequested += OnCleanRequested;
        Vm.Logger.EntryAdded += OnLogEntryAdded;
        Vm.PropertyChanged += OnVmPropertyChanged;
        SelfTestSkipBtn.Visibility = Vm.IsStartupSelfTest ? Visibility.Visible : Visibility.Collapsed;
        UpdatePauseButton();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Vm.ProgressChanged -= OnProgress;
        Vm.CleanRequested -= OnCleanRequested;
        Vm.Logger.EntryAdded -= OnLogEntryAdded;
        Vm.PropertyChanged -= OnVmPropertyChanged;
    }

    /// <summary>清理临时文件时清空预览图，释放帧文件句柄（否则文件被锁删不掉）</summary>
    private void OnCleanRequested()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            PreviewImage.Source = null;
            _lastPreviewPath = "";
            _previewSeq++;
            PreviewHint.Visibility = Visibility.Visible;
        });
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
            // 进入不产帧的阶段：清空旧画面，避免误导（同时释放 Image 对帧文件的句柄）
            PreviewImage.Source = null;
            _lastPreviewPath = "";
            _previewSeq++;
            PreviewHint.Visibility = Visibility.Visible;
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
            if (!string.IsNullOrEmpty(p.LatestFramePath) && p.LatestFramePath != _lastPreviewPath)
            {
                _lastPreviewPath = p.LatestFramePath;
                UpdatePreviewAsync(p.LatestFramePath);
            }
        });
    }

    /// <summary>异步加载新帧，解码完成前旧帧保持显示，避免闪烁。</summary>
    private async void UpdatePreviewAsync(string path)
    {
        var seq = ++_previewSeq;
        try
        {
            // 用 FileStream + FileShare.ReadWrite 读，避免进程写帧时锁文件加载失败
            using var fs = new System.IO.FileStream(
                path, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            var bmp = new BitmapImage();
            // 等解码完成（期间旧帧不隐藏）；流在解码完成后才释放
            await bmp.SetSourceAsync(fs.AsRandomAccessStream());
            // 期间已有更新的帧 → 丢弃本次，防止旧解码覆盖新帧
            if (seq != _previewSeq) return;
            // 关闭预览后丢弃已解码帧
            if (!Vm.ShowPreview) return;
            PreviewImage.Source = bmp;
            PreviewHint.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private void OnStop(object sender, RoutedEventArgs e) => Vm.Stop();
    private void OnCopyLog(object sender, RoutedEventArgs e) => Vm.CopyLog();
    private void OnClearLog(object sender, RoutedEventArgs e) { Vm.ClearLog(); CommandLogBox.Text = ""; }

    /// <summary>跳过处理前测试（仅测试阶段可见）：停止测试，但随后仍会开始正式处理。</summary>
    private void OnSkipSelfTest(object sender, RoutedEventArgs e)
    {
        Vm.RequestSkipPreTest();
    }

    /// <summary>暂停/继续切换（立即挂起或恢复当前工具进程）</summary>
    private void OnPauseToggle(object sender, RoutedEventArgs e)
    {
        if (Vm.IsPaused) Vm.ResumeProcessing(); else Vm.PauseProcessing();
    }
}
