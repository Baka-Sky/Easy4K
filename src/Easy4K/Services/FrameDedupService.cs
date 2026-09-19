using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace Easy4K.Services;

/// <summary>相邻帧去重（论文「四阶级联判决」）。
/// 第一阶像素差粗筛、第二阶 dHash 细筛、第三阶 QR 分解精判、第四阶 Farnebäck 光流终判。
/// 性能模式 = 前两阶；完美模式 = 四阶全开。
/// 本类只做「判决」并给出保留/回填索引表，**不修改任何文件**；剔除与回填由流水线负责
/// （判为重复的帧不是真删除，只是不进超分/补帧，合并前按 ExpandMap 原样补回，
///  因此成品时长、帧率与音频保持不变）。</summary>
public static class FrameDedupService
{
    /// <summary>单像素差异阈值（论文建议 10）：超过该值才算"这个像素变了"</summary>
    private const int PixelThreshold = 10;

    /// <summary>判决参数。阈值取自论文建议值；完美模式整体收紧一档以压低误删。</summary>
    public sealed record Options(
        string Mode,
        double MadThreshold,
        double RatioThreshold,
        int HashThreshold,
        int MinRunLength,
        double QrThreshold,
        double FlowThreshold,
        double FlowVarThreshold)
    {
        /// <summary>按模式名取参数（performance / uhd）。
        /// 光流阈值取论文建议值 τ_flow=0.5、τ_var=0.1；QR 阈值因归一化修正后重新标定为 1.0 灰阶
        /// （论文的 0.05 对应的是 D_Mink/(K·B) 那套归一化，与这里量纲不同，不能直接照搬）。
        /// 标定依据：1080p 动画 MV 3600 帧、566 个候选帧对，QR 距离分布 P25 0.016 / P50 0.532 /
        /// P75 1.479 / P90 2.136 / max 4.53；阈值从 0.5 扫到 5.0 灰阶，最终判重帧数恒为 102 帧不变，
        /// 差别只在送进光流的对数（τ=1.0 可省掉约 1/3 光流调用），所以取下限附近、按"只挡明显不同的帧对"取值。</summary>
        public static Options FromMode(string? mode) => mode == "uhd"
            ? new Options("uhd", 1.0, 0.006, 4, 3, 1.0, 0.5, 0.1)
            : new Options("performance", 2.0, 0.01, 5, 3, 1.0, 0.5, 0.1);
    }

    /// <summary>判决所处阶（用于统计各阶淘汰量与进度文本）</summary>
    public enum Stage
    {
        /// <summary>首帧或无对比对象</summary>
        FirstFrame = 0,
        /// <summary>第一阶像素差淘汰</summary>
        PixelDiff = 1,
        /// <summary>第二阶 dHash 淘汰</summary>
        Hash = 2,
        /// <summary>第三阶 QR 淘汰</summary>
        Qr = 3,
        /// <summary>第四阶光流淘汰</summary>
        Flow = 4,
        /// <summary>四阶全过 → 判为重复</summary>
        Duplicate = 5,
    }

    /// <summary>阶段短名（进度文本 / 日志用）</summary>
    public static string StageName(Stage s) => s switch
    {
        Stage.FirstFrame => "首帧",
        Stage.PixelDiff => "像素差",
        Stage.Hash => "dHash",
        Stage.Qr => "QR分解",
        Stage.Flow => "光流",
        _ => "判重",
    };

    /// <summary>单帧判决结果（Index 从 1 开始，与 ffmpeg 输出的 00000001.png 对齐）</summary>
    public sealed record FrameDecision(
        int Index, bool Duplicate, double Mad, int Hamming,
        double Qr, double MeanFlow, double VarFlow, Stage Decided);

    /// <summary>整段分析结果</summary>
    public sealed record Result(IReadOnlyList<FrameDecision> Decisions, int Total, int DuplicateCount)
    {
        /// <summary>实际需要送去超分/补帧的帧数</summary>
        public int KeptCount => Total - DuplicateCount;

        /// <summary>去重率（0~1）</summary>
        public double DedupRate => Total <= 0 ? 0 : (double)DuplicateCount / Total;

        /// <summary>回填表：展开后的第 k 帧（1 基）对应原始第几帧（1 基）。长度 = Total</summary>
        public IReadOnlyList<int> ExpandMap { get; init; } = Array.Empty<int>();
    }

    /// <summary>判决相邻两帧是否重复。a/b 为 BGRA8 像素、尺寸相同。</summary>
    public static (bool Duplicate, double Mad, int Hamming, double Qr, double MeanFlow, double VarFlow, Stage Decided) Judge(
        byte[] a, byte[] b, int width, int height, Options opt)
    {
        var pixels = width * height;
        if (pixels <= 0 || a.Length < pixels * 4 || b.Length < pixels * 4)
            return (false, 0, 64, double.MaxValue, 0, 0, Stage.PixelDiff);

        // ============ 第 1 阶：像素差粗筛（灰度化 + 平均绝对差 + 变化像素比） ============
        double sum = 0;
        var changed = 0;
        for (int i = 0, p = 0; i < pixels; i++, p += 4)
        {
            // BGRA 布局：p+2=R, p+1=G, p=B（ITU-R BT.601 灰度权重）
            var ga = (a[p + 2] * 299 + a[p + 1] * 587 + a[p] * 114) / 1000;
            var gb = (b[p + 2] * 299 + b[p + 1] * 587 + b[p] * 114) / 1000;
            var d = Math.Abs(ga - gb);
            sum += d;
            if (d > PixelThreshold) changed++;
        }

        var mad = sum / pixels;
        var ratio = (double)changed / pixels;
        if (mad > opt.MadThreshold || ratio > opt.RatioThreshold)
            return (false, mad, 64, double.MaxValue, 0, 0, Stage.PixelDiff); // 明显不同，直接保留

        // ============ 第 2 阶：dHash 细筛（9×8 灰度水平差分 → 64bit → 汉明距离） ============
        var hamming = HammingDistance(DHash(a, width, height), DHash(b, width, height));
        if (hamming > opt.HashThreshold)
            return (false, mad, hamming, double.MaxValue, 0, 0, Stage.Hash);

        // ============ 性能模式到此结束 ============
        if (opt.Mode != "uhd")
            return (true, mad, hamming, 0, 0, 0, Stage.Duplicate);

        // ============ 第 3 阶：QR 分解精判（8×8 分块 → diag(R) 特征 → Minkowski 距离） ============
        var qr = QrDistance(a, b, width, height);
        if (qr > opt.QrThreshold)
            return (false, mad, hamming, qr, 0, 0, Stage.Qr);

        // ============ 第 4 阶：Farnebäck 光流终判（MeanFlow / VarFlow 双阈值） ============
        var flow = FarnebackFlow.Compute(a, b, width, height, new FarnebackFlow.Options());
        if (flow.MeanFlow > opt.FlowThreshold || flow.VarFlow > opt.FlowVarThreshold)
            return (false, mad, hamming, qr, flow.MeanFlow, flow.VarFlow, Stage.Flow);

        return (true, mad, hamming, qr, flow.MeanFlow, flow.VarFlow, Stage.Duplicate);
    }

    /// <summary>分析进度：当前阶段 + 已处理/总数 + 已判重帧数 + 最近被标记（判为重复）的帧路径</summary>
    public readonly record struct AnalysisProgress(
        string Phase, int Done, int Total, int DuplicateCount, string MarkedFramePath);

    /// <summary>扫描帧目录做判决，返回结果与回填表（不改动文件）。</summary>
    public static async Task<Result> AnalyzeAsync(
        string framesDir, Options opt, Action<AnalysisProgress>? progress, CancellationToken ct)
    {
        var files = Directory.GetFiles(framesDir, "*.png");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var decisions = new List<FrameDecision>(files.Length);
        byte[]? prevPixels = null;
        var prevW = 0;
        var prevH = 0;
        var dupSoFar = 0;
        var batchDeepest = Stage.FirstFrame;   // 本批（16 帧）实际跑到的最深判决阶，用于进度文本
        var markedPath = "";                   // 最近被判为重复的帧（供预览框显示"被标记的图片"）

        for (var i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (pixels, w, h) = await LoadBgraAsync(files[i]);

            // 首帧没有可比对象，永远保留
            if (prevPixels is null || w != prevW || h != prevH)
            {
                decisions.Add(new FrameDecision(i + 1, false, 0, 0, 0, 0, 0, Stage.FirstFrame));
            }
            else
            {
                var (dup, mad, hamming, qr, meanFlow, varFlow, stage) = Judge(prevPixels, pixels, w, h, opt);
                decisions.Add(new FrameDecision(i + 1, dup, mad, hamming, qr, meanFlow, varFlow, stage));
                if (dup)
                {
                    dupSoFar++;
                    markedPath = files[i];        // 本帧与上一帧重复 → 标记本帧
                }
                // 判为重复时"实际跑到的阶"是模式决定的最深一阶（性能=dHash，完美=光流）
                var ran = stage == Stage.Duplicate ? (opt.Mode == "uhd" ? Stage.Flow : Stage.Hash) : stage;
                if (ran > batchDeepest) batchDeepest = ran;
            }

            prevPixels = pixels;
            prevW = w;
            prevH = h;

            if (i % 16 == 0 || i == files.Length - 1)
            {
                progress?.Invoke(new AnalysisProgress($"判决阶段({StageName(batchDeepest)})",
                    i + 1, files.Length, dupSoFar, markedPath));
                batchDeepest = Stage.FirstFrame;
            }
        }

        // 时序平滑：孤立重复（连续长度 < MinRunLength）撤销删除，避免动画刻意的 1 帧顿帧被吃掉
        progress?.Invoke(new AnalysisProgress("时序平滑", files.Length, files.Length, dupSoFar, markedPath));
        SmoothIsolatedRuns(decisions, opt.MinRunLength);

        var dupCount = decisions.Count(d => d.Duplicate);
        progress?.Invoke(new AnalysisProgress("时序平滑", files.Length, files.Length, dupCount, markedPath));
        var expand = new List<int>(decisions.Count);
        for (var i = 0; i < decisions.Count; i++)
        {
            // 重复帧在展开时复用上一帧的源帧号（即"回填"）
            expand.Add(decisions[i].Duplicate && i > 0 ? expand[i - 1] : i + 1);
        }

        return new Result(decisions, decisions.Count, dupCount) { ExpandMap = expand };
    }

    /// <summary>时序平滑：把长度不足 minRun 的连续"重复段"整段改判为保留。</summary>
    private static void SmoothIsolatedRuns(List<FrameDecision> decisions, int minRun)
    {
        var i = 0;
        while (i < decisions.Count)
        {
            if (!decisions[i].Duplicate) { i++; continue; }

            var start = i;
            while (i < decisions.Count && decisions[i].Duplicate) i++;
            var runLength = i - start;

            if (runLength < minRun)
                for (var k = start; k < i; k++)
                    decisions[k] = decisions[k] with { Duplicate = false };
        }
    }

    // ===================== 基础算子 =====================

    /// <summary>dHash：缩到 9×8 灰度，比较水平相邻像素得到 64 位哈希。</summary>
    private static ulong DHash(byte[] bgra, int width, int height)
    {
        const int cols = 9, rows = 8;
        var gray = new int[cols * rows];

        // 盒式采样（每格取落在其范围内的像素平均），比双线性插值便宜且足够稳定
        for (var r = 0; r < rows; r++)
        {
            var y0 = r * height / rows;
            var y1 = Math.Max(y0 + 1, (r + 1) * height / rows);
            for (var c = 0; c < cols; c++)
            {
                var x0 = c * width / cols;
                var x1 = Math.Max(x0 + 1, (c + 1) * width / cols);
                long acc = 0;
                var n = 0;
                for (var y = y0; y < y1 && y < height; y++)
                {
                    var rowBase = y * width;
                    for (var x = x0; x < x1 && x < width; x++)
                    {
                        var p = (rowBase + x) * 4;
                        acc += (bgra[p + 2] * 299 + bgra[p + 1] * 587 + bgra[p] * 114) / 1000;
                        n++;
                    }
                }
                gray[r * cols + c] = n > 0 ? (int)(acc / n) : 0;
            }
        }

        ulong hash = 0;
        var bit = 0;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols - 1; c++, bit++)
                if (gray[r * cols + c] > gray[r * cols + c + 1])
                    hash |= 1UL << bit;
        return hash;
    }

    private static int HammingDistance(ulong a, ulong b)
        => System.Numerics.BitOperations.PopCount(a ^ b);

    /// <summary>第三阶：QR 分解精判（对应论文第 5 节）。
    /// 流程：图像按 8×8 分块 → 每块 QR 分解 → 取 R 对角线当特征 v_k = diag(R_k)（长度 B=8）
    /// → 全部 K 块拼起来算 p=2 的 Minkowski 距离 D_Mink。
    ///
    /// 归一化说明（与论文的唯一差异，属修正而非改动算法）：
    ///   论文写的是 D_norm = D_Mink/(K·B)。但 D_Mink 是"平方和开根号"，随特征值个数的**平方根**增长，
    ///   而分母是特征值个数本身（1080p = 32400 块 × 8 = 259200），结果被压到 1e-3 量级；
    ///   阈值 0.05 换算过去等价于"平均每个特征值可以差 25 个灰阶"，真实近似帧只差 0.5~4.5 灰阶
    ///   → 该阶永远判重复、形同虚设（分辨率越高越严重）。
    ///   这里改为除以 √(K·B)：结果即"特征值的 RMS 灰阶差"，量纲直观、与分辨率无关，阈值可实测标定。
    ///   实测（1080p 动画 MV 3600 帧）：候选帧对落在 0 ~ 4.5 灰阶，中位 0.53，阈值取 2.0。
    /// 说明：论文的 R 来自 LAPACK 风格 QR，对角线符号随数据翻转；这里用改进 Gram-Schmidt 取非负对角线，
    /// 仍是 R 的对角线但不会因符号翻转让距离突变。</summary>
    private static double QrDistance(byte[] a, byte[] b, int width, int height)
    {
        const int B = 8;
        var blocksX = width / B;
        var blocksY = height / B;
        if (blocksX <= 0 || blocksY <= 0) return double.MaxValue;

        var ma = new double[B * B];
        var mb = new double[B * B];
        var q = new double[B * B];
        var tmp = new double[B];
        var diagA = new double[B];
        var diagB = new double[B];

        double sumSq = 0;
        var k = 0;

        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                var x0 = bx * B;
                var y0 = by * B;
                for (var yy = 0; yy < B; yy++)
                {
                    var rowBase = (y0 + yy) * width;
                    for (var xx = 0; xx < B; xx++)
                    {
                        var p = (rowBase + x0 + xx) * 4;
                        var i = yy * B + xx;
                        ma[i] = (a[p + 2] * 299 + a[p + 1] * 587 + a[p] * 114) / 1000.0;
                        mb[i] = (b[p + 2] * 299 + b[p + 1] * 587 + b[p] * 114) / 1000.0;
                    }
                }

                QrDiagonal(ma, B, q, tmp, diagA);
                QrDiagonal(mb, B, q, tmp, diagB);

                double blockSq = 0;
                for (var t = 0; t < B; t++)
                {
                    var d = diagA[t] - diagB[t];
                    blockSq += d * d;
                }
                sumSq += blockSq;
                k++;
            }
        }

        if (k == 0) return double.MaxValue;
        // 归一化：除以 √(K·B) → 特征值的 RMS 灰阶差（见方法注释）
        return Math.Sqrt(sumSq) / Math.Sqrt((double)k * B);
    }

    /// <summary>改进 Gram-Schmidt 求 R 的对角线（只需 diag，无需保留完整 R）。
    /// 8×8 小矩阵做一遍再正交化即可。
    /// 按列处理：对第 j 列减去它在前面各正交列上的投影，剩下的模长就是 r_jj。</summary>
    private static void QrDiagonal(double[] m, int B, double[] q, double[] tmp, double[] diag)
    {
        for (var j = 0; j < B; j++)
        {
            for (var i = 0; i < B; i++) tmp[i] = m[i * B + j];
            for (var pass = 0; pass < 2; pass++)
                for (var p = 0; p < j; p++)
                {
                    double dot = 0;
                    for (var i = 0; i < B; i++) dot += q[i * B + p] * tmp[i];
                    for (var i = 0; i < B; i++) tmp[i] -= dot * q[i * B + p];
                }
            double norm = 0;
            for (var i = 0; i < B; i++) norm += tmp[i] * tmp[i];
            norm = Math.Sqrt(norm);
            diag[j] = norm;
            if (norm > 1e-12)
                for (var i = 0; i < B; i++) q[i * B + j] = tmp[i] / norm;
            else
                for (var i = 0; i < B; i++) q[i * B + j] = 0;
        }
    }

    /// <summary>读取 PNG 为 BGRA8 像素（用系统自带的 WIC 解码，不引入第三方依赖）。</summary>
    private static async Task<(byte[] Pixels, int Width, int Height)> LoadBgraAsync(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        return (data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }
}
