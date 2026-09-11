using System.Globalization;
using Terminal.Gui.App;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.UI;

public sealed partial class MainWindow
{
    private async Task ExecuteSearchAsync()
    {
        if (_isSearching) return;

        string query = _searchField.Text.ToString()?.Trim() ?? "";
        if (string.IsNullOrEmpty(query))
        {
            await ShowSearchSuggestionsAsync();
            return;
        }

        _isSearching = true;
        ResetSearchNavigation(query);
        _songListView.SetMessage("正在搜索歌曲、歌手、专辑、歌单和歌词...", $"正在搜索「{query}」...");

        try
        {
            var overview = await QqMusicApi.GeneralSearchAsync(query, previewSize: 5).ConfigureAwait(false);
            _searchOverview = overview;
            SearchHistory.Add(query);

            Application.Invoke(() => RenderSearchOverview(overview));
        }
        catch (Exception ex)
        {
            AppLogger.Error("Search", "ExecuteSearchAsync failed", ex);
            Application.Invoke(() =>
            {
                _songListView.SetMessage("搜索失败，请稍后重试", $"搜索「{query}」失败");
                _controlBar.UpdateStatus($"[搜索失败] {ex.Message}");
            });
        }
        finally
        {
            _isSearching = false;
        }
    }

    private void ResetSearchNavigation(string query)
    {
        _currentViewMode = ViewMode.Search;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;
        _lastSearchQuery = query;
        _searchCategory = null;
        _searchCurrentPage = 1;
        _hasMoreSearchResults = false;
        _isLoadingMore = false;
        _searchSongItems.Clear();
        _searchSingerItems.Clear();
        _searchAlbumItems.Clear();
        _searchPlaylistItems.Clear();
    }

    private void RenderSearchOverview(SearchOverview overview)
    {
        var rows = new List<string>();
        var actions = new List<Func<Task>>();

        AddOverviewCategory(rows, actions, "单曲", overview.Songs.Total, SearchCategory.Song, overview.Songs.Items.Select(song =>
            ($"  ♪ {song.Title} - {song.Artist}  [{song.FormattedDuration}]", (Func<Task>)(() => PlaySongFromOverviewAsync(song)))));
        AddOverviewCategory(rows, actions, "歌手", overview.Singers.Total, SearchCategory.Singer, overview.Singers.Items.Select(singer =>
            ($"  ♫ {singer.Name}  单曲 {singer.SongCount} · 专辑 {singer.AlbumCount}", (Func<Task>)(() => DrilldownToArtistAsync(new ArtistInfo(singer.Name, singer.Mid, singer.Id))))));
        AddOverviewCategory(rows, actions, "专辑", overview.Albums.Total, SearchCategory.Album, overview.Albums.Items.Select(album =>
            ($"  ◈ {album.Name} - {album.Artist}  ({album.PublishDate})", (Func<Task>)(() => OpenSearchAlbumAsync(album)))));
        AddOverviewCategory(rows, actions, "歌单", overview.Playlists.Total, SearchCategory.Playlist, overview.Playlists.Items.Select(playlist =>
            ($"  ≡ {playlist.Name} - {playlist.Creator}  ({playlist.SongCount} 首)", (Func<Task>)(() => OpenSearchPlaylistAsync(playlist)))));
        AddOverviewCategory(rows, actions, "歌词", overview.LyricSongs.Total, SearchCategory.Lyric, overview.LyricSongs.Items.Select(song =>
            ($"  ≋ {song.Title} - {song.Artist}  [{song.FormattedDuration}]", (Func<Task>)(() => PlaySongFromOverviewAsync(song)))));

        if (overview.RelatedWords.Count > 0)
        {
            rows.Add("── 相关搜索 ──");
            actions.Add(() => Task.CompletedTask);
            foreach (string related in overview.RelatedWords)
            {
                rows.Add($"  ⌕ {related}");
                actions.Add(() => ExecuteRelatedSearchAsync(related));
            }
        }

        if (rows.Count == 0)
        {
            _songListView.SetMessage("未找到匹配结果", $"搜索「{overview.Query}」: 0 条结果");
            return;
        }

        _songListView.SetCustomItems(rows, $"搜索「{overview.Query}」· Enter 打开 · 分类标题查看更多", async index =>
        {
            if (index >= 0 && index < actions.Count) await actions[index]();
        });
        _songListView.SetFocusToList();
        _controlBar.UpdateStatus($"[搜索完成] 已按 5 个分类展示「{overview.Query}」的结果");
    }

    private void AddOverviewCategory(
        List<string> rows,
        List<Func<Task>> actions,
        string label,
        int total,
        SearchCategory category,
        IEnumerable<(string Row, Func<Task> Action)> previewRows)
    {
        rows.Add($"── {label} ({total}) · Enter 查看全部 ──");
        actions.Add(() => OpenSearchCategoryAsync(category));
        foreach (var item in previewRows)
        {
            rows.Add(item.Row);
            actions.Add(item.Action);
        }
    }

    private async Task OpenSearchCategoryAsync(SearchCategory category)
    {
        if (_isSearching || string.IsNullOrEmpty(_lastSearchQuery)) return;

        _isSearching = true;
        _searchCategory = category;
        _searchCurrentPage = 1;
        _isLoadingMore = false;
        _songListView.SetMessage("正在加载分类结果...", $"搜索「{_lastSearchQuery}」· {CategoryName(category)}");

        try
        {
            await LoadSearchCategoryPageAsync(category, 1, append: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Search", $"Failed to open category {category}", ex);
            Application.Invoke(() => _controlBar.UpdateStatus($"[分类加载失败] {ex.Message}"));
        }
        finally
        {
            _isSearching = false;
        }
    }

    private async Task LoadSearchCategoryPageAsync(SearchCategory category, int page, bool append)
    {
        switch (category)
        {
            case SearchCategory.Song:
            case SearchCategory.Lyric:
            {
                var result = await QqMusicApi.SearchSongsByTypeAsync(_lastSearchQuery, category, page, PageSize).ConfigureAwait(false);
                Application.Invoke(() => RenderSongCategory(category, result, page, append));
                break;
            }
            case SearchCategory.Singer:
            {
                var result = await QqMusicApi.SearchSingersAsync(_lastSearchQuery, page, PageSize).ConfigureAwait(false);
                Application.Invoke(() => RenderSingerCategory(result, page, append));
                break;
            }
            case SearchCategory.Album:
            {
                var result = await QqMusicApi.SearchAlbumsAsync(_lastSearchQuery, page, PageSize).ConfigureAwait(false);
                Application.Invoke(() => RenderAlbumCategory(result, page, append));
                break;
            }
            case SearchCategory.Playlist:
            {
                var result = await QqMusicApi.SearchPlaylistsAsync(_lastSearchQuery, page, PageSize).ConfigureAwait(false);
                Application.Invoke(() => RenderPlaylistCategory(result, page, append));
                break;
            }
        }
    }

    private void RenderSongCategory(SearchCategory category, SearchPage<Song> result, int page, bool append)
    {
        if (!append) _searchSongItems = [.. result.Items];
        else _searchSongItems.AddRange(result.Items);
        CompleteCategoryPage(category, page, result.Total, result.HasMore);

        string title = CategoryTitle(category, result.Total, _searchSongItems.Count);
        if (append) _songListView.AppendSongs(result.Items, title);
        else _songListView.SetSongs(_searchSongItems, title);
        if (_activeSong != null) _songListView.SetPlayingSong(_activeSong.Mid);
        _songListView.SetFocusToList();
    }

    private void RenderSingerCategory(SearchPage<SearchSinger> result, int page, bool append)
    {
        if (!append) _searchSingerItems = [.. result.Items];
        else _searchSingerItems.AddRange(result.Items);
        CompleteCategoryPage(SearchCategory.Singer, page, result.Total, result.HasMore);

        var rows = _searchSingerItems.Select((singer, index) =>
            $"{index + 1:D2}  {singer.Name}  单曲 {singer.SongCount} · 专辑 {singer.AlbumCount}").ToList();
        string title = CategoryTitle(SearchCategory.Singer, result.Total, rows.Count);
        Func<int, Task> accepted = async index =>
        {
            if (index >= 0 && index < _searchSingerItems.Count)
            {
                var singer = _searchSingerItems[index];
                await DrilldownToArtistAsync(new ArtistInfo(singer.Name, singer.Mid, singer.Id));
            }
        };
        if (append) _songListView.AppendCustomItems(result.Items.Select((singer, offset) =>
            $"{_searchSingerItems.Count - result.Items.Count + offset + 1:D2}  {singer.Name}  单曲 {singer.SongCount} · 专辑 {singer.AlbumCount}").ToList(), title);
        else _songListView.SetCustomItems(rows, title, accepted);
        _songListView.SetFocusToList();
    }

    private void RenderAlbumCategory(SearchPage<SearchAlbum> result, int page, bool append)
    {
        if (!append) _searchAlbumItems = [.. result.Items];
        else _searchAlbumItems.AddRange(result.Items);
        CompleteCategoryPage(SearchCategory.Album, page, result.Total, result.HasMore);

        var rows = _searchAlbumItems.Select((album, index) =>
            $"{index + 1:D2}  {album.Name} - {album.Artist}  {album.PublishDate}  ({album.SongCount} 首)").ToList();
        string title = CategoryTitle(SearchCategory.Album, result.Total, rows.Count);
        Func<int, Task> accepted = async index =>
        {
            if (index >= 0 && index < _searchAlbumItems.Count) await OpenSearchAlbumAsync(_searchAlbumItems[index]);
        };
        if (append) _songListView.AppendCustomItems(result.Items.Select((album, offset) =>
            $"{_searchAlbumItems.Count - result.Items.Count + offset + 1:D2}  {album.Name} - {album.Artist}  {album.PublishDate}  ({album.SongCount} 首)").ToList(), title);
        else _songListView.SetCustomItems(rows, title, accepted);
        _songListView.SetFocusToList();
    }

    private void RenderPlaylistCategory(SearchPage<SearchPlaylist> result, int page, bool append)
    {
        if (!append) _searchPlaylistItems = [.. result.Items];
        else _searchPlaylistItems.AddRange(result.Items);
        CompleteCategoryPage(SearchCategory.Playlist, page, result.Total, result.HasMore);

        var rows = _searchPlaylistItems.Select((playlist, index) =>
            $"{index + 1:D2}  {playlist.Name} - {playlist.Creator}  ({playlist.SongCount} 首 · {FormatCount(playlist.ListenCount)} 播放)").ToList();
        string title = CategoryTitle(SearchCategory.Playlist, result.Total, rows.Count);
        Func<int, Task> accepted = async index =>
        {
            if (index >= 0 && index < _searchPlaylistItems.Count) await OpenSearchPlaylistAsync(_searchPlaylistItems[index]);
        };
        if (append) _songListView.AppendCustomItems(result.Items.Select((playlist, offset) =>
            $"{_searchPlaylistItems.Count - result.Items.Count + offset + 1:D2}  {playlist.Name} - {playlist.Creator}  ({playlist.SongCount} 首 · {FormatCount(playlist.ListenCount)} 播放)").ToList(), title);
        else _songListView.SetCustomItems(rows, title, accepted);
        _songListView.SetFocusToList();
    }


    private void CompleteCategoryPage(SearchCategory category, int page, int total, bool hasMore)
    {
        _searchCategory = category;
        _searchCurrentPage = page;
        _hasMoreSearchResults = hasMore;
        _isLoadingMore = false;
    }


    private void RestoreSearchCategorySnapshot(SearchCategory category, List<Song> songs, int selectedIndex)
    {
        switch (category)
        {
            case SearchCategory.Song:
            case SearchCategory.Lyric:
                _searchSongItems = [.. songs];
                _songListView.SetSongs(songs, CategoryTitle(category, GetSearchOverviewCategoryTotal(category), songs.Count));
                break;
            case SearchCategory.Singer:
                RenderSingerCategory(new SearchPage<SearchSinger>([], GetSearchOverviewCategoryTotal(category), _hasMoreSearchResults), _searchCurrentPage, append: true);
                break;
            case SearchCategory.Album:
                RenderAlbumCategory(new SearchPage<SearchAlbum>([], GetSearchOverviewCategoryTotal(category), _hasMoreSearchResults), _searchCurrentPage, append: true);
                break;
            case SearchCategory.Playlist:
                RenderPlaylistCategory(new SearchPage<SearchPlaylist>([], GetSearchOverviewCategoryTotal(category), _hasMoreSearchResults), _searchCurrentPage, append: true);
                break;
        }
        if (selectedIndex >= 0) _songListView.SetSelectedIndex(selectedIndex);
    }
    private int GetSearchOverviewCategoryTotal(SearchCategory category) => category switch
    {
        SearchCategory.Song => _searchOverview?.Songs.Total ?? _searchSongItems.Count,
        SearchCategory.Singer => _searchOverview?.Singers.Total ?? _searchSingerItems.Count,
        SearchCategory.Album => _searchOverview?.Albums.Total ?? _searchAlbumItems.Count,
        SearchCategory.Playlist => _searchOverview?.Playlists.Total ?? _searchPlaylistItems.Count,
        SearchCategory.Lyric => _searchOverview?.LyricSongs.Total ?? _searchSongItems.Count,
        _ => 0
    };
    private async Task LoadMoreSearchResultsAsync()
    {
        if (_isLoadingMore || !_hasMoreSearchResults || _searchCategory == null || string.IsNullOrEmpty(_lastSearchQuery)) return;

        _isLoadingMore = true;
        int nextPage = _searchCurrentPage + 1;
        _controlBar.UpdateStatus($"[正在加载] 搜索「{_lastSearchQuery}」· {CategoryName(_searchCategory.Value)} 第 {nextPage} 页...");
        try
        {
            await LoadSearchCategoryPageAsync(_searchCategory.Value, nextPage, append: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Search", "LoadMoreSearchResultsAsync failed", ex);
            Application.Invoke(() => _controlBar.UpdateStatus($"[加载失败] {ex.Message}"));
        }
        finally
        {
            _isLoadingMore = false;
        }
    }

    private async Task PlaySongFromOverviewAsync(Song song)
    {
        PlaybackQueueService.Instance.Mode = _currentPlaybackMode;
        PlaybackQueueService.Instance.SetQueue([song], 0);
        await PlaySongAsync(song);
    }

    private async Task OpenSearchAlbumAsync(SearchAlbum album)
    {
        PushCurrentNavigationSnapshot();
        await DrilldownAlbumAsync(new Album(album.Id, album.Mid, album.Name, album.Artist, album.SongCount, album.PicUrl));
    }

    private async Task OpenSearchPlaylistAsync(SearchPlaylist playlist)
    {
        PushCurrentNavigationSnapshot();
        await DrilldownPlaylistAsync(playlist.ToPlaylist());
    }


    private async Task ShowSearchSuggestionsAsync()
    {
        var history = SearchHistory.GetQueries();
        string query = _searchField.Text.ToString()?.Trim() ?? "";
        var hotkeys = await QqMusicApi.GetSearchHotkeysAsync().ConfigureAwait(false);

        Application.Invoke(() =>
        {
            using var dialog = new SearchSuggestionsDialog(history, hotkeys, query);
            Application.Run(dialog);
            if (string.IsNullOrWhiteSpace(dialog.SelectedQuery)) return;

            _searchField.Text = dialog.SelectedQuery;
            _isSearchActive = false;
            _searchField.CanFocus = false;
            _ = ExecuteSearchAsync();
        });
    }

    private async Task ExecuteRelatedSearchAsync(string query)
    {
        _searchField.Text = query;
        await ExecuteSearchAsync();
    }

    private string CategoryTitle(SearchCategory category, int total, int loaded) =>
        $"搜索「{_lastSearchQuery}」· {CategoryName(category)} {loaded}/{Math.Max(total, loaded)}" +
        (_hasMoreSearchResults ? " · 向下滚动加载更多" : " · 已全部加载") + " · Esc 返回综合结果";

    private static string CategoryName(SearchCategory category) => category switch
    {
        SearchCategory.Song => "单曲",
        SearchCategory.Singer => "歌手",
        SearchCategory.Album => "专辑",
        SearchCategory.Playlist => "歌单",
        SearchCategory.Lyric => "歌词",
        _ => "搜索"
    };

    private static string FormatCount(long value) => value switch
    {
        >= 100_000_000 => (value / 100_000_000d).ToString("0.#亿", CultureInfo.InvariantCulture),
        >= 10_000 => (value / 10_000d).ToString("0.#万", CultureInfo.InvariantCulture),
        _ => value.ToString(CultureInfo.InvariantCulture)
    };
}
