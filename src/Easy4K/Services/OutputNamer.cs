using System.IO;
using Easy4K.Models;

namespace Easy4K.Services;

/// <summary>按规格书第十二章生成输出文件名。规则：
/// 无超分无补帧 → {name}_SDR.mkv
/// 仅超分        → {name}_{resName}_{origFps}fps_SDR.mkv
/// 超分+补帧     → {name}_{resName}_{targetFps}fps_SDR.mkv
/// HDR 输出      → 把 _SDR 换成 _HDR
/// 嵌入音频      → 追加 _音频嵌入</summary>
public static class OutputNamer
{
    public static string Build(string inputVideo, bool superRes, bool interpolation, bool hdr,
        int outWidth, int outHeight, double targetFps, bool audioEmbedded)
    {
        var name = Path.GetFileNameWithoutExtension(inputVideo);
        var resName = VideoInfo.ResolutionName(outWidth, outHeight);

        string tag;
        if (!superRes && !interpolation)
        {
            tag = ""; // 仅 _SDR/_HDR，不带分辨率/帧率
        }
        else
        {
            var fps = interpolation ? targetFps : targetFps; // 调用方按是否补帧传入正确 fps
            tag = $"_{resName}_{(int)Math.Round(fps)}fps";
        }

        var mode = hdr ? "HDR" : "SDR";
        var audio = audioEmbedded ? "_音频嵌入" : "";
        return $"{name}{tag}_{mode}{audio}.mkv";
    }
}
