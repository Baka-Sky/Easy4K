using System.Diagnostics;
using System.Text.Json;
using Easy4K.Models;

namespace Easy4K.Services;

/// <summary>FFprobe 包装：解析视频分辨率/帧率/时长/码率/音频/总帧数。
/// 总帧数优先用 nb_read_packets（-count_packets，精确但慢），缺失时用 duration*fps 兜底。
/// BUG-01：路径用双引号包裹，工作目录设为 FFmpeg 目录避免中文路径问题。</summary>
public sealed class VideoInfoDetector
{
    private readonly ToolPaths _tools;
    public VideoInfoDetector(ToolPaths tools) => _tools = tools;

    public VideoInfo? Detect(string videoPath)
    {
        if (!File.Exists(videoPath)) return null;
        if (!_tools.FFprobeExists) return null;

        // 注意：r_frame_rate 是分数字符串如 "24/1"；duration 在 format 下，单位秒（小数）
        var args = "-v error -select_streams v:0 -count_packets " +
                   "-show_entries stream=width,height,r_frame_rate,nb_read_packets,bit_rate " +
                   "-show_entries format=duration,bit_rate -of json " +
                   $"\"{videoPath}\"";

        var (stdout, _, exit) = RunCaptured(_tools.FFprobeExe, args);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return null;

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        var stream = root.GetProperty("streams").EnumerateArray().FirstOrDefault();

        var info = new VideoInfo();
        if (stream.TryGetProperty("width", out var w)) info.Width = w.GetInt32();
        if (stream.TryGetProperty("height", out var h)) info.Height = h.GetInt32();
        if (stream.TryGetProperty("r_frame_rate", out var fr)) info.FrameRateRaw = fr.GetString() ?? "";
        info.FrameRate = ParseFps(info.FrameRateRaw);
        if (stream.TryGetProperty("bit_rate", out var sb) && long.TryParse(sb.GetString(), out var sbr)) info.BitRate = sbr;

        if (root.TryGetProperty("format", out var fmt))
        {
            if (fmt.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), out var dur))
                info.Duration = TimeSpan.FromSeconds(dur);
            if (info.BitRate == 0 && fmt.TryGetProperty("bit_rate", out var fb) && long.TryParse(fb.GetString(), out var fbr))
                info.BitRate = fbr;
        }

        // 总帧数：优先用 nb_read_packets，否则用 duration*fps
        long frames = 0;
        if (stream.TryGetProperty("nb_read_packets", out var nb) && long.TryParse(nb.GetString(), out var n) && n > 0)
            frames = n;
        else if (info.Duration.TotalSeconds > 0 && info.FrameRate > 0)
            frames = (long)Math.Round(info.Duration.TotalSeconds * info.FrameRate);
        info.TotalFrames = frames;

        // 音频
        info.AudioCodec = DetectAudioCodec(videoPath);

        return info.IsValid ? info : null;
    }

    private string DetectAudioCodec(string videoPath)
    {
        var args = $"-v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0 \"{videoPath}\"";
        var (stdout, _, exit) = RunCaptured(_tools.FFprobeExe, args);
        if (exit != 0) return "";
        var codec = stdout.Trim();
        return string.IsNullOrWhiteSpace(codec) ? "" : codec;
    }

    private static double ParseFps(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var parts = raw.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], out var num) && double.TryParse(parts[1], out var den) && den > 0)
            return num / den;
        return double.TryParse(raw, out var v) ? v : 0;
    }

    private static (string stdout, string stderr, int exit) RunCaptured(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p == null) return ("", "", -1);
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return (stdout, stderr, p.ExitCode);
        }
        catch
        {
            return ("", "", -1);
        }
    }
}
