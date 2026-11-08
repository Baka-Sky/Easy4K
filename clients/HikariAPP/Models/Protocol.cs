using System.Text.Json;
using HikariAPP2.Services;

namespace HikariAPP2.Models;

/// <summary>显卡信息（来自服务端 capabilities.gpu）</summary>
public sealed class GpuInfo
{
    public string Name { get; init; } = "";
    public string VramText { get; init; } = "";
    public bool IsNvidia { get; init; }
    public bool IsRtx { get; init; }
    public int Series { get; init; }
    public string Driver { get; init; } = "";
    public bool SupportsHdr { get; init; }
}

/// <summary>超分模型（某个算法下的一个权重）</summary>
public sealed class SrModel
{
    public string Name { get; init; } = "";
    public List<int> Scales { get; init; } = new();
    public string Scene { get; init; } = "";
    public int Stars { get; init; }
}

/// <summary>超分算法（种类）</summary>
public sealed class SrFamily
{
    public int Index { get; init; }
    public string Label { get; init; } = "";
    public string Scene { get; init; } = "";
    public int Stars { get; init; }
    public List<SrModel> Models { get; init; } = new();

    public List<SrModel> ModelsFor(int scale)
    {
        var list = Models.Where(m => m.Scales.Contains(scale)).ToList();
        return list.Count > 0 ? list : Models;
    }
}

/// <summary>补帧模型族（种类）</summary>
public sealed class VfiFamily
{
    public int Index { get; init; }
    public string Label { get; init; } = "";
    public string Scene { get; init; } = "";
    public int Stars { get; init; }
    public bool Torch { get; init; }
    public bool Available { get; init; } = true;
    public List<string> Models { get; init; } = new();
}

public sealed class LastResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = "";
    public string Steps { get; init; } = "";
    public long TimeMs { get; init; }
}

public sealed class Capabilities
{
    public string Version { get; init; } = "";
    public GpuInfo Gpu { get; init; } = new();
    public List<SrFamily> SrFamilies { get; init; } = new();
    public List<VfiFamily> VfiFamilies { get; init; } = new();

    public int SrScale { get; init; } = 2;
    public int SrEngineIndex { get; init; }
    public string SrModel { get; init; } = "";
    public int IfMultiplier { get; init; } = 2;
    public int IfEngineIndex { get; init; }
    public string IfModel { get; init; } = "";
    public double TargetFps { get; init; }

    public string OutputRoot { get; init; } = "";
    public string TempRoot { get; init; } = "";
    public string OutputName { get; init; } = "";

    public bool SuperResolution { get; init; } = true;
    public bool Interpolation { get; init; } = true;
    public bool MergeVideo { get; init; } = true;
    public bool MergeAudio { get; init; } = true;
    public bool SdrToHdr { get; init; }
    public bool SplitFrames { get; init; } = true;
    public bool HdrEnabled { get; init; }
    public bool Dedup { get; init; }
    public bool AudioSr { get; init; }
    public bool TurboMode { get; init; }
    public bool KeepSubtitles { get; init; }
    public bool HasSubtitles { get; init; }
    public int Crf { get; init; } = 18;
    public string HwQualityMode { get; init; } = "";
    public int HwQuality { get; init; } = 18;
    public string DedupMode { get; init; } = "performance";
    public string AudioSrPrecision { get; init; } = "fp16";
    public bool IfMultiplierLocked { get; init; }

    // —— 高级页全部可选项（与桌面端 AdvancedPage 对齐）——
    public bool SceneGuard { get; init; }
    public int TurboSegments { get; init; } = 2;
    public int SrDecodeThreads { get; init; } = 1;
    public int SrEncodeThreads { get; init; } = 2;
    public int IfDecodeThreads { get; init; } = 1;
    public int IfEncodeThreads { get; init; } = 2;
    public int PipelineGpuIndex { get; init; }
    public List<string> PipelineGpuOptions { get; init; } = new();
    public int HdrSaturation { get; init; } = 200;
    public int HdrContrast { get; init; } = 200;
    public int HdrAiStrength { get; init; } = 100;
    public bool HdrAiPath { get; init; }
    public bool SkipStartupSelfTest { get; init; }
    public bool UseSafeFrameRate { get; init; }
    public bool LowerQualityForVram { get; init; }
    public bool UseGpuAcceleration { get; init; } = true;
    public bool UseCpuProcessing { get; init; }
    public bool TryOldFfmpegOnCrash { get; init; }
    public bool NoReport { get; init; }
    public bool ReportAutoOpen { get; init; } = true;
    public string ReportDir { get; init; } = "Reports";
    public bool PreventAutoSleep { get; init; }

    public string ExternalFramesDir { get; init; } = "";

    public int SfKindIndex { get; init; }
    public string SfInput { get; init; } = "";
    public int SfImageScale { get; init; } = 2;
    public string SfImageModel { get; init; } = "";
    public List<string> SfImageModels { get; init; } = new();
    public string SfAudioPrecision { get; init; } = "fp16";
    public bool SfRunning { get; init; }
    public bool SfCanStart { get; init; }
    public string SfStatus { get; init; } = "";

    public LastResult? LastResult { get; init; }
    public bool IsProcessing { get; init; }
    public bool IsPaused { get; init; }

    public List<VfiFamily> UsableVfiFamilies => VfiFamilies.Where(f => f.Available).ToList();
}

/// <summary>视频信息（来自服务端 video_info）</summary>
public sealed class VideoInfo
{
    public bool Valid { get; init; }
    public string Path { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public string Resolution { get; init; } = "";
    public double Fps { get; init; }
    public string DurationText { get; init; } = "";
    public long TotalFrames { get; init; }
    public string AudioCodec { get; init; } = "";
    public bool HasSubtitles { get; init; }
    public string Error { get; init; } = "";
}

/// <summary>单文件超分信息（sf_info）</summary>
public sealed class SfInfo
{
    public bool Valid { get; init; }
    public string Error { get; init; } = "";
    public int KindIndex { get; init; }
    public bool IsImage { get; init; } = true;
    public string Input { get; init; } = "";
    public int ImageScale { get; init; } = 2;
    public string ImageModel { get; init; } = "";
    public List<string> ImageModels { get; init; } = new();
    public string AudioPrecision { get; init; } = "fp16";
    public string Output { get; init; } = "";
    public bool CanStart { get; init; }
    public bool Running { get; init; }
    public string Status { get; init; } = "";
}

public sealed class DirEntry
{
    public string Name { get; init; } = "";
    public bool IsDir { get; init; }
    public long Size { get; init; }
    public string FullPath { get; init; } = "";
    public bool IsParent { get; init; }

    public string SizeText => FormatSize(Size);

    public string Glyph => IsParent ? ".." : IsDir ? "📁" : "🎬";

    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "",
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B"
    };
}

public sealed class DirListing
{
    public string Path { get; init; } = "";
    public List<DirEntry> Entries { get; init; } = new();
    public string Error { get; init; } = "";
}

public static class ProtocolParser
{
    public static Capabilities ParseCapabilities(JsonElement o)
    {
        var g = Easy4kClient.Obj(o, "gpu");
        var gpu = new GpuInfo
        {
            Name = Easy4kClient.GetStr(g, "name"),
            VramText = Easy4kClient.GetStr(g, "vram"),
            IsNvidia = Easy4kClient.GetBool(g, "isNvidia"),
            IsRtx = Easy4kClient.GetBool(g, "isRtx"),
            Series = Easy4kClient.GetInt(g, "series"),
            Driver = Easy4kClient.GetStr(g, "driver"),
            SupportsHdr = Easy4kClient.GetBool(g, "hdr")
        };

        var srFamilies = new List<SrFamily>();
        if (o.TryGetProperty("sr", out var srEl) && srEl.TryGetProperty("families", out var srFams)
            && srFams.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in srFams.EnumerateArray())
            {
                var models = new List<SrModel>();
                if (f.TryGetProperty("models", out var ms) && ms.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in ms.EnumerateArray())
                    {
                        var scales = new List<int>();
                        if (m.TryGetProperty("scales", out var sc) && sc.ValueKind == JsonValueKind.Array)
                            foreach (var s in sc.EnumerateArray())
                                if (s.TryGetInt32(out var sv)) scales.Add(sv);

                        models.Add(new SrModel
                        {
                            Name = Easy4kClient.GetStr(m, "name"),
                            Scales = scales,
                            Scene = Easy4kClient.GetStr(m, "scene"),
                            Stars = Easy4kClient.GetInt(m, "stars")
                        });
                    }
                }

                srFamilies.Add(new SrFamily
                {
                    Index = Easy4kClient.GetInt(f, "index"),
                    Label = Easy4kClient.GetStr(f, "label"),
                    Scene = Easy4kClient.GetStr(f, "scene"),
                    Stars = Easy4kClient.GetInt(f, "stars"),
                    Models = models
                });
            }
        }

        var vfiFamilies = new List<VfiFamily>();
        if (o.TryGetProperty("vfi", out var vfiEl) && vfiEl.TryGetProperty("families", out var vfiFams)
            && vfiFams.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in vfiFams.EnumerateArray())
            {
                var models = new List<string>();
                if (f.TryGetProperty("models", out var ms) && ms.ValueKind == JsonValueKind.Array)
                    foreach (var m in ms.EnumerateArray())
                        if (m.ValueKind == JsonValueKind.String) models.Add(m.GetString() ?? "");

                vfiFamilies.Add(new VfiFamily
                {
                    Index = Easy4kClient.GetInt(f, "index"),
                    Label = Easy4kClient.GetStr(f, "label"),
                    Scene = Easy4kClient.GetStr(f, "scene"),
                    Stars = Easy4kClient.GetInt(f, "stars"),
                    Torch = Easy4kClient.GetBool(f, "torch"),
                    Available = Easy4kClient.GetBool(f, "available", true),
                    Models = models
                });
            }
        }

        var opt = Easy4kClient.Obj(o, "options");
        var cfg = Easy4kClient.Obj(o, "config");
        var paths = Easy4kClient.Obj(o, "paths");
        var sf = Easy4kClient.Obj(o, "sf");
        var st = Easy4kClient.Obj(o, "state");

        var sfModels = new List<string>();
        if (sf.ValueKind == JsonValueKind.Object && sf.TryGetProperty("imageModels", out var sfm)
            && sfm.ValueKind == JsonValueKind.Array)
            foreach (var m in sfm.EnumerateArray())
                if (m.ValueKind == JsonValueKind.String) sfModels.Add(m.GetString() ?? "");

        var gpuOptions = new List<string>();
        if (opt.ValueKind == JsonValueKind.Object && opt.TryGetProperty("pipelineGpuOptions", out var pgo)
            && pgo.ValueKind == JsonValueKind.Array)
            foreach (var m in pgo.EnumerateArray())
                if (m.ValueKind == JsonValueKind.String) gpuOptions.Add(m.GetString() ?? "");

        LastResult? last = null;
        var lr = Easy4kClient.Obj(o, "lastResult");
        if (lr.ValueKind == JsonValueKind.Object)
        {
            last = new LastResult
            {
                Success = Easy4kClient.GetBool(lr, "success"),
                Output = Easy4kClient.GetStr(lr, "output"),
                Steps = Easy4kClient.GetStr(lr, "steps"),
                TimeMs = Easy4kClient.GetInt(lr, "time")
            };
        }

        return new Capabilities
        {
            Version = Easy4kClient.GetStr(o, "version"),
            Gpu = gpu,
            SrFamilies = srFamilies,
            VfiFamilies = vfiFamilies,
            SrScale = Easy4kClient.GetInt(cfg, "srScale", 2),
            SrEngineIndex = Easy4kClient.GetInt(cfg, "srEngineIndex"),
            SrModel = Easy4kClient.GetStr(cfg, "srModel"),
            IfMultiplier = Easy4kClient.GetInt(cfg, "ifMultiplier", 2),
            IfEngineIndex = Easy4kClient.GetInt(cfg, "ifEngineIndex"),
            IfModel = Easy4kClient.GetStr(cfg, "ifModel"),
            TargetFps = Easy4kClient.GetDbl(cfg, "targetFps"),
            OutputRoot = Easy4kClient.GetStr(paths, "outputRoot"),
            TempRoot = Easy4kClient.GetStr(paths, "tempRoot"),
            OutputName = Easy4kClient.GetStr(paths, "outputName"),
            SuperResolution = Easy4kClient.GetBool(opt, "superResolution", true),
            Interpolation = Easy4kClient.GetBool(opt, "interpolation", true),
            MergeVideo = Easy4kClient.GetBool(opt, "mergeVideo", true),
            MergeAudio = Easy4kClient.GetBool(opt, "mergeAudio", true),
            SdrToHdr = Easy4kClient.GetBool(opt, "sdrToHdr"),
            SplitFrames = Easy4kClient.GetBool(opt, "splitFrames", true),
            HdrEnabled = Easy4kClient.GetBool(opt, "hdrEnabled"),
            Dedup = Easy4kClient.GetBool(opt, "dedup"),
            AudioSr = Easy4kClient.GetBool(opt, "audioSr"),
            TurboMode = Easy4kClient.GetBool(opt, "turboMode"),
            KeepSubtitles = Easy4kClient.GetBool(opt, "keepSubtitles"),
            HasSubtitles = Easy4kClient.GetBool(opt, "hasSubtitles"),
            Crf = Easy4kClient.GetInt(opt, "crf", 18),
            HwQualityMode = Easy4kClient.GetStr(opt, "hwQualityMode"),
            HwQuality = Easy4kClient.GetInt(opt, "hwQuality", 18),
            DedupMode = Easy4kClient.GetStr(opt, "dedupMode") is { Length: > 0 } dm ? dm : "performance",
            AudioSrPrecision = Easy4kClient.GetStr(opt, "audioSrPrecision") is { Length: > 0 } ap ? ap : "fp16",
            IfMultiplierLocked = Easy4kClient.GetBool(opt, "ifMultiplierLocked"),
            SceneGuard = Easy4kClient.GetBool(opt, "sceneGuard"),
            TurboSegments = Easy4kClient.GetInt(opt, "turboSegments", 2),
            SrDecodeThreads = Easy4kClient.GetInt(opt, "srDecodeThreads", 1),
            SrEncodeThreads = Easy4kClient.GetInt(opt, "srEncodeThreads", 2),
            IfDecodeThreads = Easy4kClient.GetInt(opt, "ifDecodeThreads", 1),
            IfEncodeThreads = Easy4kClient.GetInt(opt, "ifEncodeThreads", 2),
            PipelineGpuIndex = Easy4kClient.GetInt(opt, "pipelineGpuIndex"),
            PipelineGpuOptions = gpuOptions,
            HdrSaturation = Easy4kClient.GetInt(opt, "hdrSaturation", 200),
            HdrContrast = Easy4kClient.GetInt(opt, "hdrContrast", 200),
            HdrAiStrength = Easy4kClient.GetInt(opt, "hdrAiStrength", 100),
            HdrAiPath = Easy4kClient.GetBool(opt, "hdrAiPath"),
            SkipStartupSelfTest = Easy4kClient.GetBool(opt, "skipStartupSelfTest"),
            UseSafeFrameRate = Easy4kClient.GetBool(opt, "useSafeFrameRate"),
            LowerQualityForVram = Easy4kClient.GetBool(opt, "lowerQualityForVram"),
            UseGpuAcceleration = Easy4kClient.GetBool(opt, "useGpuAcceleration", true),
            UseCpuProcessing = Easy4kClient.GetBool(opt, "useCpuProcessing"),
            TryOldFfmpegOnCrash = Easy4kClient.GetBool(opt, "tryOldFfmpegOnCrash"),
            NoReport = Easy4kClient.GetBool(opt, "noReport"),
            ReportAutoOpen = Easy4kClient.GetBool(opt, "reportAutoOpen", true),
            ReportDir = Easy4kClient.GetStr(opt, "reportDir") is { Length: > 0 } rd ? rd : "Reports",
            PreventAutoSleep = Easy4kClient.GetBool(opt, "preventAutoSleep"),
            ExternalFramesDir = Easy4kClient.GetStr(paths, "externalFramesDir"),
            SfKindIndex = Easy4kClient.GetInt(sf, "kindIndex"),
            SfInput = Easy4kClient.GetStr(sf, "input"),
            SfImageScale = Easy4kClient.GetInt(sf, "imageScale", 2),
            SfImageModel = Easy4kClient.GetStr(sf, "imageModel"),
            SfImageModels = sfModels,
            SfAudioPrecision = Easy4kClient.GetStr(sf, "audioPrecision") is { Length: > 0 } sp ? sp : "fp16",
            SfRunning = Easy4kClient.GetBool(sf, "running"),
            SfCanStart = Easy4kClient.GetBool(sf, "canStart"),
            SfStatus = Easy4kClient.GetStr(sf, "status"),
            LastResult = last,
            IsProcessing = Easy4kClient.GetBool(st, "isProcessing"),
            IsPaused = Easy4kClient.GetBool(st, "isPaused")
        };
    }

    public static VideoInfo ParseVideoInfo(JsonElement o) => new()
    {
        Valid = Easy4kClient.GetBool(o, "valid"),
        Path = Easy4kClient.GetStr(o, "path"),
        Width = Easy4kClient.GetInt(o, "width"),
        Height = Easy4kClient.GetInt(o, "height"),
        Resolution = Easy4kClient.GetStr(o, "resolution"),
        Fps = Easy4kClient.GetDbl(o, "fps"),
        DurationText = Easy4kClient.GetStr(o, "durationText"),
        TotalFrames = Easy4kClient.GetLong(o, "totalFrames"),
        AudioCodec = Easy4kClient.GetStr(o, "audioCodec"),
        HasSubtitles = Easy4kClient.GetBool(o, "hasSubtitles"),
        Error = Easy4kClient.GetStr(o, "error")
    };

    public static SfInfo ParseSfInfo(JsonElement o)
    {
        var models = new List<string>();
        if (o.TryGetProperty("imageModels", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var m in arr.EnumerateArray())
                if (m.ValueKind == JsonValueKind.String) models.Add(m.GetString() ?? "");

        return new SfInfo
        {
            Valid = Easy4kClient.GetBool(o, "valid"),
            Error = Easy4kClient.GetStr(o, "error"),
            KindIndex = Easy4kClient.GetInt(o, "kindIndex"),
            IsImage = Easy4kClient.GetBool(o, "isImage", true),
            Input = Easy4kClient.GetStr(o, "input"),
            ImageScale = Easy4kClient.GetInt(o, "imageScale", 2),
            ImageModel = Easy4kClient.GetStr(o, "imageModel"),
            ImageModels = models,
            AudioPrecision = Easy4kClient.GetStr(o, "audioPrecision") is { Length: > 0 } p ? p : "fp16",
            Output = Easy4kClient.GetStr(o, "output"),
            CanStart = Easy4kClient.GetBool(o, "canStart"),
            Running = Easy4kClient.GetBool(o, "running"),
            Status = Easy4kClient.GetStr(o, "status")
        };
    }

    public static DirListing ParseDirListing(JsonElement o)
    {
        var path = Easy4kClient.GetStr(o, "path");
        var entries = new List<DirEntry>();
        if (o.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                var name = Easy4kClient.GetStr(e, "name");
                if (name.Length == 0) continue;
                entries.Add(new DirEntry
                {
                    Name = name,
                    IsDir = Easy4kClient.GetBool(e, "isDir"),
                    Size = Easy4kClient.GetLong(e, "size"),
                    FullPath = JoinPath(path, name)
                });
            }
        }

        return new DirListing
        {
            Path = path,
            Entries = entries,
            Error = Easy4kClient.GetStr(o, "error")
        };
    }

    public static string JoinPath(string? basePath, string name)
    {
        if (string.IsNullOrEmpty(basePath)) return name;
        return basePath.EndsWith('\\') ? basePath + name : basePath + "\\" + name;
    }

    public static string? ParentOf(string path)
    {
        var t = path.TrimEnd('\\');
        if (t.Length <= 2 && t.EndsWith(':')) return null;
        var i = t.LastIndexOf('\\');
        if (i < 0) return null;
        return i <= 2 ? t[..3] : t[..i];
    }
}
