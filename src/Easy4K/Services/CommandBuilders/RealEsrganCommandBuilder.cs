using System.IO;
using Easy4K.Models;

namespace Easy4K.Services.CommandBuilders;

/// <summary>Real-ESRGAN-ncnn-Vulkan 命令构建。
/// BUG-02：-o 只接文件夹，不接文件名模式（直接给 4k_frames 目录）。
/// BUG-06：显存紧张时加 -u（UHD 模式）和 -j 1:1:1（降线程）。
/// 模型命名：realesr-animevideov3 在不同 scale 下对应不同模型文件
///   -s 2 → realesr-animevideov3-x2
///   -s 3 → realesr-animevideov3-x3
///   -s 4 → realesr-animevideov3-x4 / realesrgan-x4plus / realesrgan-x4plus-anime
/// 模型路径：本工具版本忽略 -m 参数，始终从 exe 所在目录的 models\{模型名}.param 加载，
/// 因此各模型的 .param/.bin 必须放在 models 根级（子目录形式不识别）。</summary>
public static class RealEsrganCommandBuilder
{
    /// <summary>构建超分命令。model 已是完整文件名（如 realesr-animevideov3-x2）。
    /// jThreads 控制线程（load:proc:save）："1:1:1" 安全帧率单线程；空串不加 -j 用工具默认。
    /// useUhdMode：勾选「降低部分画质以降低显存占用」时加 -u（UHD 模式，降画质省显存）。</summary>
    public static string Build(string inputFramesDir, string outputFramesDir, string model,
        int scale, string jThreads, bool useUhdMode = false)
    {
        Directory.CreateDirectory(outputFramesDir);
        // -g 0 GPU0；-s scale 显式指定倍率；-n model；-u UHD（勾选才加）；-j 线程数（空则不加，用默认）
        var j = string.IsNullOrEmpty(jThreads) ? "" : $" -j {jThreads}";
        var u = useUhdMode ? " -u" : "";
        var args = $"-i \"{inputFramesDir}\" -o \"{outputFramesDir}\" -n {model} -s {scale} -g 0{u}{j}";
        return args;
    }

    /// <summary>列出某 scale 下可用的模型。规则：模型文件名含 "x{scale}"。</summary>
    public static IReadOnlyList<string> ListModels(string modelsRoot, int scale)
    {
        var result = new List<string>();
        if (!Directory.Exists(modelsRoot)) return result;

        // 优先取根级 .param/.bin 对（参数模型名）
        var needle = $"x{scale}";
        foreach (var p in Directory.EnumerateFiles(modelsRoot, "*.param"))
        {
            var name = Path.GetFileNameWithoutExtension(p);
            if (name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                result.Add(name);
        }

        // 也兼容子目录形式
        foreach (var d in Directory.EnumerateDirectories(modelsRoot))
        {
            var name = Path.GetFileName(d);
            if (name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                // 子目录里包含同名 param/bin，模型名就是目录名
                if (File.Exists(Path.Combine(d, name + ".param")) && !result.Contains(name))
                    result.Add(name);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}
