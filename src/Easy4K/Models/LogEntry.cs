using Microsoft.UI.Xaml.Media;

namespace Easy4K.Models;

/// <summary>单条日志记录。格式：[时间] [级别] 消息</summary>
public sealed class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; } = LogLevel.Info;
    public string Message { get; init; } = "";

    public string LevelText => Level switch
    {
        LogLevel.Info => "INFO",
        LogLevel.Success => "SUCCESS",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Command => "CMD",
        _ => Level.ToString().ToUpperInvariant()
    };

    /// <summary>供 x:Bind DataTemplate 绑定的格式化文本</summary>
    public string LogText => $"[{Timestamp:HH:mm:ss}] [{LevelText}] {Message}";

    /// <summary>日志级别对应颜色 Brush（供 x:Bind DataTemplate 绑定 Foreground）</summary>
    public Brush LevelBrush => Level switch
    {
        LogLevel.Success => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 80, 200, 100)),
        LogLevel.Warning => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 230, 180, 40)),
        LogLevel.Error   => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 230, 80, 80)),
        LogLevel.Command => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 180, 255)),
        _                => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 180, 180))
    };

    public override string ToString() => $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{LevelText}] {Message}";
}
