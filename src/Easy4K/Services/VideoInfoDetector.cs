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
        // 详细字段：编码/档次/像素格式/色彩（位深、HDR 判断都从这几项推）
        var args = "-v error -select_streams v:0 -count_packets " +
                   "-show_entries stream=width,height,r_frame_rate,avg_frame_rate,nb_read_packets,bit_rate," +
                   "codec_name,profile,pix_fmt,color_space,color_transfer " +
                   "-show_entries format=duration,bit_rate,format_name,size -of json " +
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
        if (stream.TryGetProperty("avg_frame_rate", out var afr))
            info.AverageFrameRate = ParseFps(afr.GetString() ?? "");
        if (stream.TryGetProperty("bit_rate", out var sb) && long.TryParse(sb.GetString(), out var sbr)) info.BitRate = sbr;

        if (stream.TryGetProperty("codec_name", out var cn)) info.VideoCodec = cn.GetString() ?? "";
        if (stream.TryGetProperty("profile", out var pf)) info.Profile = pf.GetString() ?? "";
        if (stream.TryGetProperty("pix_fmt", out var pxf)) info.PixelFormat = pxf.GetString() ?? "";
        if (stream.TryGetProperty("color_space", out var cs)) info.ColorSpace = cs.GetString() ?? "";
        if (stream.TryGetProperty("color_transfer", out var ct)) info.ColorTransfer = ct.GetString() ?? "";
        info.VideoBitRate = info.BitRate;

        if (root.TryGetProperty("format", out var fmt))
        {
            if (fmt.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), out var dur))
                info.Duration = TimeSpan.FromSeconds(dur);
            if (info.BitRate == 0 && fmt.TryGetProperty("bit_rate", out var fb) && long.TryParse(fb.GetString(), out var fbr))
                info.BitRate = fbr;
            if (fmt.TryGetProperty("format_name", out var fn)) info.FormatName = fn.GetString() ?? "";
            if (fmt.TryGetProperty("size", out var sz) && long.TryParse(sz.GetString(), out var size)) info.FileSize = size;
        }

        // 总帧数：优先用 nb_read_packets，否则用 duration*fps
        long frames = 0;
        if (stream.TryGetProperty("nb_read_packets", out var nb) && long.TryParse(nb.GetString(), out var n) && n > 0)
            frames = n;
        else if (info.Duration.TotalSeconds > 0 && info.FrameRate > 0)
            frames = (long)Math.Round(info.Duration.TotalSeconds * info.FrameRate);
        info.TotalFrames = frames;

        // 动态帧率（VFR）判定：标称帧率与平均帧率明显不一致，或按总帧数/时长算出的实际帧率
        // 与标称帧率明显不一致，就按动态帧率处理（常见于手机拍摄、录屏与部分剪辑导出）。
        info.IsVariableFrameRate = DetectVariableFrameRate(info);

        // 音频：编解码器 + 采样率 + 声道 + 码率（采样率供音频链路判断"这次有没有重采样"）
        var audio = DetectAudio(videoPath);
        info.AudioCodec = audio.codec;
        info.AudioSampleRate = audio.sampleRate;
        info.AudioChannels = audio.channels;
        info.AudioBitRate = audio.bitRate;

        // 字幕：有字幕流才允许勾「保留原视频内嵌字幕」，否则该项在界面上保持禁用
        info.SubtitleCount = DetectSubtitles(videoPath);
        info.HasSubtitles = info.SubtitleCount > 0;

        return info.IsValid ? info : null;
    }

    /// <summary>字幕流数量（内嵌或外挂软字幕都算，不看具体格式）。</summary>
    private int DetectSubtitles(string videoPath)
    {
        var args = $"-v error -select_streams s -show_entries stream=index -of csv=p=0 \"{videoPath}\"";
        var (stdout, _, exit) = RunCaptured(_tools.FFprobeExe, args);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return 0;
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>取音轨信息（无音轨时返回空）。用 JSON 解析，字段缺失时不会像 csv 那样错位。</summary>
    private (string codec, int sampleRate, int channels, long bitRate) DetectAudio(string videoPath)
    {
        var args = $"-v error -select_streams a:0 -show_entries stream=codec_name,sample_rate,channels,bit_rate -of json \"{videoPath}\"";
        var (stdout, _, exit) = RunCaptured(_tools.FFprobeExe, args);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return ("", 0, 0, 0);

        using var doc = JsonDocument.Parse(stdout);
        if (!doc.RootElement.TryGetProperty("streams", out var streams)) return ("", 0, 0, 0);
        var stream = streams.EnumerateArray().FirstOrDefault();
        if (stream.ValueKind != JsonValueKind.Object) return ("", 0, 0, 0);

        var codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
        var rate = stream.TryGetProperty("sample_rate", out var r) && int.TryParse(r.GetString(), out var rr) ? rr : 0;
        var channels = stream.TryGetProperty("channels", out var ch) && ch.TryGetInt32(out var chn) ? chn : 0;
        var bitRate = stream.TryGetProperty("bit_rate", out var b) && long.TryParse(b.GetString(), out var br) ? br : 0;

        return (string.IsNullOrWhiteSpace(codec) ? "" : codec, rate, channels, bitRate);
    }

    /// <summary>是否为动态帧率。容差 0.5%：正常 CFR 视频的标称/平均帧率只会有极小的取整误差。</summary>
    private static bool DetectVariableFrameRate(VideoInfo info)
    {
        if (info.FrameRate <= 0) return false;
        const double tolerance = 0.005;

        // avg_frame_rate 与 r_frame_rate 不一致（VFR 最典型的特征）
        if (info.AverageFrameRate > 0 &&
            Math.Abs(info.AverageFrameRate - info.FrameRate) > info.FrameRate * tolerance)
            return true;

        // 辅助判断：总帧数 ÷ 时长 得到的实际平均帧率与标称帧率不一致
        if (info.TotalFrames > 0 && info.Duration.TotalSeconds > 0)
        {
            var measured = info.TotalFrames / info.Duration.TotalSeconds;
            if (Math.Abs(measured - info.FrameRate) > info.FrameRate * tolerance)
                return true;
        }

        return false;
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
