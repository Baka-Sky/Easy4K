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

    /// <summary>AudioSR 音频超分目录 Tools\AudioSR（驱动脚本 + 两个权重包）</summary>
    public string AudioSrDir { get; set; } = "";
    /// <summary>AudioSR 驱动脚本 audiosr_onnx.py</summary>
    public string AudioSrScript { get; set; } = "";
    /// <summary>AudioSR fp16 权重包（仅 GPU，1.26 GiB）</summary>
    public string AudioSrModelsFp16 { get; set; } = "";
    /// <summary>AudioSR fp32 权重包（CPU/GPU 都能跑，2.51 GiB）</summary>
    public string AudioSrModelsFp32 { get; set; } = "";

    public bool FFmpegExists => File.Exists(FFmpegExe);
    public bool FFprobeExists => File.Exists(FFprobeExe);
    public bool RealEsrganExists => File.Exists(RealEsrganExe);
    public bool RifeExists => File.Exists(RifeExe);
    public bool NvEncExists => File.Exists(NvEncExe);
    /// <summary>Offical RIFE 模型是否就绪（run.py 与模型目录存在）</summary>
    public bool OfficalRifeExists => File.Exists(OfficalRifeRunPy) && Directory.Exists(OfficalRifeModelsRoot);

    /// <summary>AudioSR 驱动脚本是否就绪</summary>
    public bool AudioSrScriptExists => File.Exists(AudioSrScript);
    /// <summary>AudioSR fp16 权重包是否就绪（只看 UNet 图与权重，两个都在才算）</summary>
    public bool AudioSrFp16Exists =>
        File.Exists(Path.Combine(AudioSrModelsFp16, "ddpm.onnx")) &&
        File.Exists(Path.Combine(AudioSrModelsFp16, "ddpm.onnx.data"));
    /// <summary>AudioSR fp32 权重包是否就绪</summary>
    public bool AudioSrFp32Exists =>
        File.Exists(Path.Combine(AudioSrModelsFp32, "ddpm.onnx")) &&
        File.Exists(Path.Combine(AudioSrModelsFp32, "ddpm.onnx.data"));
    /// <summary>按精度取权重包路径；"fp16" 取 fp16 包，其余取 fp32 包</summary>
    public string AudioSrModelsFor(string? precision) =>
        string.Equals(precision, "fp16", StringComparison.OrdinalIgnoreCase) ? AudioSrModelsFp16 : AudioSrModelsFp32;
    /// <summary>指定精度档的权重包是否可用</summary>
    public bool AudioSrPackExists(string? precision) =>
        string.Equals(precision, "fp16", StringComparison.OrdinalIgnoreCase) ? AudioSrFp16Exists : AudioSrFp32Exists;

    public bool CoreToolsOk => FFmpegExists && FFprobeExists && RealEsrganExists && RifeExists;
}
