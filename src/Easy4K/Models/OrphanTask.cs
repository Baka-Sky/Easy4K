using System.Text.Json.Serialization;

namespace Easy4K.Models;

/// <summary>一条孤儿执行任务：到点自动启动 Easy4K，按任务里存的选项把这支视频跑完，然后自动关闭。
///
/// 选项在「添加为孤儿执行任务」那一刻整体快照到 Settings，之后用户改全局设置不会影响已排队的任务，
/// 因为用户的心智是「先把选项设置好，再把这条任务排进去」。
/// 一次性任务：跑完就从 orphans.json 移除（执行进程一启动就先移除，避免崩溃后被反复重跑）。</summary>
public sealed class OrphanTask
{
    /// <summary>任务标识（GUID），守护进程与执行进程靠它对上号</summary>
    public string Id { get; set; } = "";

    /// <summary>预定启动时间（本地时间）</summary>
    public DateTime RunAt { get; set; }

    /// <summary>输入视频路径（与帧文件夹二选一）</summary>
    public string InputVideo { get; set; } = "";

    /// <summary>帧文件夹路径（非空则跳过拆帧，直接用它作为输入帧）</summary>
    public string ExternalFramesDir { get; set; } = "";

    /// <summary>列表里显示的名字（视频文件名，或帧文件夹名）</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>添加任务时的全部选项快照：孤儿执行进程用它替代 appsettings.json，因此不动用户配置</summary>
    public AppSettings Settings { get; set; } = new();

    /// <summary>上次执行结果（留空 = 尚未执行）</summary>
    public string LastResult { get; set; } = "";

    /// <summary>列表里显示的时间（只到时分秒，不写进 json）</summary>
    [JsonIgnore]
    public string RunAtText => RunAt.ToString("HH:mm:ss");

    /// <summary>列表里显示的日期（非今天才显示，避免每天看到的都是「今天」）</summary>
    [JsonIgnore]
    public string DateText => RunAt.Date == DateTime.Today ? "" : RunAt.ToString("MM-dd");
}
