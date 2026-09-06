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
    /// 纯内存双引擎并发识别 16000Hz PCM 采样切片并联动 QQ 音乐检索
    /// (同时向 Apple Shazam 与 ACRCloud 发送数据，以苹果为优先)
    /// </summary>
    public static async Task<RecognitionResult> RecognizeAndMatchPcmAsync(short[] pcmSamples, CancellationToken cancellationToken = default)
    {
        if (pcmSamples == null || pcmSamples.Length < 16000 * 2)
        {
            return new RecognitionResult(false, "", "", "", null, "音频样本过短，请等待累积更多音频");
        }

        try
        {
            // 1. 同时向 Apple Shazam 与 ACRCloud 启动并发识别
            var shazamTask = NativeShazamService.RecognizePcmSamplesAsync(pcmSamples, cancellationToken);
            var acrTask = AcrCloudService.RecognizePcmSamplesAsync(pcmSamples, cancellationToken);

            // 2. 竞态与优先仲裁：
            // 若 Shazam 率先完成且成功，直接采纳；
            // 若 ACRCloud 率先完成且成功，为尊重“苹果优先”规则，给 Shazam 预留最多 600ms 宽限期；若超时或失败立即采纳 ACRCloud；
            // 若两者都完成，优先取 Shazam。
            (bool Success, string Title, string Artist, string Album, string Error) winner = default;

            var firstCompleted = await Task.WhenAny(shazamTask, acrTask);
            if (firstCompleted == shazamTask)
            {
                var shazamRes = await shazamTask;
                if (shazamRes.Success && !string.IsNullOrWhiteSpace(shazamRes.Title))
                {
                    winner = shazamRes;
                }
                else
                {
                    var acrRes = await acrTask;
                    winner = (acrRes.Success && !string.IsNullOrWhiteSpace(acrRes.Title)) ? acrRes : shazamRes;
                }
            }
            else
            {
                var acrRes = await acrTask;
                if (acrRes.Success && !string.IsNullOrWhiteSpace(acrRes.Title))
                {
                    // ACRCloud 率先出结果，给慢引擎 Shazam 预留最多 600ms
                    var delayTask = Task.Delay(600, cancellationToken);
                    var completed = await Task.WhenAny(shazamTask, delayTask);
                    if (completed == shazamTask)
                    {
                        var shazamRes = await shazamTask;
                        winner = (shazamRes.Success && !string.IsNullOrWhiteSpace(shazamRes.Title)) ? shazamRes : acrRes;
                    }
                    else
                    {
                        winner = acrRes;
                    }
                }
                else
                {
                    var shazamRes = await shazamTask;
                    winner = (shazamRes.Success && !string.IsNullOrWhiteSpace(shazamRes.Title)) ? shazamRes : acrRes;
                }
            }

            if (winner.Success && !string.IsNullOrWhiteSpace(winner.Title))
            {
                return await MatchWithQqMusicAsync(winner.Title, winner.Artist, winner.Album);
            }

            return new RecognitionResult(false, "", "", "", null, string.IsNullOrEmpty(winner.Error) ? "未识别到匹配的歌曲信息" : winner.Error);
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

    private static Song? FindBestMatchedSong(string targetTitle, string targetArtist, string targetAlbum, List<Song> candidates)
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
