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
    /// <summary>Offical RIFE（pkl 模型）目录 Tools\officalrife</summary>
    public string OfficalRifeDir { get; set; } = "";
    public string OfficalRifeRunPy { get; set; } = "";
    public string OfficalRifeModelsRoot { get; set; } = "";
    /// <summary>便携 Python（Tools\officalrife\python\python.exe），不存在时回退系统 python</summary>
    public string OfficalPythonExe { get; set; } = "";

    public bool FFmpegExists => File.Exists(FFmpegExe);
    public bool FFprobeExists => File.Exists(FFprobeExe);
    public bool RealEsrganExists => File.Exists(RealEsrganExe);
    public bool RifeExists => File.Exists(RifeExe);
    public bool NvEncExists => File.Exists(NvEncExe);
    /// <summary>Offical RIFE 模型是否就绪（run.py 与模型目录存在）</summary>
    public bool OfficalRifeExists => File.Exists(OfficalRifeRunPy) && Directory.Exists(OfficalRifeModelsRoot);

    public bool CoreToolsOk => FFmpegExists && FFprobeExists && RealEsrganExists && RifeExists;
}
