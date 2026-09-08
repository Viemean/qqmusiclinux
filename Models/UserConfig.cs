using System.IO;
using System.Text.Json;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Models;

/// <summary>
/// 用户全局偏好配置 (~/.config/qqmusic-tui/config.json)
/// </summary>
public sealed class UserConfig
{
    private static readonly string s_configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "qqmusic-tui"
    );
    private static readonly string s_configPath = Path.Combine(s_configDir, "config.json");

    public static UserConfig Current { get; private set; } = new();

    /// <summary>
    /// 是否在切歌时弹出 Linux 原生桌面气泡通知 (D-Bus org.freedesktop.Notifications)
    /// </summary>
    public bool EnableSongSwitchNotification { get; set; } = true;

    /// <summary>
    /// 从 ~/.config/qqmusic-tui/config.json 加载偏好配置
    /// </summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(s_configPath))
            {
                Current = new UserConfig();
                return;
            }

            var json = File.ReadAllText(s_configPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var config = new UserConfig();
            if (root.TryGetProperty("enable_song_switch_notification", out var nProp))
            {
                config.EnableSongSwitchNotification = nProp.GetBoolean();
            }

            Current = config;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UserConfig", $"Failed to load config.json, using defaults: {ex.Message}");
            Current = new UserConfig();
        }
    }

    /// <summary>
    /// 持久化保存至 ~/.config/qqmusic-tui/config.json
    /// </summary>
    public void Save()
    {
        try
        {
            if (!Directory.Exists(s_configDir))
            {
                Directory.CreateDirectory(s_configDir);
            }

            var json = $"{{\n  \"enable_song_switch_notification\": {(EnableSongSwitchNotification ? "true" : "false")}\n}}\n";
            File.WriteAllText(s_configPath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UserConfig", $"Failed to save config.json: {ex.Message}");
        }
    }
}
