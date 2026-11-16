using System.IO;
using Easy4K.Models;

namespace Easy4K.LuminolEngine;

/// <summary>把 appsettings 里的工具路径解析为运行目录下的绝对路径。
/// 自包含分发策略：Tools 必须位于软件运行目录（exe 旁），禁止回退到开发机外部路径（如 I:\Easy4K\Tools），
/// 否则软件分发给他人后会因找不到工具无法运行。</summary>
public static class ToolPathResolver
{
    public static string ResolveToolsRoot(string configured)
    {
        var baseDir = AppContext.BaseDirectory;
        // 一律定位到运行目录（exe 旁）下名为 ToolsRoot（默认 Tools）的文件夹；
        // 配置里写绝对路径时也强制用运行目录的 Tools，保证分发后路径不跑偏
        var name = Path.IsPathRooted(configured) ? "Tools" : configured;
        return Path.GetFullPath(Path.Combine(baseDir, name));
    }

    /// <summary>根据 settings + pathConfig 解析所有工具绝对路径</summary>
    public static ToolPaths Resolve(AppSettings settings, ToolPathConfig pathConfig)
    {
        var toolsRoot = ResolveToolsRoot(settings.ToolsRoot);

        var reDir = Path.Combine(toolsRoot, pathConfig.RealEsrganDir);
        var rifeDir = Path.Combine(toolsRoot, pathConfig.RifeDir);
        var portablePythonDir = Path.Combine(toolsRoot, "python");
        var audioSrDir = Path.Combine(toolsRoot, "AudioSR");
        // PyTorch 轨（DRBA/GMFSS/UMSS/GIMM-VFI）：独立 Python 3.12 环境 + 统一驱动脚本
        var torchPythonDir = Path.Combine(toolsRoot, "python-torch");
        var frameInterpDir = Path.Combine(toolsRoot, "FrameInterp");
        // 另外 4 个 ncnn-Vulkan 超分引擎（均为 nihui 的便携构建，A/N/Intel 显卡通用）
        var realCuganDir = Path.Combine(toolsRoot, "realcugan-ncnn");
        var waifu2xDir = Path.Combine(toolsRoot, "waifu2x-ncnn");
        var srmdDir = Path.Combine(toolsRoot, "srmd-ncnn");
        var realsrDir = Path.Combine(toolsRoot, "realsr-ncnn");

        return new ToolPaths
        {
            ToolsRoot = toolsRoot,
            // 兼容两种发行布局：官方布局 <dir>\bin\ffmpeg.exe，以及把 exe 直接放在 <dir>\ 根下；
            // 配置里的目录名对不上（如旧配置写着 FFmpeg-Lei）时也回退到默认 FFmpeg-8，避免自检误报缺失。
            FFmpegExe = ResolveExe(toolsRoot, pathConfig.FFmpegDir, "FFmpeg-8", "ffmpeg.exe"),
            FFprobeExe = ResolveExe(toolsRoot, pathConfig.FFmpegDir, "FFmpeg-8", "ffprobe.exe"),
            RealEsrganExe = Path.Combine(reDir, "realesrgan-ncnn-vulkan.exe"),
            RealEsrganModelsRoot = Path.Combine(reDir, "models"),
            RealCuganExe = Path.Combine(realCuganDir, "realcugan-ncnn-vulkan.exe"),
            Waifu2xExe = Path.Combine(waifu2xDir, "waifu2x-ncnn-vulkan.exe"),
            SrmdExe = Path.Combine(srmdDir, "srmd-ncnn-vulkan.exe"),
            RealsrExe = Path.Combine(realsrDir, "realsr-ncnn-vulkan.exe"),
            RifeExe = Path.Combine(rifeDir, "rife-ncnn-vulkan.exe"),
            // RIFE 模型在 I:\NEWSVFI\rife\rife-v4.6\ 等子目录下，工具 exe 在 rife\ 根下
            RifeModelsRoot = rifeDir,
            NvEncExe = ResolveExe(toolsRoot, pathConfig.NvEncDir, "NVEncC-7", "NVEncC64.exe"),
            // 便携 Python 解释器（现供 AudioSR 音频超分使用；补帧已统一走 NCNN，不再有 PyTorch 依赖）
            PortablePythonExe = Path.Combine(portablePythonDir, "python.exe"),
            AudioSrDir = audioSrDir,
            AudioSrScript = Path.Combine(audioSrDir, "audiosr_onnx.py"),
            AudioSrModelsFp16 = Path.Combine(audioSrDir, "models-fp16"),
            AudioSrModelsFp32 = Path.Combine(audioSrDir, "models-fp32"),
            TorchPythonExe = Path.Combine(torchPythonDir, "python.exe"),
            FrameInterpDir = frameInterpDir,
            VfiDriverScript = Path.Combine(frameInterpDir, "vfi_driver.py"),
            SceneDetectScript = Path.Combine(toolsRoot, "SceneDetect", "scene_detect.py")
        };
    }

    /// <summary>解析单个工具 exe：依次尝试「配置目录 / 默认目录」×「bin 子目录 / 目录根」，
    /// 都不存在时返回默认布局路径（自检据此报告具体缺哪个文件）。</summary>
    private static string ResolveExe(string toolsRoot, string configuredDir, string defaultDir, string exeName)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredDir)) dirs.Add(configuredDir);
        if (!string.Equals(configuredDir, defaultDir, StringComparison.OrdinalIgnoreCase)) dirs.Add(defaultDir);

        foreach (var dir in dirs)
        {
            var inBin = Path.Combine(toolsRoot, dir, "bin", exeName);
            if (File.Exists(inBin)) return inBin;
            var atRoot = Path.Combine(toolsRoot, dir, exeName);
            if (File.Exists(atRoot)) return atRoot;
        }
        return Path.Combine(toolsRoot, defaultDir, "bin", exeName);
    }
}
