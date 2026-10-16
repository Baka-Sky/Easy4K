using System.Globalization;
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
    /// -y 覆盖旧帧：从头完整拆一遍，不做断点续传。始终多线程全速。
    /// useGpu：勾选「使FFmpeg尝试使用GPU加速」时加 -hwaccel auto（GPU 解码，失败由上层回退 CPU）。</summary>
    public static (string args, string framePattern) SplitFrames(string videoPath, string framesDir, bool useGpu = false)
    {
        Directory.CreateDirectory(framesDir);
        var pattern = Path.Combine(framesDir, "%08d.png").Replace('\\', '/');
        var hw = useGpu ? " -hwaccel auto" : "";
        // -q:v 2 高质量 PNG；-y 覆盖
        var args = $"-y{hw} -i \"{videoPath}\" -q:v 2 -vsync 0 \"{pattern}\"";
        return (args, pattern);
    }

    /// <summary>把 PNG 序列合成视频。BUG-08：自动探测起始帧编号。
    /// encArgs 由上层决定（GPU 硬件编码或 CPU libx265）；-threads 0 = 使用全部 CPU 核心，不限制。
    /// subtitleSource 非空时把该文件作为第二个输入，只取它的字幕流原样复制（-map 1:s?，没有字幕也不报错）：
    /// 成片是从帧序列重建的，原文件里的字幕不会自己跟过来，必须显式映射；
    /// 合并产物是 matroska，所以字幕可以直接 -c:s copy（mp4 不支持大多数字幕格式的 copy）。</summary>
    public static string MergeFrames(string framesDir, string outputVideo, double fps, string encArgs,
        string? subtitleSource = null)
    {
        var pattern = Path.Combine(framesDir, "%08d.png").Replace('\\', '/');
        var startNum = DetectStartNumber(framesDir);
        // 帧率固定用不变区域格式化：目标帧率可以是小数（如 59.94），缺了这一步在部分区域设置下会写成 "59,94"
        var fpsText = fps.ToString("0.##", CultureInfo.InvariantCulture);
        var sub = string.IsNullOrWhiteSpace(subtitleSource)
            ? ""
            : $" -i \"{subtitleSource}\" -map 0:v:0 -map 1:s? -c:s copy";
        var args = $"-y -framerate {fpsText} -start_number {startNum} -i \"{pattern}\"{sub} " +
                   $"-c:v {encArgs} -threads 0 -pix_fmt yuv420p -an \"{outputVideo}\"";
        return args;
    }

    /// <summary>把音频嵌入到最终视频。BUG-07：-map 0:v:0 -map 1:a:0 顺序正确。
    /// sampleRate：默认 96000（原音轨重采样到 PCM 24bit 96kHz）；开了音频超分时传 48000，
    /// 直接用 AudioSR 产出的 48kHz WAV，不再让 ffmpeg 重采样。
    /// useGpu：勾选「使FFmpeg尝试使用GPU加速」时加 -hwaccel auto（对 -c:v copy 无实际加速，仅保持"尝试"语义）。</summary>
    public static string EmbedAudio(string videoPath, string audioPath, string outputPath, bool useGpu = false,
        int sampleRate = 96000, string? comment = null)
    {
        var hw = useGpu ? " -hwaccel auto" : "";
        // comment：把"这段音频是不是 AudioSR 处理的"写进成品文件信息（MediaInfo / ffprobe 的 Comment 字段）。
        // 只覆盖 comment 一个字段，Title/日期等其余元数据仍由 -map_metadata 默认继承。
        var meta = string.IsNullOrEmpty(comment) ? "" : $" -metadata comment=\"{comment}\"";
        return $"-y{hw} -i \"{videoPath}\" -i \"{audioPath}\" " +
               $"-c:v copy -c:a pcm_s24le -ar {sampleRate} -ac 2 -map 0:v:0 -map 1:a:0{meta} \"{outputPath}\"";
    }

    /// <summary>把任意音轨转成 AudioSR 要求的 PCM WAV（48kHz / float32 / 保持声道数）。
    /// AudioSR 的驱动只读 WAV，所以 flac/mp3/aac 都要先过这一步。</summary>
    public static string ToWav48k(string inputAudio, string outputWav)
    {
        var dir = Path.GetDirectoryName(outputWav);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return $"-y -i \"{inputAudio}\" -vn -c:a pcm_f32le -ar 48000 \"{outputWav}\"";
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
