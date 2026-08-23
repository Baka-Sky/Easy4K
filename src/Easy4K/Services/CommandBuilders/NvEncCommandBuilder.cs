using System.IO;

namespace Easy4K.Services.CommandBuilders;

/// <summary>NVEncC64 SDR→HDR 命令构建。
/// --vpp-ngx-truehdr 需要 NVEncC ≥ 9.32 (BUG-05) 且 NVIDIA RTX 20/30/40/50 系列 + 驱动 ≥ 570。
/// 饱和度/对比度默认 200/200（规格书）。</summary>
public static class NvEncCommandBuilder
{
    /// <summary>SDR → HDR10 转换。</summary>
    public static string Build(string inputVideo, string outputVideo, int saturation, int contrast)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputVideo)!);

        var args =
            $"-i \"{inputVideo}\" " +
            $"--vpp-ngx-truehdr saturation={saturation},contrast={contrast} " +
            $"-c hevc --preset quality --profile main10 " +
            $"--lookahead 32 --aq-temporal " +
            $"--colormatrix bt2020nc --colorprim bt2020 --transfer smpte2084 " +
            $"--audio-copy " +
            $"-o \"{outputVideo}\"";
        return args;
    }

    /// <summary>把 HDR 参数限制在规格书允许的范围内（saturation 100-400, contrast 100-400）。</summary>
    public static int ClampParam(int v) => Math.Clamp(v, 100, 400);
}
