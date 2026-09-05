using System.Text.Json;

namespace QQMusic.Tui.Services.AcrCloud;

/// <summary>
/// ACRCloud 增强音频识别配置
/// </summary>
public sealed class AcrCloudConfig
{
    private static readonly string s_configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "qqmusic-tui"
    );
    private static readonly string s_configPath = Path.Combine(s_configDir, "acrcloud.json");

    public static AcrCloudConfig Current { get; private set; } = Load();

    public string Host { get; set; } = "identify-cn-north-1.acrcloud.cn";
    public string AccessKey { get; set; } = "";
    public string AccessSecret { get; set; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(AccessSecret);

    public static AcrCloudConfig Load()
    {
        try
        {
            if (File.Exists(s_configPath))
            {
                var json = File.ReadAllText(s_configPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var config = new AcrCloudConfig();
                if (root.TryGetProperty("host", out var h) && !string.IsNullOrWhiteSpace(h.GetString()))
                {
                    config.Host = h.GetString()!.Trim();
                }
                if (root.TryGetProperty("access_key", out var k))
                {
                    config.AccessKey = k.GetString()?.Trim() ?? "";
                }
                if (root.TryGetProperty("access_secret", out var s))
                {
                    config.AccessSecret = s.GetString()?.Trim() ?? "";
                }
                return config;
            }
        }
        catch { }

        return new AcrCloudConfig();
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(s_configDir))
            {
                Directory.CreateDirectory(s_configDir);
            }

            var json = $$"""
            {
              "host": "{{Host.Replace("\"", "")}}",
              "access_key": "{{AccessKey.Replace("\"", "")}}",
              "access_secret": "{{AccessSecret.Replace("\"", "")}}"
            }
            """;
            File.WriteAllText(s_configPath, json);
            Current = this;
        }
        catch { }
    }
}
