using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Api;

public sealed partial class QqMusicApi
{
    public static async Task<List<Song>> SearchAsync(string query, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        try
        {
            var escapedQuery = JsonEncodedText.Encode(query).ToString();
            var payload = $$"""
            {
              "music.search.SearchCgiService": {
                "module": "music.search.SearchCgiService",
                "method": "DoSearchForQQMusicDesktop",
                "param": {
                  "query": "{{escapedQuery}}",
                  "page_num": {{page}},
                  "num_per_page": {{pageSize}},
                  "search_type": 0
                }
              }
            }
            """;

            var json = await PostAg1Async(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("music.search.SearchCgiService", out var svc) &&
                svc.TryGetProperty("data", out var data) &&
                data.TryGetProperty("body", out var body) &&
                body.TryGetProperty("song", out var songObj) &&
                songObj.TryGetProperty("list", out var songList) &&
                songList.ValueKind == JsonValueKind.Array)
            {
                var list = new List<Song>(songList.GetArrayLength());
                foreach (var item in songList.EnumerateArray())
                {
                    var song = ParseSongFromElement(item);
                    if (song != null)
                    {
                        list.Add(song);
                    }
                }
                return list;
            }

            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error("Search", $"SearchAsync failed for query '{query}'", ex);
            return [];
        }
    }

    /// <summary>
    /// 并发探测指定歌曲在 4 档品质下的真实可用状态及直链
    /// </summary>

    public sealed record FavoriteSongsResult(List<Song> Songs, int Total, bool HasMore);

    public static async Task<FavoriteSongsResult> GetFavoriteSongsAsync(int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn) return new FavoriteSongsResult([], 0, false);

        await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

        var uin = UserSession.Current.Uin;
        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";

        var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
            $"\"req_fav\":{{\"module\":\"music.musicasset.PlaylistDetailRead\",\"method\":\"GetUniformSongDetailInfo\"," +
            $"\"param\":{{\"uin\":\"{uin}\",\"dirid\":201,\"bPaged\":true,\"offset\":{(page - 1) * pageSize},\"size\":{pageSize}}}}}}}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var cookieHeader = UserSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                req.Headers.Add("Cookie", cookieHeader);
            }

            using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var list = new List<Song>(pageSize > 0 ? pageSize : 30);
            int total = 0;
            bool hasMore = false;

            if (root.TryGetProperty("req_fav", out var favObj) &&
                favObj.TryGetProperty("data", out var dataObj))
            {
                if (dataObj.TryGetProperty("total", out var totalProp) && totalProp.ValueKind == JsonValueKind.Number)
                {
                    total = totalProp.GetInt32();
                }
                else if (dataObj.TryGetProperty("total_song_num", out var tsnProp) && tsnProp.ValueKind == JsonValueKind.Number)
                {
                    total = tsnProp.GetInt32();
                }

                if (dataObj.TryGetProperty("hasmore", out var hmProp))
                {
                    if (hmProp.ValueKind == JsonValueKind.Number) hasMore = hmProp.GetInt32() == 1;
                    else if (hmProp.ValueKind == JsonValueKind.True) hasMore = true;
                    else if (hmProp.ValueKind == JsonValueKind.False) hasMore = false;
                }

                int rawCount = 0;
                if (dataObj.TryGetProperty("list", out var songArray) && songArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in songArray.EnumerateArray())
                    {
                        rawCount++;
                        var song = ParseSongFromElement(item);
                        if (song != null) list.Add(song);
                    }
                }

                // 容错判定：若服务端未直接返回 hasmore=true，但只要未达到 total 或原始批次等于 pageSize，均继续保持分页可拉取
                if (!hasMore && total > 0 && ((page - 1) * pageSize + rawCount < total))
                {
                    hasMore = true;
                }
                else if (!hasMore && total == 0 && rawCount >= pageSize)
                {
                    hasMore = true;
                }
            }

            return new FavoriteSongsResult(list, total, hasMore);
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", "GetFavoriteSongsAsync error", ex);
            return new FavoriteSongsResult([], 0, false);
        }
    }

    /// <summary>
    /// 获取用户每日推荐歌单（每日30首）
    /// </summary>

    public static async Task<List<Playlist>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn) return [];

        await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

        var uin = UserSession.Current.Uin;
        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";

        var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
            $"\"self_playlists\":{{\"module\":\"music.musicasset.PlaylistBaseRead\",\"method\":\"GetPlaylistByUin\",\"param\":{{\"uin\":\"{uin}\"}}}}," +
            $"\"fav_playlists\":{{\"module\":\"music.musicasset.PlaylistFavRead\",\"method\":\"GetPlaylistFavInfo\",\"param\":{{\"uin\":\"{uin}\"}}}}}}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var cookieHeader = UserSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                req.Headers.Add("Cookie", cookieHeader);
            }

            using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var playlists = new List<Playlist>(32);

            if (root.TryGetProperty("self_playlists", out var selfObj) &&
                selfObj.TryGetProperty("data", out var selfData) &&
                selfData.TryGetProperty("v_playlist", out var selfArr) &&
                selfArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in selfArr.EnumerateArray())
                {
                    long dirId = item.TryGetProperty("dirId", out var d) ? d.GetInt64() : 0;
                    string name = item.TryGetProperty("dirName", out var n) ? n.GetString() ?? "" : "";
                    int count = item.TryGetProperty("songNum", out var c) ? c.GetInt32() : 0;
                    long tid = item.TryGetProperty("tid", out var t) ? t.GetInt64() : 0;
                    string pic = item.TryGetProperty("picUrl", out var p) ? p.GetString() ?? "" : "";

                    if (dirId > 0 && !string.IsNullOrEmpty(name))
                    {
                        playlists.Add(new Playlist(dirId, name, count, tid, IsFav: false, pic));
                    }
                }
            }

            if (root.TryGetProperty("fav_playlists", out var favObj) &&
                favObj.TryGetProperty("data", out var favData) &&
                favData.TryGetProperty("v_list", out var favArr) &&
                favArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in favArr.EnumerateArray())
                {
                    long tid = item.TryGetProperty("tid", out var t) ? t.GetInt64() : 0;
                    long dirId = item.TryGetProperty("dirId", out var d) ? d.GetInt64() : 0;
                    string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    int count = item.TryGetProperty("songnum", out var c) ? c.GetInt32() : 0;
                    string logo = item.TryGetProperty("logo", out var l) ? l.GetString() ?? "" : "";

                    if (tid > 0 && !string.IsNullOrEmpty(name))
                    {
                        playlists.Add(new Playlist(dirId, name, count, tid, IsFav: true, logo));
                    }
                }
            }

            return playlists;
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", "GetPlaylistsAsync error", ex);
            return [];
        }
    }

    /// <summary>
    /// 获取指定歌单内的所有歌曲（自建歌单与外部收藏歌单均支持）
    /// </summary>
    public static async Task<List<Song>> GetPlaylistSongsAsync(Playlist playlist, int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        if (!playlist.IsFav)
        {
            if (!UserSession.Current.IsLoggedIn) return [];
            await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

            var uin = UserSession.Current.Uin;
            var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
            var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
                $"\"req_pl\":{{\"module\":\"music.musicasset.PlaylistDetailRead\",\"method\":\"GetUniformSongDetailInfo\"," +
                $"\"param\":{{\"uin\":\"{uin}\",\"dirid\":{playlist.DirId},\"bPaged\":true,\"offset\":{(page - 1) * pageSize},\"size\":{pageSize}}}}}}}";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                var cookieHeader = UserSession.Current.GetCookieHeader();
                if (!string.IsNullOrEmpty(cookieHeader)) req.Headers.Add("Cookie", cookieHeader);

                using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("req_pl", out var plObj) &&
                    plObj.TryGetProperty("data", out var dataObj) &&
                    dataObj.TryGetProperty("list", out var songArray) &&
                    songArray.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<Song>(songArray.GetArrayLength());
                    foreach (var item in songArray.EnumerateArray())
                    {
                        var song = ParseSongFromElement(item);
                        if (song != null) list.Add(song);
                    }
                    return list;
                }
                return [];
            }
            catch (Exception ex)
            {
                AppLogger.Error("QqMusicApi", $"GetPlaylistSongsAsync (dirId: {playlist.DirId}) error", ex);
                return [];
            }
        }
        else
        {
            var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
            var uin = UserSession.Current.IsLoggedIn ? UserSession.Current.Uin : "0";
            var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
                $"\"req_diss\":{{\"module\":\"music.srfDissInfo.aiDissInfo\",\"method\":\"uniform_get_Dissinfo\"," +
                $"\"param\":{{\"disstid\":{playlist.Tid},\"userinfo\":1,\"tag\":1}}}}}}";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                var cookieHeader = UserSession.Current.GetCookieHeader();
                if (!string.IsNullOrEmpty(cookieHeader)) req.Headers.Add("Cookie", cookieHeader);

                using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("req_diss", out var dissObj) &&
                    dissObj.TryGetProperty("data", out var dataObj) &&
                    dataObj.TryGetProperty("songlist", out var songArray) &&
                    songArray.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<Song>(songArray.GetArrayLength());
                    foreach (var item in songArray.EnumerateArray())
                    {
                        var song = ParseSongFromElement(item);
                        if (song != null) list.Add(song);
                    }
                    return list;
                }
                return [];
            }
            catch (Exception ex)
            {
                AppLogger.Error("QqMusicApi", $"GetPlaylistSongsAsync (tid: {playlist.Tid}) error", ex);
                return [];
            }
        }
    }

    /// <summary>
    /// 添加歌曲到指定歌单（dirId: 201 即为“我喜欢”）
    /// </summary>
    public static async Task<bool> AddSongToPlaylistAsync(long dirId, long songId, CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn || songId <= 0) return false;

        await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

        var payload = $"{{\"comm\":{{\"ct\":24,\"cv\":0}}," +
            $"\"addSongsToPlayList\":{{\"module\":\"music.musicasset.PlaylistDetailWrite\",\"method\":\"AddSonglist\"," +
            $"\"param\":{{\"dirId\":{dirId},\"v_songInfo\":[{{\"songId\":{songId},\"songType\":0}}]}}}}}}";

        try
        {
            AppLogger.Info("QqMusicApi", $"AddSongToPlaylistAsync (AG-1) requesting: dirId={dirId}, songId={songId}");
            var json = await PostAg1Async(payload, ct).ConfigureAwait(false);
            AppLogger.Info("QqMusicApi", $"AddSongToPlaylistAsync (AG-1) response: {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("addSongsToPlayList", out var addObj))
            {
                int code = -1;
                if (addObj.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number)
                {
                    code = codeProp.GetInt32();
                }
                else if (addObj.TryGetProperty("subcode", out var subProp) && subProp.ValueKind == JsonValueKind.Number)
                {
                    code = subProp.GetInt32();
                }

                if (code == 0)
                {
                    return true;
                }
                AppLogger.Warn("QqMusicApi", $"AddSongToPlaylistAsync (AG-1) returned non-zero code: {code}");
            }
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", $"AddSongToPlaylistAsync (AG-1) exception for dirId={dirId}, songId={songId}", ex);
            return false;
        }
    }

    /// <summary>
    /// 从指定歌单中移除歌曲（dirId: 201 即为“我喜欢”）
    /// </summary>
    public static async Task<bool> RemoveSongFromPlaylistAsync(long dirId, long songId, CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn || songId <= 0) return false;

        await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

        var payload = $"{{\"comm\":{{\"ct\":24,\"cv\":0}}," +
            $"\"delSongsFromPlayList\":{{\"module\":\"music.musicasset.PlaylistDetailWrite\",\"method\":\"DelSonglist\"," +
            $"\"param\":{{\"dirId\":{dirId},\"v_songInfo\":[{{\"songId\":{songId},\"songType\":0}}]}}}}}}";

        try
        {
            AppLogger.Info("QqMusicApi", $"RemoveSongFromPlaylistAsync (AG-1) requesting: dirId={dirId}, songId={songId}");
            var json = await PostAg1Async(payload, ct).ConfigureAwait(false);
            AppLogger.Info("QqMusicApi", $"RemoveSongFromPlaylistAsync (AG-1) response: {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("delSongsFromPlayList", out var delObj))
            {
                int code = -1;
                if (delObj.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number)
                {
                    code = codeProp.GetInt32();
                }
                else if (delObj.TryGetProperty("subcode", out var subProp) && subProp.ValueKind == JsonValueKind.Number)
                {
                    code = subProp.GetInt32();
                }

                if (code == 0)
                {
                    return true;
                }
                AppLogger.Warn("QqMusicApi", $"RemoveSongFromPlaylistAsync (AG-1) returned non-zero code: {code}");
            }
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", $"RemoveSongFromPlaylistAsync (AG-1) exception for dirId={dirId}, songId={songId}", ex);
            return false;
        }
    }

    /// <summary>
    /// 添加歌曲到指定歌单（Playlist 模型重载）
    /// </summary>
    public static async Task<bool> AddSongToPlaylistAsync(Playlist playlist, Song song, CancellationToken ct = default)
    {
        long songId = song.Id;
        if (songId <= 0 && !string.IsNullOrEmpty(song.Mid))
        {
            songId = await ResolveSongIdAsync(song.Mid, ct).ConfigureAwait(false);
            if (songId > 0) song.Id = songId;
        }
        if (songId <= 0) return false;

        return await AddSongToPlaylistAsync(playlist.DirId, songId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 从指定歌单中移除歌曲（Playlist 模型重载）
    /// </summary>
    public static async Task<bool> RemoveSongFromPlaylistAsync(Playlist playlist, Song song, CancellationToken ct = default)
    {
        long songId = song.Id;
        if (songId <= 0 && !string.IsNullOrEmpty(song.Mid))
        {
            songId = await ResolveSongIdAsync(song.Mid, ct).ConfigureAwait(false);
            if (songId > 0) song.Id = songId;
        }
        if (songId <= 0) return false;

        return await RemoveSongFromPlaylistAsync(playlist.DirId, songId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 添加歌曲到“我喜欢”
    /// </summary>
    public static async Task<bool> AddSongToFavoriteAsync(Song song, CancellationToken ct = default)
    {
        long songId = song.Id;
        if (songId <= 0 && !string.IsNullOrEmpty(song.Mid))
        {
            songId = await ResolveSongIdAsync(song.Mid, ct).ConfigureAwait(false);
            if (songId > 0) song.Id = songId;
        }
        if (songId <= 0)
        {
            AppLogger.Warn("QqMusicApi", $"AddSongToFavoriteAsync: Unable to resolve songId for mid={song.Mid}, title={song.Title}");
            return false;
        }

        var ok = await AddSongToPlaylistAsync(201, songId, ct).ConfigureAwait(false);
        AppLogger.Info("QqMusicApi", $"AddSongToFavoriteAsync: mid={song.Mid}, songId={songId}, title={song.Title}, result={ok}");
        return ok;
    }

    /// <summary>
    /// 从“我喜欢”中移除歌曲
    /// </summary>
    public static async Task<bool> RemoveSongFromFavoriteAsync(Song song, CancellationToken ct = default)
    {
        long songId = song.Id;
        if (songId <= 0 && !string.IsNullOrEmpty(song.Mid))
        {
            songId = await ResolveSongIdAsync(song.Mid, ct).ConfigureAwait(false);
            if (songId > 0) song.Id = songId;
        }
        if (songId <= 0)
        {
            AppLogger.Warn("QqMusicApi", $"RemoveSongFromFavoriteAsync: Unable to resolve songId for mid={song.Mid}, title={song.Title}");
            return false;
        }

        var ok = await RemoveSongFromPlaylistAsync(201, songId, ct).ConfigureAwait(false);
        AppLogger.Info("QqMusicApi", $"RemoveSongFromFavoriteAsync: mid={song.Mid}, songId={songId}, title={song.Title}, result={ok}");
        return ok;
    }

}
