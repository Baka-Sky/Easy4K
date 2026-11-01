using System.Diagnostics;
using System.Globalization;
using System.IO;
using Easy4K.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Easy4K.LuminolEngine;

/// <summary>AI SDR→HDR 重建（HDRTVNet++ AGCM+LE）。
///
/// 与 NVEncC 的 NGX TrueHDR 不是一个东西，但作用相同：按画面内容重建高光、扩展动态范围，
/// 输出 PQ/BT.2020 的 HDR10。模型不依赖 NVIDIA，DirectML 下 A/N/Intel 都能跑。
///
/// 数据流（不落中间帧文件）：
///   ffmpeg 解码 → rgb24 管道 → ONNX 推理 → rgb48le 管道 → ffmpeg 编码 HDR10 (HEVC 10-bit)。
/// 之所以用 rgb48le 而不是 rgb24：模型输出是 PQ 编码的 16 位量化值，用 8 位会直接压掉 HDR 精度。
/// </summary>
public static class HdrAiConverter
{
    /// <summary>模型目录（相对 Tools 根）</summary>
    private const string ModelDir = "HDRTVNet";
    private const string ModelFile = "hdr_ensemble.onnx";

    /// <summary>条件网络输入的下采样倍数（训练时的 cond_scale，不可改）</summary>
    private const int CondScale = 4;

    /// <summary>HDR10 静态元数据（1000 nits 峰值 / 400 nits 平均），与 NVEncC 输出档位一致</summary>
    private const string MasterDisplay =
        "G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1)";

    public static string ModelPathFor(string toolsRoot) => Path.Combine(toolsRoot, ModelDir, ModelFile);

    public static bool IsAvailable(ToolPaths tools) =>
        tools.FFmpegExists && tools.FFprobeExists && File.Exists(ModelPathFor(tools.ToolsRoot));

    /// <summary>把整支视频做一次 AI HDR 重建。strengthPercent：亮度强度 50~150（100 = 模型原始映射）。
    /// progress 回调参数：(已处理帧, 总帧数或 0)。</summary>
    public static Task<bool> ConvertAsync(
        ToolPaths tools, string input, string output, int strengthPercent,
        Action<long, long>? progress, Logger logger, CancellationToken ct)
        => Task.Run(() => ConvertCore(tools, input, output, strengthPercent, progress, logger, ct), ct);

    private static bool ConvertCore(
        ToolPaths tools, string input, string output, int strengthPercent,
        Action<long, long>? progress, Logger logger, CancellationToken ct)
    {
        if (!File.Exists(ModelPathFor(tools.ToolsRoot)))
        {
            logger.Error($"AI HDR：找不到模型 {ModelPathFor(tools.ToolsRoot)}");
            return false;
        }

        var (w, h, fps, frames) = Probe(tools.FFprobeExe, input);
        if (w <= 0 || h <= 0 || fps <= 0)
        {
            logger.Error("AI HDR：无法读取视频分辨率/帧率");
            return false;
        }
        // 模型按 4 倍下采样取条件输入，宽高必须是 4 的倍数
        w = w / CondScale * CondScale;
        h = h / CondScale * CondScale;

        using var session = CreateSession(ModelPathFor(tools.ToolsRoot), out var provider);
        logger.Info($"AI HDR 模型已加载（{provider}），输出 {w}x{h} @ {fps:0.###}fps");

        var cw = w / CondScale;
        var ch = h / CondScale;
        var pixels = w * h;
        var cond = new float[3 * cw * ch];
        var rgb = new byte[pixels * 3];
        var rgb48 = new byte[pixels * 6];

        // 显存策略：1080p 及以下整帧推理；更高的分辨率分块推理。
        // 网络里有 3 次 stride=2 下采样，特征图按整帧算会到 GB 级（4K 能吃掉 6~8GB 显存），
        // 分块后每块固定 1024×576，显存占用不随视频分辨率增长。
        var tiles = BuildTiles(w, h, out var tileW, out var tileH);
        var tiled = tiles.Count > 1;
        var lq = new float[3 * tileW * tileH];
        var acc = new float[3 * pixels];
        var wsum = new float[pixels];
        var tileWeights = BuildTileWeights(tileW, tileH);
        if (tiled)
            logger.Info($"显存保护：整帧 {w}x{h} 改为 {tiles.Count} 块推理（每块 {tileW}x{tileH}）");

        // 亮度强度：在 PQ 线性光域乘增益再编码回 PQ（LUT 预计算，逐像素只查表）
        var lut = BuildPqGainLut(strengthPercent);
        if (Math.Clamp(strengthPercent, 50, 150) != 100)
            logger.Info($"AI HDR 亮度强度：{Math.Clamp(strengthPercent, 50, 150)}%");

        var frameIndex = 0L;
        var sw = Stopwatch.StartNew();

        // 先编码成 MP4（容器会保留 BT.2020 / PQ 标签），再无损转封 MKV：
        // 直接编码到 MKV 时，这个 FFmpeg 的 matroska 封装器会丢掉 primaries/transfer 标签。
        var videoTemp = Path.Combine(Path.GetDirectoryName(output) ?? ".", Path.GetFileNameWithoutExtension(output) + ".ai.mp4");

        using var decode = StartProcess(tools.FFmpegExe,
            $"-hide_banner -nostdin -v error -i \"{input}\" -map 0:v:0 -f rawvideo -pix_fmt rgb24 pipe:1",
            redirectOutput: true);
        using var encode = StartProcess(tools.FFmpegExe,
            BuildEncodeArgs(videoTemp, w, h, fps),
            redirectInput: true);
        var encodeErr = new System.Text.StringBuilder();
        encode.ErrorDataReceived += (_, e) => { if (e.Data is not null) encodeErr.AppendLine(e.Data); };
        encode.BeginErrorReadLine();
        // 解码端的 stderr 也要取走，否则管道写满会让进程卡死
        decode.ErrorDataReceived += (_, _) => { };
        decode.BeginErrorReadLine();

        var stopped = false;
        try
        {
            var stdin = encode.StandardInput.BaseStream;
            var stdout = decode.StandardOutput.BaseStream;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!ReadFull(stdout, rgb, rgb.Length)) break;

                // 条件输入始终取整帧（全局色彩映射必须看到完整画面）
                FillCond(rgb, cond, w, h, cw, ch);
                Array.Clear(acc, 0, acc.Length);
                Array.Clear(wsum, 0, wsum.Length);

                var condTensor = new DenseTensor<float>(cond, new[] { 1, 3, ch, cw });
                foreach (var (x0, y0, tw, th) in tiles)
                {
                    FillLqTile(rgb, lq, w, x0, y0, tw, th);
                    var lqTensor = new DenseTensor<float>(lq, new[] { 1, 3, th, tw });
                    using var results = session.Run(new[]
                    {
                        NamedOnnxValue.CreateFromTensor("lq", lqTensor),
                        NamedOnnxValue.CreateFromTensor("cond", condTensor)
                    });
                    var hdr = results.First(r => r.Name == "hdr").AsTensor<float>();
                    BlendTile(hdr, acc, wsum, tileWeights, w, h, x0, y0, tw, th);
                }

                WriteRgb48(acc, wsum, rgb48, pixels, lut);
                stdin.Write(rgb48, 0, rgb48.Length);

                frameIndex++;
                if (frameIndex % 5 == 0 || frameIndex == frames)
                    progress?.Invoke(frameIndex, frames);
            }
            stdin.Flush();
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }
        catch (Exception ex)
        {
            logger.Error($"AI HDR 推理中断：{ex.Message}");
            stopped = true;
        }
        finally
        {
            try { encode.StandardInput.Close(); } catch { }
            try { if (!decode.HasExited) decode.Kill(true); } catch { }
            try { encode.WaitForExit(120_000); } catch { }
            try { decode.WaitForExit(5_000); } catch { }
        }

        sw.Stop();
        var ok = !stopped && decode.ExitCode == 0 && encode.ExitCode == 0 && File.Exists(videoTemp);
        if (!ok)
        {
            var tail = encodeErr.Length > 0 ? encodeErr.ToString().Trim() : "";
            logger.Error($"AI HDR 失败（decode={decode.ExitCode} encode={encode.ExitCode}）{(tail.Length > 0 ? "\n" + tail : "")}");
        }
        else
        {
            ok = MuxToMkv(tools.FFmpegExe, videoTemp, input, output, logger);
        }

        try { if (File.Exists(videoTemp)) File.Delete(videoTemp); } catch { }
        if (!ok)
        {
            if (File.Exists(output)) try { File.Delete(output); } catch { }
            return false;
        }

        var perFrame = frameIndex > 0 ? sw.Elapsed.TotalSeconds / frameIndex : 0;
        logger.Success($"AI HDR 完成：{frameIndex} 帧，用时 {sw.Elapsed:mm\\:ss}，平均 {perFrame:0.00}s/帧");
        progress?.Invoke(frameIndex, frames > 0 ? frames : frameIndex);
        return true;
    }

    // ===================== 推理 =====================

    private static InferenceSession CreateSession(string path, out string provider)
    {
        try
        {
            var dml = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            dml.AppendExecutionProvider_DML(0);
            var s = new InferenceSession(path, dml);
            provider = "DirectML";
            return s;
        }
        catch (Exception)
        {
            var cpu = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Environment.ProcessorCount
            };
            var s = new InferenceSession(path, cpu);
            provider = "CPU";
            return s;
        }
    }

    /// <summary>整帧 → 1/4 盒式下采样，作为条件网络的全局色彩输入。</summary>
    private static void FillCond(byte[] rgb, float[] cond, int w, int h, int cw, int ch)
    {
        var cn = cw * ch;
        const float inv = 1f / 255f / (CondScale * CondScale);
        for (var y = 0; y < ch; y++)
        {
            var sy = y * CondScale;
            for (var x = 0; x < cw; x++)
            {
                var sx = x * CondScale;
                int r = 0, g = 0, b = 0;
                for (var dy = 0; dy < CondScale; dy++)
                {
                    var row = (sy + dy) * w;
                    for (var dx = 0; dx < CondScale; dx++)
                    {
                        var s = (row + sx + dx) * 3;
                        r += rgb[s];
                        g += rgb[s + 1];
                        b += rgb[s + 2];
                    }
                }
                var q = y * cw + x;
                cond[q] = r * inv;
                cond[cn + q] = g * inv;
                cond[2 * cn + q] = b * inv;
            }
        }
    }

    /// <summary>从整帧里裁一块 → 模型输入（float RGB，planar）。</summary>
    private static void FillLqTile(byte[] rgb, float[] lq, int frameW, int x0, int y0, int tw, int th)
    {
        const float Inv255 = 1f / 255f;
        var n = tw * th;
        for (var y = 0; y < th; y++)
        {
            var srcRow = (y0 + y) * frameW + x0;
            var dstRow = y * tw;
            for (var x = 0; x < tw; x++)
            {
                var s = (srcRow + x) * 3;
                var p = dstRow + x;
                lq[p] = rgb[s] * Inv255;
                lq[n + p] = rgb[s + 1] * Inv255;
                lq[2 * n + p] = rgb[s + 2] * Inv255;
            }
        }
    }

    /// <summary>把一块的推理结果按羽化权重叠加到整帧缓冲（重叠区自动融合，无接缝）。</summary>
    private static void BlendTile(Tensor<float> hdr, float[] acc, float[] wsum, float[] weights,
        int frameW, int frameH, int x0, int y0, int tw, int th)
    {
        var src = hdr as DenseTensor<float>;
        var span = src is not null ? src.Buffer.Span : hdr.ToArray();
        var n = tw * th;
        var framePixels = frameW * frameH;
        for (var y = 0; y < th; y++)
        {
            var gy = y0 + y;
            if (gy >= frameH) break;
            var dstRow = gy * frameW + x0;
            var wRow = y * tw;
            for (var x = 0; x < tw; x++)
            {
                if (x0 + x >= frameW) break;
                var wt = weights[wRow + x];
                var p = dstRow + x;
                var s = wRow + x;
                acc[p] += span[s] * wt;
                acc[framePixels + p] += span[n + s] * wt;
                acc[2 * framePixels + p] += span[2 * n + s] * wt;
                wsum[p] += wt;
            }
        }
    }

    /// <summary>归一化叠加结果 → rgb48le 交错缓冲（按亮度强度 LUT 做 PQ 域增益）。</summary>
    private static void WriteRgb48(float[] acc, float[] wsum, byte[] dst, int pixels, ushort[] lut)
    {
        for (var p = 0; p < pixels; p++)
        {
            var inv = 1f / Math.Max(wsum[p], 1e-6f);
            var d = p * 6;
            Put16(dst, d, acc[p] * inv, lut);
            Put16(dst, d + 2, acc[pixels + p] * inv, lut);
            Put16(dst, d + 4, acc[2 * pixels + p] * inv, lut);
        }
    }

    /// <summary>PQ 码值 → 线性光（0~1 对应 0~10000 nits）。</summary>
    private static double PqToLinear(double n)
    {
        const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128;
        const double c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
        var p = Math.Pow(Math.Clamp(n, 0, 1), 1 / m2);
        var num = Math.Max(p - c1, 0);
        var den = Math.Max(c2 - c3 * p, 1e-10);
        return Math.Pow(num / den, 1 / m1);
    }

    /// <summary>线性光（0~1）→ PQ 码值。</summary>
    private static double LinearToPq(double l)
    {
        const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128;
        const double c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
        var p = Math.Pow(Math.Clamp(l, 0, 1), m1);
        return Math.Pow((c1 + c2 * p) / (1 + c3 * p), m2);
    }

    /// <summary>按亮度强度生成 65536 项查表：PQ → 乘增益 → PQ。</summary>
    private static ushort[] BuildPqGainLut(int strengthPercent)
    {
        var gain = Math.Clamp(strengthPercent, 50, 150) / 100.0;
        var lut = new ushort[65536];
        for (var i = 0; i < lut.Length; i++)
        {
            var lin = PqToLinear(i / 65535.0) * gain;
            lut[i] = (ushort)Math.Round(Math.Clamp(LinearToPq(lin), 0, 1) * 65535.0);
        }
        return lut;
    }

    // ---- 分块参数（显存保护） ----
    private const int TileWidth = 1024;
    private const int TileHeight = 576;
    private const int TileOverlap = 32;
    /// <summary>不超过这个像素数就整帧推理（≈1080p），超过则分块，避免特征图撑爆显存</summary>
    private const long SinglePassPixelLimit = 2_400_000;

    private static List<(int X0, int Y0, int W, int H)> BuildTiles(int w, int h, out int tileW, out int tileH)
    {
        if ((long)w * h <= SinglePassPixelLimit)
        {
            tileW = w;
            tileH = h;
            return new List<(int, int, int, int)> { (0, 0, w, h) };
        }

        tileW = Math.Min(TileWidth, Math.Max(64, w / 16 * 16));
        tileH = Math.Min(TileHeight, Math.Max(64, h / 16 * 16));

        var stepX = Math.Max(16, tileW - TileOverlap * 2);
        var stepY = Math.Max(16, tileH - TileOverlap * 2);
        var list = new List<(int, int, int, int)>();
        for (var y = 0; ; y += stepY)
        {
            var yy = Math.Min(y, h - tileH);
            for (var x = 0; ; x += stepX)
            {
                var xx = Math.Min(x, w - tileW);
                list.Add((xx, yy, tileW, tileH));
                if (xx >= w - tileW) break;
            }
            if (yy >= h - tileH) break;
        }
        return list;
    }

    /// <summary>块的羽化权重：中间 1，靠近块边缘线性降到 0.05（重叠区按权重加权平均）。</summary>
    private static float[] BuildTileWeights(int tw, int th)
    {
        var wgt = new float[tw * th];
        for (var y = 0; y < th; y++)
        {
            var wy = EdgeWeight(y, th);
            for (var x = 0; x < tw; x++)
                wgt[y * tw + x] = wy * EdgeWeight(x, tw);
        }
        return wgt;
    }

    private static float EdgeWeight(int p, int len)
    {
        if (len <= TileOverlap * 2) return 1f;
        var d = Math.Min(p, len - 1 - p);
        return d >= TileOverlap ? 1f : Math.Max(0.05f, (float)d / TileOverlap);
    }

    private static void Put16(byte[] dst, int offset, float value, ushort[] lut)
    {
        var index = (int)MathF.Round(Math.Clamp(value, 0f, 1f) * 65535f);
        var v = lut[index];
        dst[offset] = (byte)(v & 0xFF);
        dst[offset + 1] = (byte)(v >> 8);
    }

    // ===================== ffmpeg =====================

    private static string BuildEncodeArgs(string videoTemp, int w, int h, double fps)
    {
        var rate = fps.ToString("0.######", CultureInfo.InvariantCulture);
        var x265 = "hdr-opt=1:repeat-headers=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc"
                   + $":master-display={MasterDisplay}:max-cll=1000,400";
        return "-hide_banner -v error -f rawvideo -pix_fmt rgb48le " +
               $"-s {w}x{h} -r {rate} -i pipe:0 " +
               "-vf \"scale=in_range=full:out_range=tv\" " +
               "-c:v libx265 -preset fast -crf 18 -pix_fmt yuv420p10le " +
               "-color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -color_range tv " +
               $"-x265-params \"{x265}\" -tag:v hvc1 -movflags +faststart -y \"{videoTemp}\"";
    }

    /// <summary>视频轨（HDR10 MP4）+ 原音轨/字幕 → MKV（不重新编码，只转封装）。</summary>
    private static bool MuxToMkv(string ffmpeg, string videoMp4, string source, string output, Logger logger)
    {
        try
        {
            using var p = StartProcess(ffmpeg,
                $"-hide_banner -v error -i \"{videoMp4}\" -i \"{source}\" " +
                $"-map 0:v:0 -map 1:a? -map 1:s? -c copy -y \"{output}\"");
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);
            if (p.ExitCode == 0 && File.Exists(output)) return true;
            logger.Error($"AI HDR 封装失败（退出码 {p.ExitCode}）{(string.IsNullOrWhiteSpace(err) ? "" : "\n" + err.Trim())}");
            return false;
        }
        catch (Exception ex)
        {
            logger.Error($"AI HDR 封装异常：{ex.Message}");
            return false;
        }
    }

    private static (int w, int h, double fps, long frames) Probe(string ffprobe, string input)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffprobe,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate " +
                                $"-show_entries format=duration -of json \"{input}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            p.Start();
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
                return (0, 0, 0, 0);

            var stream = streams[0];
            var w = stream.TryGetProperty("width", out var we) ? we.GetInt32() : 0;
            var h = stream.TryGetProperty("height", out var he) ? he.GetInt32() : 0;

            var fps = 0.0;
            if (stream.TryGetProperty("r_frame_rate", out var re))
            {
                var parts = (re.GetString() ?? "").Split('/');
                if (parts.Length == 2
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den > 0
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                    fps = num / den;
            }

            // nb_frames 在 mp4 里经常是错的（-c copy 切出来的文件会翻倍），按时长算更可靠
            var frames = 0L;
            if (root.TryGetProperty("format", out var fmt)
                && fmt.TryGetProperty("duration", out var de)
                && double.TryParse(de.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur)
                && dur > 0 && fps > 0)
                frames = (long)Math.Round(dur * fps);

            return (w, h, fps, frames);
        }
        catch
        {
            return (0, 0, 0, 0);
        }
    }

    private static Process StartProcess(string exe, string args, bool redirectOutput = false, bool redirectInput = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var p = new Process { StartInfo = psi };
        p.Start();
        return p;
    }

    /// <summary>把缓冲区读满；返回 false 表示流已结束（最后一帧不足一帧时也返回 false）。</summary>
    private static bool ReadFull(Stream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }
}
