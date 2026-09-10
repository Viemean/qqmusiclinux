using System.Diagnostics;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services.AcrCloud;
using QQMusic.Tui.Services.QqAudioRecognition;
using QQMusic.Tui.Services.Shazam;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

/// <summary>
/// 听歌识曲结果
/// </summary>
public record RecognitionResult(
    bool Success,
    string Title,
    string Artist,
    string Album,
    Song? MatchedSong = null,
    string ErrorMessage = "",
    string Source = "QQMusic",
    double OffsetSeconds = 0.0
);

/// <summary>
/// 音频识别与 QQ 音乐联动服务
/// (优先官方 QQ 音乐优图源精准匹配；未命中时降级走 Shazam / ACRCloud 多源并发)
/// </summary>
public static class AudioRecognitionService
{
    /// <summary>
    /// 识别本地音频文件，并联动检索 QQ 音乐官方曲库
    /// </summary>
    public static async Task<RecognitionResult> RecognizeAndMatchAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(audioFilePath))
        {
            return new RecognitionResult(false, "", "", "", null, "音频样本文件不存在");
        }

        try
        {
            // 优先级 1: 优先尝试 QQ 音乐官方优图源 (若为 PCM 样本或可通过 Native Runner 提取)
            if (audioFilePath.EndsWith(".pcm", StringComparison.OrdinalIgnoreCase))
            {
                byte[] rawBytes = await File.ReadAllBytesAsync(audioFilePath, cancellationToken);
                short[] pcm8k = new short[rawBytes.Length / 2];
                Buffer.BlockCopy(rawBytes, 0, pcm8k, 0, rawBytes.Length);

                if (pcm8k.Length >= 8000 * 2)
                {
                    var feature = QafpNativeRunner.Extract(pcm8k);
                    if (feature != null)
                    {
                        var res = await QqMusicRecognizeClient.SearchAsync(feature, cancellationToken);
                        if (res.Success && res.Song != null)
                        {
                            AppLogger.Force("AudioRecognitionService", $"[QQ音乐优图命中] 文件精准匹配: {res.Song.Title} - {res.Song.Artist}");
                            return new RecognitionResult(true, res.Song.Title, res.Song.Artist, res.Song.Album, res.Song, "", "QQMusic", res.OffsetSeconds);
                        }
                    }
                }
            }

            // 优先级 2: 降级回退到 Shazam 文件识别并联动匹配
            var (recOk, title, artist, album, err) = await NativeShazamService.RecognizeWavAsync(audioFilePath, cancellationToken);
            if (!recOk || string.IsNullOrWhiteSpace(title))
            {
                return new RecognitionResult(false, "", "", "", null, string.IsNullOrEmpty(err) ? "未识别到匹配的歌曲信息" : err);
            }

            var match = await MatchWithQqMusicAsync(title, artist, album);
            return match with { Source = "Shazam" };
        }
        catch (OperationCanceledException)
        {
            return new RecognitionResult(false, "", "", "", null, "识别已取消");
        }
        catch (Exception ex)
        {
            return new RecognitionResult(false, "", "", "", null, $"识别服务异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 预热听歌识曲网络连接
    /// </summary>
    public static void PreWarm()
    {
        _ = QqMusicRecognitionService.IsAvailable;
        _ = NativeShazamService.PreWarmConnectionAsync();
    }

    /// <summary>
    /// 识别 16000Hz PCM 采样切片并联动 QQ 音乐官方曲库
    /// (优先 QQ 音乐优图源精准匹配，未命中降级走 Shazam 与 ACRCloud 并发)
    /// </summary>
    public static async Task<RecognitionResult> RecognizeAndMatchPcmAsync(
        short[] pcmSamples, 
        QafpWorkerSession? workerSession = null, 
        CancellationToken cancellationToken = default)
    {
        if (pcmSamples == null || pcmSamples.Length < (int)(16000 * 1.8))
        {
            return new RecognitionResult(false, "", "", "", null, "音频样本过短，请等待累积更多音频");
        }

        try
        {
            // 优先级 1: 优先走 QQ 音乐官方优图源（高精度，直接返回官方曲库精确实体，无需重新搜索）
            if (QqMusicRecognitionService.IsAvailable)
            {
                var officialRes = await QqMusicRecognitionService.RecognizePcmSamplesAsync(pcmSamples, workerSession, cancellationToken);
                if (officialRes != null && officialRes.Success && officialRes.MatchedSong != null)
                {
                    AppLogger.Force("AudioRecognitionService", $"[QQ音乐优图命中] 精准匹配: {officialRes.MatchedSong.Title} - {officialRes.MatchedSong.Artist} (Mid: {officialRes.MatchedSong.Mid})");
                    return officialRes with { Source = "QQMusic" };
                }
            }

            // 优先级 2: 降级保护
            // 若音频样本不足 3.0 秒且官方源可用，不急于走第三方截胡，等待录音继续累积至 QQ 音乐最佳特征窗（3.0s+）
            double durationSec = (double)pcmSamples.Length / 16000.0;
            if (durationSec < 3.0 && QqMusicRecognitionService.IsAvailable)
            {
                return new RecognitionResult(false, "", "", "", null, "样本正在累积以供 QQ 音乐官方源精准识别");
            }

            // 优先级 3: 音频累积充足（>= 3.0s）且 QQ 音乐仍未命中时，才降级走通用第三方引擎 (Shazam 与 ACRCloud 独立并发竞态)
            var shazamTask = NativeShazamService.RecognizePcmSamplesAsync(pcmSamples, cancellationToken);
            var isAcrConfigured = AcrCloudConfig.Current.IsConfigured;
            var acrTask = isAcrConfigured
                ? AcrCloudService.RecognizePcmSamplesAsync(pcmSamples, cancellationToken)
                : null;

            var pendingTasks = acrTask != null
                ? new List<Task<(bool Success, string Title, string Artist, string Album, string Error)>> { shazamTask, acrTask }
                : new List<Task<(bool Success, string Title, string Artist, string Album, string Error)>> { shazamTask };

            (bool Success, string Title, string Artist, string Album, string Error) lastErrorRes = default;

            while (pendingTasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(pendingTasks);
                pendingTasks.Remove(completedTask);

                var res = await completedTask;
                if (res.Success && !string.IsNullOrWhiteSpace(res.Title))
                {
                    string sourceName = completedTask == shazamTask ? "Shazam" : "ACRCloud";
                    AppLogger.Force("AudioRecognitionService", $"[{sourceName}命中] 正在检索匹配 QQ 音乐曲库: {res.Title} - {res.Artist}");
                    var matchRes = await MatchWithQqMusicAsync(res.Title, res.Artist, res.Album);
                    return matchRes with { Source = sourceName };
                }

                lastErrorRes = res;
            }

            return new RecognitionResult(false, "", "", "", null, string.IsNullOrEmpty(lastErrorRes.Error) ? "未识别到匹配的歌曲信息" : lastErrorRes.Error);
        }
        catch (OperationCanceledException)
        {
            return new RecognitionResult(false, "", "", "", null, "识别已取消");
        }
        catch (Exception ex)
        {
            return new RecognitionResult(false, "", "", "", null, $"识别服务异常: {ex.Message}");
        }
    }

    private static async Task<RecognitionResult> MatchWithQqMusicAsync(string title, string artist, string album)
    {
        // 1. 构建检索词 (提取声优、角色名、专辑组合)
        var searchQueries = BuildSearchQueries(title, artist, album);
        List<Song>? searchSongs = null;

        foreach (var query in searchQueries)
        {
            var results = await QqMusicApi.SearchAsync(query, 1, 15);
            if (results != null && results.Count > 0)
            {
                searchSongs = results;
                // 若包含声优或完整歌手匹配，优先停在该列表
                break;
            }
        }

        Song? matchedSong = null;
        if (searchSongs != null && searchSongs.Count > 0)
        {
            matchedSong = FindBestMatchedSong(title, artist, album, searchSongs);
        }

        return new RecognitionResult(true, title, artist, album, matchedSong, "");
    }

    private static List<string> BuildSearchQueries(string title, string artist, string album)
    {
        var queries = new List<string>();

        // 1. 若含有 (CV: xxx) 或 [CV: xxx]，提取声优名优先检索 (如 "星めぐりの歌 宮本侑芽")
        string cvName = ExtractCvName(artist);
        if (!string.IsNullOrWhiteSpace(cvName))
        {
            queries.Add($"{title} {cvName}");
        }

        // 2. 原始完整检索
        if (!string.IsNullOrWhiteSpace(artist))
        {
            queries.Add($"{title} {artist}");
        }

        // 3. 净化后的歌手名 (去除括号备注如 feat./CV)
        string cleanArtist = CleanArtistName(artist);
        if (!string.IsNullOrWhiteSpace(cleanArtist) && cleanArtist != artist && cleanArtist != cvName)
        {
            queries.Add($"{title} {cleanArtist}");
        }

        // 4. 带核心专辑名检索 (如 "星めぐりの歌 死亡遊戯で飯を食う。")
        string cleanAlbum = CleanAlbumName(album);
        if (!string.IsNullOrWhiteSpace(cleanAlbum))
        {
            queries.Add($"{title} {cleanAlbum}");
        }

        // 5. 兜底纯歌名检索
        queries.Add(title);

        return queries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ExtractCvName(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        var match = System.Text.RegularExpressions.Regex.Match(
            artist,
            @"[\(\[（]CV\s*[:：]\s*(?<name>[^\)\]）]+)[\)\]）]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["name"].Value.Trim() : "";
    }

    private static string CleanArtistName(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        var cleaned = System.Text.RegularExpressions.Regex.Replace(artist, @"[\(\[（].*?[\)\]）]", "").Trim();
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s*(feat\.|ft\.).*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        return cleaned;
    }

    private static string CleanAlbumName(string album)
    {
        if (string.IsNullOrWhiteSpace(album)) return "";
        var cleaned = System.Text.RegularExpressions.Regex.Replace(album, @"[\(\[（].*?[\)\]）]", "").Trim();
        cleaned = cleaned.Replace("『", "").Replace("』", "").Replace("「", "").Replace("」", "").Trim();
        return cleaned;
    }

    public static Song? FindBestMatchedSong(string targetTitle, string targetArtist, string targetAlbum, List<Song> candidates)
    {
        string cvName = ExtractCvName(targetArtist);
        string cleanArtist = CleanArtistName(targetArtist);
        string cleanAlbum = CleanAlbumName(targetAlbum);

        Song? bestSong = null;
        int maxScore = 0;

        foreach (var song in candidates)
        {
            int score = 0;

            // 歌名匹配
            bool titleExact = string.Equals(song.Title, targetTitle, StringComparison.OrdinalIgnoreCase);
            bool titleContains = song.Title.Contains(targetTitle, StringComparison.OrdinalIgnoreCase) ||
                                 targetTitle.Contains(song.Title, StringComparison.OrdinalIgnoreCase);

            if (titleExact) score += 40;
            else if (titleContains) score += 25;

            // 歌手匹配 (包括 CV 声优名、净化歌手名、原歌手名)
            if (!string.IsNullOrWhiteSpace(cvName) && song.Artist.Contains(cvName, StringComparison.OrdinalIgnoreCase))
            {
                score += 50; // 声优匹配
            }
            else if (!string.IsNullOrWhiteSpace(cleanArtist) && song.Artist.Contains(cleanArtist, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }
            else if (!string.IsNullOrWhiteSpace(targetArtist) && song.Artist.Contains(targetArtist, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }

            // 专辑匹配
            if (!string.IsNullOrWhiteSpace(targetAlbum) && !string.IsNullOrWhiteSpace(song.Album))
            {
                if (song.Album.Contains(targetAlbum, StringComparison.OrdinalIgnoreCase) ||
                    targetAlbum.Contains(song.Album, StringComparison.OrdinalIgnoreCase))
                {
                    score += 50;
                }
                else if (!string.IsNullOrWhiteSpace(cleanAlbum) && song.Album.Contains(cleanAlbum, StringComparison.OrdinalIgnoreCase))
                {
                    score += 40;
                }
            }

            if (score > maxScore)
            {
                maxScore = score;
                bestSong = song;
            }
        }

        // 歌手或专辑需要有匹配度 (score >= 65)，才允许匹配；避免同名翻唱误匹配
        if (maxScore >= 65)
        {
            return bestSong;
        }

        if (string.IsNullOrWhiteSpace(targetArtist) && maxScore >= 40)
        {
            return bestSong;
        }

        return null;
    }
}
