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
    /// <summary>对比帧路径：帧去重时填"上一帧"（预览框左侧「疑似帧」显示用）；
    /// 空表示当前阶段没有对比对象，预览框只显示单图</summary>
    public string CompareFramePath { get; set; } = "";
    /// <summary>降级提示文本（安全帧率触发时设置，空则无降级）</summary>
    public string DegradeNotice { get; set; } = "";
    /// <summary>进度以百分比显示（如 HDR 转换），false 时按帧数显示</summary>
    public bool PercentDisplay { get; set; }
    public double Percent => Total > 0 ? Math.Clamp(Current * 100.0 / Total, 0, 100) : 0;
    public string DetailText => Total > 0
        ? (PercentDisplay ? $"{StageText} {Current}%" : $"{StageText} 第{Current}帧/共{Total}帧")
        : StageText;
}
