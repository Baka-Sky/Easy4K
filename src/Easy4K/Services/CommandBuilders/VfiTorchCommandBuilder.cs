using System.Globalization;
using System.IO;

namespace Easy4K.Services.CommandBuilders;

/// <summary>PyTorch 轨补帧命令构建（「ncnn + PyTorch 双轨」里的 PyTorch 轨）。
///
/// 为什么单独一轨：DRBA / GMFSS / GIMM-VFI 这些模型上游只有 PyTorch 实现，没有 ncnn 版本，
/// 跑不了 rife-ncnn-vulkan 那条 Vulkan 通路，必须用 PyTorch + CUDA。
/// 依赖 cupy / torch.cuda，**仅 NVIDIA 显卡可用**（AMD、核显不可用）。
///
/// 走 Tools\python-torch\python.exe（独立 Python 3.12）+ Tools\FrameInterp\vfi_driver.py，
/// 输入输出都是帧目录，输出沿用 rife-ncnn-vulkan 的 %08d.png 命名，可直接替换现有补帧阶段。
/// 「模型 → driverKey」的对应关系见 VfiCatalog。</summary>
public static class VfiTorchCommandBuilder
{
    /// <summary>光流缩放：2.5K 以上用 0.5 省显存（上游建议 1080P 用 1.0、4K 用 0.5）</summary>
    public static double ScaleFor(int width) => width >= 2560 ? 0.5 : 1.0;

    /// <summary>构建推理命令。driverKey 是 VfiCatalog 里对应版本的 DriverKey（vfi_driver.py 的 --model）。
    /// totalFrames 是目标输出总帧数（与 rife-ncnn-vulkan 的 -n 同义），
    /// 由驱动脚本按「源位置线性采样」精确产出这么多帧，所以「手动目标帧率」在这条轨上同样生效。
    /// threads = 界面「处理线程」（落到驱动的 torch/OpenCV 线程数），fp16 = 界面「降低部分画质」。</summary>
    public static string Build(string scriptPath, string driverKey, string inputDir, string outputDir,
        long totalFrames, double scale, bool sceneDetect, int threads = 0, bool fp16 = false)
    {
        Directory.CreateDirectory(outputDir);
        var sc = sceneDetect ? " --scdet" : "";
        var th = threads > 0 ? $" --threads {threads}" : "";
        var half = fp16 ? " --fp16" : "";
        // -X utf8：驱动脚本的进度/日志是中文，强制 UTF-8 输出，保证与 ProcessRunner 的 UTF-8 解码一致
        return $"-X utf8 \"{scriptPath}\" --model {driverKey} "
             + $"--input \"{inputDir}\" --output \"{outputDir}\" "
             + $"--frames {totalFrames} --scale {scale.ToString("0.###", CultureInfo.InvariantCulture)}{sc}{th}{half}";
    }
}
