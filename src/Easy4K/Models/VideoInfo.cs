namespace Easy4K.Models;

/// <summary>FFprobe 检测到的视频信息</summary>
public sealed class VideoInfo
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }          // 解析后的浮点帧率，如 24.0
    public string FrameRateRaw { get; set; } = ""; // 原始分数，如 "24/1"
    /// <summary>平均帧率（ffprobe avg_frame_rate）。与标称帧率不一致时通常说明是动态帧率（VFR）。</summary>
    public double AverageFrameRate { get; set; }
    /// <summary>是否动态帧率（VFR）：帧间隔不固定，拆帧/补帧按恒定帧率重建会出错，建议先转成静态帧率。</summary>
    public bool IsVariableFrameRate { get; set; }
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
    /// <summary>字幕语言（去重后，如 zho / eng；未标记语言的为空）</summary>
    public List<string> SubtitleLanguages { get; } = new();
    /// <summary>字幕编码（去重后，如 subrip / ass）</summary>
    public List<string> SubtitleCodecs { get; } = new();
    /// <summary>音轨数量（多音轨的片源会大于 1）</summary>
    public int AudioTrackCount { get; set; }
    /// <summary>音频编码档次，如 AAC 的 LC / HE-AAC</summary>
    public string AudioProfile { get; set; } = "";
    /// <summary>音轨语言（去重后，如 zho / eng）</summary>
    public List<string> AudioLanguages { get; } = new();
    /// <summary>视频流数量（不含封面图，多视角片源会大于 1）</summary>
    public int VideoStreamCount { get; set; } = 1;
    /// <summary>附件数量（mkv 内嵌字体/封面等都算）</summary>
    public int AttachmentCount { get; set; }
    /// <summary>源文件写入的编码器标记（format 层的 encoder 标签）</summary>
    public string EncoderTag { get; set; } = "";
    /// <summary>音轨声道布局，如 stereo / 5.1 / 7.1（ffprobe channel_layout）</summary>
    public string AudioChannelLayout { get; set; } = "";
    /// <summary>显示宽高比（ffprobe display_aspect_ratio，如 16:9；缺失时按分辨率推算）</summary>
    public string DisplayAspectRaw { get; set; } = "";
    /// <summary>色彩范围：limited（TV）/ full（PC）</summary>
    public string ColorRange { get; set; } = "";
    /// <summary>扫描方式：progressive 逐行 / tt、bb 等隔行</summary>
    public string FieldOrder { get; set; } = "";
    /// <summary>章节数量</summary>
    public int ChapterCount { get; set; }
    /// <summary>封面图尺寸（0 = 未探测到）</summary>
    public int CoverWidth { get; set; }
    public int CoverHeight { get; set; }

    public string ResolutionText => $"{Width} x {Height}";
    public string FpsText => $"{FrameRate:0.00} fps";
    /// <summary>帧率类型：动态帧率 / 静态帧率（帧率未知时为空）</summary>
    public string FrameRateKindText => FrameRate <= 0 ? "" : (IsVariableFrameRate ? "动态帧率" : "静态帧率");
    public string DurationText => Duration.ToString(Duration.TotalHours >= 1 ? "hh\\:mm\\:ss" : "mm\\:ss");
    public string BitRateText => $"{BitRate / 1000} kbps";
    public bool HasAudio => !string.IsNullOrWhiteSpace(AudioCodec) && !AudioCodec.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否存在字幕流（ffprobe 探测结果）。决定主页「保留原视频内嵌字幕」是否可勾。</summary>
    public bool HasSubtitles { get; set; }

    /// <summary>格式层用户元数据标签数量（title / artist / comment / creation_time 等；
    /// encoder、major_brand 这类技术字段不计入）。决定「嵌入原视频元数据」是否可勾。</summary>
    public int MetadataTagCount { get; set; }

    /// <summary>原视频是否带封面图（mp4 的 attached_pic 视频流 / mkv 的封面附件）。
    /// 决定「嵌入原视频封面」是否可勾。</summary>
    public bool HasCover => CoverStreamIndex >= 0;

    /// <summary>封面所在的流索引（-1 = 没有封面）</summary>
    public int CoverStreamIndex { get; set; } = -1;

    /// <summary>封面是容器的附件流（mkv 的 cover.jpg）而不是 attached_pic 视频流</summary>
    public bool CoverIsAttachment { get; set; }

    /// <summary>封面图片编码，如 mjpeg / png</summary>
    public string CoverCodec { get; set; } = "";

    /// <summary>是否存在可写入成品的用户元数据</summary>
    public bool HasMetadata => MetadataTagCount > 0;

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

    /// <summary>音频描述，如「AAC · 48000 Hz · 立体声 · 128 kbps」</summary>
    public string AudioText
    {
        get
        {
            if (!HasAudio) return "无";
            var parts = new List<string> { AudioCodec.ToUpperInvariant() };
            if (!string.IsNullOrWhiteSpace(AudioProfile)) parts[0] += $"（{AudioProfile}）";
            if (AudioSampleRate > 0) parts.Add($"{AudioSampleRate} Hz");
            var layout = AudioLayoutText;
            if (!string.IsNullOrEmpty(layout)) parts.Add(layout);
            if (AudioBitRate > 0) parts.Add($"{AudioBitRate / 1000} kbps");
            // 单音轨不显示语言：容器里的 language 标签经常是下载工具随便写的（例如日语歌标成 eng），
            // 只有多音轨时才把语言和轨数一起列出来，跟字幕行的规则保持一致
            if (AudioTrackCount > 1)
            {
                var langs = AudioLanguages.Count > 0 ? $"（{string.Join(" / ", AudioLanguages)}）" : "";
                parts.Add($"{AudioTrackCount} 条音轨{langs}");
            }
            return string.Join(" · ", parts);
        }
    }

    /// <summary>显示宽高比：优先用 ffprobe 的 display_aspect_ratio，缺失时按分辨率约分（1920x1080 → 16:9）</summary>
    public string AspectText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayAspectRaw) && DisplayAspectRaw != "0:1") return DisplayAspectRaw;
            if (Width <= 0 || Height <= 0) return "";
            var a = Width;
            var b = Height;
            while (b != 0) { var t = a % b; a = b; b = t; }
            return a <= 0 ? "" : $"{Width / a}:{Height / a}";
        }
    }

    /// <summary>色彩范围描述（limited = TV 范围，full = 全范围）</summary>
    public string ColorRangeText => ColorRange.ToLowerInvariant() switch
    {
        "tv" or "limited" => "limited（TV）",
        "pc" or "full" => "full（PC）",
        _ => ""
    };

    /// <summary>扫描方式（逐行 / 隔行）</summary>
    public string ScanText => FieldOrder.ToLowerInvariant() switch
    {
        "progressive" => "逐行",
        "tt" or "bb" or "tb" or "bt" => "隔行",
        _ => ""
    };

    /// <summary>声道布局中文描述（stereo → 立体声，5.1(side) → 5.1）</summary>
    public string AudioLayoutText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AudioChannelLayout))
                return AudioChannels > 0 ? (AudioChannels == 1 ? "单声道" : AudioChannels == 2 ? "立体声" : $"{AudioChannels} ch") : "";
            var layout = AudioChannelLayout.ToLowerInvariant();
            if (layout.StartsWith("mono")) return "单声道";
            if (layout.StartsWith("stereo")) return "立体声";
            var dot = layout.IndexOf('(');
            return (dot > 0 ? layout[..dot] : layout).ToUpperInvariant();
        }
    }

    /// <summary>字幕描述，如「2 条（zho / eng · subrip）」</summary>
    public string SubtitleDetailText
    {
        get
        {
            if (!HasSubtitles) return "无";
            var parts = new List<string>();
            if (SubtitleLanguages.Count > 0) parts.Add(string.Join(" / ", SubtitleLanguages));
            if (SubtitleCodecs.Count > 0) parts.Add(string.Join("/", SubtitleCodecs));
            return parts.Count == 0 ? $"{SubtitleCount} 条" : $"{SubtitleCount} 条（{string.Join(" · ", parts)}）";
        }
    }

    /// <summary>封面描述，如「mjpeg 1280×720」；没有封面为「无」</summary>
    public string CoverText
    {
        get
        {
            if (!HasCover) return "无";
            var codec = string.IsNullOrWhiteSpace(CoverCodec) ? "" : CoverCodec;
            var size = CoverWidth > 0 && CoverHeight > 0 ? $"{CoverWidth}×{CoverHeight}" : "";
            var text = string.Join(" ", new[] { codec, size }.Where(s => !string.IsNullOrEmpty(s)));
            return string.IsNullOrEmpty(text) ? "有" : text;
        }
    }

    /// <summary>元数据描述：只有「有 / 无」</summary>
    public string MetadataText => MetadataTagCount > 0 ? "有" : "无";

    /// <summary>视频流数量描述（不含封面）</summary>
    public string VideoStreamText => $"{Math.Max(1, VideoStreamCount)} 条视频流";

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
