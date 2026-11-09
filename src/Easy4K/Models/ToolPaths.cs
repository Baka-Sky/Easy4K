namespace Easy4K.Models;

/// <summary>解析后的工具绝对路径。所有路径已规范化，可直接用于 ProcessStartInfo</summary>
public sealed class ToolPaths
{
    public string ToolsRoot { get; set; } = "";
    public string FFmpegExe { get; set; } = "";
    public string FFprobeExe { get; set; } = "";
    public string RealEsrganExe { get; set; } = "";
    public string RealEsrganModelsRoot { get; set; } = "";
    /// <summary>Real-CUGAN 引擎（ Tools\realcugan-ncnn\realcugan-ncnn-vulkan.exe，动漫超分）</summary>
    public string RealCuganExe { get; set; } = "";
    /// <summary>waifu2x 引擎（Tools\waifu2x-ncnn\waifu2x-ncnn-vulkan.exe）</summary>
    public string Waifu2xExe { get; set; } = "";
    /// <summary>SRMD 引擎（Tools\srmd-ncnn\srmd-ncnn-vulkan.exe，实拍轻量）</summary>
    public string SrmdExe { get; set; } = "";
    /// <summary>RealSR 引擎（Tools\realsr-ncnn\realsr-ncnn-vulkan.exe，实拍 4x）</summary>
    public string RealsrExe { get; set; } = "";
    public string RifeExe { get; set; } = "";
    public string RifeModelsRoot { get; set; } = "";
    public string NvEncExe { get; set; } = "";
    /// <summary>老版本 NVEncC（Tools\NVEncC-Old\NVEncC64.exe，7.74）。新版在部分老驱动上跑不起来时兜底。</summary>
    public string NvEncOldExe { get; set; } = "";
    /// <summary>便携 Python（Tools\python\python.exe）：音频超分（AudioSR）驱动脚本用它运行，
    /// 不存在时回退系统 python。补帧已统一走 NCNN（Vulkan），不再需要 PyTorch。</summary>
    public string PortablePythonExe { get; set; } = "";

    /// <summary>AudioSR 音频超分目录 Tools\AudioSR（驱动脚本 + 两个权重包）</summary>
    public string AudioSrDir { get; set; } = "";
    /// <summary>AudioSR 驱动脚本 audiosr_onnx.py</summary>
    public string AudioSrScript { get; set; } = "";
    /// <summary>AudioSR fp16 权重包（仅 GPU，1.26 GiB）</summary>
    public string AudioSrModelsFp16 { get; set; } = "";
    /// <summary>AudioSR fp32 权重包（CPU/GPU 都能跑，2.51 GiB）</summary>
    public string AudioSrModelsFp32 { get; set; } = "";

    /// <summary>PyTorch 轨补帧解释器（Tools\python-torch\python.exe）。与 AudioSR 的便携 Python 分开：
    /// 那套是 3.14（跑 ONNX/DirectML），这套是 3.12（PyTorch 没有 3.14 的 CUDA 轮子）。</summary>
    public string TorchPythonExe { get; set; } = "";
    /// <summary>PyTorch 轨补帧目录 Tools\FrameInterp（驱动脚本 + 各模型源码/权重）</summary>
    public string FrameInterpDir { get; set; } = "";
    /// <summary>PyTorch 轨统一驱动 vfi_driver.py</summary>
    public string VfiDriverScript { get; set; } = "";

    /// <summary>PySceneDetect 转场检测脚本（Tools\SceneDetect\scene_detect.py）</summary>
    public string SceneDetectScript { get; set; } = "";

    public bool FFmpegExists => File.Exists(FFmpegExe);
    public bool FFprobeExists => File.Exists(FFprobeExe);
    public bool RealEsrganExists => File.Exists(RealEsrganExe);
    public bool RifeExists => File.Exists(RifeExe);
    public bool NvEncExists => File.Exists(NvEncExe);
    /// <summary>老版本 NVEncC 是否已部署（HDR 兜底重试的前提）</summary>
    public bool NvEncOldExists => File.Exists(NvEncOldExe);

    /// <summary>AI SDR→HDR 模型（HDRTVNet++ AGCM+LE，ONNX）。存在即表示可用 DirectML 做 HDR 重建。</summary>
    public string HdrAiModel => Path.Combine(ToolsRoot, "HDRTVNet", "hdr_ensemble.onnx");
    public bool HdrAiExists => File.Exists(HdrAiModel);

    /// <summary>帧去重 AI（SFDP 孪生网络）FP32 模型：完美模式使用。属必需项，缺失时不允许启动。</summary>
    public string SfdpModelFp32 => Path.Combine(ToolsRoot, "SFDP", "model.onnx");
    /// <summary>帧去重 AI（SFDP 孪生网络）FP16 模型：性能模式使用。属必需项，缺失时不允许启动。</summary>
    public string SfdpModelFp16 => Path.Combine(ToolsRoot, "SFDP", "model_fp16.onnx");
    /// <summary>两个去重模型都在才算就绪（完美 / 性能模式各用一个，不允许静默回退）</summary>
    public bool SfdpModelsOk => File.Exists(SfdpModelFp32) && File.Exists(SfdpModelFp16);

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

    /// <summary>按算法 key 取对应超分引擎 exe；未知 key 或未部署时返回空串。
    /// key 取值见 SrCatalog.Family.ExeKey。</summary>
    public string SrExeFor(string exeKey) => exeKey switch
    {
        "esrgan" => RealEsrganExe,
        "cugan" => RealCuganExe,
        "waifu2x" => Waifu2xExe,
        "srmd" => SrmdExe,
        "realsr" => RealsrExe,
        _ => "",
    };

    /// <summary>PyTorch 轨是否就绪（解释器 + 驱动脚本都在）。模型权重单独判断。</summary>
    public bool VfiTorchReady => File.Exists(TorchPythonExe) && File.Exists(VfiDriverScript);

    /// <summary>转场检测是否可用（PySceneDetect 脚本 + 带 cv2 的 Python 解释器）</summary>
    public bool SceneDetectReady => File.Exists(SceneDetectScript) && File.Exists(TorchPythonExe);
}
