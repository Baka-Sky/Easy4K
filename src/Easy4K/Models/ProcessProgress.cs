namespace Easy4K.Models;

/// <summary>实时进度信息，用于进度条与状态文本</summary>
public sealed class ProcessProgress
{
    public ProcessStage Stage { get; set; } = ProcessStage.Idle;
    public string StageText { get; set; } = "";
    public long Current { get; set; }
    public long Total { get; set; }
    /// <summary>最新产出的帧文件路径（供预览图实时刷新，无则空）</summary>
    public string LatestFramePath { get; set; } = "";
    public double Percent => Total > 0 ? Math.Clamp(Current * 100.0 / Total, 0, 100) : 0;
    public string DetailText => Total > 0 ? $"{StageText} 第{Current}帧/共{Total}帧" : StageText;
}
