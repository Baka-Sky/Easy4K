using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Easy4K.Services;

/// <summary>零依赖 HTML 处理报告生成器：把本次运行摘要写成自包含单文件 .html（图片内嵌 base64），
/// 记录：本次开启的选项、调用的构建命令、测试帧、随机抽取的输出中间帧，随后可用默认浏览器打开。</summary>
public static class HtmlReportService
{
    /// <summary>生成 HTML 报告并返回文件路径（失败返回 null）。</summary>
    /// <param name="reportDir">报告保存目录（绝对路径，由调用方解析）</param>
    /// <param name="videoName">输入视频文件名</param>
    /// <param name="outputPath">最终输出文件（可空）</param>
    /// <param name="stepsText">步骤摘要</param>
    /// <param name="optionLines">本次开启的选项描述行</param>
    /// <param name="commandLines">本次调用的命令列表（CMD 级日志）</param>
    /// <param name="sampleFrames">抽样帧路径列表（随机抽取，内嵌 base64）</param>
    /// <param name="elapsed">耗时</param>
    /// <param name="isSelfTest">是否自检</param>
    public static string? Generate(
        string reportDir, string videoName, string? outputPath, string stepsText,
        IReadOnlyList<string> optionLines, IReadOnlyList<string> commandLines,
        IReadOnlyList<string> sampleFrames, TimeSpan elapsed, bool isSelfTest)
    {
        try
        {
            if (!Directory.Exists(reportDir)) Directory.CreateDirectory(reportDir);
            var ts = DateTime.Now;
            var fileName = $"Easy4K{(isSelfTest ? "自检" : "")}_报告_{ts:yyyyMMdd_HHmmss}.html";
            var path = Path.Combine(reportDir, fileName);

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>Easy4K 处理报告</title>");
            sb.AppendLine("<style>body{font-family:'Microsoft YaHei',sans-serif;margin:28px;color:#222;background:#fff}")
              .AppendLine("h1{font-size:24px;border-bottom:2px solid #0088ff;padding-bottom:8px}")
              .AppendLine("h2{font-size:17px;margin:22px 0 6px;color:#0088ff}")
              .AppendLine(".meta{color:#666;font-size:13px;margin-bottom:4px}")
              .AppendLine("code{display:block;background:#f5f5f5;border:1px solid #ddd;border-radius:6px;padding:6px 8px;")
              .AppendLine("white-space:pre-wrap;word-break:break-all;font-size:12px;margin:2px 0}")
              .AppendLine("ul{margin:4px 0;padding-left:22px}")
              .AppendLine(".frames{display:flex;flex-wrap:wrap;gap:8px}")
              .AppendLine(".frames figure{margin:0;border:1px solid #ddd;border-radius:8px;padding:6px;background:#fafafa}")
              .AppendLine(".frames img{max-width:240px;max-height:150px;border-radius:4px}")
              .AppendLine(".frames figcaption{font-size:11px;color:#888;text-align:center;margin-top:4px}")
              .AppendLine("</style></head><body>");

            sb.AppendLine($"<h1>{(isSelfTest ? "Easy4K 启动自检报告" : "Easy4K 处理报告")}</h1>");
            sb.AppendLine($"<div class=\"meta\">生成时间：{ts:yyyy-MM-dd HH:mm:ss}</div>");
            sb.AppendLine($"<div class=\"meta\">输入视频：{Escape(videoName)}</div>");
            if (!string.IsNullOrWhiteSpace(outputPath))
                sb.AppendLine($"<div class=\"meta\">输出文件：{Escape(outputPath)}</div>");
            sb.AppendLine($"<div class=\"meta\">执行步骤：{Escape(stepsText)}</div>");
            sb.AppendLine($"<div class=\"meta\">总耗时：{(int)elapsed.TotalMinutes}分{elapsed.Seconds}秒</div>");

            sb.AppendLine("<h2>本次开启的选项</h2>");
            if (optionLines.Count == 0) sb.AppendLine("<div class=\"meta\">（无）</div>");
            else
            {
                sb.AppendLine("<ul>");
                foreach (var o in optionLines) sb.AppendLine($"<li>{Escape(o)}</li>");
                sb.AppendLine("</ul>");
            }

            sb.AppendLine("<h2>调用的构建代码 / 命令</h2>");
            if (commandLines.Count == 0) sb.AppendLine("<div class=\"meta\">（本次无外部命令）</div>");
            else
            {
                foreach (var c in commandLines) sb.AppendLine($"<code>{Escape(c)}</code>");
            }

            sb.AppendLine("<h2>输出中间帧（随机抽取）</h2>");
            if (sampleFrames.Count == 0) sb.AppendLine("<div class=\"meta\">（本次没有可用于抽帧的中间帧）</div>");
            else
            {
                sb.AppendLine("<div class=\"frames\">");
                foreach (var f in sampleFrames)
                {
                    try
                    {
                        var b64 = Convert.ToBase64String(File.ReadAllBytes(f));
                        sb.AppendLine($"<figure><img src=\"data:image/png;base64,{b64}\"><figcaption>{Escape(Path.GetFileName(f))}</figcaption></figure>");
                    }
                    catch { }
                }
                sb.AppendLine("</div>");
            }

            sb.AppendLine("</body></html>");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string Escape(string s)
        => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
