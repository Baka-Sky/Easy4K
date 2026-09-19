using System.IO;

namespace Easy4K.Services;

/// <summary>运行目录解析。区分两类目录：
/// <para>· 安装目录（InstallDir，exe 所在目录）：只读。Tools 等只读资源从这里取。</para>
/// <para>· 可写根目录（WritableRoot）：存放 appsettings.json、Temp、Output、Reports、日志等需要写入的内容。
/// 便携版就是 exe 同级目录（与旧行为一致）；MSIX 安装版的安装目录位于 WindowsApps 下且只读，
/// 必须落到 %LOCALAPPDATA%\Easy4K，否则配置保存、拆帧临时文件、输出视频全部写不进去。</para></summary>
public static class AppPaths
{
    /// <summary>exe 所在目录（MSIX 安装态下为只读的包安装目录）</summary>
    public static string InstallDir { get; } = AppContext.BaseDirectory;

    /// <summary>是否以 MSIX 打包（有包标识）方式运行</summary>
    public static bool IsPackaged { get; } = DetectPackaged();

    /// <summary>可写根目录（见类型说明）</summary>
    public static string WritableRoot { get; } = DetectWritableRoot();

    private static bool DetectPackaged()
    {
        try
        {
            // 无包标识（便携版）时访问 Package.Current 会抛异常
            _ = Windows.ApplicationModel.Package.Current.Id.Name;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DetectWritableRoot()
    {
        // 便携版且 exe 目录可写 → 维持旧行为（配置/Temp/Output 都在 exe 旁）
        if (!IsPackaged && CanWrite(InstallDir)) return InstallDir;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Easy4K");
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    /// <summary>探测目录是否可写（用户把便携版放到 Program Files 等受保护位置时会失败）</summary>
    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".e4k_write_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
