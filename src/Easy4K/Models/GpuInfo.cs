namespace Easy4K.Models;

/// <summary>显卡检测结果。NVEncC 仅支持 NVIDIA RTX 20/30/40/50 系列</summary>
public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public int VramMB { get; set; }
    public bool IsNvidia { get; set; }
    public bool IsRtx { get; set; }
    public int Series { get; set; }       // 20/30/40/50，0 表示未知或非 RTX
    public string DriverVersion { get; set; } = "";
    public int DriverMajor { get; set; }  // 解析后的主版本，570 → 570

    public bool SupportsHdr => IsNvidia && IsRtx && Series >= 20;

    /// <summary>是否 AMD 显卡（Radeon / AMD / ATI）。由名字推断而不是单独探测：
    /// 显卡名字是唯一可靠来源，探测流程里已按 NVIDIA 分支聚合，再加一条独立探测反而容易与名字不一致。
    /// 用于「使用 AMD AMF 编码」这类只对 A 卡开放的选项。</summary>
    public bool IsAmd => !IsNvidia &&
        (Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("ATI ", StringComparison.OrdinalIgnoreCase));

    /// <summary>是否 Intel 显卡/核显（Arc / Iris / Intel）。同样按名字推断，用于界面提示"这张卡会用 QSV"。</summary>
    public bool IsIntel => !IsNvidia && !IsAmd &&
        (Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("Arc ", StringComparison.OrdinalIgnoreCase) ||
         Name.Contains("Iris", StringComparison.OrdinalIgnoreCase));

    /// <summary>显存文本（VramMB 单位是 MB，按 GB 展示：6144MB → "6 GB"）</summary>
    public string VramText => VramMB > 0 ? $"{VramMB / 1024.0:0.#} GB" : "未知";

    /// <summary>RIFE 模型是否需要 8GB+ 显存。规格书：v4.25/v4.26 需要 8GB+；其他 ≥6GB 即可</summary>
    public static bool Requires8Gb(string rifeModel) =>
        rifeModel is not null &&
        (rifeModel.Contains("v4.25", StringComparison.OrdinalIgnoreCase) ||
         rifeModel.Contains("v4.26", StringComparison.OrdinalIgnoreCase));
}
