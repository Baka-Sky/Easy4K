using System.Diagnostics;
using System.Text.RegularExpressions;
using Easy4K.Models;
using Easy4K.LuminolEngine.CommandBuilders;

namespace Easy4K.LuminolEngine;

/// <summary>环境检测：工具是否齐全 + 版本（特别是 NVEncC ≥ 9.32 才支持 --vpp-ngx-truehdr），
/// 并且逐个数模型文件（超分各引擎按「模型 × 倍率」核对 .param，补帧扫 rife 模型目录，
/// SFDP / HDR / AudioSR 核对 .onnx）。输出一个检测结果列表，UI 直接展示。</summary>
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
            // ncnn 工具没有版本输出（-v 只是 verbose，会打印 usage 退出），只报是否部署
            Check("Real-ESRGAN", _tools.RealEsrganExists, extra: _tools.RealEsrganExists ? "已部署" : ""),
            Check("RIFE", _tools.RifeExists, extra: _tools.RifeExists ? "已部署" : "")
        };

        // 模型文件：逐个真扫盘核对，缺哪个模型明确列出来
        list.AddRange(CheckModelFiles());

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

        list.Add(_tools.NvEncOldExists
            ? Check("NVEncC64（老版本兜底）", true, extra: $"v{GetNvEncVersion(_tools.NvEncOldExe)}", required: false)
            : Check("NVEncC64（老版本兜底）", false, extra: "未安装", required: false,
                    hint: "可不装；HDR 转换崩溃且勾选「崩溃时尝试使用老版本处理架构」时才用到"));

        list.Add(_tools.SceneDetectReady
            ? Check("转场检测 · PySceneDetect", true, extra: "已部署", required: false)
            : Check("转场检测 · PySceneDetect", false, extra: "未部署", required: false,
                    hint: "缺少 Tools/SceneDetect/scene_detect.py 或 python-torch；不影响出片，只是没有转场保护"));

        var gpu = _gpu.Detect();
        list.Add(Check($"显卡 {gpu.Name}", gpu.VramMB > 0, extra: gpu.VramText + (gpu.IsNvidia ? " / NVIDIA" : ""), required: false));
        if (gpu.IsNvidia)
        {
            list.Add(Check("NVIDIA 驱动", gpu.DriverMajor >= 570, extra: gpu.DriverVersion, required: false,
                hint: gpu.DriverMajor >= 570 ? "" : "驱动版本需 ≥ 570.0 以支持 NVENC HDR"));
            list.Add(Check("RTX 显卡", gpu.IsRtx, extra: gpu.IsRtx ? $"RTX {gpu.Series} 系列" : "非 RTX", required: false,
                hint: gpu.IsRtx ? "" : "SDR→HDR 需要 RTX 20/30/40/50 系列"));
        }
        return list;
    }

    // ===================== 模型文件（真扫盘） =====================

    /// <summary>按磁盘上真实的 .param / .onnx 文件判断每个模型能不能用。
    /// 不再只看 exe 是否存在——引擎在、模型文件缺一半时，界面里那些模型其实一跑就失败。</summary>
    private List<CheckResult> CheckModelFiles()
    {
        var list = new List<CheckResult>();

        // ---------- 超分：每个算法一条，逐个「模型 × 倍率」核对模型文件 ----------
        foreach (var fam in SrCatalog.Families)
        {
            var exe = _tools.SrExeFor(fam.ExeKey);
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) continue;   // 引擎没部署就不列这族模型
            var dir = Path.GetDirectoryName(exe) ?? "";

            var ok = new List<string>();
            var miss = new List<string>();
            foreach (var v in fam.Variants)
            {
                foreach (var s in v.Scales)
                {
                    var path = SrModelFile(_tools, fam.ExeKey, dir, v.Name, s);
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) ok.Add($"{v.Name} ×{s}");
                    else miss.Add($"{v.Name} ×{s}");
                }
            }

            var total = ok.Count + miss.Count;
            list.Add(new CheckResult
            {
                Name = $"超分模型 · {fam.Label}",
                Ok = ok.Count > 0,
                Required = false,
                Extra = $"{ok.Count}/{total} 可用（目录 {dir}）",
                Hint = miss.Count == 0 ? "" : $"缺 {miss.Count} 个：{string.Join("、", miss.Take(6))}{(miss.Count > 6 ? " …" : "")}",
                Lines = Tail(ok, "可用模型")
            });
        }

        // ---------- 补帧：NCNN-RIFE 扫模型目录 ----------
        var rifeModels = RifeCommandBuilder.ListModels(_tools.RifeModelsRoot);
        list.Add(new CheckResult
        {
            Name = "补帧模型 · NCNN-RIFE",
            Ok = rifeModels.Count > 0,
            Required = true,
            Extra = $"{rifeModels.Count} 个（目录 {_tools.RifeModelsRoot}）",
            Hint = rifeModels.Count == 0 ? "目录下没有找到任何含 flownet.param 的 rife-v* 模型" : "",
            Lines = Tail(rifeModels.ToList(), "已部署")
        });

        // ---------- 补帧：PyTorch 轨 ----------
        var torchLines = new List<string>();
        var torchWeights = 0;
        if (!string.IsNullOrEmpty(_tools.FrameInterpDir) && Directory.Exists(_tools.FrameInterpDir))
        {
            foreach (var d in Directory.EnumerateDirectories(_tools.FrameInterpDir))
            {
                var n = Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Count(f =>
                    f.EndsWith(".pth", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".pt", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".ckpt", StringComparison.OrdinalIgnoreCase));
                torchWeights += n;
                torchLines.Add($"{Path.GetFileName(d)}：{n} 个权重");
            }
        }
        list.Add(new CheckResult
        {
            Name = "补帧模型 · PyTorch 轨（DRBA/GMFSS/GIMM-VFI）",
            Ok = _tools.VfiTorchReady && torchWeights > 0,
            Required = false,
            Extra = $"解释器 {Yes(_tools.TorchPythonExe)} · 驱动 {Yes(_tools.VfiDriverScript)} · 权重 {torchWeights} 个",
            Hint = _tools.VfiTorchReady
                ? (torchWeights == 0 ? "没有找到任何 .pth/.pt 权重，PyTorch 轨模型不可用" : "")
                : "python-torch 或 vfi_driver.py 缺失，PyTorch 轨不可用",
            Lines = torchLines
        });

        // ---------- 帧去重：SFDP 孪生网络 ----------
        var sfdpFp32 = _tools.SfdpModelFp32;
        var sfdpFp16 = _tools.SfdpModelFp16;
        var sfdpMissing = new List<string>();
        if (!File.Exists(sfdpFp32)) sfdpMissing.Add("model.onnx（完美模式）");
        if (!File.Exists(sfdpFp16)) sfdpMissing.Add("model_fp16.onnx（性能模式）");
        list.Add(new CheckResult
        {
            Name = "去重模型 · SFDP 孪生网络",
            Ok = sfdpMissing.Count == 0,
            Required = true,
            Extra = $"FP32 {SizeText(sfdpFp32)} · FP16 {SizeText(sfdpFp16)}",
            Hint = sfdpMissing.Count == 0
                ? ""
                : $"缺少 {string.Join(" / ", sfdpMissing)}；帧去重 AI 模型为必需项，缺失时启动自检会阻止进入，请补齐 Tools\\SFDP",
            Lines = new List<string> { $"完美模式 ← {sfdpFp32}", $"性能模式 ← {sfdpFp16}" }
        });

        // ---------- AI SDR→HDR ----------
        list.Add(new CheckResult
        {
            Name = "HDR 模型 · HDRTVNet++",
            Ok = _tools.HdrAiExists,
            Required = false,
            Extra = SizeText(_tools.HdrAiModel),
            Hint = _tools.HdrAiExists ? "" : "缺少 hdr_ensemble.onnx，SDR→HDR 不可用",
            Lines = new List<string> { _tools.HdrAiModel }
        });

        // ---------- 音频超分 ----------
        var asr16 = Path.Combine(_tools.AudioSrModelsFp16, "ddpm.onnx");
        var asr16d = Path.Combine(_tools.AudioSrModelsFp16, "ddpm.onnx.data");
        var asr32 = Path.Combine(_tools.AudioSrModelsFp32, "ddpm.onnx");
        var asr32d = Path.Combine(_tools.AudioSrModelsFp32, "ddpm.onnx.data");
        list.Add(new CheckResult
        {
            Name = "音频超分 · AudioSR",
            Ok = _tools.AudioSrScriptExists && (_tools.AudioSrFp16Exists || _tools.AudioSrFp32Exists),
            Required = false,
            Extra = $"脚本 {Yes(_tools.AudioSrScript)} · fp16 {SizeText(asr16d)} · fp32 {SizeText(asr32d)}",
            Hint = _tools.AudioSrScriptExists
                ? (_tools.AudioSrFp16Exists || _tools.AudioSrFp32Exists ? "" : "两个权重包都不完整（ddpm.onnx + ddpm.onnx.data 需同时存在）")
                : "缺少 audiosr_onnx.py",
            Lines = new List<string> { $"fp16：{asr16} / {asr16d}", $"fp32：{asr32} / {asr32d}" }
        });

        return list;
    }

    /// <summary>
    /// 启动自检用的「缺一不可」清单：全部超分模型（每个模型 × 每个倍率）、全部补帧模型
    /// （已部署的 rife-v* 目录必须完整，常用白名单必须存在）、SFDP 两个去重模型。
    /// 返回逐项中文描述，空列表表示全部就绪。
    /// </summary>
    public static List<string> MissingRequiredModels(ToolPaths tools)
    {
        var missing = new List<string>();

        foreach (var fam in SrCatalog.Families)
        {
            var exe = tools.SrExeFor(fam.ExeKey);
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                missing.Add($"超分引擎 {fam.Label}");
                continue;
            }

            var dir = Path.GetDirectoryName(exe) ?? "";
            foreach (var v in fam.Variants)
            {
                foreach (var s in v.Scales)
                {
                    var path = SrModelFile(tools, fam.ExeKey, dir, v.Name, s);
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                        missing.Add($"超分模型 {v.Name} ×{s}");
                }
            }
        }

        var rifeRoot = tools.RifeModelsRoot;
        if (string.IsNullOrWhiteSpace(rifeRoot) || !Directory.Exists(rifeRoot))
        {
            missing.Add("补帧模型目录（Tools\\rife）");
        }
        else
        {
            var dirs = Directory.EnumerateDirectories(rifeRoot)
                .Where(d => Path.GetFileName(d).StartsWith("rife-v", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (dirs.Count == 0) missing.Add("补帧模型（Tools\\rife 下没有任何 rife-v* 模型）");

            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (!File.Exists(Path.Combine(d, "flownet.param"))) missing.Add($"补帧模型 {name}（缺 flownet.param）");
                else if (!File.Exists(Path.Combine(d, "flownet.bin"))) missing.Add($"补帧模型 {name}（缺 flownet.bin）");
            }

            foreach (var name in RifeCommandBuilder.PreferredModels)
            {
                if (!Directory.Exists(Path.Combine(rifeRoot, name)))
                    missing.Add($"补帧模型 {name}（常用模型缺失）");
            }
        }

        if (!File.Exists(tools.SfdpModelFp32)) missing.Add("去重模型 SFDP model.onnx（完美模式）");
        if (!File.Exists(tools.SfdpModelFp16)) missing.Add("去重模型 SFDP model_fp16.onnx（性能模式）");

        return missing;
    }

    /// <summary>某个「超分模型 × 倍率」实际需要的 .param 路径；未知返回空串。</summary>
    private static string SrModelFile(ToolPaths tools, string exeKey, string engineDir, string modelName, int scale)
    {
        switch (exeKey)
        {
            case "esrgan":
                return Path.Combine(tools.RealEsrganModelsRoot, modelName + ".param");

            case "cugan":
                {
                    // cugan-{se|pro|nose}-{conservative|nodenoise|denoise1x|denoise2x|denoise3x}
                    var p = modelName.Split('-');
                    if (p.Length < 3) return "";
                    var group = p[1] switch { "se" => "models-se", "pro" => "models-pro", "nose" => "models-nose", _ => "" };
                    var stem = p[2] switch
                    {
                        "conservative" => "conservative",
                        "nodenoise" => "no-denoise",
                        "denoise1x" => "denoise1x",
                        "denoise2x" => "denoise2x",
                        "denoise3x" => "denoise3x",
                        _ => ""
                    };
                    if (group.Length == 0 || stem.Length == 0) return "";
                    return Path.Combine(engineDir, group, $"up{scale}x-{stem}.param");
                }

            case "waifu2x":
                {
                    // -m models-cunet -n 1 -s {scale} → noise1_scale{scale}.0x_model.param
                    var group = modelName switch
                    {
                        "waifu2x-cunet" => "models-cunet",
                        "waifu2x-anime" => "models-upconv_7_anime_style_art_rgb",
                        "waifu2x-photo" => "models-upconv_7_photo",
                        _ => ""
                    };
                    if (group.Length == 0) return "";
                    return Path.Combine(engineDir, group, $"noise1_scale{scale}.0x_model.param");
                }

            case "srmd":
                {
                    var stem = modelName.StartsWith("srmdnf", StringComparison.OrdinalIgnoreCase)
                        ? $"srmdnf_x{scale}"
                        : $"srmd_x{scale}";
                    return Path.Combine(engineDir, "models-srmd", stem + ".param");
                }

            case "realsr":
                {
                    var group = modelName.Contains("jpeg", StringComparison.OrdinalIgnoreCase)
                        ? "models-DF2K_JPEG"
                        : "models-DF2K";
                    return Path.Combine(engineDir, group, $"x{scale}.param");
                }

            default:
                return "";
        }
    }

    /// <summary>列表太长时只显示前 N 项，末尾补「… 共 N 项」。</summary>
    private static List<string> Tail(List<string> items, string label, int max = 10)
    {
        if (items.Count <= max) return items;
        var l = items.Take(max).ToList();
        l.Add($"… {label}共 {items.Count} 项");
        return l;
    }

    private static string Yes(string path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? "有" : "缺";

    private static string SizeText(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "缺";
            var mb = new FileInfo(path).Length / 1048576.0;
            return $"{mb:F2} MB";
        }
        catch { return "缺"; }
    }

    public sealed class CheckResult
    {
        public string Name { get; init; } = "";
        public bool Ok { get; init; }
        public string Extra { get; init; } = "";
        public bool Required { get; init; }
        public string Hint { get; init; } = "";
        /// <summary>逐条明细（扫描到的模型名 / 文件路径），UI 每项缩进一行展示</summary>
        public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
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
