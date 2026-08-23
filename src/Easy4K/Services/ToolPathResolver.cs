using System.IO;
using Easy4K.Models;

namespace Easy4K.Services;

/// <summary>把 appsettings 里的相对工具路径解析为绝对路径。
/// 策略：配置项绝对且存在 → 直接用；否则从 exe 目录向上查找名为 ToolsRoot 的子目录；
/// 最后回退到开发期硬编码路径 I:\Easy4K\Tools。BUG-01 路径问题的前置条件。</summary>
public static class ToolPathResolver
{
    /// <summary>开发期硬编码兜底（用户当前布局）</summary>
    private const string DevFallback = @"I:\Easy4K\Tools";

    public static string ResolveToolsRoot(string configured)
    {
        if (Path.IsPathRooted(configured) && Directory.Exists(configured))
            return Path.GetFullPath(configured);

        // 1. exe 目录旁的 Tools 子目录（打包布局）
        var baseDir = AppContext.BaseDirectory;
        var localTools = Path.Combine(baseDir, configured);
        if (Directory.Exists(localTools)) return Path.GetFullPath(localTools);

        // 2. 工具直接放 exe 目录根下（打包布局）
        if (Directory.Exists(Path.Combine(baseDir, "realesrgan-ncnn")) &&
            Directory.Exists(Path.Combine(baseDir, "rife")))
            return baseDir;

        // 3. 向上查找（开发布局：I:\Easy4K\Tools）
        var dir = Directory.GetParent(baseDir)?.FullName ?? "";
        for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, configured);
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            candidate = Path.Combine(dir, configured.ToLowerInvariant());
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }

        // 兜底
        return Directory.Exists(DevFallback) ? DevFallback : Path.Combine(baseDir, configured);
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
