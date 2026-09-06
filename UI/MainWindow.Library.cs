using Terminal.Gui.App;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;

namespace QQMusic.Tui.UI;

public sealed partial class MainWindow
{
    private async Task LoadFavoriteSongsAsync()
    {
        _currentViewMode = ViewMode.Favorite;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;
        _favoriteCurrentPage = 1;
        _favoriteTotalCount = 0;
        _hasMoreFavorites = false;
        _isLoadingMoreFavorites = false;

        if (!UserSession.Current.IsLoggedIn)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("请按 U 键登录后同步您的“我的喜欢”收藏歌单", "我的喜欢 (未登录)");
            });
            return;
        }

        _songListView.SetMessage("正在同步云端“我的喜欢”收藏歌曲...", "我的喜欢 (加载中)");
        _controlBar.UpdateStatus("[正在加载] 正在请求“我的喜欢”收藏歌曲列表...");

        var result = await QqMusicApi.GetFavoriteSongsAsync(1, FavoritePageSize);
        var songs = result.Songs;

        Application.Invoke(() =>
        {
            if (songs.Count == 0 && result.Total == 0)
            {
                _songListView.SetMessage("您的“我的喜欢”暂无收藏歌曲，按 S 键可收藏当前播放歌曲", "我的喜欢: 0 首");
                _controlBar.UpdateStatus("[加载提示] 暂无收藏歌曲，快去探索音乐吧！");
                return;
            }

            _favoriteTotalCount = result.Total;
            _hasMoreFavorites = result.HasMore || (_favoriteTotalCount > songs.Count);

            lock (_favoriteSongMids)
            {
                foreach (var s in songs)
                {
                    if (!string.IsNullOrEmpty(s.Mid)) _favoriteSongMids.Add(s.Mid);
                    if (s.Id > 0) _favoriteSongIds.Add(s.Id);
                }
            }

            string totalHint = _favoriteTotalCount > 0 ? $"/{_favoriteTotalCount}" : "";
            var title = $"我的喜欢: 已载入 {songs.Count}{totalHint} 首" + (_hasMoreFavorites ? "，向下滚动加载更多" : "，已全部加载");
            _songListView.SetSongs(songs, title);
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
                var isFav = (!string.IsNullOrEmpty(_activeSong.Mid) && _favoriteSongMids.Contains(_activeSong.Mid)) ||
                            (_activeSong.Id > 0 && _favoriteSongIds.Contains(_activeSong.Id));
                _controlBar.SetFavoriteStatus(isFav);
            }
            _songListView.SetFocusToList();
            _controlBar.UpdateStatus($"[同步完成] 已载入我的喜欢（{songs.Count}{totalHint} 首）");
        });
    }

    private async Task LoadMoreFavoriteSongsAsync()
    {
        if (_isLoadingMoreFavorites || !_hasMoreFavorites) return;

        _isLoadingMoreFavorites = true;
        var nextPage = _favoriteCurrentPage + 1;

        Application.Invoke(() =>
        {
            string totalHint = _favoriteTotalCount > 0 ? $"/{_favoriteTotalCount}" : "";
            _songListView.Title = $"我的喜欢: 已载入 {_songListView.Songs.Count}{totalHint} 首 (正在加载第 {nextPage} 页...)";
            _controlBar.UpdateStatus($"[正在加载] 正在获取我的喜欢更多曲目 (第 {nextPage} 页)...");
        });

        try
        {
            var result = await QqMusicApi.GetFavoriteSongsAsync(nextPage, FavoritePageSize);
            var moreSongs = result.Songs;

            Application.Invoke(() =>
            {
                if (moreSongs.Count > 0)
                {
                    _favoriteCurrentPage = nextPage;
                    if (result.Total > 0) _favoriteTotalCount = result.Total;

                    lock (_favoriteSongMids)
                    {
                        foreach (var s in moreSongs)
                        {
                            if (!string.IsNullOrEmpty(s.Mid)) _favoriteSongMids.Add(s.Mid);
                            if (s.Id > 0) _favoriteSongIds.Add(s.Id);
                        }
                    }

                    var newTotalLoaded = _songListView.Songs.Count + moreSongs.Count;
                    _hasMoreFavorites = result.HasMore || (_favoriteTotalCount > newTotalLoaded);

                    string totalHint = _favoriteTotalCount > 0 ? $"/{_favoriteTotalCount}" : "";
                    var title = $"我的喜欢: 已载入 {newTotalLoaded}{totalHint} 首" + (_hasMoreFavorites ? "，向下滚动加载更多" : "，已全部加载");
                    _songListView.AppendSongs(moreSongs, title);
                    if (_activeSong != null)
                    {
                        _songListView.SetPlayingSong(_activeSong.Mid);
                    }
                    _controlBar.UpdateStatus($"[加载完成] 我的喜欢已载入 {newTotalLoaded}{totalHint} 首");
                }
                else
                {
                    _hasMoreFavorites = false;
                    string totalHint = _favoriteTotalCount > 0 ? $"/{_favoriteTotalCount}" : "";
                    _songListView.Title = $"我的喜欢: 共 {_songListView.Songs.Count}{totalHint} 首，已全部加载";
                    _controlBar.UpdateStatus($"[已全部加载] 我的喜欢共 {_songListView.Songs.Count}{totalHint} 首曲目");
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
            _isLoadingMoreFavorites = false;
        }
    }

    private async Task ToggleSongFavoriteAsync(Song song)
    {
        if (song.IsLocal)
        {
            _controlBar.UpdateStatus("[本地音乐] 本地曲目不支持在线收藏");
            return;
        }

        if (!UserSession.Current.IsLoggedIn)
        {
            _controlBar.UpdateStatus("[未登录] 请按 U 登录后再进行收藏操作");
            return;
        }

        bool isFav = (!string.IsNullOrEmpty(song.Mid) && _favoriteSongMids.Contains(song.Mid)) ||
                     (song.Id > 0 && _favoriteSongIds.Contains(song.Id)) ||
                     (_activeSong?.Mid == song.Mid && _controlBar.IsFavorite);

        if (isFav)
        {
            _controlBar.UpdateStatus($"[正在取消收藏] 正在将《{song.Title}》从我的喜欢中移除...");
            var ok = await QqMusicApi.RemoveSongFromFavoriteAsync(song);
            if (ok)
            {
                lock (_favoriteSongMids)
                {
                    if (!string.IsNullOrEmpty(song.Mid)) _favoriteSongMids.Remove(song.Mid);
                    if (song.Id > 0) _favoriteSongIds.Remove(song.Id);
                }

                if (_activeSong?.Mid == song.Mid)
                {
                    Application.Invoke(() => _controlBar.SetFavoriteStatus(false));
                    if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
                    {
                        _standaloneWebServer.IsCurrentSongFavorite = false;
                        _standaloneWebServer.BroadcastState("favorite_change");
                    }
                }

                _controlBar.UpdateStatus($"[取消收藏成功] 已将《{song.Title}》从我的喜欢中移除");

                if (_currentViewMode == ViewMode.Favorite)
                {
                    Application.Invoke(() => _songListView.RemoveSong(song));
                }
            }
            else
            {
                _controlBar.UpdateStatus($"[操作失败] 从我的喜欢移除《{song.Title}》失败");
            }
        }
        else
        {
            _controlBar.UpdateStatus($"[正在收藏] 正在将《{song.Title}》添加至我的喜欢...");
            var ok = await QqMusicApi.AddSongToFavoriteAsync(song);
            if (ok)
            {
                lock (_favoriteSongMids)
                {
                    if (!string.IsNullOrEmpty(song.Mid)) _favoriteSongMids.Add(song.Mid);
                    if (song.Id > 0) _favoriteSongIds.Add(song.Id);
                }

                if (_activeSong?.Mid == song.Mid)
                {
                    Application.Invoke(() => _controlBar.SetFavoriteStatus(true));
                    if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
                    {
                        _standaloneWebServer.IsCurrentSongFavorite = true;
                        _standaloneWebServer.BroadcastState("favorite_change");
                    }
                }

                _controlBar.UpdateStatus($"[收藏成功] 已将《{song.Title}》添加至我的喜欢");
            }
            else
            {
                _controlBar.UpdateStatus($"[收藏失败] 添加《{song.Title}》至我的喜欢失败");
            }
        }
    }

    private async Task HandleSmartFavoriteAsync()
    {
        var focused = Application.Navigation?.GetFocused();
        int focusedWindow = GetFocusedWindowIndex(focused);

        Song? targetSong;
        if (focusedWindow == 1) // 只有聚焦中间主界面时收藏选中的歌曲
        {
            if (_isViewingPlaylistsList || _isViewingAlbumsList)
            {
                _controlBar.UpdateStatus("[操作提示] 当前在目录视图，请进入歌曲列表后再按 S 收藏");
                return;
            }

            targetSong = _songListView.GetSelectedSong();
            if (targetSong == null)
            {
                _controlBar.UpdateStatus("[操作提示] 当前歌曲列表中未选中任何曲目");
                return;
            }
        }
        else // 其它界面（边栏、歌词、底栏、全屏播放界面等）均收藏当前正在播放的曲目
        {
            targetSong = _activeSong;
            if (targetSong == null)
            {
                _controlBar.UpdateStatus("[操作提示] 当前暂无播放曲目，请先点播歌曲");
                return;
            }
        }

        if (targetSong.IsLocal)
        {
            _controlBar.UpdateStatus("[本地音乐] 本地曲目不支持在线收藏");
            return;
        }

        await ToggleSongFavoriteAsync(targetSong);
    }

    private async Task HandleRemoveFromCurrentListAsync()
    {
        if (_currentViewMode == ViewMode.GuessRecommend)
        {
            _controlBar.UpdateStatus("[电台切歌] 不喜欢当前曲目，已切至下一首");
            await PlayNextRadioTrackAsync();
            return;
        }

        if (_isViewingPlaylistsList)
        {
            _controlBar.UpdateStatus("[操作提示] 当前在歌单目录，请在歌曲列表中按 D 移除歌曲");
            return;
        }

        if (_isViewingAlbumsList)
        {
            await HandleRemoveAlbumFromFavoriteAsync();
            return;
        }

        var song = _songListView.GetSelectedSong();
        if (song == null)
        {
            _controlBar.UpdateStatus("[操作提示] 请先选择要移除的歌曲");
            return;
        }

        if (_currentViewMode == ViewMode.RecentPlay)
        {
            RecentPlayHistory.Remove(song);
            _songListView.RemoveSong(song);
            _controlBar.UpdateStatus($"[移除成功] 已从最近播放记录中移除《{song.Title}》");
            return;
        }

        if (!UserSession.Current.IsLoggedIn)
        {
            _controlBar.UpdateStatus("[未登录] 请按 U 登录后操作");
            return;
        }

        var sidebarIdx = _sidebarList.SelectedItem ?? 0;
        bool isFavoriteList = (sidebarIdx == 1 && _currentDrilldownPlaylist == null) || (_currentDrilldownPlaylist?.DirId == 201);

        if (isFavoriteList)
        {
            _controlBar.UpdateStatus($"[正在移除] 正在从我的喜欢中移除《{song.Title}》...");
            var ok = await QqMusicApi.RemoveSongFromFavoriteAsync(song);
            if (ok)
            {
                lock (_favoriteSongMids)
                {
                    if (!string.IsNullOrEmpty(song.Mid)) _favoriteSongMids.Remove(song.Mid);
                    if (song.Id > 0) _favoriteSongIds.Remove(song.Id);
                }

                if (_activeSong?.Mid == song.Mid)
                {
                    Application.Invoke(() => _controlBar.SetFavoriteStatus(false));
                }

                Application.Invoke(() =>
                {
                    _songListView.RemoveSong(song);
                    _controlBar.UpdateStatus($"[移除成功] 已从我的喜欢中移除《{song.Title}》");
                });
            }
            else
            {
                _controlBar.UpdateStatus($"[移除失败] 从我的喜欢中移除《{song.Title}》失败");
            }
            return;
        }

        if (_currentDrilldownPlaylist != null)
        {
            if (!_currentDrilldownPlaylist.IsCreated)
            {
                _controlBar.UpdateStatus("[无法移除] 收藏的他人的外部歌单不支持单曲删除");
                return;
            }

            _controlBar.UpdateStatus($"[正在移除] 正在从歌单「{_currentDrilldownPlaylist.Title}」移除《{song.Title}》...");
            var ok = await QqMusicApi.RemoveSongFromPlaylistAsync(_currentDrilldownPlaylist, song);
            if (ok)
            {
                Application.Invoke(() =>
                {
                    _songListView.RemoveSong(song);
                    _controlBar.UpdateStatus($"[移除成功] 已从歌单「{_currentDrilldownPlaylist.Title}」移除《{song.Title}》");
                });
            }
            else
            {
                _controlBar.UpdateStatus($"[移除失败] 从歌单中移除《{song.Title}》失败");
            }
            return;
        }

        _controlBar.UpdateStatus("[操作提示] 只能在“我的喜欢”或自建歌单中移除歌曲");
    }

    private async Task HandleAddToPlaylistAsync()
    {
        if (_isViewingPlaylistsList)
        {
            _controlBar.UpdateStatus("[操作提示] 当前在歌单目录，请在歌曲列表中按 A 添至歌单");
            return;
        }

        var song = _songListView.GetSelectedSong();
        if (song == null)
        {
            _controlBar.UpdateStatus("[操作提示] 请先选择要添加的歌曲");
            return;
        }

        if (!UserSession.Current.IsLoggedIn)
        {
            _controlBar.UpdateStatus("[未登录] 请按 U 登录后再添加至歌单");
            return;
        }

        // 获取或复用可写的自建歌单列表
        List<Playlist> playlists = _cachedPlaylists;
        if (playlists.Count == 0)
        {
            _controlBar.UpdateStatus("[正在获取] 正在同步自建歌单列表...");
            playlists = await QqMusicApi.GetPlaylistsAsync();
            _cachedPlaylists = playlists;
        }

        var writable = playlists.Where(p => p.IsCreated).ToList();
        if (writable.Count == 0)
        {
            _controlBar.UpdateStatus("[暂无自建歌单] 当前账号无可用自建歌单");
            return;
        }

        var dlg = new AddToPlaylistDialog(song, writable, async (targetPlaylist) =>
        {
            _controlBar.UpdateStatus($"[正在添加] 正在将《{song.Title}》加入「{targetPlaylist.Title}」...");
            var ok = await QqMusicApi.AddSongToPlaylistAsync(targetPlaylist, song);
            Application.Invoke(() =>
            {
                if (ok)
                {
                    _controlBar.UpdateStatus($"[添加成功] 已将《{song.Title}》加入「{targetPlaylist.Title}」");
                }
                else
                {
                    _controlBar.UpdateStatus($"[添加失败] 添加至「{targetPlaylist.Title}」失败，请重试");
                }
            });
        });

        Application.Run(dlg);
    }

    private async Task HandleRemoveAlbumFromFavoriteAsync()
    {
        if (!UserSession.Current.IsLoggedIn)
        {
            _controlBar.UpdateStatus("[未登录] 请按 U 登录后操作");
            return;
        }

        var idx = _songListView.SelectedItem ?? -1;
        if (idx < 0 || idx >= _cachedAlbums.Count)
        {
            _controlBar.UpdateStatus("[操作提示] 请先选中要取消收藏的专辑");
            return;
        }

        var album = _cachedAlbums[idx];
        _controlBar.UpdateStatus($"[正在取消收藏] 正在取消收藏专辑《{album.Title}》...");

        var ok = await QqMusicApi.RemoveAlbumFromFavoriteAsync(album.Mid);
        if (ok)
        {
            _cachedAlbums.RemoveAt(idx);
            Application.Invoke(() =>
            {
                if (_cachedAlbums.Count == 0)
                {
                    _songListView.SetMessage("当前账号暂无收藏专辑数据", "收藏专辑: 0 张");
                }
                else
                {
                    var items = new List<string>(_cachedAlbums.Count);
                    for (int i = 0; i < _cachedAlbums.Count; i++)
                    {
                        var a = _cachedAlbums[i];
                        items.Add($"{(i + 1):D2}  {a.Title}  -  {a.Artist}  (共 {a.SongCount} 首)");
                    }

                    _songListView.SetCustomItems(items, $"收藏专辑: 共 {_cachedAlbums.Count} 张 (按 Enter 进入专辑，按 D 取消收藏)", async (newIdx) =>
                    {
                        if (newIdx >= 0 && newIdx < _cachedAlbums.Count)
                        {
                            await DrilldownAlbumAsync(_cachedAlbums[newIdx]);
                        }
                    }, (selectedIdx) =>
                    {
                        if (selectedIdx >= 0 && selectedIdx < _cachedAlbums.Count)
                        {
                            _ = PreviewAlbumDetailAsync(_cachedAlbums[selectedIdx].Mid, _cachedAlbums[selectedIdx].Title, _cachedAlbums[selectedIdx].Artist);
                        }
                    });
                }
                _controlBar.UpdateStatus($"[取消收藏成功] 已取消收藏专辑《{album.Title}》");
            });
        }
        else
        {
            Application.Invoke(() =>
            {
                _controlBar.UpdateStatus($"[操作失败] 取消收藏专辑《{album.Title}》失败，请稍后重试");
            });
        }
    }

    private async Task LoadLocalMusicAsync()
    {
        _currentViewMode = ViewMode.LocalMusic;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        var folders = QQMusic.Tui.Services.LocalMusicService.GetFolders();
        if (folders.Count == 0)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("本地音乐库为空 (请按 A 键添加本地音乐文件夹进行扫描，按 F 管理目录)", "本地音乐: 0 首");
                _controlBar.UpdateStatus("[本地音乐] 未配置扫描目录，请按 A 键添加本地音乐目录");
            });
            return;
        }

        var cachedSongs = QQMusic.Tui.Services.LocalMusicService.GetCachedSongs();
        if (cachedSongs.Count > 0)
        {
            Application.Invoke(() =>
            {
                var title = $"本地音乐: 共 {cachedSongs.Count} 首 (按 A 添加目录，按 R 重新扫描，按 F 管理目录)";
                _songListView.SetSongs(cachedSongs, title);
                if (_activeSong != null)
                {
                    _songListView.SetPlayingSong(_activeSong.Mid);
                }
                _songListView.SetFocusToList();
                _controlBar.UpdateStatus($"[本地音乐] 已载入 {cachedSongs.Count} 首本地音乐 (共 {folders.Count} 个扫描目录)");
            });
        }
        else
        {
            await RescanLocalMusicAsync();
        }
    }

    private async Task RescanLocalMusicAsync()
    {
        _currentViewMode = ViewMode.LocalMusic;
        var folders = QQMusic.Tui.Services.LocalMusicService.GetFolders();
        if (folders.Count == 0)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("本地音乐库为空 (请按 A 键添加本地音乐文件夹进行扫描，按 F 管理目录)", "本地音乐: 0 首");
                _controlBar.UpdateStatus("[本地音乐] 未配置扫描目录，请按 A 键添加本地音乐目录");
            });
            return;
        }

        Application.Invoke(() =>
        {
            _songListView.SetMessage("正在扫描本地音乐目录 (递归检索音频文件并跳过隐藏项)...", "本地音乐 (扫描中)");
            _controlBar.UpdateStatus("[正在扫描] 正在深度检索本地音频文件元数据...");
        });

        var songs = await QQMusic.Tui.Services.LocalMusicService.ScanAllFoldersAsync(progress =>
        {
            Application.Invoke(() =>
            {
                _controlBar.UpdateStatus($"[正在扫描] {progress}");
            });
        });

        Application.Invoke(() =>
        {
            if (songs.Count == 0)
            {
                _songListView.SetMessage("已配置的目录中未发现音频文件 (按 A 键添加其它文件夹，按 F 管理目录)", "本地音乐: 0 首");
                _controlBar.UpdateStatus("[扫描完成] 未发现有效音频文件，请确认目录中包含 .flac/.mp3/.m4a 等文件");
                return;
            }

            var title = $"本地音乐: 共 {songs.Count} 首 (按 A 添加目录，按 R 重新扫描，按 F 管理目录)";
            _songListView.SetSongs(songs, title);
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
            }
            _songListView.SetFocusToList();
            _controlBar.UpdateStatus($"[扫描完成] 已发现并载入 {songs.Count} 首本地音乐 (共 {folders.Count} 个扫描目录)");
        });
    }

    private void ShowAddFolderDialog()
    {
        var dlg = new AddFolderDialog(async folder =>
        {
            var added = QQMusic.Tui.Services.LocalMusicService.AddFolder(folder);
            if (added)
            {
                _controlBar.UpdateStatus($"[已添加目录] {folder}，正在触发扫描...");
                await RescanLocalMusicAsync();
            }
            else
            {
                _controlBar.UpdateStatus($"[添加失败] 目录不存在或已被添加: {folder}");
            }
        });
        Application.Run(dlg);
    }

    private void ShowFolderManageDialog()
    {
        var dlg = new FolderManageDialog(
            onFoldersChanged: () =>
            {
                var cached = QQMusic.Tui.Services.LocalMusicService.GetCachedSongs();
                if (_currentViewMode == ViewMode.LocalMusic)
                {
                    if (cached.Count == 0)
                    {
                        _songListView.SetMessage("本地音乐库为空 (请按 A 键添加本地音乐文件夹进行扫描，按 F 管理目录)", "本地音乐: 0 首");
                    }
                    else
                    {
                        var folders = QQMusic.Tui.Services.LocalMusicService.GetFolders();
                        var title = $"本地音乐: 共 {cached.Count} 首 (按 A 添加目录，按 R 重新扫描，按 F 管理目录)";
                        _songListView.SetSongs(cached, title);
                    }
                }
            },
            onAddRequested: () =>
            {
                ShowAddFolderDialog();
            },
            onRescanRequested: () =>
            {
                _ = RescanLocalMusicAsync();
            }
        );
        Application.Run(dlg);
    }
}
