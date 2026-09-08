using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.UI;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

public record ExportResult(bool Success, string Message, string? FilePath = null);

/// <summary>
/// 离线音频标签就地回写与自包含无损歌曲导出服务
/// 依托 ATL.NET 内存直写能力，将缓存音频配合高清封面、双语歌词与元数据就地写入文件头并导出
/// </summary>
public static class AudioExportService
{
    private static readonly string s_defaultExportDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Music", "QQMusicExport"
    );

    /// <summary>
    /// 导出指定歌曲为自包含独立音频文件
    /// </summary>
    public static async Task<ExportResult> ExportSongAsync(
        Song song,
        AudioQualityTier qualityTier = AudioQualityTier.SQ,
        string? customOutputDir = null,
        CancellationToken ct = default)
    {
        if (song == null)
        {
            return new ExportResult(false, "待导出歌曲对象为空");
        }

        try
        {
            // 1. 获取源音频文件路径
            string? sourcePath = await ResolveSourceAudioPathAsync(song, qualityTier, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                return new ExportResult(false, $"无法获取《{song.Title}》的本地音频数据或下载失败");
            }

            // 2. 识别音频容器真实格式并确定文件扩展名
            string ext = InferAudioExtension(sourcePath, qualityTier);

            // 3. 准备导出目标目录与文件名
            string outputDir = string.IsNullOrWhiteSpace(customOutputDir) ? s_defaultExportDir : customOutputDir;
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            string sanitizedArtist = SanitizeFileName(string.IsNullOrWhiteSpace(song.Artist) ? "未知歌手" : song.Artist);
            string sanitizedTitle = SanitizeFileName(string.IsNullOrWhiteSpace(song.Title) ? "未知歌曲" : song.Title);
            string baseFileName = $"{sanitizedArtist} - {sanitizedTitle}";
            string destPath = Path.Combine(outputDir, $"{baseFileName}{ext}");

            // 若存在同名文件，添加数字序号避免覆盖已有文件
            int counter = 1;
            while (File.Exists(destPath))
            {
                destPath = Path.Combine(outputDir, $"{baseFileName} ({counter}){ext}");
                counter++;
            }

            // 4. 执行文件拷贝
            File.Copy(sourcePath, destPath, overwrite: false);

            // 5. 依托 ATL.NET 写入元数据、内嵌封面与双语歌词
            await InjectMetadataAndAssetsAsync(destPath, song, ct).ConfigureAwait(false);

            AppLogger.Info("AudioExportService", $"Successfully exported: {destPath}");
            return new ExportResult(true, $"导出成功: {Path.GetFileName(destPath)}", destPath);
        }
        catch (Exception ex)
        {
            AppLogger.Error("AudioExportService", $"Export failed for {song.Title}", ex);
            return new ExportResult(false, $"导出失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析歌曲对应的真实源文件路径
    /// </summary>
    private static async Task<string?> ResolveSourceAudioPathAsync(Song song, AudioQualityTier qualityTier, CancellationToken ct)
    {
        // A. 本地歌曲：直接使用本地文件路径
        if (song.IsLocal && !string.IsNullOrEmpty(song.LocalFilePath) && File.Exists(song.LocalFilePath))
        {
            return song.LocalFilePath;
        }

        // B. WebDAV 歌曲：检查是否存在已缓存的本地文件
        if (song.IsWebDav && !string.IsNullOrEmpty(song.WebDavHref))
        {
            var servers = WebDavService.GetServers();
            var server = (!string.IsNullOrEmpty(song.WebDavServerId) ? servers.Find(s => s.Id == song.WebDavServerId) : null)
                         ?? WebDavService.GetActiveServer();
            if (server != null)
            {
                string cachedWebDav = WebDavService.GetLocalCachePath(server, song.WebDavHref);
                if (!string.IsNullOrEmpty(cachedWebDav) && File.Exists(cachedWebDav))
                {
                    return cachedWebDav;
                }
            }
        }

        // C. 在线歌曲：先从本地磁盘缓存查询
        if (!string.IsNullOrEmpty(song.Mid))
        {
            var cached = AudioCacheService.GetCachedAudioPath(song.Mid, qualityTier);
            if (!string.IsNullOrEmpty(cached) && File.Exists(cached))
            {
                return cached;
            }

            // 尝试检索其他已缓存品质
            AudioQualityTier[] fallbackTiers = [AudioQualityTier.SQ, AudioQualityTier.HQ, AudioQualityTier.Standard];
            foreach (var tier in fallbackTiers)
            {
                if (tier == qualityTier) continue;
                var fb = AudioCacheService.GetCachedAudioPath(song.Mid, tier);
                if (!string.IsNullOrEmpty(fb) && File.Exists(fb))
                {
                    return fb;
                }
            }

            // 若尚未缓存，发起实时缓存下载
            try
            {
                var (url, _, actualTier) = await QqMusicApi.GetPlayUrlForTierAsync(song.Mid, song.EffectiveMediaMid, qualityTier, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(url))
                {
                    var downloaded = await AudioCacheService.CacheAudioAsync(song.Mid, actualTier, url, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(downloaded) && File.Exists(downloaded))
                    {
                        return downloaded;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("AudioExportService", $"Failed to fetch/cache song URL for export: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// 根据文件头部二进制魔数推断音频格式扩展名
    /// </summary>
    public static string InferAudioExtension(string filePath, AudioQualityTier tier)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[16];
            int read = fs.Read(header);

            if (read >= 4)
            {
                // fLaC
                if (header[0] == 0x66 && header[1] == 0x4C && header[2] == 0x61 && header[3] == 0x43)
                {
                    return ".flac";
                }

                // ID3
                if (header[0] == 0x49 && header[1] == 0x44 && header[2] == 0x33)
                {
                    return ".mp3";
                }

                // MPEG Audio Syncword (0xFF, 0xFB / 0xFF, 0xF3 / 0xFF, 0xF2)
                if (header[0] == 0xFF && (header[1] & 0xFE) == 0xFA)
                {
                    return ".mp3";
                }

                // OggS
                if (header[0] == 0x4F && header[1] == 0x67 && header[2] == 0x67 && header[3] == 0x53)
                {
                    return ".ogg";
                }
            }

            if (read >= 12)
            {
                // ftyp (M4A / MP4)
                if (header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70)
                {
                    return ".m4a";
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AudioExportService", $"Failed to read audio magic bytes: {ex.Message}");
        }

        // 兜底按品质判断
        return (tier == AudioQualityTier.SQ || tier == AudioQualityTier.HiRes) ? ".flac" : ".mp3";
    }

    /// <summary>
    /// 借助 ATL.NET 将标签元数据、内嵌高清封面及歌词写入目标文件
    /// </summary>
    private static async Task InjectMetadataAndAssetsAsync(string destPath, Song song, CancellationToken ct)
    {
        try
        {
            var track = new ATL.Track(destPath);

            // 1. 基础 Tag 信息
            if (!string.IsNullOrWhiteSpace(song.Title)) track.Title = song.Title;
            if (!string.IsNullOrWhiteSpace(song.Artist)) track.Artist = song.Artist;
            if (!string.IsNullOrWhiteSpace(song.Album)) track.Album = song.Album;

            // 2. 内嵌高清封面
            try
            {
                var coverPath = await TerminalImageHelper.EnsureSongCoverAsync(song).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
                {
                    byte[] coverBytes = await File.ReadAllBytesAsync(coverPath, ct).ConfigureAwait(false);
                    if (coverBytes.Length > 0)
                    {
                        track.EmbeddedPictures.Clear();
                        track.EmbeddedPictures.Add(ATL.PictureInfo.fromBinaryData(coverBytes));
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("AudioExportService", $"Failed to embed cover picture: {ex.Message}");
            }

            // 3. 内嵌双语歌词
            try
            {
                string? lrcText = await FetchLyricsLrcTextAsync(song, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(lrcText))
                {
                    track.Lyrics.Clear();
                    track.Lyrics.Add(new ATL.LyricsInfo
                    {
                        UnsynchronizedLyrics = lrcText
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("AudioExportService", $"Failed to embed lyrics: {ex.Message}");
            }

            // 4. 原子化持久化保存回写
            track.Save();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AudioExportService", $"Failed to inject metadata via ATL.NET: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取格式化后的双语 LRC 歌词文本
    /// </summary>
    private static async Task<string?> FetchLyricsLrcTextAsync(Song song, CancellationToken ct)
    {
        if (song.IsLocal || song.IsWebDav || string.IsNullOrEmpty(song.Mid))
        {
            return null;
        }

        var lyricLines = await QqMusicApi.GetLyricsAsync(song.Mid, ct).ConfigureAwait(false);
        if (lyricLines == null || lyricLines.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var line in lyricLines)
        {
            if (string.IsNullOrWhiteSpace(line.Text) || line.Text == "暂无歌词") continue;

            string timeTag = $"[{line.Timestamp:mm\\:ss\\.ff}]";
            sb.AppendLine($"{timeTag}{line.Text}");
            if (!string.IsNullOrWhiteSpace(line.Trans))
            {
                sb.AppendLine($"{timeTag}{line.Trans}");
            }
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>
    /// 清洗文件名非法字符
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (Array.IndexOf(invalid, c) >= 0 || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }
}
