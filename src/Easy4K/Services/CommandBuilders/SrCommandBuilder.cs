using System.Globalization;
using System.IO;
using Easy4K.Models;

namespace Easy4K.Services.CommandBuilders;

/// <summary>超分命令构建：5 个 ncnn-Vulkan 引擎统一走这里。
///
/// 各引擎的模型选择方式并不一致，差异全部塞在 SrCatalog 的 ArgsTemplate 里：
///   · Real-ESRGAN：-n 是「模型名」（含倍率后缀），-s 指倍率
///   · Real-CUGAN / waifu2x：-m 选模型集（models-se / models-cunet …），-n 是降噪档，-s 是倍率
///   · RealSR：-m 选模型集，倍率固定 4
///   · SRMD：-m 选模型集，-n 是降噪档（-1 = 不降噪，即 nf 版），-s 是倍率
///
/// 输入输出既可以是目录（批量逐帧）也可以是单个文件（单文件模式），各引擎都支持。</summary>
public static class SrCommandBuilder
{
    /// <summary>取该模型所属算法的引擎 exe 路径；未知模型或未部署该引擎时返回空串</summary>
    public static string ExeFor(ToolPaths tools, string? modelName)
    {
        var hit = SrCatalog.Find(modelName);
        return hit is null ? "" : tools.SrExeFor(hit.Value.Family.ExeKey);
    }

    /// <summary>构建命令。jThreads 形如 "1:2:2"，空串则不加 -j（用工具默认）。
    /// 注意：本地部署的 ncnn 超分引擎（Real-ESRGAN / Real-CUGAN / RealSR / SRMD / waifu2x）**都不支持 -u**，
    /// 传了会直接打印用法并以 exit=-1 退出；所以「降低画质省显存」只作用于补帧（RIFE 的 -u），
    /// 超分这边靠 ncnn 自带的 -t 0 自动分块按显存自适应。</summary>
    public static string Build(string modelName, int scale,
        string input, string output, string jThreads, bool lowVram = false, bool useCpu = false)
    {
        var hit = SrCatalog.Find(modelName)
            ?? throw new ArgumentException($"未知超分模型：{modelName}", nameof(modelName));

        if (!hit.Variant.Supports(scale))
            throw new ArgumentException($"{modelName} 不支持 {scale}x", nameof(scale));

        // 输出是目录时才建目录；单文件模式的输出是图片路径，不能当目录建（会建出同名文件夹）
        if (!Path.HasExtension(output)) Directory.CreateDirectory(output);

        var extra = hit.Variant.ArgsTemplate.Replace("{scale}", scale.ToString(CultureInfo.InvariantCulture));
        var j = string.IsNullOrEmpty(jThreads) ? "" : $" -j {jThreads}";
        var g = useCpu ? "-g -1" : "-g 0";

        return $"-i \"{input}\" -o \"{output}\" {extra} {g}{j}";
    }
}
