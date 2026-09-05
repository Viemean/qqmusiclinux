using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Api;

public sealed partial class QqMusicApi
{
    public static async Task<List<Song>> GetDailyRecommendSongsAsync(CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn) return [];

        await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

        var uin = UserSession.Current.Uin;
        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";

        try
        {
            // 阶段一：通过推荐 Feed 获取今日“每日30首”歌单专属 ID (disstid)
            var feedPayload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
                $"\"feed\":{{\"module\":\"music.recommend.RecommendFeed\",\"method\":\"get_recommend_feed\"," +
                $"\"param\":{{\"direction\":0,\"page\":1,\"s_num\":0,\"v_cache\":[]}}}}}}";

            long dailyDisstid = 0;
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Content = new StringContent(feedPayload, Encoding.UTF8, "application/json");
                var cookieHeader = UserSession.Current.GetCookieHeader();
                if (!string.IsNullOrEmpty(cookieHeader)) req.Headers.Add("Cookie", cookieHeader);

                using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("feed", out var feedObj) &&
                    feedObj.TryGetProperty("data", out var dataObj) &&
                    dataObj.TryGetProperty("v_shelf", out var shelves) &&
                    shelves.ValueKind == JsonValueKind.Array)
                {
                    foreach (var shelf in shelves.EnumerateArray())
                    {
                        if (shelf.TryGetProperty("v_niche", out var niches) && niches.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var niche in niches.EnumerateArray())
                            {
                                if (niche.TryGetProperty("v_card", out var cards) && cards.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var card in cards.EnumerateArray())
                                    {
                                        var title = card.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "" : "";
                                        if (title.Contains("30首") || title.Contains("每日30") || title == "每日30首")
                                        {
                                            if (card.TryGetProperty("id", out var idProp))
                                            {
                                                if (idProp.ValueKind == JsonValueKind.Number)
                                                {
                                                    dailyDisstid = idProp.GetInt64();
                                                }
                                                else if (idProp.ValueKind == JsonValueKind.String && long.TryParse(idProp.GetString(), out var parsedId))
                                                {
                                                    dailyDisstid = parsedId;
                                                }
                                            }
                                            if (dailyDisstid > 0) break;
                                        }
                                    }
                                }
                                if (dailyDisstid > 0) break;
                            }
                        }
                        if (dailyDisstid > 0) break;
                    }
                }
            }

            if (dailyDisstid <= 0)
            {
                AppLogger.Warn("QqMusicApi", "GetDailyRecommendSongsAsync: failed to locate 每日30首 card in feed.");
                return [];
            }

            AppLogger.Info("QqMusicApi", $"GetDailyRecommendSongsAsync: found daily disstid={dailyDisstid}, requesting songlist...");

            // 阶段二：通过 uniform_get_Dissinfo 拉取该专属推荐歌单的全部歌曲（30首）
            var dissPayload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
                $"\"req_diss\":{{\"module\":\"music.srfDissInfo.aiDissInfo\",\"method\":\"uniform_get_Dissinfo\"," +
                $"\"param\":{{\"disstid\":{dailyDisstid},\"userinfo\":1,\"tag\":1}}}}}}";

            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Content = new StringContent(dissPayload, Encoding.UTF8, "application/json");
                var cookieHeader = UserSession.Current.GetCookieHeader();
                if (!string.IsNullOrEmpty(cookieHeader)) req.Headers.Add("Cookie", cookieHeader);

                using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                int initialCapacity = 30;
                if (root.TryGetProperty("req_diss", out var dissObj) &&
                    dissObj.TryGetProperty("data", out var dissData) &&
                    dissData.TryGetProperty("songlist", out var songArray) &&
                    songArray.ValueKind == JsonValueKind.Array)
                {
                    initialCapacity = songArray.GetArrayLength();
                    var list = new List<Song>(initialCapacity);
                    foreach (var item in songArray.EnumerateArray())
                    {
                        var song = ParseSongFromElement(item);
                        if (song != null) list.Add(song);
                    }
                    AppLogger.Info("QqMusicApi", $"GetDailyRecommendSongsAsync: fetched {list.Count} songs for 每日30首");
                    return list;
                }

                return [];
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", "GetDailyRecommendSongsAsync error", ex);
            return [];
        }
    }

    /// <summary>
    /// 获取用户“猜你喜欢”个性化电台歌曲列表（单次按去重策略累积拉取指定数量）
    /// </summary>
    public static async Task<List<Song>> GetGuessRecommendSongsAsync(int count = 25, CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn) return [];

        await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

        var uin = UserSession.Current.Uin;
        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";

        var list = new List<Song>(count);
        var seenMids = new HashSet<string>(count, StringComparer.OrdinalIgnoreCase);

        try
        {
            // 循环多批次拉取（每批次由服务端返回 5 首并排重），最多重试 8 次以凑齐推荐曲目
            int maxBatches = Math.Min(8, Math.Max(2, (count + 4) / 5 + 1));
            for (int batch = 0; batch < maxBatches && list.Count < count; batch++)
            {
                ct.ThrowIfCancellationRequested();

                var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
                    $"\"guess\":{{\"module\":\"music.radioProxy.MbTrackRadioSvr\",\"method\":\"get_radio_track\"," +
                    $"\"param\":{{\"id\":99,\"num\":5,\"from\":0,\"scene\":0,\"song_ids\":[]}}}}}}";

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                var cookieHeader = UserSession.Current.GetCookieHeader();
                if (!string.IsNullOrEmpty(cookieHeader)) req.Headers.Add("Cookie", cookieHeader);

                using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                int batchAdded = 0;
                if (root.TryGetProperty("guess", out var guessObj) &&
                    guessObj.TryGetProperty("data", out var dataObj) &&
                    dataObj.TryGetProperty("tracks", out var trackArray) &&
                    trackArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in trackArray.EnumerateArray())
                    {
                        var song = ParseSongFromElement(item);
                        if (song != null && !string.IsNullOrEmpty(song.Mid) && seenMids.Add(song.Mid))
                        {
                            list.Add(song);
                            batchAdded++;
                        }
                    }
                }

                if (batchAdded == 0 && batch >= 2)
                {
                    break;
                }
            }

            AppLogger.Info("QqMusicApi", $"GetGuessRecommendSongsAsync: fetched {list.Count} songs for 猜你喜欢");
            return list;
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", "GetGuessRecommendSongsAsync error", ex);
            return list;
        }
    }

    /// <summary>
    /// 获取用户自建歌单与外部收藏歌单列表
    /// </summary>
}
