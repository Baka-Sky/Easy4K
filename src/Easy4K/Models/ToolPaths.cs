namespace Easy4K.Models;

/// <summary>解析后的工具绝对路径。所有路径已规范化，可直接用于 ProcessStartInfo</summary>
public sealed class ToolPaths
{
    public string ToolsRoot { get; set; } = "";
    public string FFmpegExe { get; set; } = "";
    public string FFprobeExe { get; set; } = "";
    public string RealEsrganExe { get; set; } = "";
    public string RealEsrganModelsRoot { get; set; } = "";
    public string RifeExe { get; set; } = "";
    public string RifeModelsRoot { get; set; } = "";
    public string NvEncExe { get; set; } = "";

    public bool FFmpegExists => File.Exists(FFmpegExe);
    public bool FFprobeExists => File.Exists(FFprobeExe);
    public bool RealEsrganExists => File.Exists(RealEsrganExe);
    public bool RifeExists => File.Exists(RifeExe);
    public bool NvEncExists => File.Exists(NvEncExe);

    public bool CoreToolsOk => FFmpegExists && FFprobeExists && RealEsrganExists && RifeExists;
}
