using System.Globalization;
using System.Text;
using System.Text.Json;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Api;

public sealed partial class QqMusicApi
{
    public static async Task<SearchOverview> GeneralSearchAsync(
        string query,
        int previewSize = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return EmptySearchOverview("");
        }

        int count = Math.Clamp(previewSize, 1, 20);
        var songTask = SearchSongsByTypeAsync(query, SearchCategory.Song, 1, count, ct);
        var singerTask = SearchSingersAsync(query, 1, count, ct);
        var albumTask = SearchAlbumsAsync(query, 1, count, ct);
        var playlistTask = SearchPlaylistsAsync(query, 1, count, ct);
        var lyricTask = SearchSongsByTypeAsync(query, SearchCategory.Lyric, 1, count, ct);
        var relatedTask = GetSearchSuggestionsAsync(query, count, ct);

        try
        {
            await Task.WhenAll(songTask, singerTask, albumTask, playlistTask, lyricTask, relatedTask)
                .ConfigureAwait(false);

            return new SearchOverview(
                query,
                songTask.Result,
                singerTask.Result,
                albumTask.Result,
                playlistTask.Result,
                lyricTask.Result,
                relatedTask.Result);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Search", $"GeneralSearchAsync failed for query '{query}'", ex);
            return EmptySearchOverview(query);
        }
    }

    public static Task<SearchPage<Song>> SearchSongsByTypeAsync(
        string query,
        SearchCategory category = SearchCategory.Song,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        if (category is not (SearchCategory.Song or SearchCategory.Lyric))
        {
            throw new ArgumentOutOfRangeException(nameof(category), "Only song and lyric categories return Song results.");
        }

        return SearchDesktopAsync(query, category, page, pageSize, static data =>
        {
            var items = new List<Song>();
            foreach (var item in EnumerateDesktopSearchList(data, "song"))
            {
                var song = ParseSongFromElement(item);
                if (song != null) items.Add(song);
            }
            return items;
        }, ct);
    }

    public static Task<SearchPage<SearchSinger>> SearchSingersAsync(
        string query,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default) =>
        SearchDesktopAsync(query, SearchCategory.Singer, page, pageSize, static data =>
        {
            var items = new List<SearchSinger>();
            foreach (var item in EnumerateDesktopSearchList(data, "singer"))
            {
                string name = GetString(item, "singerName", "name", "title");
                string mid = GetString(item, "singerMID", "singerMid", "mid");
                if (string.IsNullOrWhiteSpace(name)) continue;

                items.Add(new SearchSinger(
                    GetInt64(item, "singerID", "singerId", "id"),
                    mid,
                    name,
                    GetString(item, "singerPic", "pic", "picurl"),
                    GetInt32(item, "songNum", "song_num"),
                    GetInt32(item, "albumNum", "album_num")));
            }
            return items;
        }, ct);

    public static Task<SearchPage<SearchAlbum>> SearchAlbumsAsync(
        string query,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default) =>
        SearchDesktopAsync(query, SearchCategory.Album, page, pageSize, static data =>
        {
            var items = new List<SearchAlbum>();
            foreach (var item in EnumerateDesktopSearchList(data, "album"))
            {
                string name = GetString(item, "albumName", "title", "name");
                string mid = GetString(item, "albumMID", "albumMid", "mid");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(mid)) continue;

                items.Add(new SearchAlbum(
                    GetInt64(item, "albumID", "id"),
                    mid,
                    name,
                    GetString(item, "singerName", "artist", "singer"),
                    GetString(item, "singerMID", "singerMid"),
                    GetInt32(item, "song_count", "songnum", "songNum"),
                    GetString(item, "publicTime", "publish_date", "time_public"),
                    GetString(item, "albumPic", "pic", "picurl")));
            }
            return items;
        }, ct);

    public static Task<SearchPage<SearchPlaylist>> SearchPlaylistsAsync(
        string query,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default) =>
        SearchDesktopAsync(query, SearchCategory.Playlist, page, pageSize, static data =>
        {
            var items = new List<SearchPlaylist>();
            foreach (var item in EnumerateDesktopSearchList(data, "songlist"))
            {
                string name = GetString(item, "dissname", "title", "name");
                long tid = GetInt64(item, "dissid", "tid", "id");
                if (string.IsNullOrWhiteSpace(name) || tid <= 0) continue;

                string creator = "";
                if (item.TryGetProperty("creator", out var creatorElement) && creatorElement.ValueKind == JsonValueKind.Object)
                {
                    creator = GetString(creatorElement, "name", "nickname");
                }

                items.Add(new SearchPlaylist(
                    tid,
                    name,
                    creator,
                    GetInt32(item, "song_count", "songnum", "songNum"),
                    GetInt64(item, "listennum", "listen_num", "play_count"),
                    GetString(item, "imgurl", "picurl", "logo"),
                    GetString(item, "introduction", "desc", "description")));
            }
            return items;
        }, ct);


    public static async Task<List<string>> GetSearchHotkeysAsync(int limit = 12, CancellationToken ct = default)
    {
        int count = Math.Clamp(limit, 1, 50);
        var payload = BuildPublicRequest(
            "req",
            "music.musicsearch.HotkeyService",
            "GetHotkeyForQQMusicMobile",
            writer => writer.WriteString("search_id", CreateSearchId()));

        try
        {
            using var doc = await PostPublicApiAsync(payload, ct).ConfigureAwait(false);
            if (!TryGetPublicData(doc.RootElement, "req", out var data) ||
                !data.TryGetProperty("vec_hotkey", out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<string>(Math.Min(count, array.GetArrayLength()));
            foreach (var item in array.EnumerateArray())
            {
                string word = GetString(item, "query", "title").Trim();
                if (!string.IsNullOrEmpty(word) && !result.Contains(word, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(word);
                    if (result.Count == count) break;
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Search", "GetSearchHotkeysAsync failed", ex);
            return [];
        }
    }

    public static async Task<List<string>> GetSearchSuggestionsAsync(
        string query,
        int limit = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        int count = Math.Clamp(limit, 1, 30);
        var payload = BuildPublicRequest(
            "req",
            "music.smartboxCgi.SmartBoxCgi",
            "GetSmartBoxResult",
            writer =>
            {
                writer.WriteString("search_id", CreateSearchId());
                writer.WriteString("query", query);
                writer.WriteNumber("num_per_page", count);
                writer.WriteNumber("page_idx", 0);
            });

        try
        {
            using var doc = await PostPublicApiAsync(payload, ct).ConfigureAwait(false);
            if (!TryGetPublicData(doc.RootElement, "req", out var data) ||
                !data.TryGetProperty("items", out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<string>(Math.Min(count, array.GetArrayLength()));
            foreach (var item in array.EnumerateArray())
            {
                string word = GetString(item, "hint", "query", "title").Trim();
                if (!string.IsNullOrEmpty(word) && !result.Contains(word, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(word);
                    if (result.Count == count) break;
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Search", $"GetSearchSuggestionsAsync failed for query '{query}'", ex);
            return [];
        }
    }

    private static async Task<SearchPage<T>> SearchDesktopAsync<T>(
        string query,
        SearchCategory category,
        int page,
        int pageSize,
        Func<JsonElement, List<T>> parser,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return new SearchPage<T>([], 0, false);

        int normalizedPage = Math.Max(1, page);
        int normalizedSize = Math.Clamp(pageSize, 1, 100);
        int searchType = category switch
        {
            SearchCategory.Song => 0,
            SearchCategory.Singer => 1,
            SearchCategory.Album => 2,
            SearchCategory.Playlist => 3,
            SearchCategory.Lyric => 7,
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };

        try
        {
            string escapedQuery = JsonEncodedText.Encode(query).ToString();
            string payload = $$"""
            {
              "music.search.SearchCgiService": {
                "module": "music.search.SearchCgiService",
                "method": "DoSearchForQQMusicDesktop",
                "param": {
                  "query": "{{escapedQuery}}",
                  "page_num": {{normalizedPage}},
                  "num_per_page": {{normalizedSize}},
                  "search_type": {{searchType}}
                }
              }
            }
            """;

            string json = await PostAg1Async(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("music.search.SearchCgiService", out var response) ||
                !response.TryGetProperty("data", out var data))
            {
                return new SearchPage<T>([], 0, false);
            }

            var items = parser(data);
            int total = 0;
            int nextPage = -1;
            if (data.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                total = GetInt32(meta, "sum", "estimate_sum");
                nextPage = GetInt32(meta, "nextpage");
            }
            if (total <= 0) total = items.Count;

            bool hasMore = nextPage > normalizedPage || normalizedPage * normalizedSize < total;
            return new SearchPage<T>(items, total, hasMore);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Search", $"Typed search failed for '{query}', category {category}", ex);
            return new SearchPage<T>([], 0, false);
        }
    }

    private static IEnumerable<JsonElement> EnumerateDesktopSearchList(JsonElement data, string bucketName)
    {
        if (!data.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty(bucketName, out var bucket) || bucket.ValueKind != JsonValueKind.Object ||
            !bucket.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return list.EnumerateArray().ToArray();
    }

    private static SearchOverview EmptySearchOverview(string query) => new(
        query,
        new SearchPage<Song>([], 0, false),
        new SearchPage<SearchSinger>([], 0, false),
        new SearchPage<SearchAlbum>([], 0, false),
        new SearchPage<SearchPlaylist>([], 0, false),
        new SearchPage<Song>([], 0, false),
        []);

    private static string CreateSearchId() => string.Concat(
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        Random.Shared.Next(10_000_000, 99_999_999).ToString(CultureInfo.InvariantCulture));

    private static string BuildPublicRequest(
        string requestName,
        string module,
        string method,
        Action<Utf8JsonWriter> writeParameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("comm");
            writer.WriteStartObject();
            writer.WriteNumber("ct", 11);
            writer.WriteNumber("cv", 14090008);
            writer.WriteNumber("v", 14090008);
            writer.WriteString("chid", "10003505");
            writer.WriteString("tmeAppID", "qqmusic");
            writer.WriteString("uin", UserSession.Current.IsLoggedIn ? UserSession.Current.Uin : "0");
            writer.WriteString("authst", UserSession.Current.MusicKey);
            writer.WriteString("format", "json");
            writer.WriteEndObject();
            writer.WritePropertyName(requestName);
            writer.WriteStartObject();
            writer.WriteString("module", module);
            writer.WriteString("method", method);
            writer.WritePropertyName("param");
            writer.WriteStartObject();
            writeParameters(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<JsonDocument> PostPublicApiAsync(string payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        string cookieHeader = UserSession.Current.GetCookieHeader();
        if (!string.IsNullOrEmpty(cookieHeader)) request.Headers.Add("Cookie", cookieHeader);

        using var response = await s_httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(bytes);
    }

    private static bool TryGetPublicData(JsonElement root, string requestName, out JsonElement data)
    {
        if (root.TryGetProperty(requestName, out var response) &&
            response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("data", out data) &&
            data.ValueKind == JsonValueKind.Object)
        {
            return true;
        }
        data = default;
        return false;
    }

    private static string GetString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        }
        return "";
    }

    private static int GetInt32(JsonElement element, params string[] names)
    {
        long value = GetInt64(element, names);
        return value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;
    }

    private static long GetInt64(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)) return number;
        }
        return 0;
    }
}
