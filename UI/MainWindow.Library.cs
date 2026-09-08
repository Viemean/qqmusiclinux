using Terminal.Gui.App;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;
using QQMusic.Tui.Utils;

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
        if (song.IsLocal || song.IsWebDav)
        {
            _controlBar.UpdateStatus("[私有音乐] 本地/WebDAV 曲目不支持在线收藏");
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

        if (targetSong.IsLocal || targetSong.IsWebDav)
        {
            _controlBar.UpdateStatus("[私有音乐] 本地/WebDAV 曲目不支持在线收藏");
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

    private bool _isScanningLocalMusic = false;

    private async Task RescanLocalMusicAsync()
    {
        if (_isScanningLocalMusic)
        {
            _controlBar.UpdateStatus("[本地音乐] 正在扫描中，请稍候...");
            return;
        }

        _isScanningLocalMusic = true;
        try
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
        finally
        {
            _isScanningLocalMusic = false;
        }
    }

    private void ShowAddFolderDialog()
    {
        using var dlg = new AddFolderDialog(async folder =>
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
        using var dlg = new FolderManageDialog(
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

    // ==================== WebDAV 远程私有云音乐支持 ====================
    private bool _isWebDavFlatMode = true;
    private bool _isImportingWebDav = false;
    private bool _isScanningWebDavMetadata = false;
    private string _currentWebDavHref = "";
    private readonly Stack<string> _webDavPathHistory = new();
    private List<WebDavItem> _currentWebDavItems = [];

    private async Task LoadWebDavMusicAsync()
    {
        _currentViewMode = ViewMode.WebDav;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        var server = WebDavService.GetActiveServer();
        if (server == null)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("未配置 WebDAV 服务器 (请按 F 键管理/添加 WebDAV 站点)", "WebDAV: 未连接");
                _controlBar.UpdateStatus("[WebDAV] 未配置私有云站点，请按 F 键添加站点");
            });
            return;
        }

        if (_isWebDavFlatMode)
        {
            LoadWebDavFlatLibrary(server);
        }
        else
        {
            if (string.IsNullOrEmpty(_currentWebDavHref))
            {
                _currentWebDavHref = string.IsNullOrEmpty(server.RootPath) ? "/" : server.RootPath;
                _webDavPathHistory.Clear();
            }
            await LoadWebDavDirectoryAsync(server, _currentWebDavHref);
        }
    }

    private void LoadWebDavFlatLibrary(WebDavServer server)
    {
        var cached = server.CachedSongs ?? [];
        if (cached.Count == 0)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("WebDAV 曲库为空 (按 D 切换至目录树后按 A 导入目录，按 F 管理站点)", $"WebDAV: {server.Name} (0 首)");
                _controlBar.UpdateStatus($"[WebDAV · 平铺曲库] 当前站点 [{server.Name}] 暂无导入歌曲，按 D 切换到目录树");
            });
            return;
        }

        var rawSongs = cached.Select(c => WebDavService.ToSongModel(server, c));
        var songs = WebDavService.DeduplicateSongs(rawSongs);

        Application.Invoke(() =>
        {
            var dupNotice = songs.Count < cached.Count ? $"已智能去重 {cached.Count - songs.Count} 首重复镜像，" : "";
            var title = $"WebDAV 曲库: {server.Name} ({dupNotice}共 {songs.Count} 首，按 S 嗅探歌曲信息，按 A 导入目录，按 D 切目录树，按 F 管理)";
            _songListView.SetSongs(songs, title);
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
            }
            _songListView.SetFocusToList();
            _controlBar.UpdateStatus($"[WebDAV · 平铺曲库] 已载入 {songs.Count} 首歌曲 (站点: {server.Name})");
        });
    }

    private async Task LoadWebDavDirectoryAsync(WebDavServer server, string href)
    {
        Application.Invoke(() =>
        {
            _songListView.SetMessage("正在通过 WebDAV PROPFIND 读取远程目录清单...", $"WebDAV: {server.Name} (加载中)");
            _controlBar.UpdateStatus($"[WebDAV] 正在获取目录: {href}");
        });

        var items = await WebDavService.ListDirectoryAsync(server, href);
        _currentWebDavItems = items;

        var displayLines = new List<string>();
        bool hasParentBack = _webDavPathHistory.Count > 0 || (!string.IsNullOrEmpty(href) && href != "/" && href != server.RootPath);
        if (hasParentBack)
        {
            displayLines.Add("[📁 .. (返回上级目录)]");
        }

        foreach (var it in items)
        {
            if (it.IsDirectory)
            {
                displayLines.Add($"📁 {it.Name}/");
            }
            else
            {
                var sizeMb = it.ContentLength > 0 ? $" ({it.ContentLength / 1024 / 1024.0:F1}MB)" : "";
                displayLines.Add($"🎵 {it.Name}{sizeMb}");
            }
        }

        Application.Invoke(() =>
        {
            if (displayLines.Count == 0)
            {
                _songListView.SetMessage("当前 WebDAV 目录为空或未发现音频文件 (按 A 导入，按 F 管理站点)", $"WebDAV: {href}");
                _controlBar.UpdateStatus($"[WebDAV] 目录为空: {href}");
                return;
            }

            var title = $"WebDAV 目录: {href} (按 Enter 打开/播放，按 A 扫描本目录/选中项，按 D 切平铺曲库，按 F 管理)";
            _songListView.SetCustomItems(
                displayLines,
                title,
                onAccepted: async idx =>
                {
                    await HandleWebDavItemAcceptedAsync(server, idx, hasParentBack);
                }
            );
            _songListView.SetFocusToList();
            _controlBar.UpdateStatus($"[WebDAV] 目录就绪: {href} (共 {items.Count} 项)");
        });
    }

    private async Task HandleWebDavItemAcceptedAsync(WebDavServer server, int clickedIndex, bool hasParentBack)
    {
        if (hasParentBack && clickedIndex == 0)
        {
            await NavigateUpWebDavFolderAsync();
            return;
        }

        int itemIdx = hasParentBack ? clickedIndex - 1 : clickedIndex;
        if (itemIdx < 0 || itemIdx >= _currentWebDavItems.Count) return;

        var targetItem = _currentWebDavItems[itemIdx];
        if (targetItem.IsDirectory)
        {
            _webDavPathHistory.Push(_currentWebDavHref);
            _currentWebDavHref = targetItem.Href;
            await LoadWebDavDirectoryAsync(server, _currentWebDavHref);
        }
        else
        {
            // 点击音频文件：将当前目录下所有音频文件作为当前待播歌单
            var audioItems = _currentWebDavItems.Where(it => !it.IsDirectory).ToList();
            var songs = audioItems.Select(it => WebDavService.ToSongModel(server, it)).ToList();
            int curPlayIdx = audioItems.IndexOf(targetItem);
            if (curPlayIdx < 0) curPlayIdx = 0;

            if (songs.Count > 0)
            {
                PlaybackQueueService.Instance.Mode = _currentPlaybackMode;
                PlaybackQueueService.Instance.SetQueue(songs, curPlayIdx);
                await PlaySongAsync(songs[curPlayIdx]);
            }
        }
    }

    private async Task ImportCurrentWebDavFolderAsync()
    {
        var server = WebDavService.GetActiveServer();
        if (server == null) return;

        if (_isImportingWebDav)
        {
            _controlBar.UpdateStatus("[WebDAV] 正在深度检索并导入目录中，请稍候...");
            return;
        }

        _isImportingWebDav = true;
        try
        {
            string targetHref = _currentWebDavHref;
            string targetDisplayName = _currentWebDavHref;

            if (!_isWebDavFlatMode)
            {
                bool hasParentBack = _webDavPathHistory.Count > 0 || (!string.IsNullOrEmpty(_currentWebDavHref) && _currentWebDavHref != "/" && _currentWebDavHref != server.RootPath);
                int selectedIdx = _songListView.SelectedItem ?? -1;
                int itemIdx = hasParentBack ? selectedIdx - 1 : selectedIdx;

                if (itemIdx >= 0 && itemIdx < _currentWebDavItems.Count)
                {
                    var selectedItem = _currentWebDavItems[itemIdx];
                    if (selectedItem.IsDirectory)
                    {
                        targetHref = selectedItem.Href;
                        targetDisplayName = $"{selectedItem.Name}/";
                    }
                }

                if (string.IsNullOrEmpty(targetHref)) targetHref = "/";
                if (string.IsNullOrEmpty(targetDisplayName)) targetDisplayName = targetHref;
            }
            else
            {
                targetHref = server.RootPath ?? "/";
                targetDisplayName = "全部曲库";
            }

            _controlBar.UpdateStatus($"[WebDAV] 正在增量扫描目录: {targetDisplayName} ...");
            long lastRefreshTick = Environment.TickCount64;

            var count = await WebDavService.ScanFolderRecursiveAsync(server, targetHref, prog =>
            {
                Application.Invoke(() =>
                {
                    _controlBar.UpdateStatus($"[WebDAV 增量扫描] {prog}");
                    if (_isWebDavFlatMode && Environment.TickCount64 - lastRefreshTick > 1000)
                    {
                        lastRefreshTick = Environment.TickCount64;
                        LoadWebDavFlatLibrary(server);
                    }
                });
            });

            _isWebDavFlatMode = true;
            LoadWebDavFlatLibrary(server);
            _controlBar.UpdateStatus($"[WebDAV] 扫描完成！曲库现有 {server.CachedSongs?.Count ?? count} 首音频，已切换至平铺曲库 (按 D 可切回目录树)");
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainWindow.Library", $"WebDAV import failed: {ex.Message}");
            _controlBar.UpdateStatus($"[WebDAV] 导入失败: {ex.Message}");
        }
        finally
        {
            _isImportingWebDav = false;
        }
    }

    private CancellationTokenSource? _webDavScanCts;

    private async Task ScanWebDavMetadataAsync()
    {
        var server = WebDavService.GetActiveServer();
        if (server == null) return;

        if (_isScanningWebDavMetadata)
        {
            _controlBar.UpdateStatus("[WebDAV] 正在嗅探/同步歌曲元数据中，请勿重复操作...");
            return;
        }

        _isScanningWebDavMetadata = true;
        _webDavScanCts?.Dispose();
        _webDavScanCts = new CancellationTokenSource();
        var ct = _webDavScanCts.Token;

        try
        {
            _controlBar.UpdateStatus("[WebDAV 元数据嗅探] 正在启动 8 线程 HTTP Range 头部嗅探...");
            long lastRefreshTick = Environment.TickCount64;

            int enriched = await WebDavService.BatchEnrichMetadataHeadersAsync(server, (songInfo, cur, total) =>
            {
                Application.Invoke(() =>
                {
                    _controlBar.UpdateStatus($"[WebDAV 元数据嗅探] {cur}/{total} : {songInfo}");
                    if (_isWebDavFlatMode && Environment.TickCount64 - lastRefreshTick > 1000)
                    {
                        lastRefreshTick = Environment.TickCount64;
                        LoadWebDavFlatLibrary(server);
                    }
                });
            }, ct);

            _controlBar.UpdateStatus($"[WebDAV] 元数据嗅探完成！共成功识别 {enriched} 首歌曲信息");
            if (_isWebDavFlatMode)
            {
                LoadWebDavFlatLibrary(server);
            }
        }
        catch (OperationCanceledException)
        {
            _controlBar.UpdateStatus("[WebDAV] 元数据嗅探任务已停止");
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainWindow.Library", $"WebDAV metadata scan failed: {ex.Message}");
            _controlBar.UpdateStatus($"[WebDAV] 元数据嗅探失败: {ex.Message}");
        }
        finally
        {
            _isScanningWebDavMetadata = false;
            _webDavScanCts?.Dispose();
            _webDavScanCts = null;
        }
    }

    private async Task ToggleWebDavViewModeAsync()
    {
        _isWebDavFlatMode = !_isWebDavFlatMode;
        await LoadWebDavMusicAsync();
    }

    private async Task RefreshWebDavAsync()
    {
        var server = WebDavService.GetActiveServer();
        if (server == null) return;

        if (_isWebDavFlatMode)
        {
            LoadWebDavFlatLibrary(server);
        }
        else
        {
            await LoadWebDavDirectoryAsync(server, _currentWebDavHref);
        }
    }

    private async Task NavigateUpWebDavFolderAsync()
    {
        var server = WebDavService.GetActiveServer();
        if (server == null) return;

        if (_webDavPathHistory.Count > 0)
        {
            _currentWebDavHref = _webDavPathHistory.Pop();
        }
        else
        {
            var trimmed = _currentWebDavHref.TrimEnd('/');
            var lastSlash = trimmed.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                _currentWebDavHref = trimmed[..lastSlash];
                if (string.IsNullOrEmpty(_currentWebDavHref)) _currentWebDavHref = "/";
            }
            else
            {
                _currentWebDavHref = string.IsNullOrEmpty(server.RootPath) ? "/" : server.RootPath;
            }
        }
        await LoadWebDavDirectoryAsync(server, _currentWebDavHref);
    }

    private void ShowWebdavManageDialog()
    {
        using var dlg = new WebdavManageDialog(
            onServersChanged: () =>
            {
                if (_currentViewMode == ViewMode.WebDav)
                {
                    _ = LoadWebDavMusicAsync();
                }
            },
            onSelectAndOpen: server =>
            {
                _currentWebDavHref = string.IsNullOrEmpty(server.RootPath) ? "/" : server.RootPath;
                _webDavPathHistory.Clear();
                _ = LoadWebDavMusicAsync();
            }
        );
        Application.Run(dlg);
        _songListView.SetFocusToList();
    }
}
