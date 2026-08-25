using System.IO;

namespace Easy4K.Services.CommandBuilders;

/// <summary>Offical RIFE（PyTorch pkl 模型）命令构建。
/// 模型目录 Tools\officalrife\models\official_*，推理脚本 run.py。
/// Python 优先用便携版 Tools\officalrife\python\python.exe，否则回退系统 python。</summary>
public static class OfficalRifeCommandBuilder
{
    /// <summary>旧版 2.3 系列为非 jit 格式（含 RAFT/contextnet/unet），run.py 无法直接加载，不列出</summary>
    private static readonly string[] ExcludedModels = { "official_2.3", "rpr_v7_2.3" };

    /// <summary>列出 Offical 模型（official_* 且含 flownet.pkl）</summary>
    public static IReadOnlyList<string> ListModels(string modelsRoot)
    {
        var result = new List<string>();
        if (!Directory.Exists(modelsRoot)) return result;
        foreach (var d in Directory.EnumerateDirectories(modelsRoot))
        {
            var name = Path.GetFileName(d);
            if (name.StartsWith("official_", StringComparison.OrdinalIgnoreCase) &&
                !ExcludedModels.Contains(name) &&
                File.Exists(Path.Combine(d, "flownet.pkl")))
            {
                result.Add(name);
            }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>构建命令参数（含 run.py 全路径与参数，python 可执行文件由调用方作为 exe 传入）</summary>
    public static string Build(string runPy, string inputDir, string outputDir, string modelDir, int multiplier)
    {
        Directory.CreateDirectory(outputDir);
        return $"\"{runPy}\" -i \"{inputDir}\" -o \"{outputDir}\" -m \"{modelDir}\" -mult {multiplier}";
    }
}
