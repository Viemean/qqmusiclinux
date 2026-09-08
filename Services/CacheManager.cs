using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

/// <summary>
/// 缓存条目元数据（专为 proot/Termux 免 atime 依赖而设计的应用层账本）
/// </summary>
public sealed class CacheEntry
{
    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("last_used")]
    public long LastUsedTicks { get; set; }

    [JsonPropertyName("hits")]
    public int HitCount { get; set; } = 1;
}

/// <summary>
/// 全局统一磁盘缓存管理器：
/// 1. 统一纳入 online audio (audio/)、WebDAV 音频 (webdav/) 与封面图像 (covers/)；
/// 2. 默认配额上限 1GB，统一池化管理；
/// 3. 采用应用层 SLRU (Segmented LRU) 算法，按 Probation 试听段与 Protected 常用段分级淘汰，抵抗快速切歌造成的缓存污染；
/// 4. 彻底脱离底层文件系统 atime 依赖，完美兼容 Android Termux / proot / Docker 等挂载限制环境。
/// </summary>
public static class CacheManager
{
    private static readonly string s_baseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "qqmusic-tui"
    );

    public static string AudioDir { get; } = Path.Combine(s_baseDir, "audio");
    public static string WebDavDir { get; } = Path.Combine(s_baseDir, "webdav");
    public static string CoversDir { get; } = Path.Combine(s_baseDir, "covers");
    private static readonly string s_indexFile = Path.Combine(s_baseDir, "cache_index.json");

    /// <summary>
    /// 全局统一缓存容量上限（默认 1 GB）
    /// </summary>
    public static long MaxTotalSizeBytes { get; set; } = 1024L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, CacheEntry> s_entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock s_saveLock = new();
    private static int s_saveScheduled = 0;
    private static int s_cleaningInProgress = 0;

    static CacheManager()
    {
        try
        {
            Directory.CreateDirectory(AudioDir);
            Directory.CreateDirectory(WebDavDir);
            Directory.CreateDirectory(CoversDir);

            LoadIndex();

            // 启动时异步执行一次遗留碎片清理与超额淘汰
            Task.Run(() =>
            {
                CleanupOrphanTmpFiles();
                EnforceLimit();
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CacheManager", $"CacheManager init failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 记录或刷新文件的访问记录（hit++，刷新最后使用时间，并纳入应用层账本）
    /// </summary>
    /// <param name="relativePath">相对于 ~/.cache/qqmusic-tui/ 的相对路径（例如 audio/mid_tier.media 或 webdav/srv_md5.flac）</param>
    /// <param name="fileSize">文件实际大小</param>
    public static void RecordAccess(string relativePath, long fileSize)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;

        var normalizedKey = relativePath.Replace('\\', '/').TrimStart('/');
        var nowTicks = DateTime.UtcNow.Ticks;

        s_entries.AddOrUpdate(
            normalizedKey,
            _ => new CacheEntry
            {
                Size = fileSize,
                LastUsedTicks = nowTicks,
                HitCount = 1
            },
            (_, existing) =>
            {
                existing.Size = fileSize > 0 ? fileSize : existing.Size;
                existing.LastUsedTicks = nowTicks;
                existing.HitCount = Math.Min(existing.HitCount + 1, 10000);
                return existing;
            }
        );

        ScheduleSaveIndex();
    }

    /// <summary>
    /// 从账本中移除记录（例如文件被主动删除时）
    /// </summary>
    public static void Forget(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        var normalizedKey = relativePath.Replace('\\', '/').TrimStart('/');
        if (s_entries.TryRemove(normalizedKey, out _))
        {
            ScheduleSaveIndex();
        }
    }

    /// <summary>
    /// 异步触发配额检查与淘汰
    /// </summary>
    public static void EnforceLimitAsync()
    {
        _ = Task.Run(EnforceLimit);
    }

    /// <summary>
    /// 执行 SLRU 统一配额淘汰
    /// </summary>
    public static void EnforceLimit()
    {
        if (Interlocked.CompareExchange(ref s_cleaningInProgress, 1, 0) != 0)
        {
            return; // 已有正在执行的清理任务，避免重复并发扫描
        }

        try
        {
            // 1. 扫描三大目录实际物理文件
            var fileList = new List<FileInfo>();
            ScanDirectorySafe(AudioDir, fileList);
            ScanDirectorySafe(WebDavDir, fileList);
            ScanDirectorySafe(CoversDir, fileList);

            long totalBytes = 0;
            var currentFilesOnDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var nowTicks = DateTime.UtcNow.Ticks;
            foreach (var fi in fileList)
            {
                // 跳过临时文件
                if (fi.Name.Contains(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                var rel = Path.GetRelativePath(s_baseDir, fi.FullName).Replace('\\', '/');
                currentFilesOnDisk.Add(rel);
                totalBytes += fi.Length;

                // 若文件未在账本中，以文件修改时间自动初始化补录
                s_entries.GetOrAdd(rel, _ => new CacheEntry
                {
                    Size = fi.Length,
                    LastUsedTicks = fi.LastWriteTimeUtc.Ticks > 0 ? fi.LastWriteTimeUtc.Ticks : nowTicks,
                    HitCount = 1
                });
            }

            // 清理账本中磁盘已不存在的幽灵条目
            foreach (var key in s_entries.Keys)
            {
                if (!currentFilesOnDisk.Contains(key))
                {
                    s_entries.TryRemove(key, out _);
                }
            }

            if (totalBytes <= MaxTotalSizeBytes)
            {
                ScheduleSaveIndex();
                return;
            }

            AppLogger.Info("CacheManager", $"Total cache size ({totalBytes / 1024 / 1024} MB) exceeds limit ({MaxTotalSizeBytes / 1024 / 1024} MB), starting SLRU eviction...");

            // 2. 划分 Probation 试听段 (hits == 1) 与 Protected 常用段 (hits >= 2)
            var probationList = new List<KeyValuePair<string, CacheEntry>>();
            var protectedList = new List<KeyValuePair<string, CacheEntry>>();

            foreach (var kvp in s_entries)
            {
                if (!currentFilesOnDisk.Contains(kvp.Key)) continue;

                if (kvp.Value.HitCount <= 1)
                {
                    probationList.Add(kvp);
                }
                else
                {
                    protectedList.Add(kvp);
                }
            }

            // 按最后使用时间升序（最旧的排在前面）
            probationList.Sort((a, b) => a.Value.LastUsedTicks.CompareTo(b.Value.LastUsedTicks));
            protectedList.Sort((a, b) => a.Value.LastUsedTicks.CompareTo(b.Value.LastUsedTicks));

            // 清理至 80% 容量防抖
            long targetBytes = (long)(MaxTotalSizeBytes * 0.8);

            // 第一阶段：优先淘汰 Probation 试听探索段（只播放过一次的歌曲与一次性封面）
            foreach (var item in probationList)
            {
                if (totalBytes <= targetBytes) break;
                totalBytes -= DeleteCacheItem(item.Key);
            }

            // 第二阶段：若依然超出上限，才从 Protected 常用段淘汰最久未听的歌曲
            if (totalBytes > targetBytes)
            {
                foreach (var item in protectedList)
                {
                    if (totalBytes <= targetBytes) break;
                    totalBytes -= DeleteCacheItem(item.Key);
                }
            }

            ScheduleSaveIndex();
            AppLogger.Info("CacheManager", $"SLRU eviction finished. New total cache size: {totalBytes / 1024 / 1024} MB");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CacheManager", $"EnforceLimit failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref s_cleaningInProgress, 0);
        }
    }

    private static long DeleteCacheItem(string relativePath)
    {
        long freedBytes = 0;
        try
        {
            var fullPath = Path.Combine(s_baseDir, relativePath);
            if (File.Exists(fullPath))
            {
                var fi = new FileInfo(fullPath);
                freedBytes = fi.Length;
                fi.Delete();
                AppLogger.Info("CacheManager", $"Evicted cache item: {relativePath} ({freedBytes / 1024} KB)");
            }

            // 若删除的是音频，检查并联动删除同名 .lrc 文件
            var ext = Path.GetExtension(fullPath);
            if (!string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase))
            {
                var lrcPath = Path.ChangeExtension(fullPath, ".lrc");
                if (File.Exists(lrcPath))
                {
                    try
                    {
                        var lrcFi = new FileInfo(lrcPath);
                        freedBytes += lrcFi.Length;
                        lrcFi.Delete();
                        var lrcRel = Path.ChangeExtension(relativePath, ".lrc").Replace('\\', '/');
                        s_entries.TryRemove(lrcRel, out _);
                    }
                    catch {}
                }
            }

            s_entries.TryRemove(relativePath, out _);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CacheManager", $"Failed to delete cache file {relativePath}: {ex.Message}");
        }

        return freedBytes;
    }

    private static void ScanDirectorySafe(string dir, List<FileInfo> output)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            var di = new DirectoryInfo(dir);
            output.AddRange(di.GetFiles());
        }
        catch {}
    }

    private static void CleanupOrphanTmpFiles()
    {
        string[] dirs = [AudioDir, WebDavDir, CoversDir];
        foreach (var d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            try
            {
                var di = new DirectoryInfo(d);
                foreach (var f in di.GetFiles("*.tmp*"))
                {
                    try
                    {
                        // 超过 1 小时未修改视为异常断电或切歌留下的孤儿临时文件
                        if (DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromHours(1))
                        {
                            f.Delete();
                        }
                    }
                    catch {}
                }
            }
            catch {}
        }
    }

    private static void LoadIndex()
    {
        if (!File.Exists(s_indexFile)) return;
        try
        {
            var json = File.ReadAllText(s_indexFile, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize(json, CacheIndexJsonContext.Default.DictionaryStringCacheEntry);
            if (loaded != null)
            {
                s_entries.Clear();
                foreach (var kvp in loaded)
                {
                    s_entries[kvp.Key] = kvp.Value;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CacheManager", $"Load cache index failed, will re-index: {ex.Message}");
        }
    }

    private static void ScheduleSaveIndex()
    {
        if (Interlocked.CompareExchange(ref s_saveScheduled, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000).ConfigureAwait(false); // 2秒防抖批量刷盘
                Interlocked.Exchange(ref s_saveScheduled, 0);
                SaveIndex();
            });
        }
    }

    private static void SaveIndex()
    {
        lock (s_saveLock)
        {
            try
            {
                var snapshot = new Dictionary<string, CacheEntry>(s_entries, StringComparer.OrdinalIgnoreCase);
                var json = JsonSerializer.Serialize(snapshot, CacheIndexJsonContext.Default.DictionaryStringCacheEntry);
                var tmp = s_indexFile + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, s_indexFile, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CacheManager", $"Save cache index failed: {ex.Message}");
            }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(Dictionary<string, CacheEntry>))]
internal partial class CacheIndexJsonContext : JsonSerializerContext
{
}
