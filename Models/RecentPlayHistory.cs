using System.Text;
using System.Text.Json;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Models;

/// <summary>
/// 本地持久化“最近播放”历史管理器
/// 遵循 .NET 10 Native AOT 零反射规范，采用 JsonDocument 解析与手动序列化
/// </summary>
public static class RecentPlayHistory
{
    private static readonly string s_configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config",
        "qqmusic-tui"
    );

    private static readonly string s_historyPath = Path.Combine(s_configDir, "recent_play.json");
    private static readonly object s_lock = new();
    private static readonly List<Song> s_songs = [];
    private static bool s_loaded = false;
    private const int MaxHistoryCount = 200;

    /// <summary>
    /// 获取当前最近播放歌曲列表副本
    /// </summary>
    public static List<Song> GetSongs()
    {
        EnsureLoaded();
        lock (s_lock)
        {
            return [.. s_songs];
        }
    }

    /// <summary>
    /// 添加曲目至最近播放（置顶且去重，最多保留 200 首）
    /// </summary>
    public static void Add(Song song)
    {
        if (song == null || (string.IsNullOrEmpty(song.Mid) && song.Id <= 0)) return;

        EnsureLoaded();
        lock (s_lock)
        {
            s_songs.RemoveAll(s =>
                (!string.IsNullOrEmpty(song.Mid) && s.Mid == song.Mid) ||
                (song.Id > 0 && s.Id == song.Id));

            s_songs.Insert(0, song);

            if (s_songs.Count > MaxHistoryCount)
            {
                s_songs.RemoveRange(MaxHistoryCount, s_songs.Count - MaxHistoryCount);
            }
        }

        Save();
    }

    /// <summary>
    /// 从最近播放中移除指定曲目
    /// </summary>
    public static bool Remove(Song song)
    {
        if (song == null) return false;

        EnsureLoaded();
        bool removed;
        lock (s_lock)
        {
            removed = s_songs.RemoveAll(s =>
                (!string.IsNullOrEmpty(song.Mid) && s.Mid == song.Mid) ||
                (song.Id > 0 && s.Id == song.Id)) > 0;
        }

        if (removed)
        {
            Save();
        }
        return removed;
    }

    /// <summary>
    /// 清空最近播放记录
    /// </summary>
    public static void Clear()
    {
        lock (s_lock)
        {
            s_songs.Clear();
        }
        Save();
    }

    private static void EnsureLoaded()
    {
        if (s_loaded) return;

        lock (s_lock)
        {
            if (s_loaded) return;
            s_loaded = true;

            if (!File.Exists(s_historyPath)) return;

            try
            {
                var json = File.ReadAllText(s_historyPath, Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    s_songs.Clear();
                    foreach (var item in root.EnumerateArray())
                    {
                        var mid = item.TryGetProperty("mid", out var m) ? m.GetString() ?? "" : "";
                        var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                        var artist = item.TryGetProperty("artist", out var a) ? a.GetString() ?? "" : "";
                        var album = item.TryGetProperty("album", out var al) ? al.GetString() ?? "" : "";
                        var duration = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
                        var mediaMid = item.TryGetProperty("media_mid", out var mm) ? mm.GetString() ?? mid : mid;
                        long id = 0;
                        if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                        {
                            id = idProp.GetInt64();
                        }

                        if (!string.IsNullOrEmpty(mid) || id > 0)
                        {
                            s_songs.Add(new Song(mid, title, artist, album, duration, mediaMid, id));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("RecentPlayHistory", "Failed to load recent play history", ex);
            }
        }
    }

    private static void Save()
    {
        try
        {
            if (!Directory.Exists(s_configDir))
            {
                Directory.CreateDirectory(s_configDir);
            }

            var sb = new StringBuilder();
            sb.Append("[\n");

            List<Song> snapshot;
            lock (s_lock)
            {
                snapshot = [.. s_songs];
            }

            for (int i = 0; i < snapshot.Count; i++)
            {
                var s = snapshot[i];
                sb.Append("  {");
                sb.Append($"\"mid\":\"{JsonEscape(s.Mid)}\",");
                sb.Append($"\"title\":\"{JsonEscape(s.Title)}\",");
                sb.Append($"\"artist\":\"{JsonEscape(s.Artist)}\",");
                sb.Append($"\"album\":\"{JsonEscape(s.Album)}\",");
                sb.Append($"\"duration\":{s.Duration},");
                sb.Append($"\"media_mid\":\"{JsonEscape(s.MediaMid)}\",");
                sb.Append($"\"id\":{s.Id}");
                sb.Append(i < snapshot.Count - 1 ? "},\n" : "}\n");
            }
            sb.Append(']');

            File.WriteAllText(s_historyPath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppLogger.Error("RecentPlayHistory", "Failed to save recent play history", ex);
        }
    }

    private static string JsonEscape(string s) =>
        s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") ?? "";
}
