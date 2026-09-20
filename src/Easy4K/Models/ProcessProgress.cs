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
    /// <summary>对比帧路径：帧去重时填"对比帧"（与筛选帧做比较的那一帧，预览框中间显示）</summary>
    public string CompareFramePath { get; set; } = "";
    /// <summary>对比帧的原始帧号（供预览标签显示；0 表示未知）</summary>
    public int CompareFrameIndex { get; set; }
    /// <summary>判决帧路径：帧去重时填"最近被判为重复的那一帧"（预览框左侧显示，只在判出重复时更换）</summary>
    public string VerdictFramePath { get; set; } = "";
    /// <summary>判决帧的原始帧号（供预览标签显示；0 表示未知）</summary>
    public int VerdictFrameIndex { get; set; }
    /// <summary>是否处于帧去重的「判决帧 / 对比帧 / 筛选帧」三联预览模式。
    /// 与"有没有帧路径"无关：一进入去重就把三图布局摆好，还没判出重复时左图先留空。</summary>
    public bool FrameCompareMode { get; set; }
    /// <summary>降级提示文本（安全帧率触发时设置，空则无降级）</summary>
    public string DegradeNotice { get; set; } = "";
    /// <summary>进度以百分比显示（如 HDR 转换），false 时按帧数显示</summary>
    public bool PercentDisplay { get; set; }
    public double Percent => Total > 0 ? Math.Clamp(Current * 100.0 / Total, 0, 100) : 0;
    public string DetailText => Total > 0
        ? (PercentDisplay ? $"{StageText} {Current}%" : $"{StageText} 第{Current}帧/共{Total}帧")
        : StageText;
}
