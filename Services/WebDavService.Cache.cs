using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

public static partial class WebDavService
{
    public static string GetLocalCachePath(WebDavServer server, string fileHref)
    {
        var ext = Path.GetExtension(fileHref);
        var filename = $"{server.Id}_{ComputeMd5(fileHref)}{ext}";
        return Path.Combine(s_cacheDir, filename);
    }

    public static string GetLocalLrcCachePath(WebDavServer server, string fileHref)
    {
        var filename = $"{server.Id}_{ComputeMd5(fileHref)}.lrc";
        return Path.Combine(s_cacheDir, filename);
    }

    public static async Task<string?> TryDownloadRemoteLrcAsync(WebDavServer server, string audioHref, CancellationToken ct = default)
    {
        try
        {
            var localLrcPath = GetLocalLrcCachePath(server, audioHref);
            if (File.Exists(localLrcPath) && new FileInfo(localLrcPath).Length > 0)
            {
                return await File.ReadAllTextAsync(localLrcPath, Encoding.UTF8, ct).ConfigureAwait(false);
            }

            var lrcHref = Path.ChangeExtension(audioHref, ".lrc");
            var client = GetHttpClient(server);
            var lrcUri = BuildFullUri(server, lrcHref);
            using var resp = await client.GetAsync(lrcUri, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var lrcContent = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(lrcContent))
                {
                    await File.WriteAllTextAsync(localLrcPath, lrcContent, Encoding.UTF8, ct).ConfigureAwait(false);
                    CacheManager.RecordAccess($"webdav/{Path.GetFileName(localLrcPath)}", new FileInfo(localLrcPath).Length);
                    return lrcContent;
                }
            }
        }
        catch
        {
            // 远端可能没有单独的 .lrc 文件，后续回退读取内嵌歌词
        }
        return null;
    }

    public static Song EnrichSongMetadata(WebDavServer server, Song song, string localPath)
    {
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) return song;
        try
        {
            var track = new ATL.Track(localPath);
            var title = !string.IsNullOrWhiteSpace(track.Title) ? track.Title.Trim() : CleanTrackNumberPrefix(song.Title);
            var artist = !string.IsNullOrWhiteSpace(track.Artist) ? track.Artist.Trim() : song.Artist;
            var album = !string.IsNullOrWhiteSpace(track.Album) ? track.Album.Trim() : (string.IsNullOrWhiteSpace(song.Album) ? "WebDAV 专辑" : song.Album);
            var duration = track.Duration > 0 ? track.Duration : song.Duration;
            var quality = InferQualityBadge(localPath, track);

            var enriched = song with
            {
                Title = title,
                Artist = artist,
                Album = album,
                Duration = duration,
                Quality = quality,
                LocalFilePath = localPath
            };

            TryUpdateCacheMetadata(server, song.WebDavHref ?? "", localPath, title, artist, album, duration, quality);

            return enriched;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("WebDavService", $"EnrichSongMetadata failed for {localPath}: {ex.Message}");
            return song with { LocalFilePath = localPath };
        }
    }

    public static async Task<string?> GetOrDownloadAudioAsync(WebDavServer server, string fileHref, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fileHref)) return null;
        if (cancellationToken.IsCancellationRequested) return null;

        var localPath = GetLocalCachePath(server, fileHref);

        // 若本地完整缓存已存在且大于 4KB，秒开命中
        if (File.Exists(localPath))
        {
            var existingFi = new FileInfo(localPath);
            if (existingFi.Length > 4096)
            {
                CacheManager.RecordAccess($"webdav/{Path.GetFileName(localPath)}", existingFi.Length);
                return localPath;
            }
        }

        var downloadTask = s_inFlightDownloads.GetOrAdd(localPath, _ => Task.Run(async () =>
        {
            var tmpPath = localPath + $".{Environment.TickCount64}.tmp";
            try
            {
                var client = GetHttpClient(server);
                var uri = BuildFullUri(server, fileHref);

                if (cancellationToken.IsCancellationRequested) return null;
                progress?.Invoke("正在连接 WebDAV 缓冲音频流...");
                using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    AppLogger.Warn("WebDavService", $"Failed to download audio {uri}: {resp.StatusCode}");
                    return null;
                }

                var totalBytes = resp.Content.Headers.ContentLength ?? -1L;
                using var remoteStream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);

                byte[] buffer = new byte[64 * 1024];
                long downloaded = 0;
                int read;
                var lastProgressTick = Environment.TickCount64;

                while ((read = await remoteStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    downloaded += read;

                    if (Environment.TickCount64 - lastProgressTick > 250)
                    {
                        lastProgressTick = Environment.TickCount64;
                        if (cancellationToken.IsCancellationRequested) break;
                        if (totalBytes > 0)
                        {
                            int pct = (int)((downloaded * 100) / totalBytes);
                            progress?.Invoke($"正在缓冲 WebDAV 音频: {pct}% ({downloaded / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB)");
                        }
                        else
                        {
                            progress?.Invoke($"正在缓冲 WebDAV 音频: {downloaded / 1024 / 1024}MB");
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                await fileStream.FlushAsync(cancellationToken);
                fileStream.Dispose();

                if (File.Exists(localPath))
                {
                    File.Delete(localPath);
                }
                File.Move(tmpPath, localPath);

                var downloadedFi = new FileInfo(localPath);
                CacheManager.RecordAccess($"webdav/{Path.GetFileName(localPath)}", downloadedFi.Length);
                CacheManager.EnforceLimitAsync();

                // 尝试用 ATL.NET 补充解析标签并缓存
                TryUpdateCacheMetadata(server, fileHref, localPath);

                return localPath;
            }
            catch (OperationCanceledException)
            {
                AppLogger.Info("WebDavService", $"Download audio canceled: {fileHref}");
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch {}
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Error("WebDavService", $"Download audio failed {fileHref}: {ex.Message}");
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch {}
                return null;
            }
            finally
            {
                s_inFlightDownloads.TryRemove(localPath, out Task<string?>? _);
            }
        }, cancellationToken));

        try
        {
            return await downloadTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public static void TryUpdateCacheMetadata(
        WebDavServer server,
        string fileHref,
        string localPath,
        string? overrideTitle = null,
        string? overrideArtist = null,
        string? overrideAlbum = null,
        int overrideDuration = 0,
        string? overrideQuality = null)
    {
        try
        {
            ATL.Track? track = null;
            if (File.Exists(localPath))
            {
                try { track = new ATL.Track(localPath); } catch {}
            }

            lock (s_lock)
            {
                server.CachedSongs ??= [];
                var cacheItem = server.CachedSongs.Find(s => string.Equals(s.Href, fileHref, StringComparison.OrdinalIgnoreCase));
                if (cacheItem == null)
                {
                    cacheItem = new WebDavSongCache
                    {
                        ServerId = server.Id,
                        Href = fileHref
                    };
                    server.CachedSongs.Add(cacheItem);
                }

                var title = track != null && !string.IsNullOrWhiteSpace(track.Title) ? track.Title.Trim() : overrideTitle;
                var artist = track != null && !string.IsNullOrWhiteSpace(track.Artist) ? track.Artist.Trim() : overrideArtist;
                var album = track != null && !string.IsNullOrWhiteSpace(track.Album) ? track.Album.Trim() : overrideAlbum;

                if (!string.IsNullOrWhiteSpace(title)) cacheItem.Title = CleanTrackNumberPrefix(title);
                if (!string.IsNullOrWhiteSpace(artist)) cacheItem.Artist = artist;
                if (!string.IsNullOrWhiteSpace(album)) cacheItem.Album = album;

                if (track != null && track.Duration > 0) cacheItem.Duration = track.Duration;
                else if (overrideDuration > 0) cacheItem.Duration = overrideDuration;

                if (!string.IsNullOrWhiteSpace(overrideQuality)) cacheItem.Quality = overrideQuality;
                else if (File.Exists(localPath)) cacheItem.Quality = InferQualityBadge(localPath, track);

                if (File.Exists(localPath))
                {
                    cacheItem.LocalCachedPath = localPath;
                    cacheItem.FileSize = new FileInfo(localPath).Length;
                }
                SaveConfig();
            }
        }
        catch {}
    }

}
