using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Easy4K.Orphan;

/// <summary>孤儿任务调度守护进程（无界面、无窗口、无常驻 UI）。
///
/// 职责只有两件：盯着 orphans.json 里最早一条任务的预定时间；到点就拉起
/// Easy4K.exe --orphan-run &lt;id&gt; 并等它结束，然后接着盯下一条。任务列表空了就自己退出。
///
/// 它 **只读** orphans.json：任务由执行进程自己移除，所以这里不需要理解任务里的选项快照，
/// 也就不必和主程序共享任何代码（避免两边模型定义漂移）。唯一的写入是自己的 pid 与日志。</summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class FileModel
    {
        public List<TaskHead> Tasks { get; set; } = new();
    }

    /// <summary>只取调度需要的字段；任务里其它字段（选项快照等）由执行进程自己读，这里忽略即可</summary>
    private sealed class TaskHead
    {
        public string Id { get; set; } = "";
        public DateTime RunAt { get; set; }
    }

    /// <summary>本次运行里「连 exe 都拉不起来」的任务，跳过它们，避免卡在同一个 id 上死循环</summary>
    private static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);

    private static int Main()
    {
        var root = WritableRoot();
        var storePath = Path.Combine(root, "orphans.json");
        var pidPath = Path.Combine(root, "orphan_daemon.pid");
        var logPath = Path.Combine(root, "orphan_daemon.log");

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(pidPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // pid 写不进去不影响调度
        }

        Log(logPath, $"守护进程启动 pid={Environment.ProcessId}，任务文件 {storePath}");

        try
        {
            var parseFailures = 0;
            while (true)
            {
                var tasks = ReadTasks(storePath);
                if (tasks is null)
                {
                    // 文件可能正被主程序改写（写盘不是原子的）→ 稍等重读，绝不能当成"没有任务"而退出
                    parseFailures++;
                    if (parseFailures >= 20)
                    {
                        Log(logPath, "任务文件连续 20 次解析失败，守护进程退出");
                        return 0;
                    }
                    Thread.Sleep(3000);
                    continue;
                }
                parseFailures = 0;

                // 防御：文件被外部工具改坏时列表里可能出现 null 元素，这里必须跳过，
                // 否则取 Id 会抛 NullReferenceException 把守护进程直接打挂（pid 都来不及清）。
                var pending = tasks.Where(t => t is not null && !Failed.Contains(t.Id)).ToList();
                if (pending.Count == 0)
                {
                    Log(logPath, "没有待执行任务，守护进程退出");
                    return 0;
                }

                var next = pending.OrderBy(t => t.RunAt).First();
                var wait = next.RunAt - DateTime.Now;
                if (wait > TimeSpan.Zero)
                {
                    // 最多睡 30 秒就醒一次重新读表：期间用户可能又排了更早的任务
                    Thread.Sleep(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait);
                    continue;
                }

                Log(logPath, $"到点：任务 {next.Id}（预定 {next.RunAt:yyyy-MM-dd HH:mm:ss}）→ 启动 Easy4K");
                if (!StartEasy4K(next.Id, logPath)) Failed.Add(next.Id);
            }
        }
        finally
        {
            try { File.Delete(pidPath); } catch { }
        }
    }

    /// <summary>只读任务表；解析失败返回 null（调用方据此稍后重试而不是退出）</summary>
    private static List<TaskHead>? ReadTasks(string storePath)
    {
        try
        {
            if (!File.Exists(storePath)) return new List<TaskHead>();
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(storePath), JsonOpts);
            return model?.Tasks ?? new List<TaskHead>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>拉起一次孤儿执行并等它跑完（跑完才排下一条，任务之间不会互相抢 GPU）</summary>
    private static bool StartEasy4K(string id, string logPath)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Easy4K.exe");
        if (!File.Exists(exe))
        {
            Log(logPath, $"找不到主程序 {exe}，跳过该任务");
            return false;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = $"--orphan-run {id}",
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            });
            if (p is null)
            {
                Log(logPath, $"启动 {exe} 返回空进程");
                return false;
            }
            p.WaitForExit();
            Log(logPath, $"任务 {id} 结束，Easy4K 退出码 {p.ExitCode}");
            return true;
        }
        catch (Exception ex)
        {
            Log(logPath, $"启动任务 {id} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>可写根目录：与主程序 AppPaths 同一套规则（便携版 = exe 旁，只读安装目录则退到 %LOCALAPPDATA%）</summary>
    private static string WritableRoot()
    {
        var dir = AppContext.BaseDirectory;
        if (CanWrite(dir)) return dir;
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Easy4K");
        try { Directory.CreateDirectory(fallback); } catch { }
        return fallback;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".e4k_orphan_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>追加日志（超过 1MB 先清空，避免长期常驻把磁盘写满）</summary>
    private static void Log(string logPath, string message)
    {
        try
        {
            var fi = new FileInfo(logPath);
            if (fi.Exists && fi.Length > 1024 * 1024) File.Delete(logPath);
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不了就算了，不能影响调度
        }
    }
}
