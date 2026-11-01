using System.Diagnostics;
using System.Text.Json;
using Easy4K.Models;

namespace Easy4K.LuminolEngine;

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
                   "codec_name,profile,pix_fmt,color_space,color_transfer,color_range,field_order," +
                   "display_aspect_ratio,sample_aspect_ratio " +
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
        if (stream.TryGetProperty("color_range", out var cr)) info.ColorRange = cr.GetString() ?? "";
        if (stream.TryGetProperty("field_order", out var fo)) info.FieldOrder = fo.GetString() ?? "";
        if (stream.TryGetProperty("display_aspect_ratio", out var dar)) info.DisplayAspectRaw = dar.GetString() ?? "";
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

        // 音频：编解码器 + 采样率 + 声道 + 声道布局 + 码率 + 音轨数
        // （采样率供音频链路判断"这次有没有重采样"，布局/音轨数只用于信息卡展示）
        DetectAudio(videoPath, info);

        // 字幕：有字幕流才允许勾「保留原视频内嵌字幕」，否则该项在界面上保持禁用
        info.SubtitleCount = DetectSubtitles(videoPath, info.SubtitleLanguages, info.SubtitleCodecs);
        info.HasSubtitles = info.SubtitleCount > 0;

        // 章节数：只读章节起始时间，文件没有章节时返回 0
        info.ChapterCount = DetectChapterCount(videoPath);

        // 元数据 / 封面 / 流统计：同样只在原视频确实带的时候才允许勾选，没有就禁用
        DetectMetadataAndCover(videoPath, info);

        return info.IsValid ? info : null;
    }

    /// <summary>格式层标签里属于"技术信息"的键，不算用户元数据（否则几乎所有文件都会被判定为有元数据）。</summary>
    private static readonly HashSet<string> TechnicalTagKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "encoder", "major_brand", "minor_version", "compatible_brands",
        "handler_name", "vendor_id", "media_type", "language"
    };

    /// <summary>填用户元数据 / 封面 / 流统计。封面的两种常见形态都认：
    /// mp4/mov 的 attached_pic 视频流（内嵌封面图），以及 mkv 的封面附件（cover.jpg）。</summary>
    private void DetectMetadataAndCover(string videoPath, VideoInfo info)
    {
        var args = "-v error -show_entries format_tags:stream=index,codec_type,codec_name,width,height" +
                   ":stream_disposition=attached_pic:stream_tags=filename -of json " +
                   $"\"{videoPath}\"";
        var (stdout, _, exit) = RunCaptured(_tools.FFprobeExe, args);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("tags", out var tags))
            {
                foreach (var tag in tags.EnumerateObject())
                {
                    if (tag.Name.Equals("encoder", StringComparison.OrdinalIgnoreCase))
                    {
                        info.EncoderTag = tag.Value.GetString() ?? "";
                        continue;
                    }
                    if (TechnicalTagKeys.Contains(tag.Name)) continue;
                    info.MetadataTagCount++;
                }
            }

            if (root.TryGetProperty("streams", out var streams))
            {
                var videoCount = 0;
                var attachCount = 0;
                var coverFound = info.CoverStreamIndex >= 0;

                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var ct) ? ct.GetString() ?? "" : "";
                    var codec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() ?? "" : "";
                    var index = s.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var i) ? i : -1;
                    if (index < 0) continue;

                    // 形态一：attached_pic 视频流（mp4/mov 封面、新版 ffmpeg 写出的 mkv 封面）
                    var isAttachedPic = s.TryGetProperty("disposition", out var disp)
                                        && disp.TryGetProperty("attached_pic", out var ap)
                                        && ap.TryGetInt32(out var apv) && apv == 1;

                    if (type == "video")
                    {
                        if (isAttachedPic)
                        {
                            if (!coverFound)
                            {
                                info.CoverStreamIndex = index;
                                info.CoverIsAttachment = false;
                                info.CoverCodec = codec;
                                if (s.TryGetProperty("width", out var cw) && cw.TryGetInt32(out var cwi)) info.CoverWidth = cwi;
                                if (s.TryGetProperty("height", out var chh) && chh.TryGetInt32(out var chi)) info.CoverHeight = chi;
                                coverFound = true;
                            }
                            continue;
                        }
                        videoCount++;
                        continue;
                    }

                    // 形态二：真正的容器附件（mkvmerge 等写出的 cover.jpg 属于这一类）
                    if (type == "attachment")
                    {
                        attachCount++;
                        if (!coverFound
                            && s.TryGetProperty("tags", out var st)
                            && st.TryGetProperty("filename", out var fn)
                            && IsImageFileName(fn.GetString() ?? ""))
                        {
                            info.CoverStreamIndex = index;
                            info.CoverIsAttachment = true;
                            info.CoverCodec = codec;
                            if (s.TryGetProperty("width", out var aw) && aw.TryGetInt32(out var awi)) info.CoverWidth = awi;
                            if (s.TryGetProperty("height", out var ah) && ah.TryGetInt32(out var ahi)) info.CoverHeight = ahi;
                            coverFound = true;
                        }
                    }
                }

                info.VideoStreamCount = Math.Max(1, videoCount);
                info.AttachmentCount = attachCount;
            }
        }
        catch
        {
            // 探测失败保持默认值（无元数据 / 无封面），不影响主流程
        }
    }

    private static bool IsImageFileName(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp";
    }

    /// <summary>字幕流数量 + 语言/编码清单（内嵌或外挂软字幕都算，不看具体格式）。
    /// 语言为 und（未标记）时不记入清单，避免出现一串 und。</summary>
    private int DetectSubtitles(string videoPath, List<string> languages, List<string> codecs)
    {
        var args = $"-v error -select_streams s -show_entries stream=codec_name:stream_tags=language -of json \"{videoPath}\"";
        var (stdout, _, exit) = RunCaptured(_tools.FFprobeExe, args);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("streams", out var streams)) return 0;
            var count = 0;
            foreach (var s in streams.EnumerateArray())
            {
                count++;
                var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(codec) && !codecs.Contains(codec)) codecs.Add(codec);
                var lang = s.TryGetProperty("tags", out var t) && t.TryGetProperty("language", out var l) ? l.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(lang)
                    && !lang.Equals("und", StringComparison.OrdinalIgnoreCase)
                    && !languages.Contains(lang)) languages.Add(lang);
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>章节数量（没有章节返回 0）。只读每章起始时间，不读完整章节信息。</summary>
    private int DetectChapterCount(string videoPath)
    {
        var args = $"-v error -show_entries chapter=start_time -of csv=p=0 \"{videoPath}\"";
        var (stdout, _, exit) = RunCaptured(_tools.FFprobeExe, args);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return 0;
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>填音轨信息（无音轨时保持空）。用 JSON 解析，字段缺失时不会像 csv 那样错位。
    /// 取首条音轨的编码/档次/采样率/声道/码率/声道布局，并统计音轨总数与语言。</summary>
    private void DetectAudio(string videoPath, VideoInfo info)
    {
        var args = $"-v error -select_streams a -show_entries stream=codec_name,profile,sample_rate,channels," +
                   $"channel_layout,bit_rate:stream_tags=language -of json \"{videoPath}\"";
        var (stdout, _, exit) = RunCaptured(_tools.FFprobeExe, args);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("streams", out var streams)) return;

            JsonElement first = default;
            var hasFirst = false;
            var tracks = 0;
            foreach (var s in streams.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                tracks++;
                if (!hasFirst) { first = s; hasFirst = true; }

                var lang = s.TryGetProperty("tags", out var t) && t.TryGetProperty("language", out var l)
                    ? l.GetString() ?? ""
                    : "";
                if (!string.IsNullOrWhiteSpace(lang)
                    && !lang.Equals("und", StringComparison.OrdinalIgnoreCase)
                    && !info.AudioLanguages.Contains(lang))
                    info.AudioLanguages.Add(lang);
            }

            info.AudioTrackCount = tracks;
            if (!hasFirst) return;

            info.AudioCodec = first.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
            info.AudioProfile = first.TryGetProperty("profile", out var p) ? p.GetString() ?? "" : "";
            info.AudioSampleRate = first.TryGetProperty("sample_rate", out var r) && int.TryParse(r.GetString(), out var rr) ? rr : 0;
            info.AudioChannels = first.TryGetProperty("channels", out var ch) && ch.TryGetInt32(out var chn) ? chn : 0;
            info.AudioBitRate = first.TryGetProperty("bit_rate", out var b) && long.TryParse(b.GetString(), out var br) ? br : 0;
            info.AudioChannelLayout = first.TryGetProperty("channel_layout", out var cl) ? cl.GetString() ?? "" : "";
        }
        catch
        {
            // 音频探测失败按无音频处理，不阻断主流程
        }
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
