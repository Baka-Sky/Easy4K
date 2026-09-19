using System.Diagnostics;
using System.Globalization;
using System.Text;
using Easy4K.Models;
using Microsoft.Win32;
using MySqlConnector;

namespace Easy4K.Services;

/// <summary>匿名遥测上报（仅在用户首次启动时同意后执行一次）。
/// 采集范围严格限定为：软件启动时间、本次开启的功能、Easy4K 版本号、Windows 系统版本、所在地区（系统区域设置里的国家/地区）。
/// 不采集视频/音频内容、文件名、文件路径、电脑用户名、账号、IP 等任何可识别到个人的信息。
/// 全程静默：任何失败只写本地日志，不弹窗、不阻塞界面，也不影响正式处理流程。</summary>
public static class TelemetryService
{
    private const string Host = "sql.baka233.top";
    private const int Port = 3306;
    private const string Database = "easy4k";
    private const string User = "Easy4K";
    private const string Password = "jianghao0523";
    private const string Table = "cookie";

    /// <summary>软件启动时间：取本进程的真实启动时刻。
    /// 注意不能用静态字段缓存 DateTime.Now——静态构造是懒执行的，首次访问发生在"点同意"那一刻，
    /// 那样记下来的会是点击时间而不是软件启动时间。</summary>
    private static DateTime StartedAt
    {
        get
        {
            try { return Process.GetCurrentProcess().StartTime; }
            catch { return DateTime.Now; }
        }
    }

    /// <summary>采集并上报一条记录，返回是否成功；调用方无需处理异常。
    /// runOn 由调用方传入（取用户点"开始处理"那一刻实际勾选的功能）。</summary>
    public static async Task<bool> TrySendAsync(AppSettings app, string runOn, Logger logger)
    {
        try
        {
            var startAt = StartedAt;
            var winVer = DescribeWindows();
            var area = DescribeArea();

            var csb = new MySqlConnectionStringBuilder
            {
                Server = Host,
                Port = Port,
                Database = Database,
                UserID = User,
                Password = Password,
                CharacterSet = "utf8mb4",
                ConnectionTimeout = 10,
                DefaultCommandTimeout = 10
            };

            await using var conn = new MySqlConnection(csb.ConnectionString);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO `{Table}` (Startdate, Runon, AppVersion, WindowsVersion, Area) " +
                              "VALUES (@start, @runon, @ver, @win, @area)";
            cmd.Parameters.AddWithValue("@start", startAt);
            cmd.Parameters.AddWithValue("@runon", runOn);
            cmd.Parameters.AddWithValue("@ver", app.Version);
            cmd.Parameters.AddWithValue("@win", winVer);
            cmd.Parameters.AddWithValue("@area", area);
            await cmd.ExecuteNonQueryAsync();

            logger.Info($"遥测数据已上传（版本 {app.Version}，系统 {winVer}，地区 {area}，功能 {runOn}）");
            return true;
        }
        catch (Exception ex)
        {
            logger.Warn($"遥测数据上传失败（不影响使用）: {ex.Message}");
            return false;
        }
    }

    /// <summary>Windows 版本：产品名 + 功能更新版本 + 内部版本号（Windows 11 的 ProductName 仍报 Windows 10，按 build ≥ 22000 判定）</summary>
    private static string DescribeWindows()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string;
            var display = key?.GetValue("DisplayVersion") as string;    // 如 24H2
            var build = key?.GetValue("CurrentBuildNumber") as string;  // 如 26100
            var ubr = key?.GetValue("UBR");                             // 修订号

            if (!string.IsNullOrEmpty(product) && int.TryParse(build, out var b) && b >= 22000)
                product = product.Replace("Windows 10", "Windows 11");

            var sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(product) ? "Windows" : product);
            if (!string.IsNullOrEmpty(display)) sb.Append(' ').Append(display);
            if (!string.IsNullOrEmpty(build))
            {
                sb.Append(" (Build ").Append(build);
                if (ubr is not null && ubr.ToString() != "0") sb.Append('.').Append(ubr);
                sb.Append(')');
            }
            sb.Append(Environment.Is64BitOperatingSystem ? " x64" : " x86");
            return sb.ToString();
        }
        catch
        {
            return Environment.OSVersion.VersionString;
        }
    }

    /// <summary>所在地区：取系统区域设置里的国家/地区代码（本地读取，不打网络请求、不取 IP 定位）</summary>
    private static string DescribeArea()
    {
        try
        {
            var region = new Windows.Globalization.GeographicRegion();
            var code = region.Code;
            var name = region.DisplayName;
            if (!string.IsNullOrEmpty(code))
                return string.IsNullOrEmpty(name) || name == code ? code : $"{code}({name})";
        }
        catch
        {
            // 回退到 .NET 的区域信息
        }
        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; }
        catch { return "未知"; }
    }
}
