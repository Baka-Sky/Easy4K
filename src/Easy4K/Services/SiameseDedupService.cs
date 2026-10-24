using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Easy4K.SFDP;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Windows.Graphics.Imaging;

namespace Easy4K.Services;

/// <summary>SFDP 孪生网络（ONNX）帧去重判决器。
///
/// 模型：Tools\SFDP\model.onnx（FP32，完美模式）/ model_fp16.onnx（FP16，性能模式）。
/// 输入 frame_a / frame_b：(N,1,128,128) 灰度 float32、取值 [0,1]；输出 score：(N,1) 0~1 相似度，
/// 越接近 1 越可能重复。推理优先 DirectML（A/N/Intel 通用），不可用时自动回退 CPU。
///
/// 判决方式：把两帧在同一坐标的 128×128 块成批送进模型（覆盖整帧），
/// 取"最低分"作为这一对帧的相似度 —— 只要有一块明显不同，两帧就不可能完全相同，
/// 方向上是保守的（少删、不误删），与四阶判决器的取舍一致。</summary>
public sealed class SiameseDedupService
{
    /// <summary>模型输入块边长（与训练时的预处理规范一致，不可改）</summary>
    public const int PatchSize = 128;

    /// <summary>单次送模型的帧对数；DirectML 下批量推理比逐对快很多，但过大会撑爆显存</summary>
    private const int ChunkPairs = 64;

    /// <summary>模型目录名（相对 Tools 根）</summary>
    private const string ModelDirName = "SFDP";

    private static readonly object Gate = new();
    private static SiameseDedupService? _cached;
    private static string? _cachedPath;
    private static string? _cachedProvider;

    private readonly InferenceSession _session;

    /// <summary>实际使用的推理后端（DirectML / CPU）</summary>
    public string Provider { get; }

    /// <summary>本次加载的模型文件绝对路径</summary>
    public string ModelPath { get; }

    private SiameseDedupService(string path)
    {
        ModelPath = path;
        _session = CreateSession(path, out var provider);
        Provider = provider;
    }

    /// <summary>按模式取模型文件：完美模式(uhd)=FP32，性能模式=FP16（内部半精度、输入输出仍是 float32）。</summary>
    public static string ModelPathFor(string toolsRoot, string? mode) =>
        Path.Combine(toolsRoot, ModelDirName, mode == "uhd" ? "model.onnx" : "model_fp16.onnx");

    /// <summary>加载并预热模型（首次调用会真正读盘 + 建会话 + 跑一次哑推理）。
    /// 模型文件不存在时返回 null，调用方回退到四阶判决器。</summary>
    public static Task<SiameseDedupService?> WarmupAsync(string modelPath, CancellationToken ct)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            lock (Gate)
            {
                if (_cached is not null && string.Equals(_cachedPath, modelPath, StringComparison.OrdinalIgnoreCase))
                    return _cached;
                if (!File.Exists(modelPath)) return null;

                var sw = Stopwatch.StartNew();
                var svc = new SiameseDedupService(modelPath);
                svc.RunDummy();
                sw.Stop();
                _cached = svc;
                _cachedPath = modelPath;
                _cachedProvider = $"{svc.Provider} / 预热 {sw.ElapsedMilliseconds} ms";
                return svc;
            }
        }, ct);

    /// <summary>已预热会话的后端描述（未预热返回 null）</summary>
    public static string? CachedProvider => _cachedProvider;

    private static InferenceSession CreateSession(string path, out string provider)
    {
        // 先试 DirectML（跨厂商 GPU 统一路径）；显卡不支持 DX12 或缺少 DML 运行库时回退 CPU，
        // 结果不变，只是慢一些。
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

    /// <summary>哑推理：把常量缓冲区的分配、算子融合与 GPU 上传都做一遍，避免第一帧判决时卡顿。</summary>
    private void RunDummy()
    {
        var tensor = new DenseTensor<float>(new[] { 1, 1, PatchSize, PatchSize });
        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("frame_a", tensor),
            NamedOnnxValue.CreateFromTensor("frame_b", tensor)
        });
        _ = results.First(r => r.Name == "score").AsTensor<float>()[0, 0];
    }

    /// <summary>扫描帧目录做判决（与四阶判决器同签名，改由 ONNX 模型判）。</summary>
    public Task<FrameDedupService.Result> AnalyzeAsync(
        string framesDir, FrameDedupService.Options opt, Action<FrameDedupService.AnalysisProgress>? progress,
        CancellationToken ct, bool applySmoothing = true)
    {
        var files = Directory.GetFiles(framesDir, "*.png");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return AnalyzeAsync(files, opt, progress, ct, applySmoothing);
    }

    /// <summary>按给定帧清单做判决。清单顺序即处理顺序，不改动文件；
    /// 涡轮模式按块流式判决时，清单最前面会带上一块的末帧当"对比帧"。</summary>
    public Task<FrameDedupService.Result> AnalyzeAsync(
        IReadOnlyList<string> files, FrameDedupService.Options opt,
        Action<FrameDedupService.AnalysisProgress>? progress, CancellationToken ct,
        bool applySmoothing = true)
        => Task.Run(() => AnalyzeCoreAsync(files, opt, progress, ct, applySmoothing), ct);

    private async Task<FrameDedupService.Result> AnalyzeCoreAsync(
        IReadOnlyList<string> files, FrameDedupService.Options opt,
        Action<FrameDedupService.AnalysisProgress>? progress, CancellationToken ct, bool applySmoothing)
    {
        var threshold = opt.Mode == "uhd" ? 0.5f : 0.6f;
        var decisions = new List<FrameDedupService.FrameDecision>(files.Count);
        byte[]? prevPixels = null;
        var prevW = 0;
        var prevH = 0;
        var dupSoFar = 0;
        var prevPath = "";
        var markedPath = "";
        var markedIndex = 0;

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (pixels, w, h) = await FrameDedupService.LoadBgraAsync(files[i]);

            if (prevPixels is null || w != prevW || h != prevH)
            {
                decisions.Add(new FrameDedupService.FrameDecision(i + 1, false, 0, 0, 0, 0, 0, FrameDedupService.Stage.FirstFrame));
            }
            else
            {
                var score = ScorePair(prevPixels, pixels, w, h);
                var dup = score >= threshold;
                // Mad / Qr 两个数值位在 AI 模式下复用为"相似度分"，日志与预览可直接看到模型给了多少分
                decisions.Add(new FrameDedupService.FrameDecision(
                    i + 1, dup, score, 0, score, 0, 0,
                    dup ? FrameDedupService.Stage.Duplicate : FrameDedupService.Stage.Ai));
                if (dup)
                {
                    dupSoFar++;
                    markedPath = files[i];
                    markedIndex = i + 1;
                }
            }

            prevPixels = pixels;
            prevW = w;
            prevH = h;

            // AI 逐帧要跑"整帧块数"次推理，比四阶判决慢，进度 4 帧刷一次即可
            if (i % 4 == 0 || i == files.Count - 1)
            {
                progress?.Invoke(new FrameDedupService.AnalysisProgress(
                    "AI判决", i + 1, files.Count, dupSoFar,
                    files[i], i + 1,
                    prevPath, i,
                    markedPath, markedIndex));
            }

            prevPath = files[i];
        }

        var lastFrame = files.Count > 0 ? files[files.Count - 1] : "";
        var lastIndex = Math.Max(0, files.Count - 1);
        progress?.Invoke(new FrameDedupService.AnalysisProgress("时序平滑", files.Count, files.Count, dupSoFar,
            lastFrame, files.Count, prevPath, lastIndex, markedPath, markedIndex));
        if (applySmoothing) FrameDedupService.SmoothIsolatedRuns(decisions, opt.MinRunLength);

        var dupCount = decisions.Count(d => d.Duplicate);
        progress?.Invoke(new FrameDedupService.AnalysisProgress("时序平滑", files.Count, files.Count, dupCount,
            lastFrame, files.Count, prevPath, lastIndex, markedPath, markedIndex));

        var expand = new List<int>(decisions.Count);
        for (var i = 0; i < decisions.Count; i++)
            expand.Add(decisions[i].Duplicate && i > 0 ? expand[i - 1] : i + 1);

        return new FrameDedupService.Result(decisions, decisions.Count, dupCount) { ExpandMap = expand };
    }

    /// <summary>一对帧的相似度：整帧按 128×128 分块（同坐标），成批推理后取最低分。
    /// 最低分 = 最不像的那一块 —— 有一块不同就不能算重复帧。</summary>
    private float ScorePair(byte[] a, byte[] b, int width, int height)
    {
        var origins = PatchOrigins(width, height);
        var total = origins.Count;
        var minScore = 1f;
        var batches = (total + ChunkPairs - 1) / ChunkPairs;
        var bufA = new float[ChunkPairs * PatchSize * PatchSize];
        var bufB = new float[ChunkPairs * PatchSize * PatchSize];

        for (var batch = 0; batch < batches; batch++)
        {
            var start = batch * ChunkPairs;
            var count = Math.Min(ChunkPairs, total - start);

            var ta = new DenseTensor<float>(new[] { count, 1, PatchSize, PatchSize });
            var tb = new DenseTensor<float>(new[] { count, 1, PatchSize, PatchSize });
            var plane = PatchSize * PatchSize;

            for (var n = 0; n < count; n++)
            {
                var (x, y) = origins[start + n];
                FillPatch(a, width, height, x, y, bufA);
                FillPatch(b, width, height, x, y, bufB);
                for (var p = 0; p < plane; p++)
                {
                    ta[n, 0, p / PatchSize, p % PatchSize] = bufA[p];
                    tb[n, 0, p / PatchSize, p % PatchSize] = bufB[p];
                }
            }

            using var results = _session.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("frame_a", ta),
                NamedOnnxValue.CreateFromTensor("frame_b", tb)
            });
            var scores = results.First(r => r.Name == "score").AsTensor<float>();
            for (var n = 0; n < count; n++)
            {
                var s = scores[n, 0];
                if (s < minScore) minScore = s;
            }
        }

        return minScore;
    }

    /// <summary>整帧的块起点（覆盖全画面，右/下边缘对齐到最后一整块，不足一整块时靠边复制）。</summary>
    private static List<(int X, int Y)> PatchOrigins(int width, int height)
    {
        var cols = Math.Max(1, (width + PatchSize - 1) / PatchSize);
        var rows = Math.Max(1, (height + PatchSize - 1) / PatchSize);
        var list = new List<(int, int)>(rows * cols);
        for (var r = 0; r < rows; r++)
        {
            var y = Math.Max(0, Math.Min(r * PatchSize, height - PatchSize));
            for (var c = 0; c < cols; c++)
            {
                var x = Math.Max(0, Math.Min(c * PatchSize, width - PatchSize));
                list.Add((x, y));
            }
        }
        return list;
    }

    /// <summary>裁出 128×128 灰度块并归一化到 [0,1]（与训练预处理一致：灰度 → 原分辨率裁剪 → /255）。
    /// 画面不足 128 时按边缘复制补齐，保证输入形状恒定。</summary>
    private static void FillPatch(byte[] bgra, int width, int height, int x0, int y0, float[] dst)
    {
        for (var y = 0; y < PatchSize; y++)
        {
            var sy = Math.Min(Math.Max(y0 + y, 0), height - 1);
            var rowBase = sy * width;
            for (var x = 0; x < PatchSize; x++)
            {
                var sx = Math.Min(Math.Max(x0 + x, 0), width - 1);
                var p = (rowBase + sx) * 4;
                // BGRA 布局：p+2=R, p+1=G, p=B（BT.601 灰度权重）
                var gray = (bgra[p + 2] * 299 + bgra[p + 1] * 587 + bgra[p] * 114) / 1000f;
                dst[y * PatchSize + x] = gray / 255f;
            }
        }
    }
}
