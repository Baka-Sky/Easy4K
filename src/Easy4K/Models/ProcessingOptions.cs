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
    /// <summary>音频超分（AudioSR）：开启后音频改由 AudioSR 升到 48kHz，不再走 ffmpeg 重采样</summary>
    public bool AudioSr { get; set; }
    /// <summary>音频超分精度档：fp16 = 低精度性能模式（禁 CPU）；fp32 = 高精度完美模式</summary>
    public string AudioSrPrecision { get; set; } = "fp16";
}
