using System.IO;
using Easy4K.Models;

namespace Easy4K.Services.CommandBuilders;

/// <summary>RIFE-ncnn-vulkan 命令构建。
/// BUG-03：-n 用总帧数（原帧数 × 倍率），不是倍率值本身。
/// BUG-04：rife-v4.25/v4.26 在命令行版（20221029）会报 "layer MemoryData not exists"，由 Orchestrator 捕获后回退到 rife-v4.6。
/// BUG-06：显存不足加 -u 和 -j 1:1:1。
/// 模型查找：rife-v4.6 对应 rife\rife-v4.6\flownet.bin/.param（exe 工作目录就是 rife 根目录）。</summary>
public static class RifeCommandBuilder
{
    /// <summary>构建补帧命令。targetFrames = 原帧数 × 倍率。
    /// 正常模式不指定 -j，由 ncnn-vulkan 自动调用全部 CPU/GPU 资源；
    /// 仅显存不足/崩溃降级时用 -j 1:1:1 单线程保稳定。</summary>
    public static string Build(string inputFramesDir, string outputFramesDir, string model,
        long targetFrames, int ifThreads, bool lowVram)
    {
        Directory.CreateDirectory(outputFramesDir);
        // -m 模型名（不含路径，工作目录=exe 目录）  -g 0  -n 目标帧数  -u UHD
        var args = $"-i \"{inputFramesDir}\" -o \"{outputFramesDir}\" -m {model} -g 0 -n {targetFrames} -u";

        if (lowVram)
        {
            // 显存不足/安全降级：单线程，避免 OOM
            args += " -j 1:1:1";
        }
        // 正常模式：不加 -j，ncnn-vulkan 自动调用最大并行
        return args;
    }

    /// <summary>列出 RIFE 模型目录下的所有 rife-v* 子目录名。
    /// 规格书要求展示: rife-v4.6 / rife-v4.18 / rife-v4.22-lite / rife-v4.25 / rife-v4.26</summary>
    public static IReadOnlyList<string> ListModels(string rifeRoot)
    {
        var result = new List<string>();
        if (!Directory.Exists(rifeRoot)) return result;

        foreach (var d in Directory.EnumerateDirectories(rifeRoot))
        {
            var name = Path.GetFileName(d);
            // 必须含 flownet.bin/.param 才算有效模型
            if (name.StartsWith("rife-v", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(d, "flownet.param")))
            {
                result.Add(name);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>规格书要求的常用模型白名单（用于 UI 优先展示，完整列表在高级里）。
    /// 若实际目录有更多版本，UI 仍会显示真实可用的全集。</summary>
    public static readonly string[] PreferredModels =
    {
        "rife-v4.6", "rife-v4.18", "rife-v4.22-lite", "rife-v4.25", "rife-v4.26"
    };
}
