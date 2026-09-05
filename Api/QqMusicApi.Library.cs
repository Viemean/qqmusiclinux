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
        var encoded = Uri.EscapeDataString(query);
        var url = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={encoded}&n={pageSize}&p={page}&format=json";

        try
        {
            var json = await s_httpClient.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("song", out var songObj) &&
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
        catch
        {
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

    /// <summary>
    /// 获取用户云端收藏的专辑列表
    /// </summary>
    public static async Task<List<Album>> GetFavoriteAlbumsAsync(CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn) return [];

        await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

        var uin = UserSession.Current.Uin;
        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";

        var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
            $"\"fav_albums\":{{\"module\":\"music.musicasset.AlbumFavRead\",\"method\":\"GetAlbumFavInfo\"," +
            $"\"param\":{{\"uin\":\"{uin}\"}}}}}}";

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
            if (root.TryGetProperty("fav_albums", out var favObj) &&
                favObj.TryGetProperty("data", out var dataObj) &&
                dataObj.TryGetProperty("v_list", out var listArr) &&
                listArr.ValueKind == JsonValueKind.Array)
            {
                var albums = new List<Album>(listArr.GetArrayLength());
                foreach (var item in listArr.EnumerateArray())
                {
                    long id = item.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;
                    string mid = item.TryGetProperty("mid", out var midProp) ? midProp.GetString() ?? "" : "";
                    string name = item.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
                    int songCount = item.TryGetProperty("songnum", out var scProp) ? scProp.GetInt32() : 0;
                    string logo = item.TryGetProperty("logo", out var lProp) ? lProp.GetString() ?? "" : "";
                    long pubtime = item.TryGetProperty("pubtime", out var ptProp) ? ptProp.GetInt64() : 0;

                    bool hasSingers = item.TryGetProperty("v_singer", out var singerArr) && singerArr.ValueKind == JsonValueKind.Array;
                    var artists = new List<string>(hasSingers ? singerArr.GetArrayLength() : 0);
                    if (hasSingers)
                    {
                        foreach (var singer in singerArr.EnumerateArray())
                        {
                            if (singer.TryGetProperty("name", out var sNameProp))
                            {
                                var sn = sNameProp.GetString();
                                if (!string.IsNullOrEmpty(sn)) artists.Add(sn);
                            }
                        }
                    }
                    string artistStr = artists.Count > 0 ? string.Join(" / ", artists) : "群星";

                    if (!string.IsNullOrEmpty(mid) && !string.IsNullOrEmpty(name))
                    {
                        albums.Add(new Album(id, mid, name, artistStr, songCount, logo, pubtime));
                    }
                }
                return albums;
            }

            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", "GetFavoriteAlbumsAsync error", ex);
            return [];
        }
    }

    /// <summary>
    /// 获取指定专辑内的全部歌曲（下钻曲目列表）
    /// </summary>
    public static async Task<List<Song>> GetAlbumSongsAsync(string albumMid, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(albumMid)) return [];

        await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

        var uin = UserSession.Current.Uin;
        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";

        var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
            $"\"album_songs\":{{\"module\":\"music.musichallAlbum.AlbumSongList\",\"method\":\"GetAlbumSongList\"," +
            $"\"param\":{{\"albumMid\":\"{albumMid}\",\"begin\":0,\"num\":-1,\"order\":2}}}}}}";

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
            if (root.TryGetProperty("album_songs", out var albumObj) &&
                albumObj.TryGetProperty("data", out var dataObj) &&
                dataObj.TryGetProperty("songList", out var songList) &&
                songList.ValueKind == JsonValueKind.Array)
            {
                var songs = new List<Song>(songList.GetArrayLength());
                foreach (var item in songList.EnumerateArray())
                {
                    if (item.TryGetProperty("songInfo", out var songInfo))
                    {
                        var song = ParseSongFromElement(songInfo);
                        if (song != null) songs.Add(song);
                    }
                }
                return songs;
            }

            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", $"GetAlbumSongsAsync error for mid={albumMid}", ex);
            return [];
        }
    }

    /// <summary>
    /// 取消收藏指定专辑
    /// </summary>
    public static async Task<bool> RemoveAlbumFromFavoriteAsync(string albumMid, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(albumMid) || !UserSession.Current.IsLoggedIn) return false;

        await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

        var uin = UserSession.Current.Uin;
        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";

        var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
            $"\"cancel_album\":{{\"module\":\"music.musicasset.AlbumFavWrite\",\"method\":\"CancelFavAlbum\"," +
            $"\"param\":{{\"uin\":\"{uin}\",\"v_albumMid\":[\"{albumMid}\"]}}}}}}";

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

            if (root.TryGetProperty("cancel_album", out var cancelObj) &&
                cancelObj.TryGetProperty("code", out var cCode) && cCode.GetInt32() == 0)
            {
                if (cancelObj.TryGetProperty("data", out var dataObj))
                {
                    if (dataObj.TryGetProperty("result", out var resCode) && resCode.GetInt32() == 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", $"RemoveAlbumFromFavoriteAsync error for mid={albumMid}", ex);
            return false;
        }
    }

    /// <summary>
    /// 收藏指定专辑
    /// </summary>
    public static async Task<bool> AddAlbumToFavoriteAsync(string albumMid, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(albumMid) || !UserSession.Current.IsLoggedIn) return false;

        await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

        var uin = UserSession.Current.Uin;
        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";

        var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
            $"\"fav_album\":{{\"module\":\"music.musicasset.AlbumFavWrite\",\"method\":\"FavAlbum\"," +
            $"\"param\":{{\"uin\":\"{uin}\",\"v_albumMid\":[\"{albumMid}\"]}}}}}}";

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

            if (root.TryGetProperty("fav_album", out var favObj) &&
                favObj.TryGetProperty("code", out var cCode) && cCode.GetInt32() == 0)
            {
                if (favObj.TryGetProperty("data", out var dataObj))
                {
                    if (dataObj.TryGetProperty("result", out var resCode) && resCode.GetInt32() == 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", $"AddAlbumToFavoriteAsync error for mid={albumMid}", ex);
            return false;
        }
    }

    /// <summary>
    /// 当缺少歌手 mid/id 时，通过歌曲检索精确补全歌手唯一标识
    /// </summary>
    public static async Task<(string Mid, long Id)> ResolveArtistAsync(string singerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(singerName)) return ("", 0);

        try
        {
            var encoded = Uri.EscapeDataString(singerName);
            var url = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={encoded}&n=5&p=1&format=json";
            var json = await s_httpClient.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("song", out var songObj) &&
                songObj.TryGetProperty("list", out var songList) &&
                songList.ValueKind == JsonValueKind.Array)
            {
                foreach (var song in songList.EnumerateArray())
                {
                    if (song.TryGetProperty("singer", out var singerArr) && singerArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var singer in singerArr.EnumerateArray())
                        {
                            var name = singer.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                            var mid = singer.TryGetProperty("mid", out var mp) ? mp.GetString() ?? "" : "";
                            long id = 0;
                            if (singer.TryGetProperty("id", out var ip) && ip.ValueKind == JsonValueKind.Number) id = ip.GetInt64();

                            if (name.Equals(singerName, StringComparison.OrdinalIgnoreCase) ||
                                name.Contains(singerName, StringComparison.OrdinalIgnoreCase) ||
                                singerName.Contains(name, StringComparison.OrdinalIgnoreCase))
                            {
                                return (mid, id);
                            }
                        }
                    }
                }
            }
        }
        catch {}

        return ("", 0);
    }

    /// <summary>
    /// 获取歌手详情（歌手信息、生平简介、热门歌曲）
    /// </summary>
    public static async Task<ArtistDetail?> GetSingerDetailAsync(string singerMid, long singerId = 0, string singerName = "", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(singerMid) && singerId <= 0)
        {
            if (!string.IsNullOrWhiteSpace(singerName))
            {
                var (resolvedMid, resolvedId) = await ResolveArtistAsync(singerName, ct).ConfigureAwait(false);
                singerMid = resolvedMid;
                singerId = resolvedId;
            }
        }

        if (string.IsNullOrWhiteSpace(singerMid) && singerId <= 0) return null;

        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";

        string paramJson = singerId > 0 && !string.IsNullOrEmpty(singerMid)
            ? $"{{\"sort\":5,\"singermid\":\"{singerMid}\",\"singerid\":{singerId},\"sin\":0,\"num\":100}}"
            : (!string.IsNullOrEmpty(singerMid)
                ? $"{{\"sort\":5,\"singermid\":\"{singerMid}\",\"sin\":0,\"num\":100}}"
                : $"{{\"sort\":5,\"singerid\":{singerId},\"sin\":0,\"num\":100}}");

        var payload = $"{{\"comm\":{{\"ct\":24,\"cv\":0}},\"singer_detail\":{{\"module\":\"music.web_singer_info_svr\",\"method\":\"get_singer_detail_info\",\"param\":{paramJson}}}}}";

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

            if (root.TryGetProperty("singer_detail", out var sdObj) &&
                sdObj.TryGetProperty("data", out var dataObj))
            {
                string sName = "";
                string sMid = singerMid;
                long sId = singerId;
                if (dataObj.TryGetProperty("singer_info", out var sInfo))
                {
                    if (sInfo.TryGetProperty("name", out var nProp)) sName = nProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(sMid) && sInfo.TryGetProperty("mid", out var mProp)) sMid = mProp.GetString() ?? "";
                    if (sId <= 0 && sInfo.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number) sId = idProp.GetInt64();
                }

                string brief = "";
                if (dataObj.TryGetProperty("singer_brief", out var bProp))
                {
                    brief = bProp.GetString() ?? "";
                }

                bool hasSl = dataObj.TryGetProperty("songlist", out var slArray) && slArray.ValueKind == JsonValueKind.Array;
                var songList = new List<Song>(hasSl ? slArray.GetArrayLength() : 30);
                if (hasSl)
                {
                    foreach (var item in slArray.EnumerateArray())
                    {
                        var song = ParseSongFromElement(item);
                        if (song != null) songList.Add(song);
                    }
                }

                if (string.IsNullOrWhiteSpace(brief) && !string.IsNullOrEmpty(sMid))
                {
                    try
                    {
                        var descUrl = $"https://c.y.qq.com/splcloud/fcgi-bin/fcg_get_singer_desc.fcg?singermid={sMid}&format=xml&utf8=1";
                        var descXml = await s_httpClient.GetStringAsync(descUrl, ct).ConfigureAwait(false);
                        var match = Regex.Match(descXml, @"<desc><!\[CDATA\[(.*?)\]\]></desc>", RegexOptions.Singleline);
                        if (match.Success)
                        {
                            brief = match.Groups[1].Value.Trim();
                        }
                    }
                    catch
                    {
                        // 忽略备选简介获取异常
                    }
                }

                return new ArtistDetail(sMid, sId, sName, brief, songList);
            }

            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", $"GetSingerDetailAsync error for mid={singerMid}", ex);
            return null;
        }
    }

    /// <summary>
    /// 分页获取歌手歌曲列表（支持热门/最新排序与分页）
    /// order: 1 为热门(sort=5)，0 为最新(sort=2)
    /// </summary>
    public static async Task<(List<Song> Songs, int Total)> GetSingerSongListAsync(
        string singerMid,
        int begin = 0,
        int pageSize = 30,
        int order = 1,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(singerMid)) return ([], 0);

        int sort = order == 1 ? 5 : 2; // 5: 热门, 2: 最新
        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
        var payload = $"{{\"comm\":{{\"ct\":24,\"cv\":0}},\"singer_detail\":{{\"module\":\"music.web_singer_info_svr\",\"method\":\"get_singer_detail_info\",\"param\":{{\"singermid\":\"{singerMid}\",\"sort\":{sort},\"sin\":{begin},\"num\":{pageSize}}}}}}}";

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

            if (root.TryGetProperty("singer_detail", out var sdObj) &&
                sdObj.TryGetProperty("data", out var dataObj))
            {
                int total = 0;
                if (dataObj.TryGetProperty("total_song", out var totalProp) && totalProp.ValueKind == JsonValueKind.Number)
                {
                    total = totalProp.GetInt32();
                }

                bool hasSl = dataObj.TryGetProperty("songlist", out var slArray) && slArray.ValueKind == JsonValueKind.Array;
                var songs = new List<Song>(hasSl ? slArray.GetArrayLength() : 30);
                if (hasSl)
                {
                    foreach (var item in slArray.EnumerateArray())
                    {
                        var song = ParseSongFromElement(item);
                        if (song != null) songs.Add(song);
                    }
                }
                return (songs, total);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", $"GetSingerSongListAsync error for mid={singerMid}", ex);
        }

        return ([], 0);
    }

    /// <summary>
    /// 分页获取歌手专辑列表（通过官方稳定接口 fcg_v8_singer_album.fcg）
    /// </summary>
    public static async Task<List<Album>> GetSingerAlbumListAsync(
        string singerMid,
        int begin = 0,
        int pageSize = 30,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(singerMid)) return [];

        var url = $"https://c.y.qq.com/v8/fcg-bin/fcg_v8_singer_album.fcg?singermid={singerMid}&order=time&begin={begin}&num={pageSize}&songstatus=1&format=json";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var cookieHeader = UserSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                req.Headers.Add("Cookie", cookieHeader);
            }

            using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var dataObj) &&
                dataObj.TryGetProperty("list", out var alArr) && alArr.ValueKind == JsonValueKind.Array)
            {
                var albums = new List<Album>(alArr.GetArrayLength());
                foreach (var item in alArr.EnumerateArray())
                {
                    string mid = item.TryGetProperty("albumMID", out var mp) ? mp.GetString() ?? "" : "";
                    string name = item.TryGetProperty("albumName", out var np) ? np.GetString() ?? "" : "";
                    string singer = item.TryGetProperty("singerName", out var sp) ? sp.GetString() ?? "" : "";
                    string pubTime = item.TryGetProperty("pubTime", out var ptp) ? ptp.GetString() ?? "" : "";
                    int songCount = 0;
                    if (item.TryGetProperty("latest_song", out var ls) && ls.TryGetProperty("song_count", out var sc) && sc.ValueKind == JsonValueKind.Number)
                    {
                        songCount = sc.GetInt32();
                    }

                    long id = 0;
                    if (item.TryGetProperty("albumID", out var idp))
                    {
                        if (idp.ValueKind == JsonValueKind.Number) id = idp.GetInt64();
                        else if (idp.ValueKind == JsonValueKind.String && long.TryParse(idp.GetString(), out var parsedId)) id = parsedId;
                    }

                    if (!string.IsNullOrEmpty(mid) && !string.IsNullOrEmpty(name))
                    {
                        var artistDisplay = string.IsNullOrEmpty(singer) ? pubTime : (string.IsNullOrEmpty(pubTime) ? singer : $"{singer} ({pubTime})");
                        albums.Add(new Album(id, mid, name, artistDisplay, songCount));
                    }
                }
                return albums;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", $"GetSingerAlbumListAsync error for mid={singerMid}", ex);
        }

        return [];
    }

    /// <summary>
    /// 获取专辑详细信息（基本信息、详细背景介绍、曲目列表）
    /// </summary>
    public static async Task<AlbumDetail?> GetAlbumDetailInfoAsync(string albumMid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(albumMid)) return null;

        var songsTask = GetAlbumSongsAsync(albumMid, ct);

        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
        var payload = $"{{\"comm\":{{\"ct\":24,\"cv\":0}},\"album_detail\":{{\"module\":\"music.musichallAlbum.AlbumInfoServer\",\"method\":\"GetAlbumDetail\",\"param\":{{\"albumMid\":\"{albumMid}\"}}}}}}";

        string albumName = "";
        string artistName = "";
        string publishDate = "";
        string company = "";
        string desc = "";

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

            if (root.TryGetProperty("album_detail", out var adObj) &&
                adObj.TryGetProperty("data", out var dataObj) &&
                dataObj.TryGetProperty("basicInfo", out var basicInfo))
            {
                if (basicInfo.TryGetProperty("name", out var nProp)) albumName = nProp.GetString() ?? "";
                if (basicInfo.TryGetProperty("singerName", out var snProp)) artistName = snProp.GetString() ?? "";
                if (basicInfo.TryGetProperty("publishDate", out var pdProp)) publishDate = pdProp.GetString() ?? "";
                if (basicInfo.TryGetProperty("company", out var cProp)) company = cProp.GetString() ?? "";
                if (basicInfo.TryGetProperty("desc", out var dProp)) desc = dProp.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("QqMusicApi", $"GetAlbumDetailInfoAsync info error for mid={albumMid}", ex);
        }

        var songs = await songsTask.ConfigureAwait(false);

        if (string.IsNullOrEmpty(albumName) && songs.Count > 0)
        {
            albumName = songs[0].Album;
        }
        if (string.IsNullOrEmpty(artistName) && songs.Count > 0)
        {
            artistName = songs[0].Artist;
        }

        return new AlbumDetail(albumMid, albumName, artistName, publishDate, company, desc, songs);
    }
}

