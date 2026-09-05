using Terminal.Gui.App;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;

namespace QQMusic.Tui.UI;

public sealed partial class MainWindow
{
    private async Task ExecuteSearchAsync()
    {
        _currentViewMode = ViewMode.Search;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        var text = _searchField.Text.ToString()?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        _lastSearchQuery = text;
        _searchCurrentPage = 1;
        _hasMoreSearchResults = true;
        _isLoadingMore = false;

        _songListView.SetMessage("正在搜索...", $"正在搜索「{text}」...");

        var songs = await QqMusicApi.SearchAsync(text, 1, PageSize);

        Application.Invoke(() =>
        {
            if (songs.Count < PageSize)
            {
                _hasMoreSearchResults = false;
            }

            var title = $"搜索结果: 共 {songs.Count} 首" + (_hasMoreSearchResults ? " (向下滚动加载更多)" : " (已全部加载)");
            _songListView.SetSongs(songs, title);
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
            }
            if (songs.Count > 0)
            {
                _songListView.SetFocusToList();
            }
        });
    }

    private async Task LoadMoreSearchResultsAsync()
    {
        if (_isLoadingMore || !_hasMoreSearchResults || string.IsNullOrEmpty(_lastSearchQuery))
        {
            return;
        }

        _isLoadingMore = true;
        var nextPage = _searchCurrentPage + 1;

        Application.Invoke(() =>
        {
            _songListView.Title = $"搜索结果: 共 {_songListView.Songs.Count} 首 (正在加载更多...)";
        });

        var moreSongs = await QqMusicApi.SearchAsync(_lastSearchQuery, nextPage, PageSize);

        Application.Invoke(() =>
        {
            if (moreSongs.Count > 0)
            {
                _searchCurrentPage = nextPage;
                var currentCount = _songListView.Songs.Count;

                if (moreSongs.Count < PageSize)
                {
                    _hasMoreSearchResults = false;
                }

                var title = $"搜索结果: 共 {currentCount + moreSongs.Count} 首" + (_hasMoreSearchResults ? " (向下滚动加载更多)" : " (已全部加载)");
                _songListView.AppendSongs(moreSongs, title);
                if (_activeSong != null)
                {
                    _songListView.SetPlayingSong(_activeSong.Mid);
                }
            }
            else
            {
                _hasMoreSearchResults = false;
                _songListView.Title = $"搜索结果: 共 {_songListView.Songs.Count} 首 (已无更多结果)";
            }

            _isLoadingMore = false;
        });
    }

    private async Task LoadDailyRecommendSongsAsync()
    {
        _currentViewMode = ViewMode.DailyRecommend;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        if (!UserSession.Current.IsLoggedIn)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("请按 L 键登录后获取您的每日 30 首个性化推荐歌单", "每日30首 (未登录)");
            });
            return;
        }

        _songListView.SetMessage("正在同步今日推荐歌单（每日30首）...", "每日30首 (加载中)");
        _controlBar.UpdateStatus("[正在加载] 正在请求每日30首推荐曲目...");

        var songs = await QqMusicApi.GetDailyRecommendSongsAsync();

        Application.Invoke(() =>
        {
            if (songs.Count == 0)
            {
                _songListView.SetMessage("今日推荐歌单获取为空，请按 L 检查登录状态或稍后重试", "每日30首: 0 首");
                _controlBar.UpdateStatus("[加载提示] 未能获取到今日推荐歌单数据");
                return;
            }

            _songListView.SetSongs(songs, $"每日30首: 今日精选 {songs.Count} 首 (按 R 刷新)");
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
                var isFav = (!string.IsNullOrEmpty(_activeSong.Mid) && _favoriteSongMids.Contains(_activeSong.Mid)) ||
                            (_activeSong.Id > 0 && _favoriteSongIds.Contains(_activeSong.Id));
                _controlBar.SetFavoriteStatus(isFav);
            }
            _songListView.SetFocusToList();
            _controlBar.UpdateStatus($"[每日推荐] 今日 30 首推荐已成功载入（共 {songs.Count} 首）");
        });
    }

    private async Task LoadRecentPlaySongsAsync()
    {
        _currentViewMode = ViewMode.RecentPlay;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        var songs = RecentPlayHistory.GetSongs();
        Application.Invoke(() =>
        {
            if (songs.Count == 0)
            {
                _songListView.SetMessage("暂无最近播放记录，快去点播一首歌曲吧！", "最近播放 (0 首)");
                return;
            }

            _songListView.SetSongs(songs, $"最近播放: 共 {songs.Count} 首 (按 D 移除历史)");
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
            }
            _songListView.SetFocusToList();
        });
        _controlBar.UpdateStatus($"[最近播放] 已加载本地播放轨迹共 {songs.Count} 首");
    }

    private async Task LoadPlaylistsAsync()
    {
        _currentViewMode = ViewMode.PlaylistsList;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = true;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        if (!UserSession.Current.IsLoggedIn)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("请按 L 键登录后同步您的云端歌单列表", "我的歌单 (未登录)");
            });
            return;
        }

        _songListView.SetMessage("正在同步云端歌单列表...", "我的歌单 (加载中)");

        var playlists = await QqMusicApi.GetPlaylistsAsync();
        _cachedPlaylists = playlists;

        Application.Invoke(() =>
        {
            if (playlists.Count == 0)
            {
                _songListView.SetMessage("当前账号暂无歌单数据", "我的歌单: 0 个");
                return;
            }

            var items = new List<string>(playlists.Count);
            for (int i = 0; i < playlists.Count; i++)
            {
                var p = playlists[i];
                var typeStr = p.IsCreated ? (p.DirId == 201 ? "[我喜欢]" : "[自建]") : "[收藏]";
                items.Add($"{(i + 1):D2}  {typeStr,-6}  {p.Title}  (共 {p.SongNum} 首)");
            }

            _songListView.SetCustomItems(items, $"我的歌单: 共 {playlists.Count} 个 (按 Enter 进入歌单)", async (idx) =>
            {
                if (idx >= 0 && idx < _cachedPlaylists.Count)
                {
                    await DrilldownPlaylistAsync(_cachedPlaylists[idx]);
                }
            });
            _songListView.SetFocusToList();
        });
    }

    private async Task DrilldownPlaylistAsync(Playlist playlist)
    {
        _currentViewMode = ViewMode.PlaylistDrilldown;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = playlist;
        _playlistCurrentPage = 1;
        _hasMorePlaylistSongs = false;
        _isLoadingMorePlaylistSongs = false;

        _songListView.SetMessage($"正在加载歌单「{playlist.Title}」歌曲...", $"歌单: {playlist.Title} (加载中)");

        var songs = await QqMusicApi.GetPlaylistSongsAsync(playlist, 1, PlaylistPageSize);

        Application.Invoke(() =>
        {
            if (songs.Count == 0)
            {
                _songListView.SetMessage("该歌单暂无歌曲或已清空", $"歌单: {playlist.Title} (共 0 首，按 Esc 退回)");
                return;
            }

            _hasMorePlaylistSongs = songs.Count >= PlaylistPageSize;
            var title = $"歌单: {playlist.Title} (共 {songs.Count} 首" + (_hasMorePlaylistSongs ? "，向下滚动加载更多" : "，已全部加载") + "，按 Esc 退回)";
            _songListView.SetSongs(songs, title);
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
            }
            _songListView.SetFocusToList();
        });
    }

    private async Task LoadMorePlaylistSongsAsync()
    {
        if (_isLoadingMorePlaylistSongs || !_hasMorePlaylistSongs || _currentDrilldownPlaylist == null) return;

        _isLoadingMorePlaylistSongs = true;
        var nextPage = _playlistCurrentPage + 1;

        Application.Invoke(() =>
        {
            _songListView.Title = $"歌单: {_currentDrilldownPlaylist.Title} (正在加载更多...)";
            _controlBar.UpdateStatus($"[正在加载] 正在获取「{_currentDrilldownPlaylist.Title}」更多曲目 (第 {nextPage} 页)...");
        });

        try
        {
            var moreSongs = await QqMusicApi.GetPlaylistSongsAsync(_currentDrilldownPlaylist, nextPage, PlaylistPageSize);

            Application.Invoke(() =>
            {
                if (moreSongs.Count > 0)
                {
                    _playlistCurrentPage = nextPage;
                    var currentCount = _songListView.Songs.Count;
                    if (moreSongs.Count < PlaylistPageSize)
                    {
                        _hasMorePlaylistSongs = false;
                    }

                    var title = $"歌单: {_currentDrilldownPlaylist.Title} (共 {currentCount + moreSongs.Count} 首" + (_hasMorePlaylistSongs ? "，向下滚动加载更多" : "，已全部加载") + "，按 Esc 退回)";
                    _songListView.AppendSongs(moreSongs, title);
                    if (_activeSong != null)
                    {
                        _songListView.SetPlayingSong(_activeSong.Mid);
                    }
                    _controlBar.UpdateStatus($"[加载完成] 歌单已载入 {currentCount + moreSongs.Count} 首");
                }
                else
                {
                    _hasMorePlaylistSongs = false;
                    _songListView.Title = $"歌单: {_currentDrilldownPlaylist.Title} (共 {_songListView.Songs.Count} 首，已全部加载，按 Esc 退回)";
                    _controlBar.UpdateStatus($"[已全部加载] 歌单共 {_songListView.Songs.Count} 首曲目");
                }
            });
        }
        catch (Exception ex)
        {
            Application.Invoke(() =>
            {
                _controlBar.UpdateStatus($"[加载失败] 获取更多曲目异常: {ex.Message}");
            });
        }
        finally
        {
            _isLoadingMorePlaylistSongs = false;
        }
    }

    private async Task LoadFavoriteAlbumsAsync()
    {
        _currentViewMode = ViewMode.FavoriteAlbums;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = true;
        _currentDrilldownAlbum = null;

        if (!UserSession.Current.IsLoggedIn)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("请按 L 键登录后同步您的云端收藏专辑", "收藏专辑 (未登录)");
            });
            return;
        }

        _songListView.SetMessage("正在同步云端收藏专辑列表...", "收藏专辑 (加载中)");
        _controlBar.UpdateStatus("[正在加载] 正在获取收藏专辑列表...");

        var albums = await QqMusicApi.GetFavoriteAlbumsAsync();
        _cachedAlbums = albums;

        Application.Invoke(() =>
        {
            if (albums.Count == 0)
            {
                _songListView.SetMessage("当前账号暂无收藏专辑数据", "收藏专辑: 0 张");
                _controlBar.UpdateStatus("[加载提示] 暂无收藏专辑，快去探索发现好音乐吧！");
                return;
            }

            var items = new List<string>(albums.Count);
            for (int i = 0; i < albums.Count; i++)
            {
                var a = albums[i];
                items.Add($"{(i + 1):D2}  {a.Title}  -  {a.Artist}  (共 {a.SongCount} 首)");
            }

            _songListView.SetCustomItems(items, $"收藏专辑: 共 {albums.Count} 张 (按 Enter 进入专辑，按 D 取消收藏)", async (idx) =>
            {
                if (idx >= 0 && idx < _cachedAlbums.Count)
                {
                    await DrilldownAlbumAsync(_cachedAlbums[idx]);
                }
            }, (selectedIdx) =>
            {
                if (selectedIdx >= 0 && selectedIdx < _cachedAlbums.Count)
                {
                    _ = PreviewAlbumDetailAsync(_cachedAlbums[selectedIdx].Mid, _cachedAlbums[selectedIdx].Title, _cachedAlbums[selectedIdx].Artist);
                }
            });

            if (albums.Count > 0)
            {
                _ = PreviewAlbumDetailAsync(albums[0].Mid, albums[0].Title, albums[0].Artist);
            }

            _songListView.SetFocusToList();
            _controlBar.UpdateStatus($"[同步完成] 已载入收藏专辑共 {albums.Count} 张");
        });
    }

    private async Task DrilldownAlbumAsync(Album album)
    {
        _currentViewMode = ViewMode.AlbumDrilldown;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = album;

        _songListView.SetMessage($"正在加载专辑「{album.Title}」歌曲...", $"专辑: {album.Title} (加载中)");
        _controlBar.UpdateStatus($"[正在加载] 正在获取专辑「{album.Title}」曲目...");

        var songs = await QqMusicApi.GetAlbumSongsAsync(album.Mid);

        Application.Invoke(() =>
        {
            if (songs.Count == 0)
            {
                _songListView.SetMessage("该专辑暂无可用歌曲", $"专辑: {album.Title} (共 0 首，按 Esc 退回)");
                _controlBar.UpdateStatus($"[加载完成] 专辑「{album.Title}」无可用曲目");
                return;
            }

            var title = $"专辑: {album.Title} - {album.Artist} (共 {songs.Count} 首，按 Esc 退回)";
            _songListView.SetSongs(songs, title);
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
            }
            _songListView.SetFocusToList();
            _controlBar.UpdateStatus($"[加载完成] 专辑「{album.Title}」共载入 {songs.Count} 首曲目");
        });
    }

    /// <summary>
    /// 普通歌单列表：播放下一首
    /// </summary>
    private async Task PlayNextInCurrentListAsync(bool isAutoPlayback = false)
    {
        if (_songListView.Songs.Count == 0 || _activeSong == null) return;
        var songs = _songListView.Songs;
        int currentIdx = -1;
        for (int i = 0; i < songs.Count; i++)
        {
            if (songs[i].Mid == _activeSong.Mid)
            {
                currentIdx = i;
                break;
            }
        }

        // 随机播放模式
        if (_currentPlaybackMode == PlaybackMode.Shuffle && songs.Count > 1)
        {
            int nextIdx;
            do
            {
                nextIdx = Random.Shared.Next(songs.Count);
            } while (nextIdx == currentIdx && songs.Count > 1);

            await PlaySongAsync(songs[nextIdx]);
            return;
        }

        // 列表循环模式 (或单曲循环模式下用户主动按切歌键)
        if (_currentPlaybackMode == PlaybackMode.ListLoop || (!isAutoPlayback && _currentPlaybackMode == PlaybackMode.SingleLoop))
        {
            int nextIdx = (currentIdx + 1) % songs.Count;
            await PlaySongAsync(songs[nextIdx]);
            return;
        }

        // 顺序播放模式
        if (currentIdx >= 0 && currentIdx + 1 < songs.Count)
        {
            await PlaySongAsync(songs[currentIdx + 1]);
        }
        else if (!isAutoPlayback && currentIdx == -1 && songs.Count > 0)
        {
            await PlaySongAsync(songs[0]);
        }
        else if (isAutoPlayback && _currentPlaybackMode == PlaybackMode.Sequential)
        {
            // 顺序播放播完最后一首自动停止
            await _player.StopAsync();
            UpdatePlayerStatus();
        }
    }

    /// <summary>
    /// 普通歌单列表：播放上一首
    /// </summary>
    private async Task PlayPrevInCurrentListAsync()
    {
        if (_songListView.Songs.Count == 0 || _activeSong == null) return;
        var songs = _songListView.Songs;
        int currentIdx = -1;
        for (int i = 0; i < songs.Count; i++)
        {
            if (songs[i].Mid == _activeSong.Mid)
            {
                currentIdx = i;
                break;
            }
        }

        // 随机播放模式
        if (_currentPlaybackMode == PlaybackMode.Shuffle && songs.Count > 1)
        {
            int prevIdx;
            do
            {
                prevIdx = Random.Shared.Next(songs.Count);
            } while (prevIdx == currentIdx && songs.Count > 1);

            await PlaySongAsync(songs[prevIdx]);
            return;
        }

        // 列表循环模式 (或单曲循环模式下用户主动切上一首)
        if (_currentPlaybackMode == PlaybackMode.ListLoop || _currentPlaybackMode == PlaybackMode.SingleLoop)
        {
            int prevIdx = (currentIdx - 1 + songs.Count) % songs.Count;
            await PlaySongAsync(songs[prevIdx]);
            return;
        }

        // 顺序播放模式
        if (currentIdx > 0)
        {
            await PlaySongAsync(songs[currentIdx - 1]);
        }
    }
}
