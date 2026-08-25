namespace Easy4K.Models;

/// <summary>持久化到 appsettings.json 的用户配置（运行时也可改）</summary>
public sealed class AppSettings
{
    public string ToolsRoot { get; set; } = "Tools";
    public string TempRoot { get; set; } = "Temp";
    public string OutputRoot { get; set; } = "Output";

    public string DefaultSrModel { get; set; } = "realesr-animevideov3";
    public string DefaultIfModel { get; set; } = "rife-v4.6";
    /// <summary>补帧引擎种类（NCNN / Offical），持久化以便下次启动记住上次引擎</summary>
    public string DefaultIfEngine { get; set; } = "NCNN";
    public int DefaultSrScale { get; set; } = 2;
    public int DefaultIfMultiplier { get; set; } = 2;

    /// <summary>用户自定义线程数（proc/save），命令 -j 1:{n}:{n}，范围 1-32</summary>
    public int ThreadCount { get; set; } = 2;
    /// <summary>安全帧率：遇到 Vulkan 设备丢失/显存溢出时自动停止处理（不降级重试）</summary>
    public bool UseSafeFrameRate { get; set; } = true;
    /// <summary>降低部分画质以降低显存占用（-u UHD 模式），可与安全帧率共存</summary>
    public bool LowerQualityForVram { get; set; }
    /// <summary>使 FFmpeg 尝试使用 GPU 加速（拆帧解码 -hwaccel / 合并帧 GPU 编码器优先，失败自动回退 CPU）</summary>
    public bool UseGpuAcceleration { get; set; } = true;

    /// <summary>HDR 转换饱和度（NVEncC --vpp-ngx-truehdr saturation，最高 200）</summary>
    public int HdrSaturation { get; set; } = 200;
    /// <summary>HDR 转换对比度（NVEncC --vpp-ngx-truehdr contrast，最高 200）</summary>
    public int HdrContrast { get; set; } = 200;

    /// <summary>以后不再爆红：勾选后不再显示红色级警告（如显卡显存不足）</summary>
    public bool SuppressRedWarning { get; set; }

    public string EncodePreset { get; set; } = "medium";

    public string Language { get; set; } = "zh-CN";
    public string Theme { get; set; } = "system";

    /// <summary>本地版本号（从 appsettings.json 读取，仅用于显示与更新对比，不写死默认值）</summary>
    public string Version { get; set; } = "";
}

/// <summary>工具子目录相对 ToolsRoot 的路径</summary>
public sealed class ToolPathConfig
{
    public string FFmpegDir { get; set; } = "FFmpeg-Lei";
    public string FFprobeDir { get; set; } = "FFmpeg-Lei";
    public string RealEsrganDir { get; set; } = "realesrgan-ncnn";
    public string RifeDir { get; set; } = "rife";
    public string NvEncDir { get; set; } = "NVEncC_9.32_x64";
}
