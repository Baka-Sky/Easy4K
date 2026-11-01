using System.IO;

namespace Easy4K.LuminolEngine.CommandBuilders;

/// <summary>NVEncC64 SDR→HDR 命令构建。
/// --vpp-ngx-truehdr 需要 NVEncC ≥ 9.32 (BUG-05) 且 NVIDIA RTX 20/30/40/50 系列 + 驱动 ≥ 570。
/// 饱和度/对比度默认 200/200（规格书）。</summary>
public static class NvEncCommandBuilder
{
    /// <summary>SDR → HDR10 转换。saturation/contrast 按规格书钳制到 100-400。
    /// copySubtitles=true 时加 --sub-copy，把合并阶段带进来的内嵌字幕原样带到 HDR 产物里
    /// （NVEncC 默认只带视频和音轨，不显式声明会丢字幕）。</summary>
    public static string Build(string inputVideo, string outputVideo, int saturation, int contrast,
        bool copySubtitles = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputVideo)!);

        var args =
            $"-i \"{inputVideo}\" " +
            $"--vpp-ngx-truehdr saturation={ClampParam(saturation)},contrast={ClampParam(contrast)} " +
            $"-c hevc --preset quality --profile main10 " +
            $"--lookahead 32 --aq-temporal " +
            $"--colormatrix bt2020nc --colorprim bt2020 --transfer smpte2084 " +
            $"--audio-copy " +
            (copySubtitles ? "--sub-copy " : "") +
            $"-o \"{outputVideo}\"";
        return args;
    }

    /// <summary>把 HDR 参数限制在规格书允许的范围内（saturation 100-400, contrast 100-400）。</summary>
    public static int ClampParam(int v) => Math.Clamp(v, 100, 400);
}
