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
    public string VramText => VramMB > 0 ? $"{VramMB} GB" : "未知";

    /// <summary>RIFE 模型是否需要 8GB+ 显存。规格书：v4.25/v4.26 需要 8GB+；其他 ≥6GB 即可</summary>
    public static bool Requires8Gb(string rifeModel) =>
        rifeModel is not null &&
        (rifeModel.Contains("v4.25", StringComparison.OrdinalIgnoreCase) ||
         rifeModel.Contains("v4.26", StringComparison.OrdinalIgnoreCase));
}
