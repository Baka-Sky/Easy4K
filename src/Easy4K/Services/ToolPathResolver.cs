using System.IO;
using Easy4K.Models;

namespace Easy4K.Services;

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

        var ffmpegDir = Path.Combine(toolsRoot, pathConfig.FFmpegDir);
        var reDir = Path.Combine(toolsRoot, pathConfig.RealEsrganDir);
        var rifeDir = Path.Combine(toolsRoot, pathConfig.RifeDir);
        var nvDir = Path.Combine(toolsRoot, pathConfig.NvEncDir);

        return new ToolPaths
        {
            ToolsRoot = toolsRoot,
            FFmpegExe = Path.Combine(ffmpegDir, "bin", "ffmpeg.exe"),
            FFprobeExe = Path.Combine(ffmpegDir, "bin", "ffprobe.exe"),
            RealEsrganExe = Path.Combine(reDir, "realesrgan-ncnn-vulkan.exe"),
            RealEsrganModelsRoot = Path.Combine(reDir, "models"),
            RifeExe = Path.Combine(rifeDir, "rife-ncnn-vulkan.exe"),
            // RIFE 模型在 I:\NEWSVFI\rife\rife-v4.6\ 等子目录下，工具 exe 在 rife\ 根下
            RifeModelsRoot = rifeDir,
            NvEncExe = Path.Combine(nvDir, "NVEncC64.exe")
        };
    }
}
