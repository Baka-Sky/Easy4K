namespace Easy4K.Models;

/// <summary>持久化到 appsettings.json 的用户配置（运行时也可改）</summary>
public sealed class AppSettings
{
    public string ToolsRoot { get; set; } = "Tools";
    public string TempRoot { get; set; } = "Temp";
    public string OutputRoot { get; set; } = "Output";

    public string DefaultSrModel { get; set; } = "realesr-animevideov3";
    public string DefaultIfModel { get; set; } = "rife-v4.6";
    /// <summary>补帧引擎（NCNN / Offical）；切换即时写入，启动按此恢复</summary>
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
    /// <summary>使用 CPU 处理所有模型（超分/补帧 NCNN -g -1、Offical 强制 CPU），速度慢，仅在 GPU 不可用/不稳定时使用</summary>
    public bool UseCpuProcessing { get; set; }

    /// <summary>HDR 转换饱和度（NVEncC --vpp-ngx-truehdr saturation，最高 200）</summary>
    public int HdrSaturation { get; set; } = 200;
    /// <summary>HDR 转换对比度（NVEncC --vpp-ngx-truehdr contrast，最高 200）</summary>
    public int HdrContrast { get; set; } = 200;

    /// <summary>以后不再爆红：勾选后不再显示红色级警告（如显卡显存不足）</summary>
    public bool SuppressRedWarning { get; set; }

    public string EncodePreset { get; set; } = "medium";

    public string Language { get; set; } = "zh-CN";
    public string Theme { get; set; } = "system";
    /// <summary>图片背景主题（theme=image）使用的背景图路径，空 = 未选择（退化为普通亚克力）</summary>
    public string BackgroundImage { get; set; } = "";
    /// <summary>图片背景主题中覆盖在背景图上的亚克力浓度（0-100，越大越糊/越暗、文字越清晰）</summary>
    public int BackdropAcrylicPercent { get; set; } = 45;

    // ===================== 首次运行向导 / 启动自检 =====================
    /// <summary>首次运行欢迎向导是否已完成（协议/硬件声明/报告目录/主题等均已配置）</summary>
    public bool SetupCompleted { get; set; }

    /// <summary>每次启动是否先用 Res 里的 1 秒测试视频按保存的默认步骤跑一遍自检，通过后再进入正式界面</summary>
    public bool StartupSelfTest { get; set; } = true;

    // ===================== 处理完成 HTML 报告 =====================
    /// <summary>处理完成后是否自动生成 HTML 报告（记录本次选项/命令/抽帧）</summary>
    public bool ReportEnabled { get; set; } = true;
    /// <summary>报告保存目录（相对运行根，支持绝对路径），空时默认 Reports</summary>
    public string ReportDir { get; set; } = "Reports";
    /// <summary>报告生成后是否自动用默认浏览器打开</summary>
    public bool ReportAutoOpen { get; set; } = true;

    // ===================== "保存当前设置为默认"时的默认步骤（启动自检按此执行） =====================
    public bool DefaultSplitFrames { get; set; } = true;
    public bool DefaultSuperResolution { get; set; } = true;
    public bool DefaultInterpolation { get; set; } = true;
    public bool DefaultMergeVideo { get; set; } = true;
    public bool DefaultMergeAudio { get; set; } = true;
    public bool DefaultSdrToHdr { get; set; }

    // ===================== 帧去重（高级模式） =====================
    /// <summary>相邻帧去重开关：开启后在超分/补帧前剔除重复帧，处理完按索引表回填，成品时长与音频不变</summary>
    public bool DedupEnabled { get; set; }
    /// <summary>去重模式：performance = 像素差（全局+分块局部）+ dHash 细筛；uhd = 再加 QR 分解精判与 Farnebäck 光流终判</summary>
    public string DedupMode { get; set; } = "performance";

    // ===================== 音频超分 AudioSR（高级模式） =====================
    /// <summary>音频超分开关：开启后音频不再由 ffmpeg 重采样，改由 AudioSR 升到 48kHz 后再嵌入</summary>
    public bool AudioSrEnabled { get; set; }
    /// <summary>精度档：fp16 = 低精度性能模式（仅 GPU）；fp32 = 高精度完美模式（CPU/GPU 均可）</summary>
    public string AudioSrPrecision { get; set; } = "fp16";

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
