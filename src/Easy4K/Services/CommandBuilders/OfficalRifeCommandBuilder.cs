using System.IO;

namespace Easy4K.Services.CommandBuilders;

/// <summary>Offical RIFE（PyTorch pkl 模型）命令构建。
/// 模型目录 Tools\officalrife\models\official_*，推理脚本 run.py。
/// Python 优先用便携版 Tools\officalrife\python\python.exe，否则回退系统 python。</summary>
public static class OfficalRifeCommandBuilder
{
    /// <summary>列出 Offical 模型（official_* / rpr_* 且含 flownet.pkl）</summary>
    public static IReadOnlyList<string> ListModels(string modelsRoot)
    {
        var result = new List<string>();
        if (!Directory.Exists(modelsRoot)) return result;
        foreach (var d in Directory.EnumerateDirectories(modelsRoot))
        {
            var name = Path.GetFileName(d);
            if (File.Exists(Path.Combine(d, "flownet.pkl")) &&
                (name.StartsWith("official_", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("rpr_", StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(name);
            }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>构建命令参数（含 run.py 全路径与参数，python 可执行文件由调用方作为 exe 传入）。
    /// threads 对应软件线程滑块（torch 推理线程）；useUhdMode 勾选「降低画质」时加 -u（FP16 省内存）。</summary>
    public static string Build(string runPy, string inputDir, string outputDir, string modelDir,
        int multiplier, int threads, bool useUhdMode)
    {
        Directory.CreateDirectory(outputDir);
        var u = useUhdMode ? " -u" : "";
        var t = threads > 0 ? $" -threads {threads}" : "";
        return $"\"{runPy}\" -i \"{inputDir}\" -o \"{outputDir}\" -m \"{modelDir}\" -mult {multiplier}{u}{t}";
    }
}
