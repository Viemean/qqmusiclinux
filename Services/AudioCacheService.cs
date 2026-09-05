using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

/// <summary>
/// 音频本地磁盘 LRU 边播边存与秒开缓存服务
/// 遵循 XDG 规范，存储于 ~/.cache/qqmusic-tui/audio/
/// </summary>
public static class AudioCacheService
{
    private static readonly HttpClient s_httpClient = new();
    private static readonly string s_cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "qqmusic-tui", "audio"
    );

    /// <summary>
    /// 默认最大音频缓存容量（512 MB）
    /// </summary>
    public static long MaxCacheSizeBytes { get; set; } = 512L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, Task<string?>> s_inFlightDownloads = new(StringComparer.OrdinalIgnoreCase);

    static AudioCacheService()
    {
        try
        {
            if (!Directory.Exists(s_cacheDir))
            {
                Directory.CreateDirectory(s_cacheDir);
            }

            // 启动时后台异步清理遗留临时文件与超额缓存
            Task.Run(() =>
            {
                CleanupOrphanTmpFiles();
                EnforceCacheLimit();
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AudioCacheService", $"Init audio cache dir failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 尝试获取本地已缓存的音频文件路径。若命中且完整，更新最后访问时间并返回绝对路径。
    /// </summary>
    public static string? GetCachedAudioPath(string songMid, AudioQualityTier tier)
    {
        if (string.IsNullOrWhiteSpace(songMid)) return null;

        try
        {
            var targetFile = Path.Combine(s_cacheDir, $"{songMid}_{tier}.media");
            if (File.Exists(targetFile))
            {
                var fi = new FileInfo(targetFile);
                // 确保文件大小大于 64KB，排除损坏或零字节异常文件
                if (fi.Length > 64 * 1024)
                {
                    try
                    {
                        File.SetLastAccessTimeUtc(targetFile, DateTime.UtcNow);
                    }
                    catch
                    {
                        // 忽略更新访问时间失败
                    }
                    return targetFile;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AudioCacheService", $"GetCachedAudioPath failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 异步后台边播边存流式下载音频文件并原子落盘
    /// </summary>
    public static Task<string?> CacheAudioAsync(string songMid, AudioQualityTier tier, string cdnUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(songMid) || string.IsNullOrWhiteSpace(cdnUrl))
        {
            return Task.FromResult<string?>(null);
        }

        // 本地文件或已缓存无需再次下载
        if (!cdnUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !cdnUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<string?>(null);
        }

        var key = $"{songMid}_{tier}";
        return s_inFlightDownloads.GetOrAdd(key, _ => Task.Run(() => DownloadInternalAsync(songMid, tier, cdnUrl, ct)));
    }

    private static async Task<string?> DownloadInternalAsync(string songMid, AudioQualityTier tier, string cdnUrl, CancellationToken ct)
    {
        var targetFile = Path.Combine(s_cacheDir, $"{songMid}_{tier}.media");
        var tempFile = targetFile + $".tmp.{Guid.NewGuid():N}";

        try
        {
            // 双重检查：如果已存在完整目标文件，直接复用
            if (File.Exists(targetFile))
            {
                var existingFi = new FileInfo(targetFile);
                if (existingFi.Length > 64 * 1024)
                {
                    return targetFile;
                }
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, cdnUrl);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            req.Headers.Add("Referer", "https://y.qq.com/");

            using var resp = await s_httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                AppLogger.Warn("AudioCacheService", $"Audio download HTTP failed ({resp.StatusCode}): {cdnUrl}");
                return null;
            }

            await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await resp.Content.CopyToAsync(fs, ct);
            }

            var fi = new FileInfo(tempFile);
            if (fi.Length > 64 * 1024)
            {
                File.Move(tempFile, targetFile, overwrite: true);
                File.SetLastAccessTimeUtc(targetFile, DateTime.UtcNow);
                AppLogger.Info("AudioCacheService", $"Audio cached successfully ({fi.Length / 1024} KB): {targetFile}");

                // 执行磁盘配额检查
                EnforceCacheLimit();
                return targetFile;
            }
            else
            {
                AppLogger.Warn("AudioCacheService", $"Downloaded audio too small ({fi.Length} bytes), discarded.");
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AudioCacheService", $"Audio caching failed for {songMid}: {ex.Message}");
        }
        finally
        {
            var key = $"{songMid}_{tier}";
            s_inFlightDownloads.TryRemove(key, out _);

            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }

        return null;
    }

    /// <summary>
    /// LRU 容量淘汰机制：若总大小超出上限，按最后访问时间清理最旧音频至阈值以下
    /// </summary>
    public static void EnforceCacheLimit()
    {
        try
        {
            if (!Directory.Exists(s_cacheDir)) return;

            var dirInfo = new DirectoryInfo(s_cacheDir);
            var files = dirInfo.GetFiles("*.media");

            long totalBytes = 0;
            foreach (var f in files)
            {
                totalBytes += f.Length;
            }

            if (totalBytes <= MaxCacheSizeBytes) return;

            AppLogger.Info("AudioCacheService", $"Audio cache size ({totalBytes / 1024 / 1024} MB) exceeds limit ({MaxCacheSizeBytes / 1024 / 1024} MB), starting LRU eviction...");

            // 按最后访问时间升序排序（最旧的在前面）
            Array.Sort(files, (a, b) =>
            {
                var cmp = a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc);
                return cmp != 0 ? cmp : a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc);
            });

            long targetBytes = (long)(MaxCacheSizeBytes * 0.8); // 清理至 80% 容量防抖

            foreach (var f in files)
            {
                if (totalBytes <= targetBytes) break;
                try
                {
                    var len = f.Length;
                    f.Delete();
                    totalBytes -= len;
                    AppLogger.Info("AudioCacheService", $"Evicted old audio cache: {f.Name}");
                }
                catch
                {
                    // 正在占用的文件忽略
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AudioCacheService", $"EnforceCacheLimit failed: {ex.Message}");
        }
    }

    private static void CleanupOrphanTmpFiles()
    {
        try
        {
            if (!Directory.Exists(s_cacheDir)) return;

            var dirInfo = new DirectoryInfo(s_cacheDir);
            var tmpFiles = dirInfo.GetFiles("*.tmp.*");
            foreach (var tmp in tmpFiles)
            {
                try
                {
                    // 若超过 1 小时未变动则视为上次异常残留
                    if (DateTime.UtcNow - tmp.LastWriteTimeUtc > TimeSpan.FromHours(1))
                    {
                        tmp.Delete();
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}
