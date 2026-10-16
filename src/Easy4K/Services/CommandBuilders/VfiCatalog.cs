namespace Easy4K.Services.CommandBuilders;

/// <summary>补帧模型目录：把「种类（模型族）→ 模型（版本）」整张表集中在一处，
/// 供 UI 下拉框、命令构建、处理编排共用，避免三处各写一份映射导致对不上。
///
/// 术语固定为两级：
///   · 种类 = 模型族（NCNN-RIFE / RIFE / DRBA / GMFSS / GIMM-VFI）
///   · 模型 = 该族下的具体版本（如 DRBA_RIFE_v4.26、GMFSS_Fortuna、GIMM-VFI-R-P）
///
/// DriverKey 是 Tools\FrameInterp\vfi_driver.py 里 --model 的取值；
/// 空字符串表示这个族走 ncnn 轨（rife-ncnn-vulkan，Vulkan，全显卡通用）。
/// Stars 是 1-5 的推荐度，UI 上在补帧设置面板右下角与「适合场景」一起显示。</summary>
public static class VfiCatalog
{
    /// <summary>一个补帧版本（「模型」下拉框里的一项）</summary>
    public sealed record Variant(string Name, string DriverKey, string Scene, int Stars)
    {
        /// <summary>是否属于 PyTorch 轨（依赖 CUDA / cupy，仅 NVIDIA 显卡可用）</summary>
        public bool IsTorch => DriverKey.Length > 0;
    }

    /// <summary>一个补帧种类（「种类」下拉框里的一项，等于一个模型族）。
    /// Variants 为空表示这个族的模型列表是运行时扫目录得到的（目前只有 NCNN-RIFE）。</summary>
    public sealed record Family(string Label, string Scene, int Stars, IReadOnlyList<Variant> Variants)
    {
        /// <summary>该族是否整族都属 PyTorch 轨；模型列表是动态扫目录的族按非 PyTorch 处理</summary>
        public bool IsTorch => Variants.Count > 0 && Variants.All(v => v.IsTorch);
    }

    /// <summary>种类顺序即 UI 顺序：ncnn 打头（全显卡可用），其后依次是 PyTorch 各模型族</summary>
    public static IReadOnlyList<Family> Families { get; } = new Family[]
    {
        new("NCNN-RIFE（Vulkan，全显卡通用）",
            "全显卡通用 · 速度最快 · 显存占用低；画质略逊于 PyTorch 版 RIFE",
            3,
            Array.Empty<Variant>()),

        new("RIFE（PyTorch，仅 NVIDIA）",
            "实拍为主 · 通用性最好 · 速度快；动漫表现一般",
            4,
            new Variant[]
            {
                new("RIFE 4.26",       "rife_426",       "上游最新版，实拍/动漫通用", 4),
                new("RIFE 4.25",       "rife_425",       "上游默认推荐，实拍的均衡之选", 4),
                new("RIFE 4.18",       "rife_418",       "开启双向动态光流，实拍大运动更稳", 4),
                new("RIFE 4.26 heavy", "rife_426_heavy", "社区增强版，细节更锐利、略慢", 4),
            }),

        new("DRBA（PyTorch，仅 NVIDIA）",
            "动漫专用 · 保留原画节奏与作画完整性；不适合实拍",
            5,
            new Variant[]
            {
                new("DRBA_RIFE_v4.26",   "drba_rife",        "最快；刷番、实时预览", 4),
                new("DRBA_GMFSS_pg",     "drba_gmfss",       "慢但稳；动画高质量导出", 5),
                new("DRBA_GMFSS_union",  "drba_gmfss_union", "动漫 + 实拍混合素材", 4),
            }),

        new("GMFSS（PyTorch，仅 NVIDIA）",
            "动漫专用 · 画质最高 · 速度最慢、显存占用大；不建议 4K 以上补帧",
            5,
            new Variant[]
            {
                new("GMFSS_Fortuna",       "gmfss_fortuna",       "动漫最强，线条与色块最稳", 5),
                new("GMFSS_Fortuna_union", "gmfss_fortuna_union", "动漫/实拍通用，纹理保持更好", 4),
                new("GMFSS 初代",           "gmfss_basic",         "老模型，稳定性好、速度慢", 3),
            }),

        new("GIMM-VFI（PyTorch，仅 NVIDIA）",
            "实拍与动漫通用 · 隐式运动建模、支持任意时刻插值；显存占用高",
            4,
            new Variant[]
            {
                new("GIMM-VFI-R-P", "gimm_r_p", "官方默认：RAFT 主干 + LPIPS 感知增强", 5),
                new("GIMM-VFI-F-P", "gimm_f_p", "FlowFormer 主干 + 感知增强，大运动更稳", 4),
                new("GIMM-VFI-R",   "gimm_r",   "RAFT 主干，无感知增强（指标向）", 4),
                new("GIMM-VFI-F",   "gimm_f",   "FlowFormer 主干，无感知增强（指标向）", 3),
            }),
    };

    /// <summary>「种类」下拉框的显示文本</summary>
    public static IReadOnlyList<string> FamilyLabels { get; } =
        Families.Select(f => f.Label).ToList();

    /// <summary>按索引取种类；越界返回 null</summary>
    public static Family? At(int index) =>
        index >= 0 && index < Families.Count ? Families[index] : null;

    /// <summary>模型名 → 所属模型族在 Families 里的索引；找不到（包括 NCNN 族的模型）返回 0。
    /// 启动时用它把「种类」下拉框对齐到配置里保存的模型 —— 否则选了 PyTorch 轨模型（RIFE / DRBA /
    /// GMFSS / GIMM-VFI）重启后会被打回 NCNN 族，而且回落的选中值还会把 DefaultIfModel 覆盖掉。</summary>
    public static int FamilyIndexOf(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return 0;
        for (var i = 0; i < Families.Count; i++)
            if (Families[i].Variants.Any(v => v.Name == modelName)) return i;
        return 0;
    }

    /// <summary>该种类是否整体属于 PyTorch 轨（仅 NVIDIA 可用）</summary>
    public static bool IsTorchFamily(int index) => At(index)?.IsTorch == true;

    /// <summary>按「种类索引 + 模型名」反查版本；NCNN 族或名字对不上时返回 null</summary>
    public static Variant? Find(int familyIndex, string? modelName) =>
        string.IsNullOrEmpty(modelName)
            ? null
            : At(familyIndex)?.Variants.FirstOrDefault(v => v.Name == modelName);

    /// <summary>跨族按模型名查找版本（用于按名字判断是否 PyTorch 轨）</summary>
    public static Variant? FindAny(string? modelName) =>
        string.IsNullOrEmpty(modelName)
            ? null
            : Families.SelectMany(f => f.Variants).FirstOrDefault(v => v.Name == modelName);

    /// <summary>模型名 → vfi_driver 的 --model 取值；ncnn 模型返回空串</summary>
    public static string DriverKey(string? modelName) => FindAny(modelName)?.DriverKey ?? "";

    /// <summary>模型名 → 适合场景文字；NCNN 族返回 null（由调用方补 ncnn 的说明）</summary>
    public static string? SceneOf(string? modelName) => FindAny(modelName)?.Scene;

    /// <summary>模型名 → 推荐星级；NCNN 族返回 0（由调用方回落到族星级）</summary>
    public static int StarsOf(string? modelName) => FindAny(modelName)?.Stars ?? 0;

    /// <summary>该模型是否走 PyTorch 轨（依赖 CUDA / cupy，仅 NVIDIA 可用）</summary>
    public static bool IsTorchModel(string? modelName) => DriverKey(modelName).Length > 0;
}
