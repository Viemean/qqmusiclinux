using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.UI;

public static partial class TerminalImageHelper
{
    /// 校验 PNG 文件头魔数与 IEND 尾部，确保文件完整有效
    /// </summary>
    public static bool IsValidPngFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 100) return false;

            using var fs = File.OpenRead(path);
            byte[] header = new byte[8];
            if (fs.Read(header, 0, 8) < 8) return false;

            // PNG 魔法头: 89 50 4E 47 0D 0A 1A 0A
            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47 ||
                header[4] != 0x0D || header[5] != 0x0A || header[6] != 0x1A || header[7] != 0x0A)
            {
                return false;
            }

            // 检查末尾 12 字节是否包含 IEND
            fs.Seek(-12, SeekOrigin.End);
            byte[] tail = new byte[12];
            if (fs.Read(tail, 0, 12) < 12) return false;
            var tailAscii = Encoding.ASCII.GetString(tail);
            return tailAscii.Contains("IEND");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 校验 JPEG 文件头 SOI 与尾部 EOI
    /// </summary>
    public static bool IsValidJpgFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 100) return false;

            using var fs = File.OpenRead(path);
            byte[] header = new byte[2];
            if (fs.Read(header, 0, 2) < 2) return false;
            if (header[0] != 0xFF || header[1] != 0xD8) return false;

            fs.Seek(-2, SeekOrigin.End);
            byte[] tail = new byte[2];
            if (fs.Read(tail, 0, 2) < 2) return false;
            return tail[0] == 0xFF && tail[1] == 0xD9;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 流式下载远程图像并原子落盘，避免大尺寸封面分配到大对象堆 (LOH)
    /// </summary>
    private static async Task<bool> DownloadImageStreamToFileAsync(string url, string destinationFile)
    {
        var tempFile = destinationFile + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            req.Headers.Add("Referer", "https://y.qq.com/");

            using var resp = await s_httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return false;

            await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            {
                await resp.Content.CopyToAsync(fs);
            }

            var fi = new FileInfo(tempFile);
            if (fi.Length > 2048)
            {
                File.Move(tempFile, destinationFile, overwrite: true);
                return true;
            }
        }
        catch
        {
            // 容错重试
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
        return false;
    }

    /// <summary>
    /// 获取封面本地缓存路径，如未缓存或损坏则自愈重新下载并转为 Kitty 协议兼容的 PNG 格式
    /// </summary>
    public static async Task<string?> EnsureAlbumCoverAsync(string albumMid)
    {
        if (string.IsNullOrWhiteSpace(albumMid)) return null;

        var pngFile = Path.Combine(s_cacheDir, $"{albumMid}.png");
        if (File.Exists(pngFile))
        {
            var fi = new FileInfo(pngFile);
            if (fi.Length <= 2500 * 1024 && IsValidPngFile(pngFile))
            {
                CacheManager.RecordAccess($"covers/{Path.GetFileName(pngFile)}", fi.Length);
                return pngFile;
            }
            try { File.Delete(pngFile); } catch {}
        }

        var localFile = Path.Combine(s_cacheDir, $"{albumMid}.jpg");
        if (File.Exists(localFile) && !IsValidJpgFile(localFile))
        {
            try { File.Delete(localFile); } catch {}
        }

        if (!File.Exists(localFile) || new FileInfo(localFile).Length == 0)
        {
            var rawMid = albumMid.Contains('_') ? albumMid.Split('_')[0] : albumMid;
            string[] resolutionUrls =
            [
                $"https://y.qq.com/music/photo_new/T002R1200x1200M000{albumMid}.jpg?max_age=2592000",
                $"https://y.qq.com/music/photo_new/T002R800x800M000{albumMid}.jpg?max_age=2592000",
                $"https://y.qq.com/music/photo_new/T002R800x800M000{rawMid}_1.jpg?max_age=2592000",
                $"https://y.qq.com/music/photo_new/T002R800x800M000{rawMid}_2.jpg?max_age=2592000",
                $"https://y.gtimg.cn/music/photo_new/T002R800x800M000{albumMid}.jpg?max_age=2592000",
                $"https://y.gtimg.cn/music/photo_new/T002R800x800M000{rawMid}_1.jpg?max_age=2592000",
                $"https://y.qq.com/music/photo_new/T002R500x500M000{albumMid}.jpg?max_age=2592000",
                $"https://y.qq.com/music/photo_new/T002R300x300M000{albumMid}.jpg?max_age=2592000"
            ];

            foreach (var url in resolutionUrls)
            {
                if (await DownloadImageStreamToFileAsync(url, localFile))
                {
                    break;
                }
            }
        }

        if (!File.Exists(localFile) || new FileInfo(localFile).Length == 0)
        {
            return null;
        }

        if (!IsImageSupported)
        {
            return localFile;
        }

        // 应用平滑 6px 圆角遮罩处理（无外扩阴影）
        var processed = await ApplyRoundedCornersAsync(localFile, pngFile);
        if (!string.IsNullOrEmpty(processed) && File.Exists(processed))
        {
            CacheManager.RecordAccess($"covers/{Path.GetFileName(processed)}", new FileInfo(processed).Length);
            CacheManager.EnforceLimitAsync();
        }
        return processed ?? localFile;
    }

    /// <summary>
    /// 处理本地图片（内嵌封面或本地 cover.jpg），添加平滑 6px 抗锯齿圆角并转为 Kitty 协议兼容 PNG
    /// </summary>
    public static async Task<string?> EnsureLocalImageProcessedAsync(string localRawImagePath, string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(localRawImagePath) || !File.Exists(localRawImagePath)) return null;
        if (!IsImageSupported) return localRawImagePath;

        var pngFile = Path.Combine(s_cacheDir, $"local_{cacheKey}.png");
        if (File.Exists(pngFile))
        {
            var fi = new FileInfo(pngFile);
            if (fi.Length > 2500 * 1024 || !IsValidPngFile(pngFile))
            {
                try { File.Delete(pngFile); } catch {}
            }
            else
            {
                return pngFile;
            }
        }

        var processed = await ApplyRoundedCornersAsync(localRawImagePath, pngFile);
        if (!string.IsNullOrEmpty(processed) && File.Exists(processed))
        {
            CacheManager.RecordAccess($"covers/{Path.GetFileName(processed)}", new FileInfo(processed).Length);
            CacheManager.EnforceLimitAsync();
        }
        return processed ?? localRawImagePath;
    }

    /// <summary>
    /// 获取歌手写真本地缓存路径，如未缓存则异步下载并转为 Kitty 协议兼容的 6px 圆角 PNG 格式
    /// </summary>
    public static async Task<string?> EnsureSingerCoverAsync(string singerMid)
    {
        if (string.IsNullOrWhiteSpace(singerMid)) return null;

        var pngFile = Path.Combine(s_cacheDir, $"singer_{singerMid}.png");
        if (File.Exists(pngFile))
        {
            var fi = new FileInfo(pngFile);
            if (fi.Length <= 2500 * 1024 && IsValidPngFile(pngFile))
            {
                CacheManager.RecordAccess($"covers/{Path.GetFileName(pngFile)}", fi.Length);
                return pngFile;
            }
            try { File.Delete(pngFile); } catch {}
        }

        var localFile = Path.Combine(s_cacheDir, $"singer_{singerMid}.jpg");
        if (File.Exists(localFile) && !IsValidJpgFile(localFile))
        {
            try { File.Delete(localFile); } catch {}
        }
        if (!File.Exists(localFile) || new FileInfo(localFile).Length == 0)
        {
            string[] resolutionUrls =
            [
                $"https://y.qq.com/music/photo_new/T001R500x500M000{singerMid}.jpg?max_age=2592000",
                $"https://y.qq.com/music/photo_new/T001R300x300M000{singerMid}.jpg?max_age=2592000"
            ];

            foreach (var url in resolutionUrls)
            {
                if (await DownloadImageStreamToFileAsync(url, localFile))
                {
                    break;
                }
            }
        }

        if (!File.Exists(localFile) || new FileInfo(localFile).Length == 0)
        {
            return null;
        }

        if (!IsImageSupported)
        {
            return localFile;
        }

        var processed = await ApplyRoundedCornersAsync(localFile, pngFile);
        if (!string.IsNullOrEmpty(processed) && File.Exists(processed))
        {
            CacheManager.RecordAccess($"covers/{Path.GetFileName(processed)}", new FileInfo(processed).Length);
            CacheManager.EnforceLimitAsync();
        }
        return processed ?? localFile;
    }

    /// <summary>
    /// 获取单曲封面，优先拉取高分辨率版本
    /// </summary>
    public static async Task<string?> EnsureSingleCoverAsync(string songMid, string vsMid)
    {
        if (string.IsNullOrWhiteSpace(songMid) || string.IsNullOrWhiteSpace(vsMid)) return null;

        var pngFile = Path.Combine(s_cacheDir, $"single_{songMid}.png");
        if (File.Exists(pngFile))
        {
            var fi = new FileInfo(pngFile);
            if (fi.Length <= 2500 * 1024 && IsValidPngFile(pngFile))
            {
                CacheManager.RecordAccess($"covers/{Path.GetFileName(pngFile)}", fi.Length);
                return pngFile;
            }
            try { File.Delete(pngFile); } catch {}
        }

        var localFile = Path.Combine(s_cacheDir, $"single_{songMid}.jpg");
        if (File.Exists(localFile) && !IsValidJpgFile(localFile))
        {
            try { File.Delete(localFile); } catch {}
        }

        if (!File.Exists(localFile) || new FileInfo(localFile).Length == 0)
        {
            string[] resolutionUrls =
            [
                $"https://y.qq.com/music/photo_new/T062M000{vsMid}.jpg?max_age=2592000",          // 官方原画档案档（无二次有损下采样，极度细腻）
                $"https://y.qq.com/music/photo_new/T062R1200x1200M000{vsMid}.jpg?max_age=2592000",    // 1200x1200 超高清大图
                $"https://y.qq.com/music/photo_new/T062R800x800M000{vsMid}.jpg?max_age=2592000",      // 800x800 高清档
                $"https://y.gtimg.cn/music/photo_new/T062R1200x1200M000{vsMid}.jpg?max_age=2592000",  // 腾讯云 CDN 备用
                $"https://y.qq.com/music/photo_new/T062R500x500M000{vsMid}.jpg?max_age=2592000"
            ];

            foreach (var url in resolutionUrls)
            {
                if (await DownloadImageStreamToFileAsync(url, localFile))
                {
                    break;
                }
            }
        }

        if (!File.Exists(localFile) || new FileInfo(localFile).Length == 0)
        {
            return null;
        }

        if (!IsImageSupported)
        {
            return localFile;
        }

        var processed = await ApplyRoundedCornersAsync(localFile, pngFile);
        return processed ?? localFile;
    }

    /// <summary>
    /// 获取歌曲播放时对应的超高清封面（智能自愈：优先专辑1200，单曲智能调用T062原画/1200，本地音频提取嵌入封面）
    /// </summary>
    public static async Task<string?> EnsureSongCoverAsync(Song? song)
    {
        if (song == null) return null;

        if (song.IsWebDav)
        {
            var server = QQMusic.Tui.Services.WebDavService.GetActiveServer();
            if (server != null)
            {
                var wdCover = await QQMusic.Tui.Services.WebDavService.EnsureCoverAsync(server, song).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(wdCover))
                {
                    return wdCover;
                }
            }
        }

        if (song.IsLocal)
        {
            var filePath = song.LocalFilePath;
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                return await QQMusic.Tui.Services.LocalMusicService.EnsureCoverAsync(song with { LocalFilePath = filePath }).ConfigureAwait(false);
            }
            return null;
        }

        // 1. 若拥有 AlbumMid，优先获取专辑 1200 超高清封面
        if (!string.IsNullOrWhiteSpace(song.AlbumMid))
        {
            var albumCover = await EnsureAlbumCoverAsync(song.AlbumMid);
            if (!string.IsNullOrEmpty(albumCover))
            {
                return albumCover;
            }
        }

        // 2. 若无 AlbumMid 或拉取不到（单曲），自动解析单曲专属视觉 MID 并拉取 T062 原画大图
        if (!string.IsNullOrWhiteSpace(song.Mid))
        {
            var vsMid = await QqMusicApi.GetSongVisualMidAsync(song.Mid);
            if (!string.IsNullOrWhiteSpace(vsMid))
            {
                var singleCover = await EnsureSingleCoverAsync(song.Mid, vsMid);
                if (!string.IsNullOrEmpty(singleCover))
                {
                    return singleCover;
                }
            }
        }

        return null;
    }

}
