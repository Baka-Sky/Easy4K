namespace Easy4K.Models;

/// <summary>处理选项勾选状态。本类只表达 UI 上的勾选语义，依赖联动由 ViewModel 维护</summary>
public sealed class ProcessingOptions
{
    public bool SplitFrames { get; set; } = true;     // 强制勾选，恒为 true
    public bool SuperResolution { get; set; } = true;
    public bool Interpolation { get; set; } = true;
    public bool MergeVideo { get; set; } = true;
    public bool MergeAudio { get; set; } = true;       // 合并原视频音频到最终视频（从原视频提取）
    public bool SdrToHdr { get; set; } = false;
    /// <summary>补帧引擎："NCNN"（rife-ncnn-vulkan）或 "Offical"（PyTorch pkl 模型）</summary>
    public string IfEngine { get; set; } = "NCNN";
}
