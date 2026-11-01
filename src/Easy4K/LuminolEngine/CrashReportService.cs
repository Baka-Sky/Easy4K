using System.Diagnostics;
using System.Globalization;
using System.Text;
using Easy4K.Models;

namespace Easy4K.LuminolEngine;

/// <summary>流水线失败报告：把失败现场原样写到用户桌面，方便直接拿去排查或反馈。
///
/// 报告只做"汇总"，不删减任何原始内容：外部进程的 stdout / stderr 全量、完整日志、异常堆栈、
/// 处理参数、视频详细参数、工具就绪情况、磁盘空间、阶段时间线、临时目录产物都会写进去。</summary>
public static class CrashReportService
{
    /// <summary>生成报告并返回文件完整路径；写盘失败返回 null（不能因为写报告再抛一次异常）。</summary>
    public static string? Write(
        string version,
        ProcessingContext ctx,
        string? stageText,
        int exitCode,
        Exception? exception,
        string stdout,
        string stderr,
        IReadOnlyList<LogEntry> logs,
        IReadOnlyList<ProcessRunner.ProcessRecord>? history = null,
        IReadOnlyList<string>? timeline = null)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
                desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var path = Path.Combine(desktop, $"Easy4K_处理失败_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            var sb = new StringBuilder();
            void Head(string title)
            {
                sb.AppendLine();
                sb.AppendLine(title);
                sb.AppendLine(new string('-', 72));
            }

            sb.AppendLine("Easy4K 处理失败报告（完整原始输出，未做任何精简）");
            sb.AppendLine(new string('=', 72));
            sb.AppendLine($"生成时间   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"程序版本   : {version}");
            sb.AppendLine($"系统       : {Environment.OSVersion} / {(Environment.Is64BitProcess ? "x64" : "x86")}");
            sb.AppendLine($"CPU 逻辑核 : {Environment.ProcessorCount}");
            sb.AppendLine($"进程内存   : {Environment.WorkingSet / 1024 / 1024} MB");
            sb.AppendLine($"运行目录   : {AppContext.BaseDirectory}");
            sb.AppendLine($"显卡       : {ctx.Gpu.Name} / {ctx.Gpu.VramText} / 驱动 {ctx.Gpu.DriverVersion}");

            Head("一、输入与目录");
            sb.AppendLine($"输入视频   : {ctx.InputVideo}");
            if (!string.IsNullOrWhiteSpace(ctx.InputVideo))
                sb.AppendLine($"输入存在   : {File.Exists(ctx.InputVideo)}"
                    + (File.Exists(ctx.InputVideo) ? $" / {Size(new FileInfo(ctx.InputVideo).Length)}" : ""));
            sb.AppendLine($"临时目录   : {ctx.TempRoot}");
            sb.AppendLine($"输出目录   : {ctx.OutputRoot}");
            sb.AppendLine($"外部帧目录 : {ctx.ExternalFramesDir}");

            Head("二、视频详细参数（自动检测结果）");
            var v = ctx.Video;
            if (v is null)
            {
                sb.AppendLine("(无视频信息)");
            }
            else
            {
                sb.AppendLine($"分辨率     : {v.ResolutionText} / 宽高比 {v.AspectText}");
                sb.AppendLine($"帧率       : {v.FpsText} / 平均 {v.AverageFrameRate.ToString("0.###", CultureInfo.InvariantCulture)} fps"
                    + $" / {(v.IsVariableFrameRate ? "动态帧率(VFR)" : "静态帧率")}");
                sb.AppendLine($"总帧数     : {v.TotalFrames}");
                sb.AppendLine($"时长/大小  : {v.DurationText} / {v.FileSizeText}");
                sb.AppendLine($"码率       : 总 {v.BitRate / 1000} kbps / 视频 {v.VideoBitRate / 1000} kbps");
                sb.AppendLine($"视频编码   : {v.CodecText} / {v.VideoStreamText}");
                sb.AppendLine($"色彩       : {v.ColorText} / 范围 {v.ColorRangeText} / 扫描 {v.ScanText}");
                sb.AppendLine($"音频       : {v.AudioText}");
                sb.AppendLine($"字幕       : {v.SubtitleDetailText}");
                sb.AppendLine($"章节/附件  : {v.ChapterCount} / {v.AttachmentCount}");
                sb.AppendLine($"元数据     : {v.MetadataTagCount} 项");
                sb.AppendLine($"封面       : {v.CoverText}");
                sb.AppendLine($"编码器标记 : {v.EncoderTag}");
                sb.AppendLine($"容器       : {v.ContainerText} / {v.FormatName}");
            }

            Head("三、处理参数");
            var o = ctx.Options;
            var s = ctx.Settings;
            sb.AppendLine($"勾选步骤   : 拆帧={o.SplitFrames} 超分={o.SuperResolution} 补帧={o.Interpolation} "
                + $"合并={o.MergeVideo} 音频={o.MergeAudio} HDR={o.SdrToHdr}");
            sb.AppendLine($"保留字幕   : {o.KeepSubtitles} / 嵌入元数据 {o.EmbedMetadata} / 嵌入封面 {o.EmbedCover}");
            sb.AppendLine($"超分       : 模型 {ctx.SrModel} / 倍率 {ctx.SrScale}");
            sb.AppendLine($"补帧       : 模型 {ctx.IfModel} / 驱动 {ctx.IfDriverKey} / 倍率 {ctx.IfMultiplier} / 目标帧率 {o.TargetFps}");
            sb.AppendLine($"音频超分   : 开启={o.AudioSr} / 精度 {o.AudioSrPrecision}");
            sb.AppendLine($"去重       : 开启={s.DedupEnabled} / 模式 {s.DedupMode}");
            sb.AppendLine($"线程/显存  : 线程 {s.ThreadCount} / 安全帧率 {s.UseSafeFrameRate} / 低画质省显存 {s.LowerQualityForVram}");
            sb.AppendLine($"硬件加速   : GPU加速 {s.UseGpuAcceleration} / CPU处理 {s.UseCpuProcessing}");
            sb.AppendLine($"编码       : preset {s.EncodePreset} / CRF {s.Crf} / 硬编 {s.HwQualityMode} {s.HwQuality}");
            sb.AppendLine($"涡轮模式   : {s.TurboMode} / 分段 {s.TurboSegments}");
            sb.AppendLine($"HDR        : 饱和度 {ctx.HdrSaturation} / 对比度 {ctx.HdrContrast} / AI 强度 {ctx.HdrAiStrength}");

            Head("四、工具与环境");
            var t = ctx.Tools;
            void Tool(string name, string path, bool exists)
                => sb.AppendLine($"{name,-14}: {(exists ? "就绪" : "缺失")}  {path}");
            sb.AppendLine($"Tools 根目录 : {t.ToolsRoot}");
            Tool("FFmpeg", t.FFmpegExe, t.FFmpegExists);
            Tool("FFprobe", t.FFprobeExe, t.FFprobeExists);
            Tool("FFmpeg-Old", t.FFmpegOldExe, t.FFmpegOldExists);
            Tool("NVEncC", t.NvEncExe, t.NvEncExists);
            Tool("NVEncC-Old", t.NvEncOldExe, t.NvEncOldExists);
            Tool("RealESRGAN", t.RealEsrganExe, t.RealEsrganExists);
            Tool("RIFE", t.RifeExe, t.RifeExists);
            Tool("HDRTVNet 模型", t.HdrAiModel, t.HdrAiExists);
            Tool("AudioSR 脚本", t.AudioSrScript, t.AudioSrScriptExists);
            Tool("AudioSR FP16", t.AudioSrModelsFp16, t.AudioSrFp16Exists);
            Tool("AudioSR FP32", t.AudioSrModelsFp32, t.AudioSrFp32Exists);
            Tool("便携 Python", t.PortablePythonExe, File.Exists(t.PortablePythonExe));
            Tool("PyTorch Python", t.TorchPythonExe, File.Exists(t.TorchPythonExe));
            Tool("PySceneDetect", t.SceneDetectScript, File.Exists(t.SceneDetectScript));

            Head("五、磁盘空间");
            foreach (var (name, dir) in new[] { ("临时目录盘", ctx.TempRoot), ("输出目录盘", ctx.OutputRoot) })
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) { sb.AppendLine($"{name} : (未设置)"); continue; }
                    var root = Path.GetPathRoot(Path.GetFullPath(dir));
                    if (string.IsNullOrEmpty(root)) { sb.AppendLine($"{name} : 无法解析盘符 {dir}"); continue; }
                    var d = new DriveInfo(root);
                    sb.AppendLine($"{name} : {root} 剩余 {d.AvailableFreeSpace / 1024.0 / 1024 / 1024:0.0} GB"
                        + $" / 总计 {d.TotalSize / 1024.0 / 1024 / 1024:0.0} GB");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"{name} : 读取失败 {ex.Message}");
                }
            }

            Head("六、失败现场");
            sb.AppendLine($"失败阶段   : {(string.IsNullOrWhiteSpace(stageText) ? "(未记录)" : stageText)}");
            sb.AppendLine($"退出码     : {exitCode}");
            if (exception is not null)
            {
                sb.AppendLine();
                sb.AppendLine("异常（完整 ToString，含全部堆栈）：");
                sb.AppendLine(exception.ToString());
            }
            else
            {
                sb.AppendLine("异常       : 无托管异常（流水线通过 Fail() 主动停止，原因见下方原始输出与日志）");
            }

            Head("七、阶段时间线（按进度推送记录）");
            if (timeline is not null && timeline.Count > 0)
            {
                foreach (var line in timeline) sb.AppendLine(line);
            }
            else
            {
                sb.AppendLine("(本次没有记录到阶段切换)");
            }

            Head("八、全部外部进程原始输出（按执行顺序，全量，未截断）");
            if (history is not null && history.Count > 0)
            {
                sb.AppendLine($"本轮共调用外部进程 {history.Count} 次。下面按时间顺序逐条列出命令行、退出码与完整输出。");
                for (int i = 0; i < history.Count; i++)
                {
                    var h = history[i];
                    sb.AppendLine();
                    sb.AppendLine($"===== [{i + 1}/{history.Count}] {h.Time:HH:mm:ss}  exit={h.ExitCode} =====");
                    sb.AppendLine($"EXE : {h.Exe}");
                    sb.AppendLine($"ARGS: {h.Args}");
                    sb.AppendLine("----- stdout（全量） -----");
                    sb.AppendLine(string.IsNullOrEmpty(h.Stdout) ? "(空)" : h.Stdout);
                    sb.AppendLine("----- stderr（全量） -----");
                    sb.AppendLine(string.IsNullOrEmpty(h.Stderr) ? "(空)" : h.Stderr);
                }
            }
            else
            {
                // 兜底：没有历史记录时至少写出最近一次
                sb.AppendLine("----- 最近一次 stdout（全量，未截断） -----");
                sb.AppendLine(string.IsNullOrEmpty(stdout) ? "(空)" : stdout);
                sb.AppendLine();
                sb.AppendLine("----- 最近一次 stderr（全量，未截断） -----");
                sb.AppendLine(string.IsNullOrEmpty(stderr) ? "(空)" : stderr);
            }

            Head("九、临时目录现有产物");
            AppendTempListing(sb, ctx.TempRoot);

            Head($"十、完整日志（共 {logs.Count} 条，未裁剪、无条数上限）");
            foreach (var e in logs) sb.AppendLine(e.ToString());

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>列出临时目录里的文件（名/大小/时间）。最多 500 条，超出部分只写数量。</summary>
    private static void AppendTempListing(StringBuilder sb, string tempRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tempRoot) || !Directory.Exists(tempRoot))
            {
                sb.AppendLine("(临时目录不存在)");
                return;
            }
            var files = new DirectoryInfo(tempRoot)
                .GetFiles("*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
            sb.AppendLine($"根目录文件 {files.Count} 个：");
            foreach (var f in files.Take(500))
                sb.AppendLine($"  {f.LastWriteTime:HH:mm:ss}  {Size(f.Length),10}  {f.Name}");

            var dirs = Directory.GetDirectories(tempRoot);
            foreach (var dir in dirs.Take(20))
            {
                var count = Directory.GetFiles(dir).Length;
                sb.AppendLine($"  [目录] {Path.GetFileName(dir)}  文件 {count} 个");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(读取临时目录失败: {ex.Message})");
        }
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B"
    };

    /// <summary>用系统默认程序打开报告（失败也不影响主流程）。</summary>
    public static void Open(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { }
    }
}
