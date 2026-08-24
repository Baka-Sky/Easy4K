using System.Collections.ObjectModel;
using Easy4K.Models;

namespace Easy4K.Services;

/// <summary>线程安全的日志聚合器。UI 通过 LogEntries 绑定，并通过事件接收追加通知</summary>
public sealed class Logger
{
    private readonly object _lock = new();
    private readonly List<LogEntry> _all = new();   // 线程安全完整日志（供快照/导出，不受 UI 调度时序影响）

    /// <summary>UI 绑定源（最大保留 200 条，超过自动裁剪最早行，避免长日志拖垮 UI 渲染）</summary>
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    public event Action<LogEntry>? EntryAdded;

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
        }

        void DoUiAdd()
        {
            lock (_lock)
            {
                LogEntries.Add(entry);
                // 超过上限批量裁剪，维持 ~200 行，减少频繁 RemoveAt(0) 的 O(n) 开销
                if (LogEntries.Count > 200)
                {
                    for (int i = 0; i < 40; i++) LogEntries.RemoveAt(0);
                }
            }
            EntryAdded?.Invoke(entry);
        }

        // 有 UI 调度器就走 UI 线程，否则直接加（如早期启动阶段）
        if (UiDispatcher is not null) UiDispatcher(DoUiAdd);
        else DoUiAdd();
    }

    /// <summary>线程安全快照（供 selftest 报告导出等，不依赖 UI 调度时序）</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_lock) return _all.ToList();
    }

    public void Clear()
    {
        lock (_lock) { _all.Clear(); LogEntries.Clear(); }
    }

    /// <summary>导出全量日志到文件</summary>
    public void Export(string path)
    {
        lock (_lock)
        {
            File.WriteAllLines(path, LogEntries.Select(e => e.ToString()));
        }
    }
}
