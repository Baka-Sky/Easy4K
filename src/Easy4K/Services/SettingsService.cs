using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Easy4K.Models;

namespace Easy4K.Services;

/// <summary>读写 appsettings.json（运行时写入会落回 exe 目录旁，方便下次启动保留用户改动）</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true, // 兼容文件里 PascalCase 与 Save 写入的 camelCase
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _settingsPath;

    public SettingsService()
    {
        // exe 旁边的 appsettings.json（dotnet build 会把项目内的复制过来）
        _settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    public (AppSettings Settings, ToolPathConfig ToolPaths) Load()
    {
        AppSettings? settings = null;
        ToolPathConfig? paths = null;

        if (File.Exists(_settingsPath))
        {
            try
            {
                using var stream = File.OpenRead(_settingsPath);
                var doc = JsonDocument.Parse(stream);
                // 兼容大小写：源文件用 PascalCase(AppConfig)，Save 写入 camelCase(appConfig)
                if (doc.RootElement.TryGetProperty("AppConfig", out var cfg) ||
                    doc.RootElement.TryGetProperty("appConfig", out cfg))
                    settings = cfg.Deserialize<AppSettings>(JsonOpts);
                if (doc.RootElement.TryGetProperty("ToolPaths", out var tp) ||
                    doc.RootElement.TryGetProperty("toolPaths", out tp))
                    paths = tp.Deserialize<ToolPathConfig>(JsonOpts);
            }
            catch
            {
                // 损坏则忽略，用默认值
            }
        }

        settings ??= new AppSettings();
        paths ??= new ToolPathConfig();

        // 若文件不存在（首次运行）或解析失败，自动生成默认配置，方便用户编辑
        if (!File.Exists(_settingsPath))
        {
            try { Save(settings, paths); } catch { }
        }

        return (settings, paths);
    }

    public void Save(AppSettings settings, ToolPathConfig paths)
    {
        try
        {
            var obj = new { AppConfig = settings, ToolPaths = paths };
            var json = JsonSerializer.Serialize(obj, JsonOpts);
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // 写盘失败不阻塞主流程
        }
    }
}
