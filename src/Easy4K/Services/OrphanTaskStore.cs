using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Easy4K.Models;

namespace Easy4K.Services;

/// <summary>孤儿任务的读写与后台守护进程的拉起。
///
/// 两个进程共用 orphans.json（与 appsettings.json 同目录）：
///   · 主程序（Easy4K.exe）负责增删任务，增删后按需拉起守护进程；
///   · 守护进程（Easy4K.Orphan.exe）只读任务、到点拉起 Easy4K.exe --orphan-run &lt;id&gt;，
///     没有任务了就自己退出。任务本身由执行进程移除，守护进程不写这个文件，
///     这样它不需要理解任务里的选项快照，也就不必与主程序共享任何代码。</summary>
public sealed class OrphanTaskStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>json 外壳：留一层对象便于以后加字段（版本号、全局开关等）</summary>
    private sealed class FileModel
    {
        public List<OrphanTask> Tasks { get; set; } = new();
    }

    /// <summary>任务文件路径（便携版 = exe 旁，MSIX 安装版 = %LOCALAPPDATA%\Easy4K）</summary>
    public static string StorePath => Path.Combine(AppPaths.WritableRoot, "orphans.json");

    /// <summary>守护进程 pid 文件，用来判断它是否还活着</summary>
    public static string PidPath => Path.Combine(AppPaths.WritableRoot, "orphan_daemon.pid");

    /// <summary>守护进程日志（它没有界面，出问题只能看这个文件）</summary>
    public static string LogPath => Path.Combine(AppPaths.WritableRoot, "orphan_daemon.log");

    /// <summary>守护进程 exe 路径（与 Easy4K.exe 同级）</summary>
    public static string DaemonPath => Path.Combine(AppPaths.InstallDir, "Easy4K.Orphan.exe");

    public List<OrphanTask> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return new List<OrphanTask>();
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(StorePath), JsonOpts);
            return model?.Tasks ?? new List<OrphanTask>();
        }
        catch
        {
            return new List<OrphanTask>(); // 文件损坏就当作没有任务，不阻塞主流程
        }
    }

    public void Save(List<OrphanTask> tasks)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.WritableRoot);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(new FileModel { Tasks = tasks }, JsonOpts));
        }
        catch
        {
            // 写盘失败不阻塞主流程
        }
    }

    public void Add(OrphanTask task)
    {
        var tasks = Load();
        tasks.Add(task);
        Save(tasks);
    }

    public void Remove(string id)
    {
        var tasks = Load();
        tasks.RemoveAll(t => t.Id == id);
        Save(tasks);
    }

    /// <summary>取走一条任务：先把它从列表里删掉再返回。
    /// 执行进程一启动就调用，这样即使处理中途崩溃也不会被守护进程反复重跑。</summary>
    public OrphanTask? Take(string id)
    {
        var tasks = Load();
        var hit = tasks.FirstOrDefault(t => t.Id == id);
        if (hit is null) return null;
        tasks.Remove(hit);
        Save(tasks);
        return hit;
    }

    /// <summary>守护进程是否还活着（读 pid 文件，再用进程表核对，避免 pid 被复用误判）</summary>
    public bool IsDaemonRunning()
    {
        try
        {
            if (!File.Exists(PidPath)) return false;
            if (!int.TryParse(File.ReadAllText(PidPath).Trim(), out var pid)) return false;
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("Easy4K.Orphan", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>确保守护进程在跑（有任务才需要）。已经活着就不重复拉起。</summary>
    public bool EnsureDaemon()
    {
        if (Load().Count == 0) return false;
        if (IsDaemonRunning()) return true;
        if (!File.Exists(DaemonPath)) return false;
        try
        {
            var psi = new ProcessStartInfo(DaemonPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,       // 守护进程无界面，不能弹黑框
                WorkingDirectory = AppPaths.InstallDir
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>任务被清空时顺手停掉守护进程，不让它空转</summary>
    public void KillDaemon()
    {
        try
        {
            if (!IsDaemonRunning()) return;
            var pid = int.Parse(File.ReadAllText(PidPath).Trim());
            using var p = Process.GetProcessById(pid);
            p.Kill();
        }
        catch
        {
            // 进程可能刚好自己退了，忽略
        }
    }
}
