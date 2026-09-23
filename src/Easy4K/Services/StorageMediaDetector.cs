using System.IO;
using System.Management;

namespace Easy4K.Services;

/// <summary>探测某个路径所在的物理磁盘是机械盘还是固态盘。
/// 涡轮模式会让拆帧 / 去重判决 / 回填 / 超分 / 补帧同时读写临时目录，机械盘会被随机 IO 拖死，
/// 因此勾选时需要提示用户。
/// 实现走 Storage 命名空间的 WMI：MSFT_Partition 由盘符找到 DiskNumber，再查 MSFT_PhysicalDisk 的 MediaType。</summary>
public static class StorageMediaDetector
{
    public enum MediaKind
    {
        /// <summary>查不到（老系统、虚拟磁盘、权限受限等）——当作不需要警告处理</summary>
        Unknown,
        Hdd,
        Ssd,
    }

    /// <summary>探测路径所在磁盘类型；任何异常都返回 Unknown（不阻塞用户）</summary>
    public static MediaKind DetectForPath(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root)) return MediaKind.Unknown;

            var letter = root.TrimEnd('\\', '/', ':').ToUpperInvariant();
            if (letter.Length != 1 || letter[0] < 'A' || letter[0] > 'Z') return MediaKind.Unknown;

            var diskNumber = FindDiskNumber(letter[0]);
            if (diskNumber is null)
            {
                // 盘符映射不到分区（网络盘 / 虚拟盘等）：退回按盘符查 MSFT_LogicalDisk 无意义，直接判为未知
                return MediaKind.Unknown;
            }

            return QueryMediaType(diskNumber.Value);
        }
        catch
        {
            return MediaKind.Unknown;
        }
    }

    /// <summary>由盘符（如 'C'）查所在物理磁盘编号</summary>
    private static int? FindDiskNumber(char driveLetter)
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\Microsoft\Windows\Storage",
            $"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = {(int)driveLetter}");
        foreach (var item in searcher.Get())
        {
            var value = item["DiskNumber"];
            if (value is not null) return Convert.ToInt32(value);
        }
        return null;
    }

    /// <summary>查物理磁盘介质类型：3 = HDD，4 = SSD，5 = SCM（按 SSD 处理）</summary>
    private static MediaKind QueryMediaType(int diskNumber)
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\Microsoft\Windows\Storage",
            $"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskNumber}'");
        foreach (var item in searcher.Get())
        {
            var value = item["MediaType"];
            if (value is null) continue;
            return Convert.ToInt32(value) switch
            {
                3 => MediaKind.Hdd,
                4 => MediaKind.Ssd,
                5 => MediaKind.Ssd,
                _ => MediaKind.Unknown,
            };
        }
        return MediaKind.Unknown;
    }
}
