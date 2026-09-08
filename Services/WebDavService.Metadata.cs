using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.UI;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

public static partial class WebDavService
{
    public static List<Song> DeduplicateSongs(IEnumerable<Song> songs)
    {
        var result = new List<Song>();
        var seenFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in songs)
        {
            var cleanTitle = CleanTrackNumberPrefix(s.Title).Trim().ToLowerInvariant();
            var artist = (s.Artist ?? "").Trim().ToLowerInvariant();
            bool isUnknownArtist = string.IsNullOrEmpty(artist) || artist == "未知歌手";

            string fp;
            if (!isUnknownArtist && s.Duration > 0)
            {
                fp = $"meta:{cleanTitle}|{artist}|{s.Duration}";
            }
            else if (!isUnknownArtist)
            {
                fp = $"meta:{cleanTitle}|{artist}";
            }
            else
            {
                fp = $"href:{s.WebDavHref}";
            }

            if (seenFingerprints.Add(fp))
            {
                result.Add(s);
            }
        }

        return result;
    }

    public static Song ToSongModel(WebDavServer server, WebDavSongCache cache)
    {
        var fakeMid = $"webdav_{server.Id[..6]}_{ComputeMd5(cache.Href)[..10]}";
        var hashId = Math.Abs((long)cache.Href.GetHashCode());

        return new Song(
            Mid: fakeMid,
            Title: CleanTrackNumberPrefix(cache.Title),
            Artist: cache.Artist,
            Album: cache.Album,
            Duration: cache.Duration,
            MediaMid: fakeMid,
            Id: hashId,
            AlbumMid: ""
        )
        {
            WebDavServerId = server.Id,
            WebDavHref = cache.Href,
            LocalFilePath = cache.LocalCachedPath,
            PlayUrl = cache.LocalCachedPath ?? "",
            Quality = cache.Quality
        };
    }

    public static Song ToSongModel(WebDavServer server, WebDavItem item)
    {
        // 优先从已有的曲库缓存中查找信息
        lock (s_lock)
        {
            if (server.CachedSongs != null)
            {
                var cached = server.CachedSongs.Find(s => string.Equals(s.Href, item.Href, StringComparison.OrdinalIgnoreCase));
                if (cached != null)
                {
                    return ToSongModel(server, cached);
                }
            }
        }

        var parsed = InferTitleArtist(item.Name);
        var fakeMid = $"webdav_{server.Id[..6]}_{ComputeMd5(item.Href)[..10]}";
        var hashId = Math.Abs((long)item.Href.GetHashCode());

        return new Song(
            Mid: fakeMid,
            Title: parsed.Title,
            Artist: parsed.Artist,
            Album: "WebDAV",
            Duration: 0,
            MediaMid: fakeMid,
            Id: hashId,
            AlbumMid: ""
        )
        {
            WebDavServerId = server.Id,
            WebDavHref = item.Href,
            Quality = InferQualityBadge(item.Name, null)
        };
    }

    public static string CleanTrackNumberPrefix(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var s = raw.Trim();
        // 剥离如 "01. ", "02 - ", "01 ", "[01] ", "(01) " 等音轨序号前缀
        var match = System.Text.RegularExpressions.Regex.Match(s, @"^(?:\[?\d{1,3}\]?[\.\-_\s]+)(.+)");
        if (match.Success && match.Groups.Count > 1)
        {
            var cleaned = match.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(cleaned))
            {
                return cleaned;
            }
        }
        return s;
    }

    public static (string Title, string Artist) InferTitleArtist(string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename).Trim();
        if (name.Contains(" - "))
        {
            var parts = name.Split(" - ", 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                var p0 = CleanTrackNumberPrefix(parts[0].Trim());
                var p1 = CleanTrackNumberPrefix(parts[1].Trim());
                return (p1, p0);
            }
        }
        var cleaned = CleanTrackNumberPrefix(name);
        return (cleaned, "未知歌手");
    }

    private static string InferQualityBadge(string filepath, ATL.Track? track)
    {
        var ext = Path.GetExtension(filepath).ToLowerInvariant();
        if (track != null && (track.BitDepth > 16 || track.SampleRate > 48000))
        {
            return "Hi-Res 无损";
        }
        if (ext is ".flac" or ".wav" or ".ape")
        {
            return "SQ 无损";
        }
        if (ext is ".m4a" or ".ogg" or ".opus")
        {
            return "HQ 高品质";
        }
        return "标准 128k";
    }

    public static Uri BuildFullUri(WebDavServer server, string relativeHref)
    {
        var serverRaw = (server.Url ?? string.Empty).Trim();
        if (!serverRaw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !serverRaw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            serverRaw = "http://" + serverRaw;
        }
        var serverUri = new Uri(serverRaw.TrimEnd('/') + "/");

        if (string.IsNullOrWhiteSpace(relativeHref) || relativeHref == "/")
        {
            return serverUri;
        }

        // 1. 若为完整绝对 URL 直接使用
        if (Uri.TryCreate(relativeHref, UriKind.Absolute, out var absUri) &&
            (absUri.Scheme == Uri.UriSchemeHttp || absUri.Scheme == Uri.UriSchemeHttps))
        {
            return absUri;
        }

        // 2. 解码后进行绝对/相对路径判定
        var decoded = Uri.UnescapeDataString(relativeHref);
        var path = decoded.StartsWith('/') ? decoded : "/" + decoded;

        // 获取服务器 URL 的子路径（例如 "/dav"）
        var serverBasePath = Uri.UnescapeDataString(serverUri.AbsolutePath).TrimEnd('/');

        if (!string.IsNullOrEmpty(serverBasePath) && serverBasePath != "/")
        {
            // 如果 path 已经以 serverBasePath 开头（例如 "/dav/RMedia/"），直接挂在 Host 根下，绝不重复拼接 /dav
            if (path.Equals(serverBasePath, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(serverBasePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                var hostBase = new Uri(serverUri.GetLeftPart(UriPartial.Authority));
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var escapedPath = "/" + string.Join("/", segments.Select(Uri.EscapeDataString));
                if (path.EndsWith('/')) escapedPath += "/";
                return new Uri(hostBase, escapedPath);
            }
        }

        // 否则相对挂在 serverUri 路径后
        var relSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var relEscaped = string.Join("/", relSegments.Select(Uri.EscapeDataString));
        if (path.EndsWith('/')) relEscaped += "/";
        return new Uri(serverUri, relEscaped);
    }

    /// <summary>
    /// 构建包含 BasicAuth 用户名密码凭据的流式直链 URI（供 GStreamer playbin 直接流式秒播）
    /// </summary>
    public static string BuildStreamingUriWithAuth(WebDavServer server, string relativeHref)
    {
        var fullUri = BuildFullUri(server, relativeHref);
        if (!string.IsNullOrWhiteSpace(server.Username))
        {
            var builder = new UriBuilder(fullUri)
            {
                UserName = Uri.EscapeDataString(server.Username),
                Password = Uri.EscapeDataString(server.Password ?? string.Empty)
            };
            return builder.Uri.AbsoluteUri;
        }
        return fullUri.AbsoluteUri;
    }

    private static string ComputeMd5(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static async Task<bool> TryEnrichSingleSongHeaderAsync(WebDavServer server, WebDavSongCache cache)
    {
        if (server == null || string.IsNullOrEmpty(cache.Href)) return false;
        try
        {
            var client = GetHttpClient(server);
            var uri = BuildFullUri(server, cache.Href);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            // 请求文件头部 128KB
            req.Headers.Range = new RangeHeaderValue(0, 131071);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.PartialContent)
            {
                return false;
            }

            var ext = Path.GetExtension(cache.Href);
            var tmpFile = Path.Combine(Path.GetTempPath(), $"webdav_hdr_{Guid.NewGuid():N}{ext}");
            try
            {
                using (var fs = new FileStream(tmpFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await resp.Content.CopyToAsync(fs);
                }

                if (File.Exists(tmpFile) && new FileInfo(tmpFile).Length > 0)
                {
                    var track = new ATL.Track(tmpFile);
                    bool foundTag = !string.IsNullOrWhiteSpace(track.Title) || !string.IsNullOrWhiteSpace(track.Artist);
                    if (foundTag)
                    {
                        lock (s_lock)
                        {
                            if (!string.IsNullOrWhiteSpace(track.Title)) cache.Title = CleanTrackNumberPrefix(track.Title.Trim());
                            if (!string.IsNullOrWhiteSpace(track.Artist)) cache.Artist = track.Artist.Trim();
                            if (!string.IsNullOrWhiteSpace(track.Album)) cache.Album = track.Album.Trim();
                            if (track.Duration > 0) cache.Duration = track.Duration;
                            cache.Quality = InferQualityBadge(tmpFile, track);
                        }
                        return true;
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch {}
            }

            // 若音频头部未写入元数据标签，以清洗后的歌名通过在线 API 智能匹配歌手与专辑
            var cleanTitle = CleanTrackNumberPrefix(cache.Title);
            if (!string.IsNullOrWhiteSpace(cleanTitle) && (string.IsNullOrWhiteSpace(cache.Artist) || cache.Artist == "未知歌手"))
            {
                var matches = await QqMusicApi.SearchAsync(cleanTitle, 1, 3);
                if (matches.Count > 0)
                {
                    var m = matches[0];
                    lock (s_lock)
                    {
                        cache.Title = cleanTitle;
                        if (!string.IsNullOrWhiteSpace(m.Artist)) cache.Artist = m.Artist;
                        if (!string.IsNullOrWhiteSpace(m.Album)) cache.Album = m.Album;
                        if (m.Duration > 0 && cache.Duration == 0) cache.Duration = m.Duration;
                    }
                    return true;
                }
            }
        }
        catch {}
        return false;
    }

    public static async Task<int> BatchEnrichMetadataHeadersAsync(WebDavServer server, Action<string, int, int>? progress = null, CancellationToken ct = default)
    {
        List<WebDavSongCache> toScan;
        lock (s_lock)
        {
            if (server.CachedSongs == null || server.CachedSongs.Count == 0) return 0;
            toScan = server.CachedSongs
                .Where(s => string.IsNullOrWhiteSpace(s.Artist) || s.Artist == "未知歌手" || string.IsNullOrWhiteSpace(s.Album) || s.Album == "WebDAV 专辑")
                .ToList();
        }

        if (toScan.Count == 0) return 0;

        int total = toScan.Count;
        int completed = 0;
        int enrichedCount = 0;
        using var semaphore = new SemaphoreSlim(8, 8); // 8 线程并发嗅探
        var tasks = new List<Task>();

        foreach (var songCache in toScan)
        {
            if (ct.IsCancellationRequested) break;

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (ct.IsCancellationRequested) return;

                    bool ok = await TryEnrichSingleSongHeaderAsync(server, songCache).ConfigureAwait(false);
                    int cur = Interlocked.Increment(ref completed);
                    if (ok) Interlocked.Increment(ref enrichedCount);

                    var currentTitle = songCache.Title;
                    var currentArtist = songCache.Artist;
                    progress?.Invoke($"{currentTitle} - {currentArtist}", cur, total);

                    if (cur % 10 == 0)
                    {
                        lock (s_lock) { SaveConfig(); }
                    }
                }
                catch (OperationCanceledException) {}
                catch (Exception ex)
                {
                    AppLogger.Warn("WebDavService", $"Single song enrich error for {songCache.Href}: {ex.Message}");
                }
                finally
                {
                    try { semaphore.Release(); } catch {}
                }
            }, ct));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) {}
        finally
        {
            tasks.Clear();
            lock (s_lock) { SaveConfig(); }
        }

        return enrichedCount;
    }

    /// <summary>
    /// 获取 WebDAV 曲目的封面（支持本地封面缓存直读、已缓存音频直读、轻量 Range 提取内嵌封面、同目录 cover.jpg 及在线匹配）
    /// </summary>
    public static async Task<string?> EnsureCoverAsync(WebDavServer server, Song song, CancellationToken ct = default)
    {
        if (server == null || string.IsNullOrEmpty(song.WebDavHref)) return null;

        var md5 = ComputeMd5(song.WebDavHref);
        var cacheKey = $"webdav_{md5}";
        var targetPng = Path.Combine(CacheManager.CoversDir, $"local_{cacheKey}.png");
        if (File.Exists(targetPng))
        {
            var fi = new FileInfo(targetPng);
            if (fi.Length > 0)
            {
                CacheManager.RecordAccess($"covers/{Path.GetFileName(targetPng)}", fi.Length);
                return targetPng;
            }
        }

        // A. 若本地完整音频已下载缓存，直接通过 LocalMusicService 提取
        var localAudio = GetLocalCachePath(server, song.WebDavHref);
        if (File.Exists(localAudio) && new FileInfo(localAudio).Length > 4096)
        {
            return await LocalMusicService.EnsureCoverAsync(song with { LocalFilePath = localAudio });
        }

        // B. 流式未缓存模式：通过 HTTP Range 请求拉取文件头部前 1MB 提取内嵌封面
        var ext = Path.GetExtension(song.WebDavHref);
        var tmpHeaderFile = Path.Combine(Path.GetTempPath(), $"webdav_cov_hdr_{Guid.NewGuid():N}{ext}");
        var tempExtractImg = Path.Combine(CacheManager.CoversDir, $"raw_wd_{md5}.tmp");
        try
        {
            var client = GetHttpClient(server);
            var uri = BuildFullUri(server, song.WebDavHref);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Range = new RangeHeaderValue(0, 1048575); // 1MB 足够覆盖 ID3v2 APIC 与 FLAC PICTURE 块
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.PartialContent)
            {
                using (var fs = new FileStream(tmpHeaderFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                }

                if (File.Exists(tmpHeaderFile) && new FileInfo(tmpHeaderFile).Length > 0)
                {
                    var track = new ATL.Track(tmpHeaderFile);
                    if (track.EmbeddedPictures != null && track.EmbeddedPictures.Count > 0)
                    {
                        var pic = track.EmbeddedPictures[0];
                        if (pic.PictureData != null && pic.PictureData.Length > 0)
                        {
                            await File.WriteAllBytesAsync(tempExtractImg, pic.PictureData, ct).ConfigureAwait(false);
                            var result = await TerminalImageHelper.EnsureLocalImageProcessedAsync(tempExtractImg, cacheKey).ConfigureAwait(false);
                            try { if (File.Exists(tempExtractImg)) File.Delete(tempExtractImg); } catch {}
                            if (!string.IsNullOrEmpty(result) && File.Exists(result))
                            {
                                return result;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("WebDavService", $"Range cover extraction failed for {song.WebDavHref}: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tmpHeaderFile)) File.Delete(tmpHeaderFile); } catch {}
            try { if (File.Exists(tempExtractImg)) File.Delete(tempExtractImg); } catch {}
        }

        // C. 回退：查找同目录下的常见封面命名 (cover.jpg, folder.jpg 等)
        try
        {
            var href = song.WebDavHref;
            var lastSlash = href.LastIndexOf('/');
            if (lastSlash > 0)
            {
                var parentDir = href[..(lastSlash + 1)];
                string[] candidateNames = ["cover.jpg", "cover.png", "folder.jpg", "front.jpg", "Cover.jpg", "Folder.jpg"];
                var client = GetHttpClient(server);
                foreach (var name in candidateNames)
                {
                    var remoteCoverHref = parentDir + name;
                    var coverUri = BuildFullUri(server, remoteCoverHref);
                    using var headReq = new HttpRequestMessage(HttpMethod.Head, coverUri);
                    using var headResp = await client.SendAsync(headReq, ct).ConfigureAwait(false);
                    if (headResp.IsSuccessStatusCode)
                    {
                        using var getReq = new HttpRequestMessage(HttpMethod.Get, coverUri);
                        using var getResp = await client.SendAsync(getReq, ct).ConfigureAwait(false);
                        if (getResp.IsSuccessStatusCode)
                        {
                            var bytes = await getResp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                            if (bytes.Length > 1024)
                            {
                                await File.WriteAllBytesAsync(tempExtractImg, bytes, ct).ConfigureAwait(false);
                                var result = await TerminalImageHelper.EnsureLocalImageProcessedAsync(tempExtractImg, cacheKey).ConfigureAwait(false);
                                try { if (File.Exists(tempExtractImg)) File.Delete(tempExtractImg); } catch {}
                                if (!string.IsNullOrEmpty(result) && File.Exists(result))
                                {
                                    return result;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch {}

        // D. 回退：通过歌曲标题与歌手尝试拉取在线专辑封面
        try
        {
            var cleanTitle = CleanTrackNumberPrefix(song.Title);
            if (!string.IsNullOrWhiteSpace(cleanTitle))
            {
                var query = string.IsNullOrWhiteSpace(song.Artist) || song.Artist == "未知歌手" ? cleanTitle : $"{cleanTitle} {song.Artist}";
                var matches = await QqMusicApi.SearchAsync(query, 1, 3, ct).ConfigureAwait(false);
                if (matches.Count > 0 && !string.IsNullOrWhiteSpace(matches[0].AlbumMid))
                {
                    var onlineCover = await TerminalImageHelper.EnsureAlbumCoverAsync(matches[0].AlbumMid).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(onlineCover) && File.Exists(onlineCover))
                    {
                        return onlineCover;
                    }
                }
            }
        }
        catch {}

        return null;
    }
}
