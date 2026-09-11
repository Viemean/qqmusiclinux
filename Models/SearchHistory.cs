using System.Text;
using System.Text.Json;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Models;

public static class SearchHistory
{
    private static readonly string s_configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "qqmusic-tui");
    private static readonly string s_historyPath = Path.Combine(s_configDir, "search_history.json");
    private static readonly object s_lock = new();
    private static readonly List<string> s_queries = [];
    private static bool s_loaded;
    private const int MaxHistoryCount = 30;

    public static List<string> GetQueries()
    {
        EnsureLoaded();
        lock (s_lock)
        {
            return [.. s_queries];
        }
    }

    public static void Add(string query)
    {
        string value = query.Trim();
        if (string.IsNullOrEmpty(value)) return;

        EnsureLoaded();
        lock (s_lock)
        {
            s_queries.RemoveAll(item => item.Equals(value, StringComparison.OrdinalIgnoreCase));
            s_queries.Insert(0, value);
            if (s_queries.Count > MaxHistoryCount)
            {
                s_queries.RemoveRange(MaxHistoryCount, s_queries.Count - MaxHistoryCount);
            }
            SaveLocked();
        }
    }

    public static void Clear()
    {
        EnsureLoaded();
        lock (s_lock)
        {
            s_queries.Clear();
            SaveLocked();
        }
    }

    private static void EnsureLoaded()
    {
        lock (s_lock)
        {
            if (s_loaded) return;
            s_loaded = true;
            if (!File.Exists(s_historyPath)) return;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(s_historyPath, Encoding.UTF8));
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string value = item.GetString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(value) && !s_queries.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        s_queries.Add(value);
                        if (s_queries.Count == MaxHistoryCount) break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SearchHistory", $"Failed to load search history: {ex.Message}");
            }
        }
    }

    private static void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(s_configDir);
            string tempPath = s_historyPath + ".tmp";
            using (var stream = File.Create(tempPath))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartArray();
                foreach (string query in s_queries)
                {
                    writer.WriteStringValue(query);
                }
                writer.WriteEndArray();
            }
            File.Move(tempPath, s_historyPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SearchHistory", $"Failed to save search history: {ex.Message}");
        }
    }
}
