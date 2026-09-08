using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

/// <summary>
/// WebDAV 远程私有云音乐服务：
/// 1. 基于原生 HttpClient 发送 PROPFIND，通过 XDocument 解析目录树；
/// 2. 支持自签名证书与 Basic Auth 凭据认证；
/// 3. 本地边播边存隔离，防止 NAS 密码在 GStreamer/D-Bus 广播中泄露；
/// 4. 目录树与平铺大曲库无缝轮转。
/// </summary>
public static class WebDavService
{
    private static readonly string s_configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "qqmusic-tui"
    );
    private static readonly string s_configFile = Path.Combine(s_configDir, "webdav.json");
    private static readonly string s_cacheDir = CacheManager.WebDavDir;

    private static readonly HashSet<string> s_supportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".wav", ".ogg", ".aac", ".opus", ".ape"
    };

    private static readonly Lock s_lock = new();
    private static WebDavConfig s_config = new();
    private static bool s_loaded = false;

    private static readonly ConcurrentDictionary<string, HttpClient> s_clientCache = new();
    private static readonly ConcurrentDictionary<string, Task<string?>> s_inFlightDownloads = new(StringComparer.OrdinalIgnoreCase);

    static WebDavService()
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
                    var cfg = JsonSerializer.Deserialize(json, WebDavJsonContext.Default.WebDavConfig);
                    if (cfg != null)
                    {
                        s_config = cfg;
                        s_config.Servers ??= [];
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("WebDavService", $"Failed to load webdav.json: {ex.Message}");
                }
            }

            s_config = new WebDavConfig
            {
                Servers = []
            };
        }
    }

    public static void SaveConfig()
    {
        lock (s_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(s_config, WebDavJsonContext.Default.WebDavConfig);
                File.WriteAllText(s_configFile, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Error("WebDavService", $"Failed to save webdav.json: {ex.Message}");
            }
        }
    }

    public static List<WebDavServer> GetServers()
    {
        LoadConfig();
        lock (s_lock)
        {
            return new List<WebDavServer>(s_config.Servers);
        }
    }

    public static WebDavServer? GetActiveServer()
    {
        LoadConfig();
        lock (s_lock)
        {
            if (s_config.Servers.Count == 0) return null;
            if (string.IsNullOrEmpty(s_config.ActiveServerId))
            {
                return s_config.Servers[0];
            }
            return s_config.Servers.Find(s => s.Id == s_config.ActiveServerId) ?? s_config.Servers[0];
        }
    }

    public static void SetActiveServer(string serverId)
    {
        LoadConfig();
        lock (s_lock)
        {
            s_config.ActiveServerId = serverId;
            SaveConfig();
        }
    }

    public static void SaveServer(WebDavServer server)
    {
        LoadConfig();
        lock (s_lock)
        {
            int idx = s_config.Servers.FindIndex(s => s.Id == server.Id);
            if (idx >= 0)
            {
                s_config.Servers[idx] = server;
            }
            else
            {
                s_config.Servers.Add(server);
                if (string.IsNullOrEmpty(s_config.ActiveServerId))
                {
                    s_config.ActiveServerId = server.Id;
                }
            }
            // 清理对应 Client 缓存
            s_clientCache.TryRemove(server.Id, out var oldClient);
            oldClient?.Dispose();
            SaveConfig();
        }
    }

    public static void RemoveServer(string serverId)
    {
        LoadConfig();
        lock (s_lock)
        {
            s_config.Servers.RemoveAll(s => s.Id == serverId);
            if (s_config.ActiveServerId == serverId)
            {
                s_config.ActiveServerId = s_config.Servers.Count > 0 ? s_config.Servers[0].Id : null;
            }
            s_clientCache.TryRemove(serverId, out var oldClient);
            oldClient?.Dispose();
            SaveConfig();
        }
    }

    private static HttpClient GetHttpClient(WebDavServer server)
    {
        return s_clientCache.GetOrAdd(server.Id, _ =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(10),
                AutomaticDecompression = DecompressionMethods.All
            };

            if (server.TrustSelfSigned)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            if (!string.IsNullOrEmpty(server.Username) || !string.IsNullOrEmpty(server.Password))
            {
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{server.Username}:{server.Password}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }

            client.DefaultRequestHeaders.Add("User-Agent", "QQMusicTui/1.0 WebDAV Client");
            return client;
        });
    }

    public static async Task<(bool Success, string Message)> TestConnectionAsync(WebDavServer server)
    {
        try
        {
            var client = GetHttpClient(server);
            var uri = BuildFullUri(server, server.RootPath);

            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
            req.Headers.Add("Depth", "0");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.MultiStatus)
            {
                return (true, "连接成功，WebDAV 权限正常");
            }
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                return (false, "认证失败：用户名或密码错误 (401)");
            }
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                return (false, "访问受限：无权限访问该路径 (403)");
            }
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return (false, "路径不存在：请检查根路径配置 (404)");
            }

            return (false, $"HTTP 错误状态码: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, $"连接异常: {ex.Message}");
        }
    }

    public static async Task<List<WebDavItem>> ListDirectoryAsync(WebDavServer server, string relativeHref)
    {
        var items = new List<WebDavItem>();
        try
        {
            var client = GetHttpClient(server);
            var uri = BuildFullUri(server, relativeHref);

            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
            req.Headers.Add("Depth", "1");

            const string propfindBody = """
            <?xml version="1.0" encoding="utf-8" ?>
            <D:propfind xmlns:D="DAV:">
              <D:prop>
                <D:displayname/>
                <D:resourcetype/>
                <D:getcontentlength/>
                <D:getlastmodified/>
              </D:prop>
            </D:propfind>
            """;
            req.Content = new StringContent(propfindBody, Encoding.UTF8, "application/xml");

            using var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.MultiStatus)
            {
                AppLogger.Warn("WebDavService", $"PROPFIND failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return items;
            }

            var xmlContent = await resp.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xmlContent);
            XNamespace d = "DAV:";

            var reqPath = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');

            foreach (var respElem in doc.Descendants(d + "response"))
            {
                var hrefElem = respElem.Element(d + "href");
                if (hrefElem == null) continue;

                var rawHref = hrefElem.Value.Trim();
                Uri itemUri;
                if (Uri.TryCreate(rawHref, UriKind.Absolute, out var parsedAbs))
                {
                    itemUri = parsedAbs;
                }
                else
                {
                    itemUri = new Uri(uri, rawHref);
                }

                var itemPath = Uri.UnescapeDataString(itemUri.AbsolutePath).TrimEnd('/');

                // 排除当前请求目录自身
                if (string.Equals(itemPath, reqPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var propElem = respElem.Element(d + "propstat")?.Element(d + "prop");
                bool isDir = propElem?.Element(d + "resourcetype")?.Element(d + "collection") != null;

                var displayName = propElem?.Element(d + "displayname")?.Value?.Trim();
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = Path.GetFileName(itemPath);
                }
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = itemPath;
                }

                // 过滤隐藏文件与无关文件
                if (displayName.StartsWith('.') || displayName.StartsWith('@') || displayName.StartsWith('#'))
                {
                    continue;
                }

                long len = 0;
                var lenStr = propElem?.Element(d + "getcontentlength")?.Value;
                if (!string.IsNullOrEmpty(lenStr) && long.TryParse(lenStr, out var parsedLen))
                {
                    len = parsedLen;
                }

                DateTime? modDate = null;
                var modStr = propElem?.Element(d + "getlastmodified")?.Value;
                if (!string.IsNullOrEmpty(modStr) && DateTime.TryParse(modStr, out var dt))
                {
                    modDate = dt;
                }

                var storeHref = Uri.UnescapeDataString(itemUri.AbsolutePath);
                if (isDir && !storeHref.EndsWith('/'))
                {
                    storeHref += "/";
                }

                if (isDir)
                {
                    items.Add(new WebDavItem
                    {
                        Name = displayName,
                        Href = storeHref,
                        IsDirectory = true,
                        ContentLength = len,
                        LastModified = modDate
                    });
                }
                else
                {
                    var ext = Path.GetExtension(displayName);
                    if (s_supportedAudioExtensions.Contains(ext))
                    {
                        items.Add(new WebDavItem
                        {
                            Name = displayName,
                            Href = storeHref,
                            IsDirectory = false,
                            ContentLength = len,
                            LastModified = modDate
                        });
                    }
                }
            }

            // 文件夹排在前面，其余按文件名正序排列
            items.Sort((a, b) =>
            {
                if (a.IsDirectory != b.IsDirectory)
                {
                    return a.IsDirectory ? -1 : 1;
                }
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("WebDavService", $"ListDirectoryAsync exception: {ex.Message}");
        }

        return items;
    }

    public static string GetLocalCachePath(WebDavServer server, string fileHref)
    {
        var ext = Path.GetExtension(fileHref);
        var filename = $"{server.Id}_{ComputeMd5(fileHref)}{ext}";
        return Path.Combine(s_cacheDir, filename);
    }

    public static async Task TryDownloadRemoteLrcAsync(WebDavServer server, string audioHref, string localAudioPath)
    {
        try
        {
            var lrcHref = Path.ChangeExtension(audioHref, ".lrc");
            var localLrcPath = Path.ChangeExtension(localAudioPath, ".lrc");
            if (File.Exists(localLrcPath) && new FileInfo(localLrcPath).Length > 0)
            {
                return;
            }

            var client = GetHttpClient(server);
            var lrcUri = BuildFullUri(server, lrcHref);
            using var resp = await client.GetAsync(lrcUri);
            if (resp.IsSuccessStatusCode)
            {
                var lrcContent = await resp.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(lrcContent))
                {
                    await File.WriteAllTextAsync(localLrcPath, lrcContent, Encoding.UTF8);
                    CacheManager.RecordAccess($"webdav/{Path.GetFileName(localLrcPath)}", new FileInfo(localLrcPath).Length);
                }
            }
        }
        catch
        {
            // 远端可能没有单独的 .lrc 文件，后续回退读取内嵌歌词
        }
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

    public static async Task<int> ScanFolderRecursiveAsync(WebDavServer server, string folderHref, Action<string>? progress = null)
    {
        var discovered = new List<WebDavItem>();
        var queue = new Queue<string>();
        queue.Enqueue(folderHref);

        progress?.Invoke("正在深度检索 WebDAV 目录树...");

        int newBatchCount = 0;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var list = await ListDirectoryAsync(server, current);
            var folderNewItems = new List<WebDavItem>();

            foreach (var item in list)
            {
                if (item.IsDirectory)
                {
                    queue.Enqueue(item.Href);
                }
                else
                {
                    discovered.Add(item);
                    folderNewItems.Add(item);
                }
            }

            // 增量分批写入缓存并存盘，支持文件更新与变动检测
            if (folderNewItems.Count > 0)
            {
                lock (s_lock)
                {
                    server.CachedSongs ??= [];
                    foreach (var item in folderNewItems)
                    {
                        var existing = server.CachedSongs.Find(s => string.Equals(s.Href, item.Href, StringComparison.OrdinalIgnoreCase));
                        if (existing == null)
                        {
                            var parsed = InferTitleArtist(item.Name);
                            server.CachedSongs.Add(new WebDavSongCache
                            {
                                ServerId = server.Id,
                                Href = item.Href,
                                Title = parsed.Title,
                                Artist = parsed.Artist,
                                Album = "WebDAV 专辑",
                                Duration = 0,
                                Quality = InferQualityBadge(item.Name, null),
                                FileSize = item.ContentLength,
                                LastModified = item.LastModified
                            });
                            newBatchCount++;
                        }
                        else
                        {
                            bool sizeChanged = item.ContentLength > 0 && existing.FileSize != item.ContentLength;
                            bool timeChanged = item.LastModified.HasValue && existing.LastModified.HasValue && item.LastModified != existing.LastModified;
                            if (sizeChanged || timeChanged)
                            {
                                existing.FileSize = item.ContentLength;
                                existing.LastModified = item.LastModified;
                                if (!string.IsNullOrEmpty(existing.LocalCachedPath) && File.Exists(existing.LocalCachedPath))
                                {
                                    try { File.Delete(existing.LocalCachedPath); } catch {}
                                    existing.LocalCachedPath = null;
                                }
                                newBatchCount++;
                            }
                        }
                    }

                    if (newBatchCount >= 10)
                    {
                        SaveConfig();
                        newBatchCount = 0;
                    }
                }
            }

            int cachedCount = server.CachedSongs?.Count ?? discovered.Count;
            progress?.Invoke($"检索中... 发现 {discovered.Count} 首音频 (已录入曲库 {cachedCount} 首)");
        }

        lock (s_lock)
        {
            server.CachedSongs ??= [];
            foreach (var item in discovered)
            {
                var existing = server.CachedSongs.Find(s => string.Equals(s.Href, item.Href, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    var parsed = InferTitleArtist(item.Name);
                    server.CachedSongs.Add(new WebDavSongCache
                    {
                        ServerId = server.Id,
                        Href = item.Href,
                        Title = parsed.Title,
                        Artist = parsed.Artist,
                        Album = "WebDAV 专辑",
                        Duration = 0,
                        Quality = InferQualityBadge(item.Name, null),
                        FileSize = item.ContentLength,
                        LastModified = item.LastModified
                    });
                }
                else
                {
                    bool sizeChanged = item.ContentLength > 0 && existing.FileSize != item.ContentLength;
                    bool timeChanged = item.LastModified.HasValue && existing.LastModified.HasValue && item.LastModified != existing.LastModified;
                    if (sizeChanged || timeChanged)
                    {
                        existing.FileSize = item.ContentLength;
                        existing.LastModified = item.LastModified;
                        if (!string.IsNullOrEmpty(existing.LocalCachedPath) && File.Exists(existing.LocalCachedPath))
                        {
                            try { File.Delete(existing.LocalCachedPath); } catch {}
                            existing.LocalCachedPath = null;
                        }
                    }
                }
            }

            if (!server.ImportedPaths.Contains(folderHref))
            {
                server.ImportedPaths.Add(folderHref);
            }
            SaveConfig();
        }

        return server.CachedSongs?.Count ?? discovered.Count;
    }

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
}
