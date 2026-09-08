using System.Diagnostics;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services.AcrCloud;
using QQMusic.Tui.Services.Shazam;

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
    string ErrorMessage = ""
);

/// <summary>
/// 音频识别与 QQ 音乐联动服务
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
            var (recOk, title, artist, album, err) = await NativeShazamService.RecognizeWavAsync(audioFilePath, cancellationToken);
            if (!recOk || string.IsNullOrWhiteSpace(title))
            {
                return new RecognitionResult(false, "", "", "", null, string.IsNullOrEmpty(err) ? "未识别到匹配的歌曲信息" : err);
            }

            return await MatchWithQqMusicAsync(title, artist, album);
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
        _ = NativeShazamService.PreWarmConnectionAsync();
    }

    /// <summary>
    /// 纯内存双引擎并发识别 16000Hz PCM 采样切片并联动 QQ 音乐检索
    /// (Shazam 与 ACRCloud 真正独立并发竞态，任意引擎命中立即返回)
    /// </summary>
    public static async Task<RecognitionResult> RecognizeAndMatchPcmAsync(short[] pcmSamples, CancellationToken cancellationToken = default)
    {
        if (pcmSamples == null || pcmSamples.Length < (int)(16000 * 1.8))
        {
            return new RecognitionResult(false, "", "", "", null, "音频样本过短，请等待累积更多音频");
        }

        try
        {
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
                    return await MatchWithQqMusicAsync(res.Title, res.Artist, res.Album);
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
