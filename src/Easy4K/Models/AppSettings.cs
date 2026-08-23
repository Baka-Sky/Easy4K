namespace Easy4K.Models;

/// <summary>持久化到 appsettings.json 的用户配置（运行时也可改）</summary>
public sealed class AppSettings
{
    public string ToolsRoot { get; set; } = "Tools";
    public string TempRoot { get; set; } = "Temp";
    public string OutputRoot { get; set; } = "Output";

    public string DefaultSrModel { get; set; } = "realesr-animevideov3";
    public string DefaultIfModel { get; set; } = "rife-v4.6";
    public int DefaultSrScale { get; set; } = 2;
    public int DefaultIfMultiplier { get; set; } = 2;

    public int SrThreads { get; set; } = 2;
    public int IfThreads { get; set; } = 2;
    public string EncodePreset { get; set; } = "medium";

    public string Language { get; set; } = "zh-CN";
    public string Theme { get; set; } = "system";
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
