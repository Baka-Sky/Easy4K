using System.Diagnostics;
using System.Globalization;
using System.Text;
using Easy4K.Models;

namespace Easy4K.Services;

/// <summary>流水线失败报告：把失败现场原样写到用户桌面，方便直接拿去排查或反馈。
///
/// 报告只做"汇总"，不删减任何原始内容：外部进程的 stdout / stderr 全量、完整日志、异常堆栈、
/// 处理参数与运行环境都会写进去（用户明确要求"不要精简，要最详细的原始输出"）。</summary>
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
        IReadOnlyList<ProcessRunner.ProcessRecord>? history = null)
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
            sb.AppendLine($"临时目录   : {ctx.TempRoot}");
            sb.AppendLine($"输出目录   : {ctx.OutputRoot}");
            sb.AppendLine($"外部帧目录 : {ctx.ExternalFramesDir}");
            if (ctx.Video is not null)
            {
                sb.AppendLine($"视频参数   : {ctx.Video.Width}x{ctx.Video.Height} / "
                    + $"{ctx.Video.FrameRate.ToString("0.###", CultureInfo.InvariantCulture)} fps / "
                    + $"{ctx.Video.TotalFrames} 帧 / 音频 {(ctx.Video.HasAudio ? "有" : "无")}");
            }

            Head("二、处理参数");
            var o = ctx.Options;
            var s = ctx.Settings;
            sb.AppendLine($"勾选步骤   : 拆帧={o.SplitFrames} 超分={o.SuperResolution} 补帧={o.Interpolation} "
                + $"合并={o.MergeVideo} 音频={o.MergeAudio} HDR={o.SdrToHdr} 字幕={o.KeepSubtitles}");
            sb.AppendLine($"超分       : 模型 {ctx.SrModel} / 倍率 {ctx.SrScale}");
            sb.AppendLine($"补帧       : 模型 {ctx.IfModel} / 驱动 {ctx.IfDriverKey} / 倍率 {ctx.IfMultiplier} / 目标帧率 {o.TargetFps}");
            sb.AppendLine($"音频超分   : 开启={o.AudioSr} / 精度 {o.AudioSrPrecision}");
            sb.AppendLine($"去重       : 开启={s.DedupEnabled} / 模式 {s.DedupMode}");
            sb.AppendLine($"线程/显存  : 线程 {s.ThreadCount} / 安全帧率 {s.UseSafeFrameRate} / 低画质省显存 {s.LowerQualityForVram}");
            sb.AppendLine($"硬件加速   : GPU加速 {s.UseGpuAcceleration} / CPU处理 {s.UseCpuProcessing}");
            sb.AppendLine($"编码       : preset {s.EncodePreset} / CRF {s.Crf} / 硬编 {s.HwQualityMode} {s.HwQuality}");
            sb.AppendLine($"涡轮模式   : {s.TurboMode} / 块大小 {s.TurboBlockFrames}");
            sb.AppendLine($"HDR        : 饱和度 {ctx.HdrSaturation} / 对比度 {ctx.HdrContrast}");

            Head("三、失败现场");
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

            Head("四、全部外部进程原始输出（按执行顺序，全量，未截断）");
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

            Head($"五、完整日志（共 {logs.Count} 条，未裁剪）");
            foreach (var e in logs) sb.AppendLine(e.LogText);

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return path;
        }
        catch
        {
            return null;
        }
    }

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
