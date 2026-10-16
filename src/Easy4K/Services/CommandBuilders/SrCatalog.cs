namespace Easy4K.Services.CommandBuilders;

/// <summary>超分模型目录：把「算法（种类）→ 模型（版本）」整张表集中在一处，
/// 供 UI 下拉框、命令构建、处理编排共用。
///
/// 术语与补帧那边保持一致的两级：
///   · 种类 = 超分算法（Real-ESRGAN / Real-CUGAN / RealSR / SRMD / waifu2x）
///   · 模型 = 该算法下的具体权重（含倍率与降噪档）
///
/// 全部算法都走 ncnn + Vulkan，**A 卡 / N 卡 / 核显通用**，不需要 CUDA。
/// ExeKey 用于从 ToolPaths 取对应引擎的 exe；ArgsTemplate 里 {scale} 会被当前倍率替换。</summary>
public static class SrCatalog
{
    /// <summary>一个超分模型（「模型」下拉框里的一项）</summary>
    public sealed record Variant(
        string Name,
        IReadOnlyList<int> Scales,
        string ArgsTemplate,
        string Scene,
        int Stars)
    {
        /// <summary>该模型是否支持指定倍率</summary>
        public bool Supports(int scale) => Scales.Contains(scale);
    }

    /// <summary>一个超分算法（「种类」下拉框里的一项 = 一个引擎）</summary>
    public sealed record Family(
        string Label,
        string ExeKey,
        string Scene,
        int Stars,
        IReadOnlyList<Variant> Variants);

    /// <summary>算法顺序即 UI 顺序：最通用的 Real-ESRGAN 打头，其后按「动漫 → 实拍 → 轻量」排列</summary>
    public static IReadOnlyList<Family> Families { get; } = new Family[]
    {
        new("Real-ESRGAN（ncnn，动漫/实拍通用）", "esrgan",
            "动漫与实拍通吃 · 速度最快 · 最稳的默认选择",
            4,
            new Variant[]
            {
                new("realesr-animevideov3-x2", new[] { 2 }, "-n realesr-animevideov3-x2 -s 2",
                    "动漫 2x，番剧/1080p 片源最常用", 5),
                new("realesr-animevideov3-x3", new[] { 3 }, "-n realesr-animevideov3-x3 -s 3",
                    "动漫 3x，720p 直上 2160p", 4),
                new("realesr-animevideov3-x4", new[] { 4 }, "-n realesr-animevideov3-x4 -s 4",
                    "动漫 4x，线条保持干净", 4),
                new("realesrgan-x4plus", new[] { 4 }, "-n realesrgan-x4plus -s 4",
                    "实拍 4x，纹理保守、不易出鬼影", 3),
                new("realesrgan-x4plus-anime", new[] { 4 }, "-n realesrgan-x4plus-anime -s 4",
                    "动漫 4x，色块更纯净", 3),
            }),

        new("Real-CUGAN（ncnn，动漫）", "cugan",
            "动漫最强 · 降噪最狠 · 老番脏源首选；不建议用于实拍",
            5,
            new Variant[]
            {
                new("cugan-se-conservative", new[] { 2, 3, 4 }, "-m models-se -n -1 -s {scale}",
                    "标准模型·保守档，几乎不改原画，最稳", 5),
                new("cugan-se-nodenoise", new[] { 2, 3, 4 }, "-m models-se -n 0 -s {scale}",
                    "标准模型·不降噪，片源干净时保细节", 5),
                // models-se 只在 2x 下提供了 denoise1x/denoise2x；3x/4x 只有 conservative(‑1)/no-denoise(0)/denoise3x
                new("cugan-se-denoise1x", new[] { 2 }, "-m models-se -n 1 -s {scale}",
                    "标准模型·轻度降噪（仅 2x）", 5),
                new("cugan-se-denoise2x", new[] { 2 }, "-m models-se -n 2 -s {scale}",
                    "标准模型·中度降噪（仅 2x）", 5),
                new("cugan-se-denoise3x", new[] { 2, 3, 4 }, "-m models-se -n 3 -s {scale}",
                    "标准模型·重度降噪，DVD/老番脏源", 5),
                new("cugan-pro-conservative", new[] { 2, 3 }, "-m models-pro -n -1 -s {scale}",
                    "增强模型·保守档，比 SE 更锐", 4),
                new("cugan-pro-nodenoise", new[] { 2, 3 }, "-m models-pro -n 0 -s {scale}",
                    "增强模型·不降噪", 4),
                new("cugan-pro-denoise3x", new[] { 2, 3 }, "-m models-pro -n 3 -s {scale}",
                    "增强模型·重度降噪", 4),
                new("cugan-nose-nodenoise", new[] { 2 }, "-m models-nose -n 0 -s {scale}",
                    "极简模型，最快，完全不降噪", 4),
            }),

        new("RealSR（ncnn，实拍）", "realsr",
            "实拍照片/视频专用 · 细节最丰富；只支持 4x",
            4,
            new Variant[]
            {
                new("realsr-df2k", new[] { 4 }, "-m models-DF2K -s 4",
                    "实拍 4x，DF2K 训练，细节最强", 4),
                new("realsr-df2k-jpeg", new[] { 4 }, "-m models-DF2K_JPEG -s 4",
                    "实拍 4x，专治 JPEG 压缩伪影", 4),
            }),

        new("SRMD（ncnn，实拍）", "srmd",
            "实拍轻量路线 · 速度快、显存低；画质不如 RealSR",
            3,
            new Variant[]
            {
                new("srmd-x2", new[] { 2 }, "-m models-srmd -n 3 -s 2",
                    "实拍 2x，速度快，中等降噪", 3),
                new("srmd-x3", new[] { 3 }, "-m models-srmd -n 3 -s 3",
                    "实拍 3x，中等降噪", 3),
                new("srmd-x4", new[] { 4 }, "-m models-srmd -n 3 -s 4",
                    "实拍 4x，中等降噪", 3),
                new("srmdnf-x2", new[] { 2 }, "-m models-srmd -n -1 -s 2",
                    "实拍 2x，不降噪（干净片源）", 3),
                new("srmdnf-x3", new[] { 3 }, "-m models-srmd -n -1 -s 3",
                    "实拍 3x，不降噪（干净片源）", 3),
                new("srmdnf-x4", new[] { 4 }, "-m models-srmd -n -1 -s 4",
                    "实拍 4x，不降噪（干净片源）", 3),
            }),

        new("waifu2x（ncnn，动漫）", "waifu2x",
            "动漫元老级算法 · 线条干净但速度慢；只支持 2x / 4x",
            3,
            new Variant[]
            {
                new("waifu2x-cunet", new[] { 2, 4 }, "-m models-cunet -n 1 -s {scale}",
                    "cunet 模型，质量最好但也最慢", 3),
                new("waifu2x-anime", new[] { 2, 4 }, "-m models-upconv_7_anime_style_art_rgb -n 1 -s {scale}",
                    "动漫向，线条更干净，速度快", 3),
                new("waifu2x-photo", new[] { 2, 4 }, "-m models-upconv_7_photo -n 1 -s {scale}",
                    "实拍向，速度最快", 3),
            }),
    };

    /// <summary>「种类」下拉框的显示文本</summary>
    public static IReadOnlyList<string> FamilyLabels { get; } = Families.Select(f => f.Label).ToList();

    /// <summary>按索引取算法；越界返回 null</summary>
    public static Family? At(int index) =>
        index >= 0 && index < Families.Count ? Families[index] : null;

    /// <summary>跨族按模型名反查（同时给出所属算法）；找不到返回 null</summary>
    public static (Family Family, Variant Variant)? Find(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return null;
        foreach (var f in Families)
        {
            var v = f.Variants.FirstOrDefault(x => x.Name == modelName);
            if (v is not null) return (f, v);
        }
        return null;
    }

    /// <summary>模型名 → 所属算法在 Families 里的索引；未知返回 0。
    /// 启动时用它把「算法」下拉框对齐到配置里保存的模型，避免算法显示 A 而模型是 B 引擎的。</summary>
    public static int FamilyIndexOf(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return 0;
        for (var i = 0; i < Families.Count; i++)
            if (Families[i].Variants.Any(v => v.Name == modelName)) return i;
        return 0;
    }

    /// <summary>模型所支持的倍率；未知模型返回空</summary>
    public static IReadOnlyList<int> ScalesOf(string? modelName) =>
        Find(modelName)?.Variant.Scales ?? Array.Empty<int>();

    /// <summary>按「算法索引 + 当前倍率」筛出可选模型名（供 UI 重建下拉框）</summary>
    public static IReadOnlyList<string> ModelsFor(int familyIndex, int scale)
    {
        var f = At(familyIndex);
        if (f is null) return Array.Empty<string>();
        return f.Variants.Where(v => v.Supports(scale)).Select(v => v.Name).ToList();
    }

    /// <summary>模型名 → 适合场景；未知道模型返回 null</summary>
    public static string? SceneOf(string? modelName) => Find(modelName)?.Variant.Scene;

    /// <summary>模型名 → 推荐星级；未知模型返回 0</summary>
    public static int StarsOf(string? modelName) => Find(modelName)?.Variant.Stars ?? 0;
}
