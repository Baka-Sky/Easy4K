using System.IO;
using Easy4K.Models;

namespace Easy4K.Services.CommandBuilders;

/// <summary>RIFE-ncnn-vulkan 命令构建。
/// BUG-03：-n 用总帧数（原帧数 × 倍率），不是倍率值本身。
/// BUG-04：rife-v4.25/v4.26 在命令行版（20221029）会报 "layer MemoryData not exists"，由 Orchestrator 捕获后回退到 rife-v4.6。
/// BUG-06：显存不足加 -u 和 -j 1:1:1。
/// BUG-09：只有 rife-v4 及以后支持自定义目标帧数 -n；rife-v2/v3 的 -n 只接受插值倍数，否则报
/// "only rife-v4 model support custom numframe and timestep"。
/// 模型查找：rife-v4.6 对应 rife\rife-v4.6\flownet.bin/.param（exe 工作目录就是 rife 根目录）。</summary>
public static class RifeCommandBuilder
{
    /// <summary>构建补帧命令。multiplier = 补帧倍率，targetFrames = 原帧数 × 倍率。
    /// rife-v4+ 用 -n {targetFrames}；rife-v2/v3 用 -n {multiplier}。
    /// jThreads 控制线程（load:proc:save）："1:1:1" 安全帧率单线程；空串不加 -j 用工具默认。
    /// useUhdMode：勾选「降低部分画质以降低显存占用」时加 -u（UHD 模式，降画质省显存）。
    /// useCpu：使用 CPU 推理（-g -1），仅在 GPU 不可用/不稳定时使用。</summary>
    public static string Build(string inputFramesDir, string outputFramesDir, string model,
        int multiplier, long targetFrames, string jThreads, bool useUhdMode = false, bool useCpu = false)
    {
        Directory.CreateDirectory(outputFramesDir);
        // BUG-09：只有 rife-v4+ 支持 -n 自定义帧数；v2/v3 传 -n 会报
        // "only rife-v4 model support custom numframe and timestep"，不传则默认 2 倍
        var isV4 = model.StartsWith("rife-v4", StringComparison.OrdinalIgnoreCase);
        var nArg = isV4 ? $"-n {targetFrames}" : "";
        // -m 模型名（不含路径，工作目录=exe 目录）  -g 0 GPU / -g -1 CPU  -u UHD（勾选才加）  -j 线程数（空则不加，用默认）
        var j = string.IsNullOrEmpty(jThreads) ? "" : $" -j {jThreads}";
        var u = useUhdMode ? " -u" : "";
        var g = useCpu ? "-g -1" : "-g 0";
        var args = $"-i \"{inputFramesDir}\" -o \"{outputFramesDir}\" -m {model} {g} {nArg}{u}{j}";
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
