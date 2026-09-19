using System.Threading.Tasks;

namespace Easy4K.Services;

/// <summary>第四阶：Farnebäck 稠密光流（论文第四阶的终判依据）。
///
/// 实现路线与 OpenCV calcOpticalFlowFarneback 同源，但用「加权矩最小二乘」直接拟合多项式系数：
///   1) 灰度化 + 高斯金字塔（自粗到细，pyr_scale / levels）
///   2) 多项式展开：每像素邻域用二次多项式 f(x) ≈ xᵀAx + bᵀx + c 拟合。
///      邻域取 |u|,|v| ≤ poly_n/2 的方形窗，权重为可分离高斯（σ = poly_sigma）；
///      6 个基函数 φ = [1, u, v, u², uv, v²] 的加权矩用 6 组可分离相关一次算完，
///      系数 c = M⁻¹·T（M = Σ W·φφᵀ 只与窗尺寸有关，全图共用一个逆矩阵）。
///   3) 位移求解：第二帧可视为第一帧平移 d → b₂ = b₁ - 2A d，故 A·d = -½(b₂-b₁)。
///      把 A 与 (b₂-b₁) 在 winsize 窗口内累加后解 2×2（比逐像素解稳，与论文一致）。
///   4) 迭代：每轮把第一帧的展开场按当前光流 warp 后重解，位移累加（iterations 次）。
///   5) 金字塔上采样：粗层位移 × 1/pyr_scale 作为细层初值。
///
/// 输出 MeanFlow / VarFlow（位移幅度的一阶/二阶统计，单位：像素），供论文判决使用。
/// 说明：本文件不依赖任何 UI 类型，便于在控制台里做闭环验证。</summary>
public static class FarnebackFlow
{
    /// <summary>参数默认值对齐 OpenCV：levels=3, pyr_scale=0.5, winsize=15, iterations=3, poly_n=5, poly_sigma=1.2</summary>
    public sealed record Options(
        int Levels = 3,
        double PyrScale = 0.5,
        int WinSize = 15,
        int Iterations = 3,
        int PolyN = 5,
        double PolySigma = 1.2);

    /// <summary>光流场 + 论文所需的两项统计</summary>
    public sealed class Field
    {
        public required float[] Dx { get; init; }
        public required float[] Dy { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        /// <summary>MeanFlow = 全图位移幅度的平均（像素）</summary>
        public double MeanFlow { get; init; }
        /// <summary>VarFlow = 位移幅度的方差</summary>
        public double VarFlow { get; init; }
    }

    /// <summary>BGRA8 两帧 → 全分辨率稠密光流（第一帧 → 第二帧）。</summary>
    public static Field Compute(byte[] aBgra, byte[] bBgra, int width, int height, Options opt)
    {
        var g1 = ToGray(aBgra, width, height);
        var g2 = ToGray(bBgra, width, height);

        float[]? flowX = null, flowY = null;
        var fw = 0;
        var fh = 0;

        for (var level = opt.Levels - 1; level >= 0; level--)
        {
            var s = Math.Pow(opt.PyrScale, level);
            var w = Math.Max(16, (int)Math.Round(width * s));
            var h = Math.Max(16, (int)Math.Round(height * s));

            var l1 = Resize(g1, width, height, w, h);
            var l2 = Resize(g2, width, height, w, h);

            // 粗层位移放大到本层作为初值（位移量按尺寸比例放大，坐标按比例采样）
            var fx = new float[w * h];
            var fy = new float[w * h];
            if (flowX is not null && fw > 0)
            {
                var kx = w / (double)fw;
                var ky = h / (double)fh;
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var i = y * w + x;
                        fx[i] = (float)(SampleBilinear(flowX, fw, fh, x / kx, y / ky) * kx);
                        fy[i] = (float)(SampleBilinear(flowY!, fw, fh, x / kx, y / ky) * ky);
                    }
            }

            var e1 = PolyExp(l1, w, h, opt);
            var e2 = PolyExp(l2, w, h, opt);
            for (var it = 0; it < opt.Iterations; it++)
                UpdateFlow(e1, e2, fx, fy, w, h, opt);

            flowX = fx;
            flowY = fy;
            fw = w;
            fh = h;
        }

        var n = fw * fh;
        double mean = 0;
        for (var i = 0; i < n; i++) mean += Magnitude(flowX![i], flowY![i]);
        mean /= Math.Max(1, n);
        double var = 0;
        for (var i = 0; i < n; i++)
        {
            var d = Magnitude(flowX![i], flowY![i]) - mean;
            var += d * d;
        }
        var /= Math.Max(1, n);

        return new Field { Dx = flowX!, Dy = flowY!, Width = fw, Height = fh, MeanFlow = mean, VarFlow = var };
    }

    private static double Magnitude(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    // ===================== 多项式展开 =====================

    /// <summary>每个像素的二次多项式系数（A 对称，只用 Axx/Axy/Ayy + Bx/By）</summary>
    private sealed class Exp
    {
        public required float[] Axx { get; init; }
        public required float[] Axy { get; init; }
        public required float[] Ayy { get; init; }
        public required float[] Bx { get; init; }
        public required float[] By { get; init; }
        public required float[] C { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
    }

    /// <summary>加权矩最小二乘拟合二次多项式：T_pq = Σ W·u^p v^q · f，系数 = M⁻¹·T</summary>
    private static Exp PolyExp(float[] src, int w, int h, Options opt)
    {
        var k = Math.Max(1, opt.PolyN / 2);
        var g = Gaussian1D(k, opt.PolySigma);

        // 基函数顺序：1, u, v, u², uv, v²  →  矩 (p,q) 取 (0,0),(1,0),(0,1),(2,0),(1,1),(0,2)
        var m00 = Correlate2(src, w, h, g, g, 0, 0);
        var m10 = Correlate2(src, w, h, g, g, 1, 0);
        var m01 = Correlate2(src, w, h, g, g, 0, 1);
        var m20 = Correlate2(src, w, h, g, g, 2, 0);
        var m11 = Correlate2(src, w, h, g, g, 1, 1);
        var m02 = Correlate2(src, w, h, g, g, 0, 2);

        var inv = MomentInverse(k, g);   // 6×6 逆矩阵（只与窗/权重有关）

        var n = w * h;
        var axx = new float[n];
        var axy = new float[n];
        var ayy = new float[n];
        var bx = new float[n];
        var by = new float[n];
        var c0 = new float[n];

        // 注意：t/r 必须线程私有（放在 Parallel.For 外面会被多线程互相覆盖）
        Parallel.For(0, h,
            () => (new double[6], new double[6]),
            (y, _, scratch) =>
            {
                var (t, r) = scratch;
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    t[0] = m00[i]; t[1] = m10[i]; t[2] = m01[i];
                    t[3] = m20[i]; t[4] = m11[i]; t[5] = m02[i];
                    for (var r0 = 0; r0 < 6; r0++)
                    {
                        double acc = 0;
                        for (var c = 0; c < 6; c++) acc += inv[r0, c] * t[c];
                        r[r0] = acc;
                    }
                    c0[i] = (float)r[0];
                    bx[i] = (float)r[1];
                    by[i] = (float)r[2];
                    axx[i] = (float)r[3];
                    axy[i] = (float)r[4];
                    ayy[i] = (float)r[5];
                }
                return scratch;
            },
            _ => { });

        return new Exp { Axx = axx, Axy = axy, Ayy = ayy, Bx = bx, By = by, C = c0, Width = w, Height = h };
    }

    /// <summary>长度 2k+1 的归一化高斯核</summary>
    private static double[] Gaussian1D(int k, double sigma)
    {
        var n = 2 * k + 1;
        var g = new double[n];
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var u = i - k;
            g[i] = Math.Exp(-(u * u) / (2 * sigma * sigma));
            sum += g[i];
        }
        for (var i = 0; i < n; i++) g[i] /= sum;
        return g;
    }

    /// <summary>可分离相关：out(x,y) = ΣΣ gx(u)·gy(v)·(u/k)^px·(v/k)^py · src(x+u, y+v)，越界按边界复制。
    /// 坐标按半窗宽 k 归一化（Farnebäck 的做法）：平坦区系数不会退化成极小值，
    /// 后面 2×2 求解的条件数才稳定。</summary>
    private static float[] Correlate2(float[] src, int w, int h, double[] g, double[] gy, int px, int py)
    {
        var k = g.Length / 2;
        var tmp = new float[w * h];
        var kx = new double[g.Length];
        for (var i = 0; i < g.Length; i++) kx[i] = g[i] * Math.Pow((i - k) / (double)k, px);

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                double acc = 0;
                for (var u = -k; u <= k; u++)
                {
                    var sx = Math.Clamp(x + u, 0, w - 1);
                    acc += kx[u + k] * src[y * w + sx];
                }
                tmp[y * w + x] = (float)acc;
            }
        });

        var ky = new double[g.Length];
        for (var i = 0; i < g.Length; i++) ky[i] = gy[i] * Math.Pow((i - k) / (double)k, py);

        var dst = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                double acc = 0;
                for (var v = -k; v <= k; v++)
                {
                    var sy = Math.Clamp(y + v, 0, h - 1);
                    acc += ky[v + k] * tmp[sy * w + x];
                }
                dst[y * w + x] = (float)acc;
            }
        });
        return dst;
    }

    /// <summary>M = Σ W·φφᵀ（φ = [1,ũ,ṽ,ũ²,ũṽ,ṽ²]，ũ = u/k）的逆，用高斯-约当求逆。</summary>
    private static double[,] MomentInverse(int k, double[] g)
    {
        var m = new double[6, 6];
        for (var u = -k; u <= k; u++)
            for (var v = -k; v <= k; v++)
            {
                var weight = g[u + k] * g[v + k];
                var un = u / (double)k;
                var vn = v / (double)k;
                var phi = new double[] { 1, un, vn, un * un, un * vn, vn * vn };
                for (var i = 0; i < 6; i++)
                    for (var j = 0; j < 6; j++)
                        m[i, j] += weight * phi[i] * phi[j];
            }
        return Invert6(m);
    }

    private static double[,] Invert6(double[,] a)
    {
        var n = 6;
        var m = new double[n, 2 * n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, n + i] = 1;
        }
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-12) continue;
            if (pivot != col)
                for (var j = 0; j < 2 * n; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
            var d = m[col, col];
            for (var j = 0; j < 2 * n; j++) m[col, j] /= d;
            for (var r = 0; r < n; r++)
            {
                if (r == col) continue;
                var f = m[r, col];
                if (f == 0) continue;
                for (var j = 0; j < 2 * n; j++) m[r, j] -= f * m[col, j];
            }
        }
        var inv = new double[n, n];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++) inv[i, j] = m[i, n + j];
        return inv;
    }

    // ===================== 位移求解 =====================

    /// <summary>一轮迭代：warp 第一帧展开场 → 逐像素 A、Δb → 窗口累加 → 解 2×2 → 位移累加</summary>
    private static void UpdateFlow(Exp e1, Exp e2, float[] fx, float[] fy, int w, int h, Options opt)
    {
        var n = w * h;
        var g11 = new double[n];
        var g12 = new double[n];
        var g22 = new double[n];
        var h1 = new double[n];
        var h2 = new double[n];

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                // 第 2 帧位置 x 的内容来自第 1 帧的 x - d，所以第 1 帧展开场要采样在 (x - fx, y - fy)
                var x1 = x - fx[i];
                var y1 = y - fy[i];

                // 第二帧系数（原地）
                var a2xx = e2.Axx[i]; var a2xy = e2.Axy[i]; var a2yy = e2.Ayy[i];
                var b2x = e2.Bx[i]; var b2y = e2.By[i];

                // 第一帧系数按其位移 warp 过来（双线性采样）
                var a1xx = SampleBilinear(e1.Axx, w, h, x1, y1);
                var a1xy = SampleBilinear(e1.Axy, w, h, x1, y1);
                var a1yy = SampleBilinear(e1.Ayy, w, h, x1, y1);
                var b1x = SampleBilinear(e1.Bx, w, h, x1, y1);
                var b1y = SampleBilinear(e1.By, w, h, x1, y1);

                var axx = 0.5 * (a1xx + a2xx);
                var axy = 0.5 * (a1xy + a2xy);
                var ayy = 0.5 * (a1yy + a2yy);
                var rx = b2x - b1x;
                var ry = b2y - b1y;

                // 加权最小二乘的窗口累加量：Σ(AᵀA) 与 Σ(AᵀΔb)
                g11[i] = axx * axx + axy * axy;
                g12[i] = (axx + ayy) * axy;
                g22[i] = axy * axy + ayy * ayy;
                h1[i] = axx * rx + axy * ry;
                h2[i] = axy * rx + ayy * ry;
            }
        });

        var k = Math.Max(1, opt.PolyN / 2);    // 归一化坐标 → 像素的换算因子
        var win = Math.Max(3, opt.WinSize);
        var s11 = BoxSum(g11, w, h, win);
        var s12 = BoxSum(g12, w, h, win);
        var s22 = BoxSum(g22, w, h, win);
        var t1 = BoxSum(h1, w, h, win);
        var t2 = BoxSum(h2, w, h, win);

        const double incLimit = 16.0;          // 单步位移上限（像素），防病态区数值爆炸
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var a11 = s11[i];
                var a12 = s12[i];
                var a22 = s22[i];
                var det = a11 * a22 - a12 * a12;

                double dx = 0, dy = 0;
                // 条件数守卫：纹理太弱（两特征值都小）或退化（一条边方向不可观测）时不动
                if (det > 1e-6 && det > 1e-3 * a11 * a22)
                {
                    // Δb = -2A(d-f)  →  d-f = -½(AᵀA)⁻¹AᵀΔb，再乘 k 换回像素
                    var tx = -0.5 * k * (a22 * t1[i] - a12 * t2[i]) / det;
                    var ty = -0.5 * k * (a11 * t2[i] - a12 * t1[i]) / det;
                    var mag = Math.Sqrt(tx * tx + ty * ty);
                    if (mag > incLimit)
                    {
                        tx *= incLimit / mag;
                        ty *= incLimit / mag;
                    }
                    dx = tx;
                    dy = ty;
                }
                fx[i] += (float)dx;
                fy[i] += (float)dy;
            }
        });
    }

    /// <summary>窗口积分（积分图）</summary>
    private static double[] BoxSum(double[] src, int w, int h, int win)
    {
        var r = win / 2;
        var sat = new double[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            double rowSum = 0;
            for (var x = 0; x < w; x++)
            {
                rowSum += src[y * w + x];
                sat[(y + 1) * (w + 1) + (x + 1)] = sat[y * (w + 1) + (x + 1)] + rowSum;
            }
        }
        var dst = new double[w * h];
        Parallel.For(0, h, y =>
        {
            var y0 = Math.Clamp(y - r, 0, h - 1);
            var y1 = Math.Clamp(y + r, 0, h - 1);
            for (var x = 0; x < w; x++)
            {
                var x0 = Math.Clamp(x - r, 0, w - 1);
                var x1 = Math.Clamp(x + r, 0, w - 1);
                var a = sat[y0 * (w + 1) + x0];
                var b = sat[y0 * (w + 1) + x1 + 1];
                var c = sat[(y1 + 1) * (w + 1) + x0];
                var d = sat[(y1 + 1) * (w + 1) + x1 + 1];
                dst[y * w + x] = d - b - c + a;
            }
        });
        return dst;
    }

    // ===================== 图像工具 =====================

    private static float[] ToGray(byte[] bgra, int w, int h)
    {
        var n = w * h;
        var g = new float[n];
        for (int i = 0, p = 0; i < n; i++, p += 4)
            g[i] = (bgra[p + 2] * 299 + bgra[p + 1] * 587 + bgra[p] * 114) / 1000f;
        return g;
    }

    /// <summary>双线性缩放（像素中心对齐）</summary>
    private static float[] Resize(float[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new float[dw * dh];
        var kx = sw / (double)dw;
        var ky = sh / (double)dh;
        Parallel.For(0, dh, y =>
        {
            var sy = (y + 0.5) * ky - 0.5;
            for (var x = 0; x < dw; x++)
                dst[y * dw + x] = (float)SampleBilinear(src, sw, sh, (x + 0.5) * kx - 0.5, sy);
        });
        return dst;
    }

    private static double SampleBilinear(float[] src, int w, int h, double x, double y)
    {
        if (w <= 0 || h <= 0) return 0;
        var cx = Math.Clamp(x, 0, w - 1);
        var cy = Math.Clamp(y, 0, h - 1);
        var x0 = (int)Math.Floor(cx);
        var y0 = (int)Math.Floor(cy);
        var x1 = Math.Min(x0 + 1, w - 1);
        var y1 = Math.Min(y0 + 1, h - 1);
        var tx = cx - x0;
        var ty = cy - y0;
        var v00 = src[y0 * w + x0];
        var v10 = src[y0 * w + x1];
        var v01 = src[y1 * w + x0];
        var v11 = src[y1 * w + x1];
        return (v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty;
    }
}
