using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.UI;

/// <summary>
/// 终端原生图像能力探测与 Kitty Graphics Protocol 图像渲染辅助类
/// </summary>
public static class TerminalImageHelper
{
    private static readonly HttpClient s_httpClient = new();
    private static readonly string s_cacheDir = Path.Combine(Path.GetTempPath(), "qqmusic-tui", "covers");
    private static bool? s_isImageSupported;

    static TerminalImageHelper()
    {
        try
        {
            if (!Directory.Exists(s_cacheDir))
            {
                Directory.CreateDirectory(s_cacheDir);
            }
        }
        catch
        {
            // Ignore directory creation failure
        }
    }

    /// <summary>
    /// 探测当前终端是否原生支持显示图片（如 Kitty 图像协议）
    /// </summary>
    public static bool IsImageSupported
    {
        get
        {
            if (s_isImageSupported.HasValue) return s_isImageSupported.Value;

            // 1. 探测 Kitty 环境变量
            var kittyWindowId = Environment.GetEnvironmentVariable("KITTY_WINDOW_ID");
            var kittyPid = Environment.GetEnvironmentVariable("KITTY_PID");
            var term = Environment.GetEnvironmentVariable("TERM") ?? "";
            var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? "";

            if (!string.IsNullOrEmpty(kittyWindowId) ||
                !string.IsNullOrEmpty(kittyPid) ||
                term.Equals("xterm-kitty", StringComparison.OrdinalIgnoreCase))
            {
                s_isImageSupported = true;
                return true;
            }

            // 2. 探测原生支持 Kitty Graphics Protocol 的终端（Ghostty, WezTerm 等）
            if (termProgram.Equals("ghostty", StringComparison.OrdinalIgnoreCase) ||
                termProgram.Equals("WezTerm", StringComparison.OrdinalIgnoreCase))
            {
                s_isImageSupported = true;
                return true;
            }

            s_isImageSupported = false;
            return false;
        }
    }

    /// <summary>
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
            if (IsValidPngFile(pngFile))
            {
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

        // 应用平滑 6px 圆角遮罩处理
        var processed = await ApplyRoundedCornersAsync(localFile, pngFile);
        return processed ?? localFile;
    }

    /// <summary>
    /// 处理本地图片（内嵌封面或本地 cover.jpg），添加平滑 6px 抗锯齿圆角并转为 Kitty 协议兼容 PNG
    /// </summary>
    public static async Task<string?> EnsureLocalImageProcessedAsync(string localRawImagePath, string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(localRawImagePath) || !File.Exists(localRawImagePath)) return null;

        var pngFile = Path.Combine(s_cacheDir, $"local_{cacheKey}.png");
        if (File.Exists(pngFile))
        {
            if (IsValidPngFile(pngFile))
            {
                return pngFile;
            }
            try { File.Delete(pngFile); } catch {}
        }

        var processed = await ApplyRoundedCornersAsync(localRawImagePath, pngFile);
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
            if (IsValidPngFile(pngFile))
            {
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

        var processed = await ApplyRoundedCornersAsync(localFile, pngFile);
        return processed ?? localFile;
    }

    /// <summary>
    /// 获取单曲专属原画/超高清封面（针对未收录在传统专辑的独立单曲），优先拉取原画档与 1200 顶级分辨率档
    /// </summary>
    public static async Task<string?> EnsureSingleCoverAsync(string songMid, string vsMid)
    {
        if (string.IsNullOrWhiteSpace(songMid) || string.IsNullOrWhiteSpace(vsMid)) return null;

        var pngFile = Path.Combine(s_cacheDir, $"single_{songMid}.png");
        if (File.Exists(pngFile))
        {
            if (IsValidPngFile(pngFile))
            {
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

        var processed = await ApplyRoundedCornersAsync(localFile, pngFile);
        return processed ?? localFile;
    }

    /// <summary>
    /// 获取歌曲播放时对应的超高清封面（智能自愈：优先专辑1200，单曲智能调用T062原画/1200，本地音频提取嵌入封面）
    /// </summary>
    public static async Task<string?> EnsureSongCoverAsync(Song? song)
    {
        if (song == null) return null;

        if (song.IsLocal)
        {
            return await QQMusic.Tui.Services.LocalMusicService.EnsureCoverAsync(song);
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

    private static async Task<string?> ApplyRoundedCornersAsync(string sourceFile, string targetPng)
    {
        if (!File.Exists(sourceFile) || new FileInfo(sourceFile).Length == 0) return null;

        var tmpPng = targetPng + $".tmp.{Guid.NewGuid():N}.png";
        bool converted = false;
        try
        {
            var psiMagick = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "magick",
                Arguments = $"\"{sourceFile}\" ( +clone -fill black -colorize 100 -fill white -draw \"roundrectangle 0,0,%[fx:w-1],%[fx:h-1],%[fx:w*0.017],%[fx:w*0.017]\" -blur 0x1.0 ) -alpha off -compose CopyOpacity -composite -depth 8 \"{tmpPng}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psiMagick);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0 && IsValidPngFile(tmpPng))
                {
                    converted = true;
                }
            }
        }
        catch
        {
            // magick 不可用时回退至 ffmpeg
        }

        if (!converted)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-y -i \"{sourceFile}\" \"{tmpPng}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    if (proc.ExitCode == 0 && IsValidPngFile(tmpPng))
                    {
                        converted = true;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TerminalImageHelper", $"ffmpeg conversion failed: {ex.Message}");
            }
        }

        if (converted && IsValidPngFile(tmpPng))
        {
            try
            {
                File.Move(tmpPng, targetPng, true);
                return targetPng;
            }
            catch
            {
                return tmpPng;
            }
        }

        try { if (File.Exists(tmpPng)) File.Delete(tmpPng); } catch {}
        return null;
    }

    private readonly record struct ImageCacheKey(string FilePath, int Cols, int Rows, long LastWriteTicks);

    private static readonly Lock s_cacheLock = new();
    private const int MaxMemoryCacheEntries = 16;
    private static readonly Dictionary<ImageCacheKey, LinkedListNode<(ImageCacheKey Key, byte[] Payload)>> s_memoryCache = new(MaxMemoryCacheEntries);
    private static readonly LinkedList<(ImageCacheKey Key, byte[] Payload)> s_lruList = new();

    /// <summary>
    /// 在终端指定行、列输出 Kitty 原生图像（f=100 PNG 格式分块传输），利用内存 LRU 缓存秒级复用
    /// </summary>
    /// <param name="filePath">本地图像文件绝对路径</param>
    /// <param name="col">屏幕 1-based 列坐标</param>
    /// <param name="row">屏幕 1-based 行坐标</param>
    /// <param name="cols">占据列宽</param>
    /// <param name="rows">占据行高</param>
    public static void RenderKittyImage(string filePath, int col, int row, int cols, int rows)
    {
        if (string.IsNullOrEmpty(filePath) || cols <= 0 || rows <= 0) return;

        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length == 0) return;

            var key = new ImageCacheKey(filePath, cols, rows, fi.LastWriteTimeUtc.Ticks);
            byte[]? cachedPayload = null;

            lock (s_cacheLock)
            {
                if (s_memoryCache.TryGetValue(key, out var node))
                {
                    s_lruList.Remove(node);
                    s_lruList.AddFirst(node);
                    cachedPayload = node.Value.Payload;
                }
            }

            if (cachedPayload == null)
            {
                byte[] fileBytes = File.ReadAllBytes(filePath);
                string base64 = Convert.ToBase64String(fileBytes);

                using var ms = new MemoryStream();
                using var writer = new StreamWriter(ms, Encoding.ASCII);

                // Kitty escape sequence: f=100 (PNG), a=T (transmit and display), c=cols, r=rows
                int chunkSize = 4096;
                for (int offset = 0; offset < base64.Length; offset += chunkSize)
                {
                    int len = Math.Min(chunkSize, base64.Length - offset);
                    string chunk = base64.Substring(offset, len);
                    bool isLast = (offset + len >= base64.Length);

                    if (offset == 0)
                    {
                        int m = isLast ? 0 : 1;
                        writer.Write($"\x1b_Ga=T,f=100,c={cols},r={rows},m={m};{chunk}\x1b\\");
                    }
                    else
                    {
                        int m = isLast ? 0 : 1;
                        writer.Write($"\x1b_Gm={m};{chunk}\x1b\\");
                    }
                }
                writer.Flush();
                cachedPayload = ms.ToArray();

                lock (s_cacheLock)
                {
                    if (!s_memoryCache.ContainsKey(key))
                    {
                        if (s_memoryCache.Count >= MaxMemoryCacheEntries)
                        {
                            var last = s_lruList.Last;
                            if (last != null)
                            {
                                s_lruList.RemoveLast();
                                s_memoryCache.Remove(last.Value.Key);
                            }
                        }

                        var newNode = new LinkedListNode<(ImageCacheKey Key, byte[] Payload)>((key, cachedPayload));
                        s_lruList.AddFirst(newNode);
                        s_memoryCache[key] = newNode;
                    }
                }
            }

            ClearImages();

            // 移动光标至指定行列（1-indexed）
            var moveCursorBytes = Encoding.ASCII.GetBytes($"\x1b[{row};{col}H");
            WriteRawBytesToTerminal(moveCursorBytes, cachedPayload);
            AppLogger.Info("TerminalImageHelper", $"RenderKittyImage sent {cachedPayload.Length} bytes at ({col}, {row}) size {cols}x{rows} (cached)");
        }
        catch (Exception ex)
        {
            AppLogger.Error("TerminalImageHelper", $"RenderKittyImage failed: {ex.Message}");
        }
    }

    private static void WriteRawBytesToTerminal(byte[] header, byte[] payload)
    {
        try
        {
            using var tty = File.OpenWrite("/dev/tty");
            tty.Write(header, 0, header.Length);
            tty.Write(payload, 0, payload.Length);
            tty.Flush();
            return;
        }
        catch
        {
            // fallback
        }

        try
        {
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(header, 0, header.Length);
            stdout.Write(payload, 0, payload.Length);
            stdout.Flush();
        }
        catch
        {
        }
    }

    private static void WriteRawBytesToTerminal(byte[] bytes)
    {
        try
        {
            using var tty = File.OpenWrite("/dev/tty");
            tty.Write(bytes, 0, bytes.Length);
            tty.Flush();
            return;
        }
        catch
        {
            // fallback
        }

        try
        {
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
        catch
        {
        }
    }

    /// <summary>
    /// 清除终端中所有 Kitty 图像，杜绝残影
    /// </summary>
    public static void ClearImages()
    {
        try
        {
            byte[] cmd = Encoding.ASCII.GetBytes("\x1b_Ga=d,d=a\x1b\\");
            WriteRawBytesToTerminal(cmd);
        }
        catch
        {
        }
    }
}
