using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Api;

public sealed partial class QqMusicApi
{
    public static async Task<List<QualityOption>> ProbeSongQualitiesAsync(string songMid, string mediaMid = "", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(mediaMid)) mediaMid = songMid;

        await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
        var uin = string.IsNullOrEmpty(UserSession.Current.Uin) ? "0" : UserSession.Current.Uin;

        var jsonPayload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
            $"\"songinfo\":{{\"module\":\"music.pf_song_detail_svr\",\"method\":\"get_song_detail_yqq\",\"param\":{{\"song_mid\":\"{songMid}\"}}}}," +
            $"\"req_hires\":{{\"module\":\"vkey.GetVkeyServer\",\"method\":\"CgiGetVkey\",\"param\":{{\"guid\":\"10000\",\"songmid\":[\"{songMid}\"],\"songtype\":[0],\"uin\":\"{uin}\",\"loginflag\":1,\"platform\":\"20\",\"filename\":[\"RS01{mediaMid}.flac\"]}}}}," +
            $"\"req_sq\":{{\"module\":\"vkey.GetVkeyServer\",\"method\":\"CgiGetVkey\",\"param\":{{\"guid\":\"10000\",\"songmid\":[\"{songMid}\"],\"songtype\":[0],\"uin\":\"{uin}\",\"loginflag\":1,\"platform\":\"20\",\"filename\":[\"F000{mediaMid}.flac\"]}}}}," +
            $"\"req_320\":{{\"module\":\"vkey.GetVkeyServer\",\"method\":\"CgiGetVkey\",\"param\":{{\"guid\":\"10000\",\"songmid\":[\"{songMid}\"],\"songtype\":[0],\"uin\":\"{uin}\",\"loginflag\":1,\"platform\":\"20\",\"filename\":[\"M800{mediaMid}.mp3\"]}}}}," +
            $"\"req_128\":{{\"module\":\"vkey.GetVkeyServer\",\"method\":\"CgiGetVkey\",\"param\":{{\"guid\":\"10000\",\"songmid\":[\"{songMid}\"],\"songtype\":[0],\"uin\":\"{uin}\",\"loginflag\":1,\"platform\":\"20\",\"filename\":[\"M500{mediaMid}.mp3\"]}}}}," +
            $"\"req_m4a\":{{\"module\":\"vkey.GetVkeyServer\",\"method\":\"CgiGetVkey\",\"param\":{{\"guid\":\"10000\",\"songmid\":[\"{songMid}\"],\"songtype\":[0],\"uin\":\"{uin}\",\"loginflag\":1,\"platform\":\"20\",\"filename\":[\"C400{mediaMid}.m4a\"]}}}}," +
            $"\"req_trial\":{{\"module\":\"vkey.GetVkeyServer\",\"method\":\"CgiGetVkey\",\"param\":{{\"guid\":\"10000\",\"songmid\":[\"{songMid}\"],\"songtype\":[0],\"uin\":\"{uin}\",\"loginflag\":1,\"platform\":\"20\",\"filename\":[\"RS02{mediaMid}.mp3\"]}}}}}}";

        var options = new List<QualityOption>(8);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var cookieHeader = UserSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                req.Headers.Add("Cookie", cookieHeader);
            }

            using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var respStr = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(respStr);
            var root = doc.RootElement;

            long sizeHires = 0;
            long sizeFlac = 0;
            long size320 = 0;
            long size128 = 0;
            long interval = 0;
            bool songInfoAvailable = false;

            if (root.TryGetProperty("songinfo", out var songInfoObj) &&
                songInfoObj.TryGetProperty("data", out var songData) &&
                songData.TryGetProperty("track_info", out var trackInfo))
            {
                songInfoAvailable = true;
                if (trackInfo.TryGetProperty("interval", out var intervalProp))
                {
                    interval = intervalProp.GetInt64();
                }

                if (trackInfo.TryGetProperty("file", out var fileObj))
                {
                    if (fileObj.TryGetProperty("size_hires", out var sh)) sizeHires = sh.GetInt64();
                    if (sizeHires == 0 && fileObj.TryGetProperty("size_96flac", out var s96)) sizeHires = s96.GetInt64();
                    if (sizeHires == 0 && fileObj.TryGetProperty("size_24bit", out var s24)) sizeHires = s24.GetInt64();

                    if (fileObj.TryGetProperty("size_flac", out var sf)) sizeFlac = sf.GetInt64();
                    if (fileObj.TryGetProperty("size_320mp3", out var s320)) size320 = s320.GetInt64();
                    if (fileObj.TryGetProperty("size_128mp3", out var s128)) size128 = s128.GetInt64();

                    if (fileObj.TryGetProperty("size_new", out var sizeNewArr) && sizeNewArr.ValueKind == JsonValueKind.Array)
                    {
                        var arrLen = sizeNewArr.GetArrayLength();
                        if (sizeHires == 0 && arrLen > 11)
                        {
                            sizeHires = sizeNewArr[11].GetInt64();
                            if (sizeHires == 0 && arrLen > 13) sizeHires = sizeNewArr[13].GetInt64();
                        }
                        if (sizeFlac == 0 && arrLen > 12)
                        {
                            sizeFlac = sizeNewArr[12].GetInt64();
                        }
                        if (size320 == 0 && arrLen > 3)
                        {
                            size320 = sizeNewArr[3].GetInt64();
                        }
                    }
                }
            }

            string? ExtractUrl(string reqKey)
            {
                if (root.TryGetProperty(reqKey, out var reqObj) &&
                    reqObj.TryGetProperty("data", out var data))
                {
                    string? sip = null;
                    if (data.TryGetProperty("sip", out var sips) && sips.ValueKind == JsonValueKind.Array && sips.GetArrayLength() > 0)
                    {
                        sip = sips[0].GetString();
                    }

                    if (data.TryGetProperty("midurlinfo", out var midUrlInfo) &&
                        midUrlInfo.ValueKind == JsonValueKind.Array &&
                        midUrlInfo.GetArrayLength() > 0)
                    {
                        var purl = midUrlInfo[0].TryGetProperty("purl", out var p) ? p.GetString() : null;
                        if (!string.IsNullOrEmpty(sip) && !string.IsNullOrEmpty(purl) && purl.Length > 5)
                        {
                            return sip + purl;
                        }
                    }
                }
                return null;
            }

            var hiresUrl = ExtractUrl("req_hires");
            var sqUrl = ExtractUrl("req_sq");
            var hqUrl = ExtractUrl("req_320");
            var stdUrl = ExtractUrl("req_128") ?? ExtractUrl("req_m4a") ?? ExtractUrl("req_trial");

            bool hasHires = songInfoAvailable ? (sizeHires > 0 && hiresUrl != null) : (hiresUrl != null);
            bool hasSq = songInfoAvailable ? ((sizeFlac > 0 || sizeHires > 0) && sqUrl != null) : (sqUrl != null);
            bool hasHq = songInfoAvailable ? ((size320 > 0 || sizeFlac > 0 || sizeHires > 0) && hqUrl != null) : (hqUrl != null);
            bool hasStd = stdUrl != null;

            string hiresBitrate = (sizeHires > 0 && interval > 0)
                ? $"{(long)Math.Round((sizeHires * 8.0) / interval / 1000.0)}kbps"
                : (hasHires ? "2968kbps" : "");

            string sqBitrate = (sizeFlac > 0 && interval > 0)
                ? $"{(long)Math.Round((sizeFlac * 8.0) / interval / 1000.0)}kbps"
                : (hasSq ? "892kbps" : "");

            options.Add(new QualityOption(
                AudioQualityTier.HiRes,
                "Hi-Res",
                "Hi-Res",
                "24bit / 96kHz",
                hiresBitrate,
                hasHires,
                hasHires ? hiresUrl : null
            ));

            options.Add(new QualityOption(
                AudioQualityTier.SQ,
                "SQ",
                "SQ",
                "16bit / 44.1kHz",
                sqBitrate,
                hasSq,
                hasSq ? sqUrl : null
            ));

            options.Add(new QualityOption(
                AudioQualityTier.HQ,
                "HQ",
                "HQ",
                "320kbps",
                "",
                hasHq,
                hasHq ? hqUrl : null
            ));

            options.Add(new QualityOption(
                AudioQualityTier.Standard,
                "标准",
                "标准",
                "128kbps",
                "",
                hasStd,
                hasStd ? stdUrl : null
            ));
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", "ProbeSongQualitiesAsync exception", ex);
            options.Add(new QualityOption(AudioQualityTier.HiRes, "Hi-Res", "Hi-Res", "24bit / 96kHz", "", false));
            options.Add(new QualityOption(AudioQualityTier.SQ, "SQ", "SQ", "16bit / 44.1kHz", "", false));
            options.Add(new QualityOption(AudioQualityTier.HQ, "HQ", "HQ", "320kbps", "", false));
            options.Add(new QualityOption(AudioQualityTier.Standard, "标准", "标准", "128kbps", "", true));
        }

        return options;
    }

    /// <summary>
    /// 根据用户指定或偏好的音质获取直链，支持智能梯度回退
    /// </summary>
    public static async Task<(string? Url, string Quality, AudioQualityTier Tier)> GetPlayUrlForTierAsync(string songMid, string mediaMid = "", AudioQualityTier preferred = AudioQualityTier.SQ, CancellationToken ct = default)
    {
        var options = await ProbeSongQualitiesAsync(songMid, mediaMid, ct).ConfigureAwait(false);

        // 先尝试用户偏好的目标档位
        var target = options.FirstOrDefault(o => o.Tier == preferred && o.Available);
        if (target != null && !string.IsNullOrEmpty(target.PlayUrl))
        {
            return (target.PlayUrl, target.Badge, target.Tier);
        }

        // 若目标档位不可用，则按优先级向下回退：HiRes -> SQ -> HQ -> Standard
        var fallbackOrder = new[] { AudioQualityTier.HiRes, AudioQualityTier.SQ, AudioQualityTier.HQ, AudioQualityTier.Standard };
        var startChecking = false;
        foreach (var tier in fallbackOrder)
        {
            if (tier == preferred) startChecking = true;
            if (startChecking)
            {
                var opt = options.FirstOrDefault(o => o.Tier == tier && o.Available);
                if (opt != null && !string.IsNullOrEmpty(opt.PlayUrl))
                {
                    return (opt.PlayUrl, opt.Badge, opt.Tier);
                }
            }
        }

        // 任意可用项兜底
        var anyAvailable = options.FirstOrDefault(o => o.Available && !string.IsNullOrEmpty(o.PlayUrl));
        if (anyAvailable != null)
        {
            return (anyAvailable.PlayUrl, anyAvailable.Badge, anyAvailable.Tier);
        }

        return (null, "无音源", AudioQualityTier.Standard);
    }

    /// <summary>
    /// 获取歌曲直链播放 URL 与对应音质档位（自动读取用户偏好音质）
    /// </summary>
    public static async Task<(string? Url, string Quality)> GetPlayUrlWithQualityAsync(string songMid, string mediaMid = "", CancellationToken ct = default)
    {
        var preferredTier = AudioQualityHelper.Parse(UserSession.Current.PreferredQuality);
        var (url, qName, _) = await GetPlayUrlForTierAsync(songMid, mediaMid, preferredTier, ct).ConfigureAwait(false);
        return (url, qName);
    }

    /// <summary>
    /// 获取歌曲直链播放 URL
    /// </summary>
    public static async Task<string?> GetPlayUrlAsync(string songMid, CancellationToken ct = default)
    {
        var (url, _) = await GetPlayUrlWithQualityAsync(songMid, songMid, ct).ConfigureAwait(false);
        return url;
    }

    /// <summary>

    public static async Task<long> ResolveSongIdAsync(string songMid, CancellationToken ct = default)
    {
        try
        {
            var uin = string.IsNullOrEmpty(UserSession.Current.Uin) ? "0" : UserSession.Current.Uin;
            var authst = UserSession.Current.MusicKey ?? "";
            var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
            var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"{authst}\",\"tmeAppID\":\"qqmusic\"}}," +
                $"\"songinfo\":{{\"module\":\"music.pf_song_detail_svr\",\"method\":\"get_song_detail_yqq\",\"param\":{{\"song_mid\":\"{songMid}\"}}}}}}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
            req.Headers.Referrer = new Uri("https://y.qq.com/");

            var cookieHeader = UserSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader)) req.Headers.Add("Cookie", cookieHeader);

            using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("songinfo", out var songInfoObj) &&
                songInfoObj.TryGetProperty("data", out var songData) &&
                songData.TryGetProperty("track_info", out var trackInfo) &&
                trackInfo.TryGetProperty("id", out var idProp) &&
                idProp.ValueKind == JsonValueKind.Number)
            {
                var id = idProp.GetInt64();
                AppLogger.Info("QqMusicApi", $"ResolveSongIdAsync: mid={songMid} resolved to id={id}");
                return id;
            }
            AppLogger.Warn("QqMusicApi", $"ResolveSongIdAsync: track_info.id not found in response for mid={songMid}: {json}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", $"ResolveSongIdAsync error for {songMid}", ex);
        }
        return 0;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> s_visualMidCache = new();

    /// <summary>
    /// 获取单曲专属视觉封面 MID（track_info.vs[1]），用于无 AlbumMid 单曲的原画/超高清封面拉取
    /// </summary>
    public static async Task<string?> GetSongVisualMidAsync(string songMid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(songMid)) return null;
        if (s_visualMidCache.TryGetValue(songMid, out var cached)) return cached;

        try
        {
            var uin = string.IsNullOrEmpty(UserSession.Current.Uin) ? "0" : UserSession.Current.Uin;
            var authst = UserSession.Current.MusicKey ?? "";
            var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
            var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"{authst}\",\"tmeAppID\":\"qqmusic\"}}," +
                $"\"songinfo\":{{\"module\":\"music.pf_song_detail_svr\",\"method\":\"get_song_detail_yqq\",\"param\":{{\"song_mid\":\"{songMid}\"}}}}}}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
            req.Headers.Referrer = new Uri("https://y.qq.com/");

            var cookieHeader = UserSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader)) req.Headers.Add("Cookie", cookieHeader);

            using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("songinfo", out var songInfoObj) &&
                songInfoObj.TryGetProperty("data", out var songData) &&
                songData.TryGetProperty("track_info", out var trackInfo) &&
                trackInfo.TryGetProperty("vs", out var vsProp) &&
                vsProp.ValueKind == JsonValueKind.Array)
            {
                var vsList = new List<string>(vsProp.GetArrayLength());
                foreach (var v in vsProp.EnumerateArray())
                {
                    vsList.Add(v.GetString() ?? "");
                }

                // 官方规范：vs[1] 恒定为 Single 主视觉封面 MID
                string? visualMid = null;
                if (vsList.Count > 1 && !string.IsNullOrWhiteSpace(vsList[1]))
                {
                    visualMid = vsList[1];
                }
                else
                {
                    visualMid = vsList.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                }

                if (!string.IsNullOrWhiteSpace(visualMid))
                {
                    s_visualMidCache[songMid] = visualMid;
                    return visualMid;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", $"GetSongVisualMidAsync error for {songMid}", ex);
        }
        return null;
    }

    /// <summary>
    /// 获取同步 LRC 歌词与翻译（优先调用官方 PlayLyricInfo 接口，自动解析 Base64 并进行双语时间轴对齐）
    /// </summary>
    public static async Task<List<LyricLine>> GetLyricsAsync(string songMid, CancellationToken ct = default)
    {
        try
        {
            // 1. 优先调用官方 PlayLyricInfo 接口以获取原生原文与翻译歌词
            var jsonPayload = $"{{\"comm\":{{\"ct\":24,\"cv\":0}},\"playLyricInfo\":{{\"module\":\"music.musichallSong.PlayLyricInfo\",\"method\":\"GetPlayLyricInfo\",\"param\":{{\"songMID\":\"{songMid}\",\"songID\":0,\"qrc\":0,\"trans\":1,\"roma\":1,\"isHQ\":1}}}}}}";

            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            using var resp = await s_httpClient.PostAsync("https://u.y.qq.com/cgi-bin/musicu.fcg", content, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("playLyricInfo", out var info) &&
                info.TryGetProperty("data", out var data))
            {
                var b64Lyric = data.TryGetProperty("lyric", out var l) ? l.GetString() : null;
                var b64Trans = data.TryGetProperty("trans", out var t) ? t.GetString() : null;

                var rawLyric = LyricParser.DecodeBase64(b64Lyric);
                var rawTrans = LyricParser.DecodeBase64(b64Trans);

                if (!string.IsNullOrWhiteSpace(rawLyric))
                {
                    return LyricParser.MergeLyrics(rawLyric, rawTrans);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", "GetLyricsAsync PlayLyricInfo error, falling back", ex);
        }

        // 2. 兜底备用：传统 fcg_query_lyric_new.fcg 接口
        try
        {
            var url = $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={songMid}&format=json&nobase64=1";
            var json = await s_httpClient.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("lyric", out var lyricElem))
            {
                var rawLrc = lyricElem.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(rawLrc))
                {
                    return LyricParser.MergeLyrics(rawLrc, "");
                }
            }
        }
        catch
        {
            // Ignore
        }

        return [new LyricLine(TimeSpan.Zero, "暂无歌词")];
    }
}
