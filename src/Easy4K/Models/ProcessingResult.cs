namespace Easy4K.Models;

/// <summary>处理结束结果，供完成弹窗展示。</summary>
public sealed class ProcessingResult
{
    public bool Success { get; init; }
    /// <summary>最终输出文件路径</summary>
    public string OutputPath { get; init; } = "";
    /// <summary>执行了哪些步骤（如 "拆帧 → 超分×2 → 补帧×2 → 合并"）</summary>
    public string StepsText { get; init; } = "";
    /// <summary>总耗时</summary>
    public TimeSpan Elapsed { get; init; }
}
