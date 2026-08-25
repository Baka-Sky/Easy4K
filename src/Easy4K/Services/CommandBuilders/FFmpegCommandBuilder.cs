using System.IO;

namespace Easy4K.Services.CommandBuilders;

/// <summary>FFmpeg 命令构建：拆帧 / 合并视频 / 嵌入音频。
/// BUG-01：所有路径用双引号包裹，正斜杠统一。
/// BUG-07：音频嵌入时视频流在前、音频流在后，-map 正确。
/// BUG-08：合并视频时自动探测输出帧起始编号，传 -start_number。</summary>
public static class FFmpegCommandBuilder
{
    /// <summary>拆分视频为 PNG 序列。输出帧编号从 1 开始，纯数字命名（%08d）。
    /// 纯数字命名是 RIFE 目录模式的硬性要求（RIFE 只识别 %08d.png，不认 frame_ 前缀）。
    /// -y 覆盖旧帧：从头完整拆一遍，不做断点续传。始终多线程全速。</summary>
    public static (string args, string framePattern) SplitFrames(string videoPath, string framesDir)
    {
        Directory.CreateDirectory(framesDir);
        var pattern = Path.Combine(framesDir, "%08d.png").Replace('\\', '/');
        // -q:v 2 高质量 PNG；-y 覆盖
        var args = $"-y -i \"{videoPath}\" -q:v 2 -vsync 0 \"{pattern}\"";
        return (args, pattern);
    }

    /// <summary>把 PNG 序列合成视频。BUG-08：自动探测起始帧编号。
    /// encArgs 由上层决定（GPU 硬件编码或 CPU libx265）；-threads 0 = 使用全部 CPU 核心，不限制。</summary>
    public static string MergeFrames(string framesDir, string outputVideo, double fps, string encArgs)
    {
        var pattern = Path.Combine(framesDir, "%08d.png").Replace('\\', '/');
        var startNum = DetectStartNumber(framesDir);
        var args = $"-y -framerate {fps:0.##} -start_number {startNum} -i \"{pattern}\" " +
                   $"-c:v {encArgs} -threads 0 -pix_fmt yuv420p -an \"{outputVideo}\"";
        return args;
    }

    /// <summary>把音频嵌入到最终视频。BUG-07：-map 0:v:0 -map 1:a:0 顺序正确，PCM 24bit 96kHz。</summary>
    public static string EmbedAudio(string videoPath, string audioPath, string outputPath)
    {
        return $"-y -i \"{videoPath}\" -i \"{audioPath}\" " +
               $"-c:v copy -c:a pcm_s24le -ar 96000 -ac 2 -map 0:v:0 -map 1:a:0 \"{outputPath}\"";
    }

    /// <summary>从原视频提取音频为 FLAC（无损，保留原始码率/位深）。
    /// 用 -vn 丢视频流、-c:a flac 编码 FLAC。</summary>
    public static string ExtractAudio(string videoPath, string outputAudio)
    {
        var dir = Path.GetDirectoryName(outputAudio);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return $"-y -i \"{videoPath}\" -vn -c:a flac \"{outputAudio}\"";
    }

    /// <summary>探测帧目录下最小的帧编号，用于 -start_number。BUG-08。</summary>
    public static int DetectStartNumber(string framesDir)
    {
        if (!Directory.Exists(framesDir)) return 0;
        int min = int.MaxValue;
        foreach (var f in Directory.EnumerateFiles(framesDir, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(f.AsSpan());
            if (int.TryParse(name, out var n))
            {
                if (n < min) min = n;
            }
        }
        return min == int.MaxValue ? 0 : min;
    }
}
