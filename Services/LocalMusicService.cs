using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using QQMusic.Tui.Models;
using QQMusic.Tui.UI;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

public sealed class LocalSongCache
{
    public string FilePath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public int Duration { get; set; }
    public string Quality { get; set; } = "标准 128k";
    public long LastModifiedTicks { get; set; }
    public bool HasEmbeddedCover { get; set; }
    public bool HasEmbeddedLyrics { get; set; }
    public string? EmbeddedLyrics { get; set; }
}

public sealed class LocalMusicConfig
{
    public List<string> Folders { get; set; } = [];
    public List<LocalSongCache> CachedSongs { get; set; } = [];
}

/// <summary>
/// 本地音乐管理服务：支持目录递归扫描（严格跳过隐藏文件与目录）、ffprobe元数据解析、内嵌歌词/封面提取
/// 遵循严格要求：默认不添加任何文件夹，必须由用户手动添加
/// </summary>
public static class LocalMusicService
{
    private static readonly string s_configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "qqmusic-tui"
    );
    private static readonly string s_configFile = Path.Combine(s_configDir, "local_music.json");
    private static readonly string s_cacheDir = Path.Combine(Path.GetTempPath(), "qqmusic-tui", "covers");

    private static readonly HashSet<string> s_supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".wav", ".ogg", ".aac", ".ape", ".opus"
    };

    private static readonly Lock s_lock = new();
    private static LocalMusicConfig s_config = new();
    private static bool s_loaded = false;

    static LocalMusicService()
    {
        try
        {
            if (!Directory.Exists(s_configDir))
            {
                Directory.CreateDirectory(s_configDir);
            }
            if (!Directory.Exists(s_cacheDir))
            {
                Directory.CreateDirectory(s_cacheDir);
            }
        }
        catch {}
    }

    public static void LoadConfig()
    {
        lock (s_lock)
        {
            if (s_loaded) return;
            s_loaded = true;

            if (File.Exists(s_configFile))
            {
                try
                {
                    var json = File.ReadAllText(s_configFile, Encoding.UTF8);
                    var cfg = JsonSerializer.Deserialize(json, LocalMusicJsonContext.Default.LocalMusicConfig);
                    if (cfg != null)
                    {
                        s_config = cfg;
                        s_config.Folders ??= [];
                        s_config.CachedSongs ??= [];
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("LocalMusicService", $"Failed to load config: {ex.Message}");
                }
            }

            // 初始空配置：默认不添加任何文件夹，必须由用户手动添加
            s_config = new LocalMusicConfig
            {
                Folders = [],
                CachedSongs = []
            };
        }
    }

    public static void SaveConfig()
    {
        lock (s_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(s_config, LocalMusicJsonContext.Default.LocalMusicConfig);
                File.WriteAllText(s_configFile, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Error("LocalMusicService", $"Failed to save config: {ex.Message}");
            }
        }
    }

    public static List<string> GetFolders()
    {
        LoadConfig();
        lock (s_lock)
        {
            return new List<string>(s_config.Folders);
        }
    }

    public static bool AddFolder(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return false;

        var path = ExpandHome(rawPath.Trim());
        if (!Directory.Exists(path)) return false;

        var fullPath = Path.GetFullPath(path);

        lock (s_lock)
        {
            LoadConfig();
            foreach (var f in s_config.Folders)
            {
                if (string.Equals(Path.GetFullPath(f), fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true; // 已经存在
                }
            }

            s_config.Folders.Add(fullPath);
            SaveConfig();
            return true;
        }
    }

    public static bool RemoveFolder(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return false;
        var fullPath = Path.GetFullPath(ExpandHome(rawPath.Trim()));

        lock (s_lock)
        {
            LoadConfig();
            var removed = s_config.Folders.RemoveAll(f =>
                string.Equals(Path.GetFullPath(f), fullPath, StringComparison.OrdinalIgnoreCase)
            ) > 0;

            if (removed)
            {
                // 清除属于该目录的缓存歌曲
                s_config.CachedSongs.RemoveAll(s =>
                    s.FilePath.StartsWith(fullPath, StringComparison.OrdinalIgnoreCase)
                );
                SaveConfig();
            }

            return removed;
        }
    }

    /// <summary>
    /// 获取当前扫描缓存的所有本地歌曲
    /// </summary>
    public static List<Song> GetCachedSongs()
    {
        LoadConfig();
        lock (s_lock)
        {
            var result = new List<Song>(s_config.CachedSongs.Count);
            foreach (var item in s_config.CachedSongs)
            {
                if (File.Exists(item.FilePath))
                {
                    result.Add(ToSongModel(item));
                }
            }
            return result;
        }
    }

    /// <summary>
    /// 异步扫描已配置的本地音乐文件夹
    /// </summary>
    public static async Task<List<Song>> ScanAllFoldersAsync(Action<string>? onProgress = null)
    {
        LoadConfig();
        List<string> folders;
        lock (s_lock)
        {
            folders = new List<string>(s_config.Folders);
        }

        if (folders.Count == 0)
        {
            return [];
        }

        // 1. 发现所有有效音频文件（跳过隐藏项）
        var foundFiles = new List<string>();
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            onProgress?.Invoke($"正在扫描目录: {Path.GetFileName(folder)} ...");
            await Task.Run(() => ScanDirectoryRecursive(new DirectoryInfo(folder), foundFiles));
        }

        // 2. 建立现有缓存映射 (按路径)
        Dictionary<string, LocalSongCache> cacheMap;
        lock (s_lock)
        {
            cacheMap = new Dictionary<string, LocalSongCache>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in s_config.CachedSongs)
            {
                cacheMap[c.FilePath] = c;
            }
        }

        var updatedSongs = new ConcurrentBag<LocalSongCache>();
        var total = foundFiles.Count;
        var processed = 0;

        // 3. 并行并发提取元数据（限制并发数以防占用过多系统进程）
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8)
        };

        await Parallel.ForEachAsync(foundFiles, parallelOptions, async (filePath, ct) =>
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists) return;

                var lastModified = fileInfo.LastWriteTimeUtc.Ticks;

                // 若缓存存在且文件未改动，直接复用
                if (cacheMap.TryGetValue(filePath, out var cached) && cached.LastModifiedTicks == lastModified)
                {
                    updatedSongs.Add(cached);
                }
                else
                {
                    // 通过 ffprobe 异步提取
                    var parsed = await ExtractMetadataWithFfprobeAsync(filePath, fileInfo);
                    updatedSongs.Add(parsed);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("LocalMusicService", $"Error processing {filePath}: {ex.Message}");
            }

            var curr = System.Threading.Interlocked.Increment(ref processed);
            if (curr % 10 == 0 || curr == total)
            {
                onProgress?.Invoke($"已解析本地音乐: {curr}/{total} 首");
            }
        });

        // 4. 排序并保存缓存
        var songList = new List<LocalSongCache>(updatedSongs);
        songList.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));

        lock (s_lock)
        {
            s_config.CachedSongs = songList;
            SaveConfig();
        }

        var finalSongs = new List<Song>(songList.Count);
        foreach (var item in songList)
        {
            finalSongs.Add(ToSongModel(item));
        }

        return finalSongs;
    }

    /// <summary>
    /// 递归扫描目录，严格跳过所有以 . 开头的隐藏目录和隐藏文件
    /// </summary>
    private static void ScanDirectoryRecursive(DirectoryInfo dir, List<string> results)
    {
        // 严格跳过隐藏目录（如 .git, .thumbnails, .cache 等）
        if (dir.Name.StartsWith('.')) return;

        try
        {
            foreach (var file in dir.EnumerateFiles())
            {
                // 严格跳过隐藏文件（以 . 开头）
                if (file.Name.StartsWith('.')) continue;

                if (s_supportedExtensions.Contains(file.Extension))
                {
                    results.Add(file.FullName);
                }
            }

            foreach (var subDir in dir.EnumerateDirectories())
            {
                if (subDir.Name.StartsWith('.')) continue;
                ScanDirectoryRecursive(subDir, results);
            }
        }
        catch
        {
            // 权限受限或读取异常忽略
        }
    }

    /// <summary>
    /// 使用系统 ffprobe 读取音频文件元数据
    /// </summary>
    private static async Task<LocalSongCache> ExtractMetadataWithFfprobeAsync(string filePath, FileInfo fileInfo)
    {
        var entry = new LocalSongCache
        {
            FilePath = filePath,
            Title = Path.GetFileNameWithoutExtension(filePath),
            Artist = "未知歌手",
            Album = "本地音乐",
            Duration = 0,
            Quality = DetermineQualityFromExtension(fileInfo.Extension),
            LastModifiedTicks = fileInfo.LastWriteTimeUtc.Ticks
        };

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return entry;

            var json = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (string.IsNullOrWhiteSpace(json)) return entry;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 1. 读取 Format 信息
            if (root.TryGetProperty("format", out var formatElem))
            {
                if (formatElem.TryGetProperty("duration", out var durElem) &&
                    double.TryParse(durElem.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var durSec))
                {
                    entry.Duration = (int)Math.Round(durSec);
                }

                if (formatElem.TryGetProperty("tags", out var tagsElem))
                {
                    foreach (var prop in tagsElem.EnumerateObject())
                    {
                        var key = prop.Name;
                        var val = prop.Value.GetString()?.Trim() ?? "";
                        if (string.IsNullOrEmpty(val)) continue;

                        if (string.Equals(key, "title", StringComparison.OrdinalIgnoreCase))
                        {
                            entry.Title = val;
                        }
                        else if (string.Equals(key, "artist", StringComparison.OrdinalIgnoreCase))
                        {
                            entry.Artist = val;
                        }
                        else if (string.Equals(key, "album", StringComparison.OrdinalIgnoreCase))
                        {
                            entry.Album = val;
                        }
                        else if (string.Equals(key, "lyrics", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(key, "unsyncedlyrics", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(key, "USLT", StringComparison.OrdinalIgnoreCase))
                        {
                            entry.EmbeddedLyrics = val;
                            entry.HasEmbeddedLyrics = true;
                        }
                    }
                }

                // 计算音质
                if (formatElem.TryGetProperty("bit_rate", out var brElem) &&
                    long.TryParse(brElem.GetString(), out var bitRate))
                {
                    entry.Quality = DetermineQualityFromBitRate(fileInfo.Extension, bitRate);
                }
            }

            // 2. 检测是否存在内嵌图片流 (Video/Attached Pic)
            if (root.TryGetProperty("streams", out var streamsElem) && streamsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streamsElem.EnumerateArray())
                {
                    if (stream.TryGetProperty("codec_type", out var ctElem) &&
                        ctElem.GetString() == "video")
                    {
                        entry.HasEmbeddedCover = true;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LocalMusicService", $"ffprobe failed for {filePath}: {ex.Message}");
        }

        return entry;
    }

    private static string DetermineQualityFromExtension(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            ".flac" or ".ape" or ".wav" => "SQ 无损",
            ".mp3" => "HQ 320k",
            ".m4a" or ".aac" or ".ogg" or ".opus" => "HQ 高品质",
            _ => "标准 128k"
        };
    }

    private static string DetermineQualityFromBitRate(string ext, long bitRate)
    {
        var lower = ext.ToLowerInvariant();
        if (lower is ".flac" or ".ape" or ".wav")
        {
            return bitRate > 2000000 ? "Hi-Res" : "SQ 无损";
        }
        if (bitRate >= 300000)
        {
            return "HQ 320k";
        }
        if (bitRate >= 190000)
        {
            return "HQ 192k";
        }
        return "标准 128k";
    }

    /// <summary>
    /// 获取本地歌曲的歌词（优先同目录同名 .lrc 文件，其次内嵌歌词）
    /// </summary>
    public static async Task<List<LyricLine>> GetLyricsAsync(Song song)
    {
        if (string.IsNullOrEmpty(song.LocalFilePath) || !File.Exists(song.LocalFilePath))
        {
            return [];
        }

        // 1. 同名 .lrc 文件检查
        var lrcPath = Path.ChangeExtension(song.LocalFilePath, ".lrc");
        if (File.Exists(lrcPath))
        {
            try
            {
                var lrcText = await File.ReadAllTextAsync(lrcPath, Encoding.UTF8);
                var parsed = LyricParser.ParseSingleLrc(lrcText);
                if (parsed.Count > 0) return parsed;
            }
            catch {}
        }

        // 2. 内嵌歌词读取
        string? embeddedLyrics = null;
        lock (s_lock)
        {
            var cached = s_config.CachedSongs.Find(s =>
                string.Equals(s.FilePath, song.LocalFilePath, StringComparison.OrdinalIgnoreCase)
            );
            if (cached != null && !string.IsNullOrEmpty(cached.EmbeddedLyrics))
            {
                embeddedLyrics = cached.EmbeddedLyrics;
            }
        }

        if (string.IsNullOrEmpty(embeddedLyrics))
        {
            // 若缓存中未记录，再次轻量提取
            var fi = new FileInfo(song.LocalFilePath);
            var entry = await ExtractMetadataWithFfprobeAsync(song.LocalFilePath, fi);
            embeddedLyrics = entry.EmbeddedLyrics;
        }

        if (!string.IsNullOrWhiteSpace(embeddedLyrics))
        {
            return LyricParser.ParseSingleLrc(embeddedLyrics);
        }

        return [];
    }

    /// <summary>
    /// 获取或提取本地歌曲的封面图片（支持内嵌封面提取与同目录 cover.jpg，带 6px 圆角处理）
    /// </summary>
    public static async Task<string?> EnsureCoverAsync(Song song)
    {
        if (string.IsNullOrEmpty(song.LocalFilePath) || !File.Exists(song.LocalFilePath))
        {
            return null;
        }

        var md5 = ComputeMd5(song.LocalFilePath);
        var targetPng = Path.Combine(s_cacheDir, $"local_{md5}.png");
        if (File.Exists(targetPng) && new FileInfo(targetPng).Length > 0)
        {
            return targetPng;
        }

        // 1. 尝试从音频文件中提取内嵌封面
        var tempExtractJpg = Path.Combine(s_cacheDir, $"raw_{md5}.jpg");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -i \"{song.LocalFilePath}\" -an -vcodec copy \"{tempExtractJpg}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0 && File.Exists(tempExtractJpg) && new FileInfo(tempExtractJpg).Length > 1024)
                {
                    var result = await TerminalImageHelper.EnsureLocalImageProcessedAsync(tempExtractJpg, md5);
                    try { File.Delete(tempExtractJpg); } catch {}
                    if (!string.IsNullOrEmpty(result)) return result;
                }
            }
        }
        catch {}

        // 2. 回退：查找同目录下的常见封面命名
        var dir = Path.GetDirectoryName(song.LocalFilePath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            string[] candidateNames = ["cover.jpg", "cover.png", "folder.jpg", "front.jpg", "Cover.jpg", "Folder.jpg"];
            foreach (var name in candidateNames)
            {
                var candidatePath = Path.Combine(dir, name);
                if (File.Exists(candidatePath) && new FileInfo(candidatePath).Length > 1024)
                {
                    var result = await TerminalImageHelper.EnsureLocalImageProcessedAsync(candidatePath, md5);
                    if (!string.IsNullOrEmpty(result)) return result;
                }
            }
        }

        return null;
    }

    private static Song ToSongModel(LocalSongCache item)
    {
        var hashId = Math.Abs((long)item.FilePath.GetHashCode());
        var fakeMid = $"local_{ComputeMd5(item.FilePath)[..14]}";

        return new Song(
            Mid: fakeMid,
            Title: item.Title,
            Artist: item.Artist,
            Album: item.Album,
            Duration: item.Duration,
            MediaMid: fakeMid,
            Id: hashId,
            AlbumMid: ""
        )
        {
            LocalFilePath = item.FilePath,
            PlayUrl = item.FilePath,
            Quality = item.Quality
        };
    }

    private static string ComputeMd5(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ExpandHome(string path)
    {
        if (path.StartsWith("~/") || path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path == "~" ? home : Path.Combine(home, path[2..]);
        }
        return path;
    }
}

[JsonSerializable(typeof(LocalMusicConfig))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<LocalSongCache>))]
[JsonSerializable(typeof(LocalSongCache))]
internal partial class LocalMusicJsonContext : JsonSerializerContext
{
}

