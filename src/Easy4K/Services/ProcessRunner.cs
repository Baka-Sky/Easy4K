using System.Diagnostics;
using System.Text;

namespace Easy4K.Services;

/// <summary>通用进程执行器：实时逐行读取 stdout/stderr，支持取消，UTF-8 编码避免中文乱码。
/// 所有外部工具（FFmpeg/RealESRGAN/RIFE/NVEncC）都通过它调用。</summary>
public sealed class ProcessRunner
{
    private readonly Logger _logger;

    public ProcessRunner(Logger logger) => _logger = logger;

    /// <summary>运行一个进程，逐行回调。返回 exit code。</summary>
    public async Task<int> RunAsync(
        string exe,
        string args,
        Action<string>? onLine = null,
        Action<string>? onStderr = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(exe))
        {
            _logger.Error($"找不到可执行文件: {exe}");
            return -1;
        }

        var exeAbsolute = Path.GetFullPath(exe);

        var workDir = Path.GetDirectoryName(exeAbsolute) ?? "";

        var psi = new ProcessStartInfo
        {
            FileName = exeAbsolute,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // 设 WorkingDirectory 为 exe 所在目录，让工具能找到同目录下的依赖 DLL/模型
            // 命令参数已全部用绝对路径，不受工作目录影响
            WorkingDirectory = Directory.Exists(workDir) ? workDir : AppContext.BaseDirectory
        };

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();

        bool started;
        try
        {
            started = p.Start();
        }
        catch (Exception ex)
        {
            _logger.Error($"启动进程失败: {Path.GetFileName(exeAbsolute)} — {ex.Message}");
            return -1;
        }

        if (!started)
        {
            _logger.Error($"启动进程返回 false: {Path.GetFileName(exeAbsolute)}");
            return -1;
        }

        // 必须先 Start() 再访问 StandardOutput/StandardError（否则抛 "hasn't started yet"，
        // 读取线程崩溃 → stderr 管道无人消费 → 进程写满管道缓冲后永久卡死，表现为"卡在第 N 帧"）
        // FFmpeg 等工具用 \r 覆盖写进度（frame=123 fps=...），ReadLineAsync 只认 \n 会全部缓冲到结束，
        // 必须按 \r/\n 都切分，才能实时逐条拿到进度 → 进度条平滑、日志实时。
        var stdoutTask = Task.Run(async () =>
        {
            try
            {
                await ReadStreamByLinesAsync(p.StandardOutput,
                    line => { stdoutBuf.AppendLine(line); try { onLine?.Invoke(line); } catch { } },
                    ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Warn($"{Path.GetFileName(exeAbsolute)} stdout 读取异常: {ex.Message}");
            }
        }, ct);

        var stderrTask = Task.Run(async () =>
        {
            try
            {
                await ReadStreamByLinesAsync(p.StandardError,
                    line =>
                    {
                        stderrBuf.AppendLine(line);
                        try { onStderr?.Invoke(line); } catch { }
                        try { onLine?.Invoke(line); } catch { }
                    },
                    ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Warn($"{Path.GetFileName(exeAbsolute)} stderr 读取异常: {ex.Message}");
            }
        }, ct);

        await using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        });

        await Task.WhenAll(stdoutTask, stderrTask);
        try { await p.WaitForExitAsync(ct); } catch (OperationCanceledException) { }

        var exit = p.HasExited ? p.ExitCode : -1;
        var stderrText = stderrBuf.ToString().Trim();
        var stdoutText = stdoutBuf.ToString().Trim();
        if (exit != 0)
        {
            if (!string.IsNullOrEmpty(stderrText))
                _logger.Error($"[exit={exit}] {Path.GetFileName(exeAbsolute)} stderr:\n{stderrText}");
            else if (!string.IsNullOrEmpty(stdoutText))
                _logger.Warn($"[exit={exit}] {Path.GetFileName(exeAbsolute)} stdout:\n{Tail(stdoutText, 800)}");
        }
        else if (!string.IsNullOrEmpty(stderrText))
        {
            // 成功时也记录 stderr 摘要（最后几行），让用户看到工具实际输出而非只有命令
            _logger.Info($"{Path.GetFileName(exeAbsolute)} 输出:\n{Tail(stderrText, 800)}");
        }
        return exit;
    }

    /// <summary>按 \r 与 \n 都切分的流读取：FFmpeg 等用 \r 覆盖写进度，必须逐条实时回调。</summary>
    private static async Task ReadStreamByLinesAsync(StreamReader sr, Action<string> onLine, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        int n;
        while ((n = await sr.ReadAsync(buf.AsMemory(), ct)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                char c = buf[i];
                if (c == '\r' || c == '\n')
                {
                    if (sb.Length > 0)
                    {
                        onLine(sb.ToString());
                        sb.Clear();
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        if (sb.Length > 0) onLine(sb.ToString());
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : "..." + s[^n..];
}
