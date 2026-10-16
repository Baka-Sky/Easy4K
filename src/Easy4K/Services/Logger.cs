using System.Collections.ObjectModel;
using Easy4K.Models;

namespace Easy4K.Services;

/// <summary>线程安全的日志聚合器。UI 通过 LogEntries 绑定，并通过事件接收追加通知。
///
/// 性能约束（实测：低主频 CPU 上运行一段时间后 UI 像死锁）：
/// 原先每来一行日志就切一次 UI 线程，界面还会把整段文本重新 split/join 一遍，
/// 而 ffmpeg / 外部工具的 stderr 是会"突发"的（一次几十上百行）。日志一多，
/// UI 线程就被这些逐行回调彻底占满，消息队列只进不出，表现就是界面卡死。
/// 改成：写日志只进内存缓冲，UI 侧由定时器每 100 ms 批量刷一次（展示上限仍是 200 行）。</summary>
public sealed class Logger
{
    /// <summary>UI 侧保留的最大行数（`_all` 里始终保留完整日志供导出）</summary>
    private const int MaxUiEntries = 200;
    /// <summary>批量刷 UI 的最小间隔：日志再密，UI 线程每 100 ms 也只需处理一次</summary>
    private const int FlushIntervalMs = 100;

    private readonly object _lock = new();
    private readonly List<LogEntry> _all = new();       // 线程安全完整日志（供快照/导出，不受 UI 调度时序影响）
    private readonly List<LogEntry> _pending = new();   // 待刷到 UI 的日志
    private Timer? _flushTimer;
    private bool _flushScheduled;

    /// <summary>UI 绑定源（最大保留 200 条，超过自动裁剪最早行，避免长日志拖垮 UI 渲染）</summary>
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    /// <summary>一批日志已进入 UI 集合。批量通知：UI 侧一次就能把文本重建完，不必逐行处理。</summary>
    public event Action<IReadOnlyList<LogEntry>>? EntriesAdded;

    /// <summary>日志被清空（供 UI 同步清空日志显示区，避免"日志已清但界面还留着"的跨页不一致）</summary>
    public event Action? Cleared;

    /// <summary>UI 线程调度器（由 ViewModel 注入 DispatcherQueue）。注入后，
    /// 所有日志追加都调度到 UI 线程执行，保证 LogEntries 只在 UI 线程修改，
    /// 避免 ListView 绑定在后台线程收到 CollectionChanged 导致切页/渲染错乱。</summary>
    public Action<Action>? UiDispatcher { get; set; }

    public void Info(string msg) => Add(LogLevel.Info, msg);
    public void Success(string msg) => Add(LogLevel.Success, msg);
    public void Warn(string msg) => Add(LogLevel.Warning, msg);
    public void Error(string msg) => Add(LogLevel.Error, msg);
    public void Command(string msg) => Add(LogLevel.Command, msg);

    public void Add(LogLevel level, string message)
    {
        var entry = new LogEntry { Level = level, Message = message };
        // 立即追加到线程安全完整列表（快照/导出立即可见，不依赖 UI 调度时序）
        lock (_lock)
        {
            _all.Add(entry);
            if (_all.Count > 5000) _all.RemoveRange(0, _all.Count - 5000);
            _pending.Add(entry);
        }

        // 没有 UI 调度器（早期启动阶段）就直接刷，行为与从前一致
        if (UiDispatcher is null) { FlushPending(); return; }

        // 有调度器：合并到下一次批量刷新，避免逐行切 UI 线程
        lock (_lock)
        {
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        _flushTimer ??= new Timer(_ => FlushPending(), null, Timeout.Infinite, Timeout.Infinite);
        try { _flushTimer.Change(FlushIntervalMs, Timeout.Infinite); } catch { }
    }

    /// <summary>把缓冲里的日志一次性交给 UI（无调度器时在当前线程直接执行）。
    /// 由定时器线程调用，整体兜异常：定时器里抛出未捕获异常会直接崩进程。</summary>
    private void FlushPending()
    {
        try
        {
            List<LogEntry> batch;
            lock (_lock)
            {
                _flushScheduled = false;
                if (_pending.Count == 0) return;
                batch = new List<LogEntry>(_pending);
                _pending.Clear();
            }

            void DoUiFlush()
            {
                try
                {
                    lock (_lock)
                    {
                        foreach (var e in batch) LogEntries.Add(e);
                        // 超过上限批量裁剪，维持 ~200 行，减少频繁 RemoveAt(0) 的 O(n) 开销
                        while (LogEntries.Count > MaxUiEntries) LogEntries.RemoveAt(0);
                    }
                    EntriesAdded?.Invoke(batch);
                }
                catch { }
            }

            if (UiDispatcher is not null) UiDispatcher(DoUiFlush);
            else DoUiFlush();
        }
        catch { }
    }

    /// <summary>线程安全快照（供 selftest 报告导出等，不依赖 UI 调度时序）</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_lock) return _all.ToList();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _all.Clear();
            _pending.Clear();
        }
        // UI 集合与通知统一走 UI 线程（与 Add 保持一致），并广播 Cleared 让各页面同步清屏
        void DoUiClear()
        {
            lock (_lock) LogEntries.Clear();
            Cleared?.Invoke();
        }
        if (UiDispatcher is not null) UiDispatcher(DoUiClear);
        else DoUiClear();
    }

    /// <summary>导出全量日志到文件</summary>
    public void Export(string path)
    {
        lock (_lock)
        {
            File.WriteAllLines(path, _all.Select(e => e.ToString()));
        }
    }
}
