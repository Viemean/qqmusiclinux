using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Views;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.UI;

public sealed partial class MainWindow
{

    private sealed record PageNavigationSnapshot(
        ViewMode ViewMode,
        string StatusTitle,
        List<Song> Songs,
        int SelectedIndex,
        bool WasDetailView,
        SingerSubMode? SingerSubMode = null,
        string? SingerMid = null,
        string? SingerName = null,
        ArtistDetail? SingerDetail = null,
        string? SingerCoverPath = null,
        List<Album>? SingerAlbums = null,
        List<Song>? SingerCachedSongs = null,
        int SingerSongOrder = 1
    );

    private readonly Stack<PageNavigationSnapshot> _navigationStack = new();

    private string _currentSingerMid = "";
    private string _currentSingerName = "";
    private ArtistDetail? _currentSingerDetail;
    private string? _currentSingerCoverPath;
    private SingerSubMode _singerSubMode = SingerSubMode.Songs;
    private int _singerSongOrder = 1; // 1: 热门, 0: 最新
    private const int SingerSongPageSize = 60;
    private bool _hasMoreSingerSongs = false;
    private bool _isLoadingMoreSingerSongs = false;
    private bool _hasMoreSingerAlbums = false;
    private bool _isLoadingMoreSingerAlbums = false;
    private List<Album> _singerAlbums = [];
    private List<Song> _singerCachedSongs = [];

    private void OnArtistClicked(Song song)
    {
        if (song.IsLocal || song.IsWebDav)
        {
            _controlBar.UpdateStatus("本地/WebDAV 曲目暂不支持查看在线歌手主页");
            return;
        }

        var singers = ExtractArtistsFromSong(song);

        if (singers.Count > 1)
        {
            Application.Invoke(async () =>
            {
                var dlg = new SelectArtistDialog(singers);
                Application.Run(dlg);
                if (dlg.SelectedArtist != null)
                {
                    await DrilldownToArtistAsync(dlg.SelectedArtist);
                }
            });
            return;
        }

        if (singers.Count == 1)
        {
            _ = DrilldownToArtistAsync(singers[0]);
            return;
        }

        _ = DrilldownToArtistAsync(new ArtistInfo(song.Artist, "", 0));
    }

    private void HandleNowPlayingArtistClicked(Song song)
    {
        if (song.IsLocal || song.IsWebDav)
        {
            _controlBar.UpdateStatus("本地/WebDAV 曲目暂不支持查看在线歌手主页");
            return;
        }

        var singers = ExtractArtistsFromSong(song);
        if (singers.Count > 1)
        {
            // 在播放界面的右侧歌词区域弹出多歌手选择窗口，避开左侧封面图层
            var dlg = new SelectArtistDialog(singers, inLyricArea: true);
            Application.Run(dlg);
            if (dlg.SelectedArtist != null)
            {
                CloseNowPlayingView();
                _ = DrilldownToArtistAsync(dlg.SelectedArtist);
            }
            return;
        }

        CloseNowPlayingView();
        var targetArtist = singers.Count == 1 ? singers[0] : new ArtistInfo(song.Artist, "", 0);
        _ = DrilldownToArtistAsync(targetArtist);
    }

    private static List<ArtistInfo> ExtractArtistsFromSong(Song song)
    {
        var singers = new List<ArtistInfo>(song.Singers);
        if (singers.Count == 0 && !string.IsNullOrWhiteSpace(song.Artist))
        {
            if (song.Artist.Contains('/'))
            {
                var splitNames = song.Artist.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                singers.AddRange(splitNames.Select(n => new ArtistInfo(n, "", 0)));
            }
            else
            {
                singers.Add(new ArtistInfo(song.Artist, "", 0));
            }
        }
        return singers;
    }

    private void OnAlbumClicked(Song song)
    {
        if (song.IsLocal || song.IsWebDav)
        {
            _controlBar.UpdateStatus("本地/WebDAV 曲目暂不支持查看在线专辑主页");
            return;
        }

        if (string.IsNullOrWhiteSpace(song.AlbumMid))
        {
            _controlBar.UpdateStatus($"[专辑提示] 《{song.Title}》无可用在线专辑信息");
            return;
        }

        _ = DrilldownToAlbumAsync(song.AlbumMid, song.Album, song.Artist);
    }

    private async Task DrilldownToArtistAsync(ArtistInfo artist)
    {
        try
        {
            PushCurrentNavigationSnapshot();
            _currentViewMode = ViewMode.ArtistDetail;

            _singerSubMode = SingerSubMode.Songs;
            _singerSongOrder = 1;
            _hasMoreSingerSongs = false;
            _isLoadingMoreSingerSongs = false;
            _singerAlbums.Clear();
            _singerCachedSongs.Clear();
            _currentSingerMid = artist.Mid;
            _currentSingerName = artist.Name;

            TerminalImageHelper.ClearImages();
            _lyricListView.Visible = false;
            _lyricTransBtn.Visible = false;
            _lyricImmersiveBtn.Visible = false;
            _lyricMatchBtn.Visible = false;
            _artistAlbumDetailView.Visible = true;
            UpdateLyricTitle($"歌手 - {artist.Name}");

            _songListView.SetSongs([], $"正在加载歌手【{artist.Name}】详细资料与热门作品...");

            // 支持通过歌手名字在缺失 mid/id 时自动检索补全
            var detail = await QqMusicApi.GetSingerDetailAsync(artist.Mid, artist.Id, artist.Name).ConfigureAwait(false);
            string? coverPath = null;
            if (TerminalImageHelper.IsImageSupported)
            {
                if (detail != null && !string.IsNullOrEmpty(detail.Mid))
                {
                    coverPath = await TerminalImageHelper.EnsureSingerCoverAsync(detail.Mid).ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(artist.Mid))
                {
                    coverPath = await TerminalImageHelper.EnsureSingerCoverAsync(artist.Mid).ConfigureAwait(false);
                }
            }

            Application.Invoke(() =>
            {
                try
                {
                    if (detail == null)
                    {
                        _songListView.SetSongs([], $"未能获取歌手【{artist.Name}】详细资料 (按 Esc 返回)");
                        return;
                    }

                    _currentSingerMid = detail.Mid;
                    _currentSingerName = detail.Name;
                    _currentSingerDetail = detail;
                    _currentSingerCoverPath = coverPath;
                    _singerCachedSongs = [.. detail.Songs];
                    _hasMoreSingerSongs = detail.Songs.Count >= 30;

                    bool isFav = !string.IsNullOrEmpty(detail.Mid) && UserSession.Current.FavoriteSingers.Contains(detail.Mid);
                    _artistAlbumDetailView.SetArtist(detail, coverPath, isFav, _singerSubMode, _singerSongOrder);
                    _artistAlbumDetailView.OnActivated();

                    var title = $"歌手: {detail.Name} - 热门作品 (共 {detail.Songs.Count} 首" +
                        (_hasMoreSingerSongs ? "，向下滚动加载更多" : "，已全部加载") + "，按 Esc 返回)";
                    _songListView.SetSongs(detail.Songs, title);
                    _songListView.SetFocusToList();
                    SetNeedsDraw();
                }
                catch (Exception ex)
                {
                    AppLogger.Error("MainWindow", $"Failed to render artist detail for {artist.Name}", ex);
                    _controlBar.UpdateStatus($"[歌手渲染失败] {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainWindow", $"DrilldownToArtistAsync failed for {artist.Name}", ex);
            Application.Invoke(() =>
            {
                _controlBar.UpdateStatus($"[歌手加载失败] {ex.Message}");
                _songListView.SetSongs([], $"歌手【{artist.Name}】加载异常 (按 Esc 返回)");
            });
        }
    }

    public async Task ToggleSingerSubModeAsync()
    {
        if (string.IsNullOrEmpty(_currentSingerMid)) return;

        if (_singerSubMode == SingerSubMode.Songs)
        {
            // 切换到专辑模式
            _singerSubMode = SingerSubMode.Albums;
            _controlBar.UpdateStatus($"[模式切换] 正在获取歌手【{_currentSingerName}】专辑列表...");

            if (_singerAlbums.Count == 0)
            {
                _singerAlbums = await QqMusicApi.GetSingerAlbumListAsync(_currentSingerMid, 0, 30);
                _hasMoreSingerAlbums = _singerAlbums.Count >= 30;
            }

            Application.Invoke(() =>
            {
                bool isFav = !string.IsNullOrEmpty(_currentSingerMid) && UserSession.Current.FavoriteSingers.Contains(_currentSingerMid);
                _artistAlbumDetailView.UpdateSingerActions(_singerSubMode, _singerSongOrder, isFav);
                RenderSingerAlbumsView(0);
            });
        }
        else
        {
            // 切换回歌曲模式
            _singerSubMode = SingerSubMode.Songs;
            Application.Invoke(() =>
            {
                if (_currentSingerDetail != null)
                {
                    bool isFav = !string.IsNullOrEmpty(_currentSingerMid) && UserSession.Current.FavoriteSingers.Contains(_currentSingerMid);
                    _artistAlbumDetailView.SetArtist(_currentSingerDetail, _currentSingerCoverPath, isFav, _singerSubMode, _singerSongOrder);
                    _artistAlbumDetailView.OnActivated();
                }

                var orderText = _singerSongOrder == 1 ? "热门" : "最新";
                var title = $"歌手: {_currentSingerName} - {orderText}作品 (共 {_singerCachedSongs.Count} 首" +
                    (_hasMoreSingerSongs ? "，向下滚动加载更多" : "，已全部加载") + "，按 Esc 返回)";
                _songListView.SetSongs(_singerCachedSongs, title);
                if (_activeSong != null)
                {
                    _songListView.SetPlayingSong(_activeSong.Mid);
                }
                _songListView.SetFocusToList();
                _controlBar.UpdateStatus($"[模式切换] 已切回歌手【{_currentSingerName}】的{orderText}作品列表");
            });
        }
    }

    public async Task ToggleSingerSongOrderAsync()
    {
        if (_singerSubMode != SingerSubMode.Songs || string.IsNullOrEmpty(_currentSingerMid))
        {
            _controlBar.UpdateStatus("[操作提示] 按 2 键或点击按钮切换 热门/最新 (仅在歌曲列表模式下有效)");
            return;
        }

        _singerSongOrder = (_singerSongOrder == 1) ? 0 : 1;
        _hasMoreSingerSongs = true;
        var orderText = _singerSongOrder == 1 ? "热门" : "最新";

        _controlBar.UpdateStatus($"[切换排序] 正在加载歌手【{_currentSingerName}】的{orderText}歌曲...");
        _songListView.SetSongs([], $"正在加载歌手【{_currentSingerName}】{orderText}作品...");

        var (songs, total) = await QqMusicApi.GetSingerSongListAsync(_currentSingerMid, 0, SingerSongPageSize, _singerSongOrder);

        Application.Invoke(() =>
        {
            _singerCachedSongs = [.. songs];
            _hasMoreSingerSongs = songs.Count >= SingerSongPageSize;

            if (_currentSingerDetail != null)
            {
                bool isFav = !string.IsNullOrEmpty(_currentSingerMid) && UserSession.Current.FavoriteSingers.Contains(_currentSingerMid);
                _artistAlbumDetailView.UpdateSingerActions(_singerSubMode, _singerSongOrder, isFav);
                _artistAlbumDetailView.OnActivated();
            }

            var title = $"歌手: {_currentSingerName} - {orderText}作品 (共 {songs.Count} 首" +
                (_hasMoreSingerSongs ? "，向下滚动加载更多" : "，已全部加载") + "，按 Esc 返回)";
            _songListView.SetSongs(songs, title);
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
            }
            _songListView.SetFocusToList();
            _controlBar.UpdateStatus($"[切换成功] 已载入歌手【{_currentSingerName}】{orderText}作品共 {songs.Count} 首");
        });
    }

    public Task ToggleSingerFavoriteAsync()
    {
        if (string.IsNullOrEmpty(_currentSingerMid))
        {
            _controlBar.UpdateStatus("[操作提示] 当前未处于歌手详情页");
            return Task.CompletedTask;
        }

        bool isFav = UserSession.Current.FavoriteSingers.Contains(_currentSingerMid);
        if (isFav)
        {
            UserSession.Current.FavoriteSingers.Remove(_currentSingerMid);
            UserSession.Current.Save();
            _artistAlbumDetailView.UpdateSingerActions(_singerSubMode, _singerSongOrder, false);
            _controlBar.UpdateStatus($"[已取消关注] 已取消关注歌手【{_currentSingerName}】");
        }
        else
        {
            UserSession.Current.FavoriteSingers.Add(_currentSingerMid);
            UserSession.Current.Save();
            _artistAlbumDetailView.UpdateSingerActions(_singerSubMode, _singerSongOrder, true);
            _controlBar.UpdateStatus($"[已关注] 成功关注歌手【{_currentSingerName}】");
        }

        return Task.CompletedTask;
    }

    public async Task LoadMoreSingerSongsAsync()
    {
        if (_isLoadingMoreSingerSongs || !_hasMoreSingerSongs || string.IsNullOrEmpty(_currentSingerMid)) return;

        _isLoadingMoreSingerSongs = true;
        var nextBegin = _songListView.Songs.Count;
        var orderText = _singerSongOrder == 1 ? "热门" : "最新";

        Application.Invoke(() =>
        {
            _controlBar.UpdateStatus($"[正在加载] 正在获取歌手【{_currentSingerName}】更多{orderText}曲目...");
        });

        try
        {
            var (moreSongs, total) = await QqMusicApi.GetSingerSongListAsync(_currentSingerMid, nextBegin, SingerSongPageSize, _singerSongOrder);

            Application.Invoke(() =>
            {
                if (moreSongs.Count > 0)
                {
                    var currentCount = _songListView.Songs.Count;
                    if (moreSongs.Count < SingerSongPageSize)
                    {
                        _hasMoreSingerSongs = false;
                    }

                    _singerCachedSongs.AddRange(moreSongs);
                    var title = $"歌手: {_currentSingerName} - {orderText}作品 (共 {currentCount + moreSongs.Count} 首" +
                        (_hasMoreSingerSongs ? "，向下滚动加载更多" : "，已全部加载") + "，按 Esc 返回)";
                    _songListView.AppendSongs(moreSongs, title);
                    if (_activeSong != null)
                    {
                        _songListView.SetPlayingSong(_activeSong.Mid);
                    }
                    _controlBar.UpdateStatus($"[加载完成] 歌手作品已载入 {currentCount + moreSongs.Count} 首");
                }
                else
                {
                    _hasMoreSingerSongs = false;
                    _songListView.Title = $"歌手: {_currentSingerName} - {orderText}作品 (共 {_songListView.Songs.Count} 首，已全部加载，按 Esc 返回)";
                    _controlBar.UpdateStatus($"[已全部加载] 歌手作品共 {_songListView.Songs.Count} 首");
                }
            });
        }
        catch (Exception ex)
        {
            Application.Invoke(() =>
            {
                _controlBar.UpdateStatus($"[加载失败] 获取更多歌手作品异常: {ex.Message}");
            });
        }
        finally
        {
            _isLoadingMoreSingerSongs = false;
        }
    }

    public async Task LoadMoreSingerAlbumsAsync()
    {
        if (_isLoadingMoreSingerAlbums || !_hasMoreSingerAlbums || string.IsNullOrEmpty(_currentSingerMid)) return;

        _isLoadingMoreSingerAlbums = true;
        var nextBegin = _singerAlbums.Count;

        Application.Invoke(() =>
        {
            _controlBar.UpdateStatus($"[正在加载] 正在获取歌手【{_currentSingerName}】更多专辑...");
        });

        try
        {
            var moreAlbums = await QqMusicApi.GetSingerAlbumListAsync(_currentSingerMid, nextBegin, 30);

            Application.Invoke(() =>
            {
                if (moreAlbums.Count > 0)
                {
                    if (moreAlbums.Count < 30)
                    {
                        _hasMoreSingerAlbums = false;
                    }

                    int startIdx = _singerAlbums.Count;
                    _singerAlbums.AddRange(moreAlbums);

                    var newItems = new List<string>(moreAlbums.Count);
                    for (int i = 0; i < moreAlbums.Count; i++)
                    {
                        var a = moreAlbums[i];
                        newItems.Add($"{(startIdx + i + 1):D2}  {a.Title}  -  {a.Artist}  (共 {a.SongCount} 首)");
                    }

                    var title = $"歌手专辑: {_currentSingerName} (共 {_singerAlbums.Count} 张" +
                        (_hasMoreSingerAlbums ? "，向下滚动加载更多" : "，已全部加载") + ", 按 Enter 进入, D 收藏)";
                    _songListView.AppendCustomItems(newItems, title);
                    _controlBar.UpdateStatus($"[加载完成] 歌手专辑已载入 {_singerAlbums.Count} 张");
                }
                else
                {
                    _hasMoreSingerAlbums = false;
                    _songListView.Title = $"歌手专辑: {_currentSingerName} (共 {_singerAlbums.Count} 张，已全部加载, 按 Enter 进入, D 收藏)";
                    _controlBar.UpdateStatus($"[已全部加载] 歌手专辑共 {_singerAlbums.Count} 张");
                }
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainWindow", "LoadMoreSingerAlbumsAsync failed", ex);
        }
        finally
        {
            _isLoadingMoreSingerAlbums = false;
        }
    }

    public async Task PreviewAlbumDetailAsync(string albumMid, string albumName, string artistName)
    {
        if (string.IsNullOrWhiteSpace(albumMid)) return;

        _lyricListView.Visible = false;
        _lyricTransBtn.Visible = false;
        _lyricImmersiveBtn.Visible = false;
        _lyricMatchBtn.Visible = false;
        _artistAlbumDetailView.Visible = true;
        UpdateLyricTitle($"专辑 - {albumName}");

        var detailTask = QqMusicApi.GetAlbumDetailInfoAsync(albumMid);
        var coverTask = TerminalImageHelper.IsImageSupported
            ? TerminalImageHelper.EnsureAlbumCoverAsync(albumMid)
            : Task.FromResult<string?>(null);

        await Task.WhenAll(detailTask, coverTask).ConfigureAwait(false);

        var detail = detailTask.Result;
        var coverPath = coverTask.Result;

        Application.Invoke(() =>
        {
            if (detail != null && _artistAlbumDetailView.Visible)
            {
                bool isSingerFav = !string.IsNullOrEmpty(_currentSingerMid) && UserSession.Current.FavoriteSingers.Contains(_currentSingerMid);
                _artistAlbumDetailView.SetAlbumPreview(detail, coverPath, isSingerFav);
                _artistAlbumDetailView.OnActivated();
                SetNeedsDraw();
            }
        });
    }

    private void RenderSingerAlbumsView(int selectIdx = 0)
    {
        if (_singerAlbums.Count == 0)
        {
            _songListView.SetMessage($"歌手【{_currentSingerName}】暂无专辑数据 (按 Esc 返回歌曲列表)", $"歌手专辑: {_currentSingerName} (共 0 张)");
            _songListView.SetFocusToList();
            return;
        }

        var items = new List<string>(_singerAlbums.Count);
        for (int i = 0; i < _singerAlbums.Count; i++)
        {
            var a = _singerAlbums[i];
            items.Add($"{(i + 1):D2}  {a.Title}  -  {a.Artist}  (共 {a.SongCount} 首)");
        }

        var title = $"歌手专辑: {_currentSingerName} (共 {_singerAlbums.Count} 张" +
            (_hasMoreSingerAlbums ? "，向下滚动加载更多" : "，已全部加载") + ", 按 Enter 进入, D 收藏)";
        _songListView.SetCustomItems(items, title, async (idx) =>
        {
            if (idx >= 0 && idx < _singerAlbums.Count)
            {
                await DrilldownToAlbumAsync(_singerAlbums[idx].Mid, _singerAlbums[idx].Title, _singerAlbums[idx].Artist);
            }
        }, (selectedIdx) =>
        {
            if (selectedIdx >= 0 && selectedIdx < _singerAlbums.Count)
            {
                _ = PreviewAlbumDetailAsync(_singerAlbums[selectedIdx].Mid, _singerAlbums[selectedIdx].Title, _singerAlbums[selectedIdx].Artist);
            }
        });

        int clampedIdx = Math.Clamp(selectIdx, 0, _singerAlbums.Count - 1);
        _songListView.SetSelectedIndex(clampedIdx);
        _ = PreviewAlbumDetailAsync(_singerAlbums[clampedIdx].Mid, _singerAlbums[clampedIdx].Title, _singerAlbums[clampedIdx].Artist);

        _songListView.SetFocusToList();
        _controlBar.UpdateStatus($"[歌手专辑] 已展示歌手【{_currentSingerName}】的 {_singerAlbums.Count} 张专辑");
    }

    public async Task HandleSingerDetailCollectAsync()
    {
        if (!UserSession.Current.IsLoggedIn)
        {
            _controlBar.UpdateStatus("[未登录] 请按 U 登录后再执行收藏操作");
            return;
        }

        if (_currentViewMode == ViewMode.ArtistDetail && _singerSubMode == SingerSubMode.Albums)
        {
            // 收藏或取消收藏当前选中的专辑
            var idx = _songListView.SelectedItem ?? -1;
            if (idx >= 0 && idx < _singerAlbums.Count)
            {
                var album = _singerAlbums[idx];
                _controlBar.UpdateStatus($"[正在收藏] 正在将专辑《{album.Title}》加入我的收藏...");
                var ok = await QqMusicApi.AddAlbumToFavoriteAsync(album.Mid);
                Application.Invoke(() =>
                {
                    if (ok)
                    {
                        _controlBar.UpdateStatus($"[收藏成功] 已将专辑《{album.Title}》添加至收藏");
                    }
                    else
                    {
                        _controlBar.UpdateStatus($"[收藏提示] 收藏专辑《{album.Title}》失败或已在收藏中");
                    }
                });
            }
            else
            {
                _controlBar.UpdateStatus("[操作提示] 请先选择要收藏的专辑");
            }
        }
        else
        {
            // 歌曲模式或专辑详情模式下，收藏当前选中的歌曲
            var song = _songListView.GetSelectedSong() ?? _activeSong;
            if (song != null)
            {
                await ToggleSongFavoriteAsync(song);
            }
            else
            {
                _controlBar.UpdateStatus("[操作提示] 请先选择要收藏的歌曲");
            }
        }
    }

    private async Task DrilldownToAlbumAsync(string albumMid, string albumName, string artistName)
    {
        PushCurrentNavigationSnapshot();
        _currentViewMode = ViewMode.AlbumDetail;

        TerminalImageHelper.ClearImages();
        _lyricListView.Visible = false;
        _lyricTransBtn.Visible = false;
        _lyricImmersiveBtn.Visible = false;
        _lyricMatchBtn.Visible = false;
        _artistAlbumDetailView.Visible = true;
        UpdateLyricTitle($"专辑 - {albumName}");

        _songListView.SetSongs([], $"正在加载专辑【{albumName}】背景资料与曲目...");

        var detailTask = QqMusicApi.GetAlbumDetailInfoAsync(albumMid);
        var coverTask = TerminalImageHelper.IsImageSupported
            ? TerminalImageHelper.EnsureAlbumCoverAsync(albumMid)
            : Task.FromResult<string?>(null);

        await Task.WhenAll(detailTask, coverTask).ConfigureAwait(false);

        var detail = detailTask.Result;
        var coverPath = coverTask.Result;

        Application.Invoke(() =>
        {
            if (detail == null)
            {
                _songListView.SetSongs([], $"未能获取专辑【{albumName}】详细资料 (按 Esc 返回)");
                return;
            }

            _artistAlbumDetailView.SetAlbum(detail, coverPath);
            _artistAlbumDetailView.SetHintText("Enter: 播放歌曲  D/S: 收藏  Esc: 返回");
            _artistAlbumDetailView.OnActivated();
            _songListView.SetSongs(detail.Songs, $"专辑: {detail.Name} - {detail.Artist} (共 {detail.Songs.Count} 首曲目, 按 Esc 返回)");
            _songListView.SetFocusToList();
            SetNeedsDraw();
        });
    }

    private void PushCurrentNavigationSnapshot()
    {
        var songs = _songListView.Songs.ToList();
        var selectedIdx = _songListView.SelectedItem ?? 0;
        var statusTitle = _songListView.Title.ToString() ?? "";
        bool wasDetail = _artistAlbumDetailView.Visible;

        _navigationStack.Push(new PageNavigationSnapshot(
            _currentViewMode,
            statusTitle,
            songs,
            selectedIdx,
            wasDetail,
            SingerSubMode: _singerSubMode,
            SingerMid: _currentSingerMid,
            SingerName: _currentSingerName,
            SingerDetail: _currentSingerDetail,
            SingerCoverPath: _currentSingerCoverPath,
            SingerAlbums: _singerAlbums.Count > 0 ? [.. _singerAlbums] : null,
            SingerCachedSongs: _singerCachedSongs.Count > 0 ? [.. _singerCachedSongs] : null,
            SingerSongOrder: _singerSongOrder
        ));
    }

    private void PopNavigationSnapshot()
    {
        if (_navigationStack.Count == 0) return;

        var snapshot = _navigationStack.Pop();
        _currentViewMode = snapshot.ViewMode;

        if (snapshot.ViewMode == ViewMode.ArtistDetail)
        {
            // 恢复歌手主页上下文，不切换到歌词
            _currentSingerMid = snapshot.SingerMid ?? "";
            _currentSingerName = snapshot.SingerName ?? "";
            _currentSingerDetail = snapshot.SingerDetail;
            _currentSingerCoverPath = snapshot.SingerCoverPath;
            _singerSubMode = snapshot.SingerSubMode ?? SingerSubMode.Songs;
            _singerSongOrder = snapshot.SingerSongOrder;
            if (snapshot.SingerAlbums != null) _singerAlbums = snapshot.SingerAlbums;
            if (snapshot.SingerCachedSongs != null) _singerCachedSongs = snapshot.SingerCachedSongs;

            _lyricListView.Visible = false;
            _lyricTransBtn.Visible = false;
            _lyricImmersiveBtn.Visible = false;
            _lyricMatchBtn.Visible = false;
            _artistAlbumDetailView.Visible = true;
            UpdateLyricTitle($"歌手 - {_currentSingerName}");

            if (_singerSubMode == SingerSubMode.Albums && _singerAlbums.Count > 0)
            {
                RenderSingerAlbumsView(snapshot.SelectedIndex);
            }
            else
            {
                if (_currentSingerDetail != null)
                {
                    bool isFav = !string.IsNullOrEmpty(_currentSingerMid) && UserSession.Current.FavoriteSingers.Contains(_currentSingerMid);
                    _artistAlbumDetailView.SetArtist(_currentSingerDetail, _currentSingerCoverPath, isFav, _singerSubMode, _singerSongOrder);
                    _artistAlbumDetailView.OnActivated();
                }
                var songsToRestore = snapshot.Songs.Count > 0 ? snapshot.Songs : _singerCachedSongs;
                _songListView.SetSongs(songsToRestore, snapshot.StatusTitle);
                if (snapshot.SelectedIndex >= 0)
                {
                    _songListView.SetSelectedIndex(snapshot.SelectedIndex);
                }
                _songListView.SetFocusToList();
            }

            SetNeedsDraw();
            return;
        }

        // 非歌手模式（搜索、我的喜欢、歌单等）：恢复普通歌曲列表，恢复右侧为歌词
        _songListView.SetSongs(snapshot.Songs, snapshot.StatusTitle);
        if (snapshot.SelectedIndex >= 0)
        {
            _songListView.SetSelectedIndex(snapshot.SelectedIndex);
        }
        _songListView.SetFocusToList();

        ShowLyricView();
        SetNeedsDraw();
    }

    private void ShowLyricView()
    {
        _artistAlbumDetailView.OnDeactivated();
        _artistAlbumDetailView.Visible = false;
        _lyricListView.Visible = true;
        _lyricTransBtn.Visible = true;
        _lyricImmersiveBtn.Visible = true;
        UpdateLyricMatchButtonHighlight();
        UpdateLyricTitle("歌词");
        TerminalImageHelper.ClearImages();
        RefreshLyricListView();
        SetNeedsDraw();
    }

    private void ClearNavigationStack()
    {
        if (_navigationStack.Count > 0 || _artistAlbumDetailView.Visible)
        {
            _navigationStack.Clear();
            ShowLyricView();
        }
    }

    private void HandleShareCurrentSong()
    {
        var song = _activeSong ?? _controlBar.CurrentSong;
        if (song == null)
        {
            _controlBar.UpdateStatus("[提示] 当前没有正在播放的曲目");
            return;
        }

        if (song.IsLocal || song.IsWebDav)
        {
            _controlBar.UpdateStatus("本地/WebDAV 曲目不支持分享");
            return;
        }

        var webUrl = $"https://y.qq.com/n/ryqq/songDetail/{song.Mid}";
        string text = $"{song.Artist} - {song.Title}\n{webUrl}";

        bool ok = ClipboardService.SetText(text);
        if (ok)
        {
            _controlBar.UpdateStatus($"已复制分享链接: {song.Artist} - {song.Title}");
        }
        else
        {
            _controlBar.UpdateStatus("[提示] 复制失败，未检测到可用剪贴板工具");
        }
    }
}
