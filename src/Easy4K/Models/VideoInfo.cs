namespace Easy4K.Models;

/// <summary>FFprobe 检测到的视频信息</summary>
public sealed class VideoInfo
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }          // 解析后的浮点帧率，如 24.0
    public string FrameRateRaw { get; set; } = ""; // 原始分数，如 "24/1"
    public TimeSpan Duration { get; set; }
    public long BitRate { get; set; }              // bps
    public string AudioCodec { get; set; } = "";   // aac / flac / 无
    /// <summary>音轨采样率（Hz，0 = 未知/无音轨）。用于判断音频链路上是否发生了重采样。</summary>
    public int AudioSampleRate { get; set; }
    public long TotalFrames { get; set; }

    // ---- 详细检测（主页信息卡展示） ----
    /// <summary>视频编码，如 h264 / hevc / av1 / vp9</summary>
    public string VideoCodec { get; set; } = "";
    /// <summary>编码档次，如 High / Main / Main 10</summary>
    public string Profile { get; set; } = "";
    /// <summary>像素格式，如 yuv420p / yuv420p10le（位深从这里面看）</summary>
    public string PixelFormat { get; set; } = "";
    /// <summary>色彩空间，如 bt709 / bt2020nc</summary>
    public string ColorSpace { get; set; } = "";
    /// <summary>传输特性：smpte2084（HDR10）/ arib-std-b67（HLG）/ bt709（SDR）</summary>
    public string ColorTransfer { get; set; } = "";
    /// <summary>视频流码率（bps，0 = 未知，用总码率兜底）</summary>
    public long VideoBitRate { get; set; }
    /// <summary>音轨声道数（0 = 无音轨/未知）</summary>
    public int AudioChannels { get; set; }
    /// <summary>音轨码率（bps，0 = 未知）</summary>
    public long AudioBitRate { get; set; }
    /// <summary>容器格式，如 mov,mp4,m4a,3gp,3g2,mj2</summary>
    public string FormatName { get; set; } = "";
    /// <summary>文件大小（字节）</summary>
    public long FileSize { get; set; }
    /// <summary>字幕流数量</summary>
    public int SubtitleCount { get; set; }

    public string ResolutionText => $"{Width} x {Height}";
    public string FpsText => $"{FrameRate:0.00} fps";
    public string DurationText => Duration.ToString(Duration.TotalHours >= 1 ? "hh\\:mm\\:ss" : "mm\\:ss");
    public string BitRateText => $"{BitRate / 1000} kbps";
    public bool HasAudio => !string.IsNullOrWhiteSpace(AudioCodec) && !AudioCodec.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否存在字幕流（ffprobe 探测结果）。决定主页「保留原视频内嵌字幕」是否可勾。</summary>
    public bool HasSubtitles { get; set; }

    public bool IsValid => Width > 0 && Height > 0 && FrameRate > 0;

    /// <summary>编码 + 档次 + 位深 + 像素格式，如「H.264 (High) · 8 bit · yuv420p」</summary>
    public string CodecText
    {
        get
        {
            var codec = VideoCodec.ToLowerInvariant() switch
            {
                "h264" => "H.264",
                "hevc" or "h265" => "H.265",
                "av1" => "AV1",
                "vp9" => "VP9",
                "mpeg4" => "MPEG-4",
                "mpeg2video" => "MPEG-2",
                "" => "未知",
                _ => VideoCodec
            };

            var parts = new List<string> { codec };
            if (!string.IsNullOrWhiteSpace(Profile)) parts.Add(Profile);
            parts.Add($"{BitDepth} bit");
            if (!string.IsNullOrWhiteSpace(PixelFormat)) parts.Add(PixelFormat);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>位深：从像素格式后缀判断（10le/12le → 10/12，其余按 8）</summary>
    public int BitDepth
    {
        get
        {
            if (PixelFormat.Contains("12", StringComparison.OrdinalIgnoreCase)) return 12;
            if (PixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase)) return 10;
            return 8;
        }
    }

    /// <summary>HDR：PQ(smpte2084) 或 HLG(arib-std-b67) 视为 HDR，其余按 SDR</summary>
    public bool IsHdr =>
        ColorTransfer.Contains("2084", StringComparison.OrdinalIgnoreCase) ||
        ColorTransfer.Contains("arib", StringComparison.OrdinalIgnoreCase) ||
        ColorTransfer.Contains("hlg", StringComparison.OrdinalIgnoreCase);

    /// <summary>色彩描述，如「BT.709 · SDR」「BT.2020 · HDR10」</summary>
    public string ColorText
    {
        get
        {
            var space = ColorSpace.ToLowerInvariant() switch
            {
                "bt709" => "BT.709",
                "bt2020nc" or "bt2020c" or "bt2020" => "BT.2020",
                "smpte170m" => "BT.601",
                "" => "未标记",
                _ => ColorSpace.ToUpperInvariant()
            };
            var hdr = IsHdr
                ? (ColorTransfer.Contains("arib", StringComparison.OrdinalIgnoreCase) ? "HLG" : "HDR10")
                : "SDR";
            return $"{space} · {hdr}";
        }
    }

    /// <summary>音频描述，如「AAC · 48000 Hz · 2 ch · 128 kbps」</summary>
    public string AudioText
    {
        get
        {
            if (!HasAudio) return "无";
            var parts = new List<string> { AudioCodec.ToUpperInvariant() };
            if (AudioSampleRate > 0) parts.Add($"{AudioSampleRate} Hz");
            if (AudioChannels > 0) parts.Add(AudioChannels == 2 ? "立体声" : $"{AudioChannels} ch");
            if (AudioBitRate > 0) parts.Add($"{AudioBitRate / 1000} kbps");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>容器格式（ffprobe 的 format_name 是一串逗号分隔的别名，mp4 家族按 MP4 显示）</summary>
    public string ContainerText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FormatName)) return "未知";
            var parts = FormatName.Split(',');
            var pick = parts.FirstOrDefault(p => p.Equals("mp4", StringComparison.OrdinalIgnoreCase))
                       ?? parts.FirstOrDefault(p => !p.Equals("mov", StringComparison.OrdinalIgnoreCase))
                       ?? parts[0];
            return pick.ToUpperInvariant();
        }
    }

    /// <summary>文件大小（自动选 MB / GB）</summary>
    public string FileSizeText
    {
        get
        {
            if (FileSize <= 0) return "未知";
            return FileSize >= 1024L * 1024 * 1024
                ? $"{FileSize / 1024.0 / 1024 / 1024:0.00} GB"
                : $"{FileSize / 1024.0 / 1024:0.0} MB";
        }
    }

    /// <summary>根据原始分辨率和超分倍率推断输出分辨率名称</summary>
    public static string ResolutionName(int width, int height) => (width, height) switch
    {
        (3840, 2160) => "4K",
        (5120, 2880) => "5K",
        (5760, 3240) => "5.7K",
        (7680, 4320) => "8K",
        _ => $"{width}x{height}"
    };
}
