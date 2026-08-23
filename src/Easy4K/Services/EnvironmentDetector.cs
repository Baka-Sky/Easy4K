using System.Diagnostics;
using System.Text.RegularExpressions;
using Easy4K.Models;

namespace Easy4K.Services;

/// <summary>环境检测：工具是否齐全 + 版本（特别是 NVEncC ≥ 9.32 才支持 --vpp-ngx-truehdr）。
/// 输出一个检测结果列表，UI 直接展示。</summary>
public sealed class EnvironmentDetector
{
    private readonly ToolPaths _tools;
    private readonly GpuDetector _gpu;
    public EnvironmentDetector(ToolPaths tools, GpuDetector gpu) { _tools = tools; _gpu = gpu; }

    public GpuInfo Gpu => _gpu.Detect();

    public List<CheckResult> CheckAll()
    {
        var list = new List<CheckResult>
        {
            Check("FFmpeg", _tools.FFmpegExists, extra: _tools.FFmpegExists ? GetVersion(_tools.FFmpegExe, "-version") : ""),
            Check("FFprobe", _tools.FFprobeExists, extra: _tools.FFprobeExists ? GetVersion(_tools.FFprobeExe, "-version") : ""),
            Check("Real-ESRGAN", _tools.RealEsrganExists, extra: _tools.RealEsrganExists ? GetVersion(_tools.RealEsrganExe, "-v") : ""),
            Check("RIFE", _tools.RifeExists, extra: _tools.RifeExists ? GetVersion(_tools.RifeExe, "-v") : "")
        };

        // NVEncC：需要版本 ≥ 9.32 才支持 --vpp-ngx-truehdr (BUG-05)
        if (_tools.NvEncExists)
        {
            var ver = GetNvEncVersion(_tools.NvEncExe);
            var ok = ParseNvEncVersion(ver) >= new Version(9, 32);
            list.Add(Check("NVEncC64", ok, extra: $"v{ver}", required: false,
                hint: ok ? "" : "版本需 ≥ 9.32 才支持 --vpp-ngx-truehdr，请升级"));
        }
        else
        {
            list.Add(Check("NVEncC64", false, extra: "未安装", required: false, hint: "仅 SDR→HDR 功能需要"));
        }

        var gpu = _gpu.Detect();
        list.Add(Check($"显卡 {gpu.Name}", gpu.VramMB > 0, extra: gpu.VramText + (gpu.IsNvidia ? " / NVIDIA" : ""), required: false));
        if (gpu.IsNvidia)
        {
            list.Add(Check("NVIDIA 驱动", gpu.DriverMajor >= 570, extra: gpu.DriverVersion, required: false,
                hint: gpu.DriverMajor >= 570 ? "" : "驱动版本需 ≥ 570.0 以支持 NVENC HDR"));
            list.Add(Check("RTX 显卡", gpu.IsRtx, extra: gpu.IsRtx ? $"RTX {gpu.Series}0 系列" : "非 RTX", required: false,
                hint: gpu.IsRtx ? "" : "SDR→HDR 需要 RTX 20/30/40/50 系列"));
        }
        return list;
    }

    public sealed class CheckResult
    {
        public string Name { get; init; } = "";
        public bool Ok { get; init; }
        public string Extra { get; init; } = "";
        public bool Required { get; init; }
        public string Hint { get; init; } = "";
        public string Status => Ok ? "OK" : (Required ? "缺失" : "缺失(可选)");
    }

    private static CheckResult Check(string name, bool ok, string extra = "", bool required = true, string hint = "") =>
        new() { Name = name, Ok = ok, Extra = extra, Required = required, Hint = hint };

    private static string GetVersion(string exe, string arg)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arg,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var line = (stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "").Trim();
            return line.Length > 80 ? line[..80] : line;
        }
        catch { return ""; }
    }

    private static string GetNvEncVersion(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "0";
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            // "NVEncC (x64) 9.32 ..." 或类似
            var m = Regex.Match(stdout, @"(\d+\.\d+(?:\.\d+)?)");
            return m.Success ? m.Groups[1].Value : "0";
        }
        catch { return "0"; }
    }

    private static Version ParseNvEncVersion(string ver) =>
        Version.TryParse(ver.Split('.')[0] + "." + (ver.Contains('.') ? ver.Split('.')[1] : "0"), out var v) ? v : new Version(0, 0);
}
