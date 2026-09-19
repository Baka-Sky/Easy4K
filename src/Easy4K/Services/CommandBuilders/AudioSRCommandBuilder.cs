using System.IO;

namespace Easy4K.Services.CommandBuilders;

/// <summary>AudioSR 音频超分命令构建（Tools\AudioSR\audiosr_onnx.py）。
/// 权重包由 --models 指定（models-fp16 / models-fp32），fp16 与 CPU 的冲突在脚本内也会再拦一道。
/// python 可执行文件由调用方作为 exe 传入（复用 Tools\officalrife\python 便携版）。</summary>
public static class AudioSrCommandBuilder
{
    /// <summary>构建推理命令。device 传 "cpu" 时强制 CPU（仅 fp32 包可用）；其余走 auto（DirectML 优先）。
    /// -X utf8：驱动脚本的进度/日志是中文，强制 UTF-8 输出，保证与 ProcessRunner 的 UTF-8 解码一致。</summary>
    public static string Build(string scriptPath, string inputWav, string outputWav, string modelsDir,
        bool useCpu)
    {
        var dir = Path.GetDirectoryName(outputWav);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var dev = useCpu ? " -device cpu" : "";
        return $"-X utf8 \"{scriptPath}\" --input \"{inputWav}\" --output \"{outputWav}\" --models \"{modelsDir}\"{dev}";
    }
}
