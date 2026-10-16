using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using Easy4K.Models;
using Microsoft.Win32;

namespace Easy4K.Services;

/// <summary>显卡检测。优先用 nvidia-smi（精确显存+驱动版本，只返回 NVIDIA），
/// 失败时用 WMI 兜底——但会过滤掉虚拟显卡（Microsoft Basic Display / Hyper-V / VMware / Virtual 等），
/// 优先返回 NVIDIA 物理显卡，其次显存最大的物理显卡。</summary>
public sealed class GpuDetector
{
    // 虚拟/软件显卡名称关键字（任意命中即视为虚拟显卡，跳过）
    private static readonly string[] VirtualGpuKeywords =
    {
        "microsoft basic display", "microsoft remote display",
        "hyper-v", "virtual", "vmware", "parallels", "virtualbox",
        "qemu", "gdsscope", "softgpu", "mirage", "indeo",
        "rdp", "teamviewer", "anydesk", "spacedesk", "deskdup",
        "oray", "idd", "gameviewer", "todesk", "sunlogin", "indirect display"
    };

    // nvidia-smi 常见安装位置（按顺序尝试）
    private static readonly string[] NvidiaSmiPaths =
    {
        @"C:\Windows\System32\nvidia-smi.exe",
        @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
        "nvidia-smi.exe"
    };

    public GpuInfo Detect()
    {
        var info = new GpuInfo();
        TryNvidiaSmi(info);
        if (!info.IsNvidia) TryWmi(info);
        return info;
    }

    private void TryNvidiaSmi(GpuInfo info)
    {
        foreach (var path in NvidiaSmiPaths)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                var stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout)) continue;

                // 样例: NVIDIA GeForce RTX 3060, 6144, 572.14
                var parts = stdout.Trim().Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                  .First().Split(',');
                if (parts.Length >= 1) info.Name = parts[0].Trim();
                if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var mb)) info.VramMB = mb;
                if (parts.Length >= 3)
                {
                    info.DriverVersion = parts[2].Trim();
                    if (Version.TryParse(parts[2].Trim().Split('.')[0], out var v)) info.DriverMajor = v.Major;
                }
                info.IsNvidia = info.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
                ParseRtxSeries(info);
                if (info.IsNvidia) return; // 拿到 NVIDIA 就结束
            }
            catch
            {
                // 当前路径不可用，试下一个
            }
        }
    }

    private void TryWmi(GpuInfo info)
    {
        try
        {
            // DXGI 64 位精确显存（一次查询，避免循环内重复）
            var dxgiVramMb = (int)(DxgiMemoryQuery.ReadDedicatedVideoMemory() / (1024 * 1024));

            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");
            GpuInfo? bestPhysical = null;     // 非 NVIDIA 的最佳物理显卡
            GpuInfo? bestNvidia = null;       // NVIDIA 物理显卡（兜底，一般已被 nvidia-smi 拿到）

            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                var name = obj["Name"]?.ToString() ?? "";
                if (IsVirtualGpu(name)) continue;   // 跳过虚拟显卡

                var candidate = new GpuInfo { Name = name };
                if (obj["AdapterRAM"] is uint ram)
                    candidate.VramMB = (int)(ram / (1024 * 1024));
                // WMI AdapterRAM 是 uint32，>4GB 显存会溢出（如 8GB 被读成约 4GB）。
                // 依次用注册表 QWORD + DXGI 64 位精确值覆盖，取最大。
                var regVram = ReadVramFromRegistry(name);
                if (regVram > candidate.VramMB) candidate.VramMB = regVram;
                if (dxgiVramMb > candidate.VramMB) candidate.VramMB = dxgiVramMb;
                if (obj["DriverVersion"] is string dv)
                {
                    candidate.DriverVersion = dv;
                    if (Version.TryParse(dv.Split('.')[0], out var v)) candidate.DriverMajor = v.Major;
                }
                candidate.IsNvidia = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
                ParseRtxSeries(candidate);

                if (candidate.IsNvidia)
                {
                    bestNvidia ??= candidate;
                    bestNvidia = candidate; // 取最后一个 NVIDIA
                }
                else
                {
                    // 取显存最大的物理显卡
                    if (bestPhysical is null || candidate.VramMB > bestPhysical.VramMB)
                        bestPhysical = candidate;
                }
            }

            // 优先 NVIDIA，其次显存最大的物理显卡
            var chosen = bestNvidia ?? bestPhysical;
            if (chosen is null) return;
            info.Name = chosen.Name;
            if (info.VramMB == 0) info.VramMB = chosen.VramMB;
            if (string.IsNullOrEmpty(info.DriverVersion)) info.DriverVersion = chosen.DriverVersion;
            if (info.DriverMajor == 0) info.DriverMajor = chosen.DriverMajor;
            info.IsNvidia = chosen.IsNvidia;
            info.IsRtx = chosen.IsRtx;
            info.Series = chosen.Series;
        }
        catch
        {
            // WMI 不可用，静默
        }
    }

    /// <summary>从注册表读取显卡显存（AMD/Intel 的 HardwareInformation.qwMemorySize，QWORD 精确，
    /// 避免 WMI AdapterRAM uint32 对 >4GB 显存的溢出）。返回显存 MB，失败返回 0。</summary>
    private static int ReadVramFromRegistry(string gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return 0;
        try
        {
            const string classGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var baseKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{classGuid}");
            if (baseKey is null) return 0;

            foreach (var sub in baseKey.GetSubKeyNames())
            {
                using var k = baseKey.OpenSubKey(sub);
                if (k is null) continue;
                var desc = (k.GetValue("DriverDesc") ?? k.GetValue("Device Description"))?.ToString() ?? "";
                if (string.IsNullOrEmpty(desc) || !desc.Contains(gpuName, StringComparison.OrdinalIgnoreCase)) continue;

                using var hw = k.OpenSubKey("HardwareInformation");
                if (hw is null) continue;
                if (hw.GetValue("qwMemorySize") is long bytes && bytes > 0)
                    return (int)(bytes / (1024 * 1024));
            }
        }
        catch { }
        return 0;
    }

    /// <summary>判断是否为虚拟/软件显卡</summary>
    private static bool IsVirtualGpu(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var lower = name.ToLowerInvariant();
        return VirtualGpuKeywords.Any(k => lower.Contains(k));
    }

    private static void ParseRtxSeries(GpuInfo info)
    {
        var m = Regex.Match(info.Name, @"RTX\s+(\d{2})", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var series))
        {
            info.IsRtx = true;
            info.Series = series;
        }
    }
}
