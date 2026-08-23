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
    public long TotalFrames { get; set; }

    public string ResolutionText => $"{Width} x {Height}";
    public string FpsText => $"{FrameRate:0.00} fps";
    public string DurationText => Duration.ToString(Duration.TotalHours >= 1 ? "hh\\:mm\\:ss" : "mm\\:ss");
    public string BitRateText => $"{BitRate / 1000} kbps";
    public bool HasAudio => !string.IsNullOrWhiteSpace(AudioCodec) && !AudioCodec.Equals("none", StringComparison.OrdinalIgnoreCase);

    public bool IsValid => Width > 0 && Height > 0 && FrameRate > 0;

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
