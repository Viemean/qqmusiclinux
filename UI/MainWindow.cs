using System.Collections.ObjectModel;
using Rectangle = System.Drawing.Rectangle;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Player;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.UI;

/// <summary>
/// 主视窗：协调顶部搜索栏、左侧导航栏、中央歌曲列表、右侧歌词视窗与底部现代化控制栏
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly GstPlayer _player;
    private readonly MprisService _mprisService;
    private PlaybackMode _currentPlaybackMode;
    private readonly TextField _searchField;
    private readonly FrameView _sidebarFrame;
    private readonly ListView _sidebarList;
    private readonly SongListView _songListView;
    private readonly FrameView _lyricFrame;
    private readonly ListView _lyricListView;
    private readonly ThinScrollBarView _lyricScrollBar;
    private readonly ArtistAlbumDetailView _artistAlbumDetailView;
    private readonly PlayerControlBar _controlBar;
    private readonly Label _hotkeyHintLabel;
    private readonly Label _sidebarTitleLabel;
    private readonly Label _songListTitleLabel;
    private readonly Label _lyricTitleLabel;
    private readonly Label _searchLabel;
    private readonly Button _userStatusBtn;
    private readonly NowPlayingView _nowPlayingView;
    private bool _isNowPlayingViewActive = false;
    private bool _isSearchActive = false;
    private long _lastTransClickTicks;
    private readonly Label _lyricTransBtn;
    private readonly Label _lyricImmersiveBtn;
    private bool _isImmersiveMode = false;
    private long _lastImmersiveActivityTick = 0;
    private object? _immersiveActivityTimerToken;
    private bool _showTranslation = true;
    private bool _hasTranslation = false;

    private readonly List<LyricLine> _currentLyrics = [];
    private readonly List<int> _lyricItemToLineIndex = [];
    private readonly Dictionary<int, int> _lyricLineToFirstItemIndex = [];
    private int _lastLyricViewportWidth = 0;
    private int _currentActiveLyricIndex = -1;
    private Song? _activeSong;
    private AudioQualityTier _preferredQualityTier;
    private AudioQualityTier _actualQualityTier;

    private string _lastSearchQuery = "";
    private int _searchCurrentPage = 1;
    private bool _isLoadingMore = false;
    private bool _hasMoreSearchResults = false;
    private const int PageSize = 50;

    private Playlist? _currentDrilldownPlaylist = null;
    private List<Playlist> _cachedPlaylists = [];
    private bool _isViewingPlaylistsList = false;

    private Album? _currentDrilldownAlbum = null;
    private List<Album> _cachedAlbums = [];
    private bool _isViewingAlbumsList = false;

    private enum ViewMode
    {
        Search,
        Favorite,
        DailyRecommend,
        GuessRecommend,
        PlaylistDrilldown,
        PlaylistsList,
        FavoriteAlbums,
        AlbumDrilldown,
        ArtistDetail,
        AlbumDetail,
        RecentPlay,
        LocalMusic,
        Other
    }
    private ViewMode _currentViewMode = ViewMode.Other;

    // 猜你喜欢（个性电台流模式）动态预取队列与收听状态
    private readonly List<Song> _radioQueue = [];
    private int _radioIndex = 0;
    private int _radioPlayedCount = 0;
    private bool _isRadioPrefetching = false;

    private readonly HashSet<string> _favoriteSongMids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> _favoriteSongIds = [];

    private int _currentFocusedWindowIndex = 1;
    private bool _sidebarClickInEmptyArea = false;
    private int _favoriteCurrentPage = 1;
    private int _favoriteTotalCount = 0;
    private bool _hasMoreFavorites = false;
    private bool _isLoadingMoreFavorites = false;
    private const int FavoritePageSize = 100;

    private int _playlistCurrentPage = 1;
    private bool _hasMorePlaylistSongs = false;
    private bool _isLoadingMorePlaylistSongs = false;
    private const int PlaylistPageSize = 100;

    private int _preMuteVolume = 80;
    private long _lastUserLyricScrollTick = 0;
    private long _lastProgressSaveTick = 0;

    public MainWindow(GstPlayer player)
    {
        _player = player;

        // 载入持久化登录凭证并在后台自动补齐官方 musickey
        UserSession.Load();
        _preferredQualityTier = AudioQualityHelper.Parse(UserSession.Current.PreferredQuality);
        _actualQualityTier = _preferredQualityTier;
        _currentPlaybackMode = UserSession.Current.PlaybackMode;
        int initialVolume = UserSession.Current.Volume > 0 ? UserSession.Current.Volume : 80;
        _preMuteVolume = initialVolume;
        _player.SetVolume(initialVolume);

        _mprisService = new MprisService();
        SetupMprisService();

        Task.Run(async () =>
        {
            var ok = await LoginService.EnsureMusicKeyAsync();
            if (ok)
            {
                Application.Invoke(() =>
                {
                    if (_userStatusBtn != null)
                    {
                        _userStatusBtn.Text = GetUserStatusText();
                    }
                });
            }
        });

        Title = "";
        BorderStyle = LineStyle.None;
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        SetScheme(MikuTheme.Base);

        // 1. 顶部搜索栏与用户信息
        _searchLabel = new Label
        {
            Text = "搜索:",
            X = 1,
            Y = 0
        };
        Add(_searchLabel);

        // 顶部用户状态按钮：禁用可获焦属性，杜绝启动与轮转时被物理焦点抢占；通过鼠标点击与 F2 快捷键唤起
        _userStatusBtn = new Button
        {
            Text = GetUserStatusText(),
            X = Pos.AnchorEnd(32),
            Y = 0,
            ShadowStyle = ShadowStyles.None,
            CanFocus = false
        };
        _userStatusBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _userStatusBtn.KeyBindings.Remove(Key.Space);
        _userStatusBtn.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                ShowLoginDialog();
            }
        };
        _userStatusBtn.Accepting += (s, e) => ShowLoginDialog();
        Add(_userStatusBtn);

        _searchField = new TextField
        {
            X = Pos.Right(_searchLabel) + 1,
            Y = 0,
            Width = Dim.Fill(34),
            Text = "",
            CanFocus = false,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _searchField.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                _searchField.CanFocus = true;
                _isSearchActive = true;
                _searchField.SetFocus();
            }
        };
        _searchField.KeyDown += async (s, k) =>
        {
            if (k == Key.Enter)
            {
                k.Handled = true;
                _isSearchActive = false;
                _searchField.CanFocus = false;
                await ExecuteSearchAsync();
                _songListView?.SetFocusToList();
                Application.Invoke(UpdateFrameBorderHighlights);
            }
            else if (k == Key.Tab || k.ToString().Contains("Tab"))
            {
                k.Handled = true;
                _isSearchActive = false;
                _searchField.CanFocus = false;
                SwitchNextFocusWindow(!k.IsShift);
            }
            else if (k == Key.Esc || k == Key.CursorDown || k == Key.CursorUp)
            {
                k.Handled = true;
                _isSearchActive = false;
                _searchField.CanFocus = false;
                _songListView?.SetFocusToList();
                Application.Invoke(UpdateFrameBorderHighlights);
            }
        };
        Add(_searchField);

        // 2. 左侧导航栏（固定 14 宽，小窗友好）
        _sidebarFrame = new FrameView
        {
            Title = "",
            X = 0,
            Y = 1,
            Width = 14,
            Height = Dim.Fill(5),
            CanFocus = true,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _sidebarFrame.SetScheme(MikuTheme.FrameBorderActive);
        _sidebarList = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        _sidebarList.KeyBindings.Remove(Key.Space);
        _sidebarList.KeyDown += (s, k) =>
        {
            if (k == Key.Tab || k.ToString().Contains("Tab"))
            {
                k.Handled = true;
                SwitchNextFocusWindow(!k.IsShift);
                return;
            }
            if (k == Key.CursorRight)
            {
                k.Handled = true;
                SetFocusToWindow(1);
            }
        };
        _sidebarList.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                SetFocusToWindow(0);
            }
            // 拦截对列表项下方空白区域的双击与多余点击，避免错误激活或重入当前选中选项
            bool inEmpty = m.Position.HasValue && m.Position.Value.Y >= (_sidebarList.Source?.Count ?? 0);
            if (inEmpty)
            {
                _sidebarClickInEmptyArea = true;
                if (m.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
                {
                    m.Handled = true;
                }
            }
            else
            {
                _sidebarClickInEmptyArea = false;
            }
        };
        _sidebarList.SetSource(new ObservableCollection<string>
        {
            "搜索结果",
            "我的喜欢",
            "每日30首",
            "猜你喜欢",
            "我的歌单",
            "收藏专辑",
            "最近播放",
            "本地音乐"
        });
        _sidebarFrame.Add(_sidebarList);
        _sidebarFrame.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                SetFocusToWindow(0);
            }
            if (m.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked))
            {
                m.Handled = true;
            }
        };
        Add(_sidebarFrame);

        // 3. 中央歌曲列表视窗（解耦封装）
        _songListView = new SongListView
        {
            X = Pos.Right(_sidebarFrame),
            Y = 1
        };
        _songListView.Clicked += () => SetFocusToWindow(1);
        _songListView.TabNavigationRequested += forward => SwitchNextFocusWindow(forward);
        _songListView.SongAccepted += PlaySongAsync;
        _songListView.LoadMoreRequested += async () =>
        {
            if (_currentViewMode == ViewMode.Search)
            {
                await LoadMoreSearchResultsAsync();
            }
            else if (_currentViewMode == ViewMode.Favorite)
            {
                await LoadMoreFavoriteSongsAsync();
            }
            else if (_currentViewMode == ViewMode.PlaylistDrilldown)
            {
                await LoadMorePlaylistSongsAsync();
            }
            else if (_currentViewMode == ViewMode.ArtistDetail)
            {
                if (_singerSubMode == SingerSubMode.Songs)
                {
                    await LoadMoreSingerSongsAsync();
                }
                else if (_singerSubMode == SingerSubMode.Albums)
                {
                    await LoadMoreSingerAlbumsAsync();
                }
            }
        };
        Add(_songListView);

        _sidebarList.Accepted += async (s, e) =>
        {
            if (_sidebarClickInEmptyArea)
            {
                _sidebarClickInEmptyArea = false;
                return;
            }
            ClearNavigationStack();
            var idx = _sidebarList.SelectedItem ?? 0;
            if (idx == 0)
            {
                _isViewingPlaylistsList = false;
                _currentDrilldownPlaylist = null;
                _isViewingAlbumsList = false;
                _currentDrilldownAlbum = null;
                await ExecuteSearchAsync();
            }
            else if (idx == 1)
            {
                _isViewingPlaylistsList = false;
                _currentDrilldownPlaylist = null;
                _isViewingAlbumsList = false;
                _currentDrilldownAlbum = null;
                await LoadFavoriteSongsAsync();
            }
            else if (idx == 2)
            {
                _isViewingPlaylistsList = false;
                _currentDrilldownPlaylist = null;
                _isViewingAlbumsList = false;
                _currentDrilldownAlbum = null;
                await LoadDailyRecommendSongsAsync();
            }
            else if (idx == 3)
            {
                _isViewingPlaylistsList = false;
                _currentDrilldownPlaylist = null;
                _isViewingAlbumsList = false;
                _currentDrilldownAlbum = null;
                await ResumeOrStartGuessRadioAsync();
            }
            else if (idx == 4)
            {
                _isViewingAlbumsList = false;
                _currentDrilldownAlbum = null;
                await LoadPlaylistsAsync();
            }
            else if (idx == 5)
            {
                _isViewingPlaylistsList = false;
                _currentDrilldownPlaylist = null;
                await LoadFavoriteAlbumsAsync();
            }
            else if (idx == 6)
            {
                _isViewingPlaylistsList = false;
                _currentDrilldownPlaylist = null;
                _isViewingAlbumsList = false;
                _currentDrilldownAlbum = null;
                await LoadRecentPlaySongsAsync();
            }
            else if (idx == 7)
            {
                _isViewingPlaylistsList = false;
                _currentDrilldownPlaylist = null;
                _isViewingAlbumsList = false;
                _currentDrilldownAlbum = null;
                await LoadLocalMusicAsync();
            }
            _songListView.SetFocusToList();
            Application.Invoke(UpdateFrameBorderHighlights);
        };

        // 4. 右侧实时歌词视窗（标题由冗长文案简化为“歌词”）
        _lyricFrame = new FrameView
        {
            Title = "",
            X = Pos.Right(_songListView),
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(5),
            CanFocus = true,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _lyricFrame.SetScheme(MikuTheme.FrameBorderActive);
        _lyricListView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        _lyricListView.KeyBindings.Remove(Key.Space);
        _lyricListView.SetSource(new ObservableCollection<string> { "暂无歌词" });
        _lyricListView.ViewportChanged += (s, e) =>
        {
            int curW = _lyricListView.Viewport.Width;
            if (curW > 0 && curW != _lastLyricViewportWidth)
            {
                _lastLyricViewportWidth = curW;
                Application.Invoke(RefreshLyricListView);
            }
        };
        _lyricListView.RowRender += (s, e) =>
        {
            if (_currentActiveLyricIndex >= 0 && e.Row >= 0 && e.Row < _lyricItemToLineIndex.Count)
            {
                int lineIdx = _lyricItemToLineIndex[e.Row];
                if (lineIdx == _currentActiveLyricIndex)
                {
                    // 当前播放句：原文与翻译同步高亮为翡翠绿
                    e.RowAttribute = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None);
                    return;
                }
            }

            // 其余未播放行与空行使用清润亮灰白
            e.RowAttribute = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqTextLyricDim, Color.None);
        };
        _lyricListView.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.WheeledUp) || m.Flags.HasFlag(MouseFlags.WheeledDown) ||
                m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                _lastUserLyricScrollTick = Environment.TickCount64;
                SetFocusToWindow(2);
            }
        };
        _lyricFrame.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.WheeledUp) || m.Flags.HasFlag(MouseFlags.WheeledDown) ||
                m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                _lastUserLyricScrollTick = Environment.TickCount64;
                SetFocusToWindow(2);
            }
        };
        _lyricListView.KeyDown += (s, k) =>
        {
            if (k == Key.Tab || k.ToString().Contains("Tab"))
            {
                k.Handled = true;
                SwitchNextFocusWindow(!k.IsShift);
                return;
            }
            if (k == Key.CursorLeft)
            {
                k.Handled = true;
                _songListView.SetFocusToList();
                return;
            }
            if (k == Key.CursorUp || k == Key.CursorDown || k == Key.PageUp || k == Key.PageDown || k == Key.Home || k == Key.End)
            {
                _lastUserLyricScrollTick = Environment.TickCount64;
            }
        };
        _lyricListView.Accepted += async (s, e) =>
        {
            _lastUserLyricScrollTick = 0;
            var idx = _lyricListView.SelectedItem ?? -1;
            if (idx >= 0 && idx < _lyricItemToLineIndex.Count && _activeSong != null)
            {
                var lyricIdx = _lyricItemToLineIndex[idx];
                if (lyricIdx >= 0 && lyricIdx < _currentLyrics.Count)
                {
                    var targetSec = _currentLyrics[lyricIdx].Timestamp.TotalSeconds;
                    await _player.SeekAsync(targetSec);
                    Application.Invoke(() =>
                    {
                        UpdateProgress(targetSec);
                    });
                }
            }
        };
        _lyricFrame.Add(_lyricListView);

        _lyricScrollBar = new ThinScrollBarView
        {
            X = Pos.AnchorEnd(1),
            Y = 0,
            Height = Dim.Fill(),
            AutoShowOnMetricsChange = false
        };
        _lyricScrollBar.ScrollPositionChanged += targetRow =>
        {
            int sourceCount = _lyricListView.Source?.Count ?? 0;
            if (sourceCount > 0)
            {
                int clamped = Math.Clamp(targetRow, 0, sourceCount - 1);
                _lyricListView.SelectedItem = clamped;
                _lyricListView.Viewport = new Rectangle(_lyricListView.Viewport.X, clamped, _lyricListView.Viewport.Width, _lyricListView.Viewport.Height);
                _lastUserLyricScrollTick = Environment.TickCount64;
            }
        };
        _lyricFrame.Add(_lyricScrollBar);

        // 歌词界面右下角纯净单字“译”与“沉浸”按钮（通过高亮/暗灰判断是否开启）
        _lyricTransBtn = new Label
        {
            Text = "译",
            X = Pos.AnchorEnd(8),
            Y = Pos.AnchorEnd(1),
            Width = 2,
            Height = 1,
            CanFocus = false,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _lyricTransBtn.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                var now = Environment.TickCount64;
                if (now - _lastTransClickTicks > 200)
                {
                    _lastTransClickTicks = now;
                    ToggleTranslation();
                }
                m.Handled = true;
            }
        };
        _lyricFrame.Add(_lyricTransBtn);
        UpdateTranslationButtonHighlight();

        _lyricImmersiveBtn = new Label
        {
            Text = "沉浸",
            X = Pos.AnchorEnd(5),
            Y = Pos.AnchorEnd(1),
            Width = 4,
            Height = 1,
            CanFocus = false,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _lyricImmersiveBtn.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                ToggleImmersiveMode();
                m.Handled = true;
            }
        };
        _lyricFrame.Add(_lyricImmersiveBtn);
        UpdateImmersiveButtonHighlight();

        _artistAlbumDetailView = new ArtistAlbumDetailView
        {
            Visible = false
        };
        _artistAlbumDetailView.Clicked += () => SetFocusToWindow(2);
        _artistAlbumDetailView.TabNavigationRequested += forward => SwitchNextFocusWindow(forward);
        _lyricFrame.Add(_artistAlbumDetailView);

        Add(_lyricFrame);

        _controlBar = new PlayerControlBar();
        _controlBar.CanFocus = true;
        _controlBar.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _controlBar.Clicked += () => SetFocusToWindow(3);
        _controlBar.TabNavigationRequested += forward => SwitchNextFocusWindow(forward);
        _controlBar.PrevClicked += async () =>
        {
            if (_currentViewMode != ViewMode.GuessRecommend)
            {
                await PlayPrevInCurrentListAsync();
            }
        };
        _controlBar.PlayPauseClicked += async () =>
        {
            if (_activeSong != null && !_player.IsPlaying && _player.TotalDurationSeconds <= 0)
            {
                await PlaySongAsync(_activeSong, UserSession.Current.LastPlaybackPositionSeconds);
            }
            else
            {
                await _player.TogglePauseAsync();
                UpdatePlayerStatus();
            }
        };
        _controlBar.NextClicked += async () =>
        {
            if (_currentViewMode == ViewMode.GuessRecommend)
            {
                await PlayNextRadioTrackAsync();
            }
            else
            {
                await PlayNextInCurrentListAsync();
            }
        };
        _controlBar.QualityClicked += ShowQualityDialog;
        _controlBar.DownloadClicked += ShowDownloadDialog;
        _controlBar.ModeClicked += TogglePlaybackMode;
        _controlBar.UpdatePlaybackMode(_currentPlaybackMode);
        _controlBar.VolumeAdjustRequested += AdjustVolume;
        _controlBar.VolumeMuteToggled += ToggleMute;
        _controlBar.ExitRequested += () =>
        {
            _songListView.SetFocusToList();
            Application.Invoke(UpdateFrameBorderHighlights);
        };
        _songListView.ArtistClicked += (s) => OnArtistClicked(s);
        _songListView.AlbumClicked += (s) => OnAlbumClicked(s);
        _controlBar.SeekRequested += async (ratio) =>
        {
            if (_player.TotalDurationSeconds > 0)
            {
                var targetSec = ratio * _player.TotalDurationSeconds;
                await _player.SeekAsync(targetSec);
                Application.Invoke(() => UpdateProgress(targetSec));
            }
        };
        _controlBar.UpdateVolume(initialVolume, initialVolume == 0);
        if (UserSession.Current.LastPlayedSong != null)
        {
            var lastSong = UserSession.Current.LastPlayedSong;
            _activeSong = lastSong;
            _controlBar.SetCurrentSong(lastSong);
            _controlBar.SetLocalMode(lastSong.IsLocal);
            _controlBar.UpdateQuality(AudioQualityHelper.GetBadge(_actualQualityTier));
            if (lastSong.Duration > 0 && UserSession.Current.LastPlaybackPositionSeconds > 0)
            {
                UpdateProgress(UserSession.Current.LastPlaybackPositionSeconds);
            }
        }
        _controlBar.FavoriteClicked += async () =>
        {
            var targetSong = _activeSong ?? _songListView.GetSelectedSong();
            if (targetSong == null)
            {
                _controlBar.UpdateStatus("[操作提示] 当前暂无播放曲目，请先选择歌曲或点播");
                return;
            }
            if (targetSong.IsLocal)
            {
                _controlBar.UpdateStatus("[本地音乐] 本地曲目不支持在线收藏");
                return;
            }
            await ToggleSongFavoriteAsync(targetSong);
        };
        Add(_controlBar);

        // 底部快捷键操作指南（独立放置在控制栏UI方框下方最底行，干净平整无边框干扰）
        _hotkeyHintLabel = new Label
        {
            Text = " [V]播放界面  [P]沉浸  [O]播放顺序  [S]收藏  [T]翻译  [/]搜索  [J]上一首  [L]下一首  [M]静音",
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            CanFocus = false,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _hotkeyHintLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextSub, MikuTheme.MikuBgSurface)
        });
        Add(_hotkeyHintLabel);

        // 顶部三大窗格置顶常驻高亮标题（即使未获焦暗化边框线条，标题文本始终保持官方翡翠薄荷绿高亮）
        _sidebarTitleLabel = new Label
        {
            Text = "┤导航├",
            X = 1,
            Y = 1,
            CanFocus = false,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _sidebarTitleLabel.SetScheme(MikuTheme.TitleHighlight);
        _sidebarTitleLabel.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                SetFocusToWindow(0);
                m.Handled = true;
            }
        };

        _songListTitleLabel = new Label
        {
            Text = "┤歌曲列表 (就绪)├",
            X = Pos.Right(_sidebarFrame) + 1,
            Y = 1,
            CanFocus = false,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _songListTitleLabel.SetScheme(MikuTheme.TitleHighlight);
        _songListTitleLabel.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                SetFocusToWindow(1);
                m.Handled = true;
            }
        };
        _songListView.DisplayTitleChanged += title =>
        {
            Application.Invoke(() =>
            {
                _songListTitleLabel.Text = $"┤{title}├";
                _songListTitleLabel.SetNeedsDraw();
                _songListView.SetNeedsDraw();
            });
        };

        _lyricTitleLabel = new Label
        {
            Text = "┤歌词├",
            X = Pos.Right(_songListView) + 1,
            Y = 1,
            CanFocus = false,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _lyricTitleLabel.SetScheme(MikuTheme.TitleHighlight);
        _lyricTitleLabel.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                SetFocusToWindow(2);
                m.Handled = true;
            }
        };

        Add(_sidebarTitleLabel);
        Add(_songListTitleLabel);
        Add(_lyricTitleLabel);

        _songListView.StatusNotification += msg => _controlBar.UpdateStatus(msg);

        // 沉浸式播放界面视图与控制栏联动
        _nowPlayingView = new NowPlayingView();
        _nowPlayingView.BackRequested += CloseNowPlayingView;
        _nowPlayingView.SeekRequested += async (time) =>
        {
            await _player.SeekAsync(time.TotalSeconds);
        };
        _nowPlayingView.ToggleTranslationRequested += ToggleTranslation;
        _nowPlayingView.ToggleImmersiveRequested += ToggleImmersiveMode;
        Add(_nowPlayingView);
        _controlBar.NowPlayingClicked += ToggleNowPlayingView;

        // 窗口整体尺寸改变时同步更新沉浸式播放界面的封面或详情页写真
        ViewportChanged += (s, e) =>
        {
            if (_isNowPlayingViewActive)
            {
                _nowPlayingView.OnWindowResized();
            }
            else if (_artistAlbumDetailView.Visible)
            {
                _artistAlbumDetailView.OnWindowResized();
            }
        };

        // 鼠标活动唤醒沉浸模式下自动隐藏的图标
        MouseEvent += (s, m) =>
        {
            TriggerImmersiveActivity();
        };

        // 全局顶层按键预捕获，除搜索框文字输入外，统一拦截分发全局播放与视图快捷键
        Application.KeyDown += async (s, k) =>
        {
            TriggerImmersiveActivity();

            // 1. 若当前处于任何弹窗（Dialog/Modal）中，绝不拦截按键，全部交由弹窗处理
            if (Application.TopRunnableView != null && Application.TopRunnableView != this)
            {
                return;
            }

            // 2. 若当前获焦控件是文本输入控件（且非主搜索框），直接放行
            var focused = Application.Navigation?.GetFocused();
            if (focused is TextField tf && tf != _searchField)
            {
                return;
            }

            // 3. 搜索框处于激活打字状态：放行所有字符输入（包括 '/'、空格等），仅处理退出搜索或提交
            if (_searchField.HasFocus && _isSearchActive)
            {
                if (k == Key.Esc || k == Key.CursorDown || k == Key.CursorUp)
                {
                    k.Handled = true;
                    _isSearchActive = false;
                    _searchField.CanFocus = false;
                    _songListView.SetFocusToList();
                    Application.Invoke(UpdateFrameBorderHighlights);
                    return;
                }
                if (k == Key.Enter)
                {
                    k.Handled = true;
                    _isSearchActive = false;
                    _searchField.CanFocus = false;
                    await ExecuteSearchAsync();
                    _songListView.SetFocusToList();
                    Application.Invoke(UpdateFrameBorderHighlights);
                    return;
                }
                if (k == Key.Tab || k.AsRune.Value == '\t' || k.ToString().Contains("Tab"))
                {
                    k.Handled = true;
                    _isSearchActive = false;
                    _searchField.CanFocus = false;
                    SwitchNextFocusWindow(!k.IsShift);
                    return;
                }
                return;
            }

            // 4. 全局 Tab 与 Shift+Tab 四大视窗流转
            if (k == Key.Tab || k.AsRune.Value == '\t' || k.ToString().Contains("Tab"))
            {
                k.Handled = true;
                _isSearchActive = false;
                _searchField.CanFocus = false;
                SwitchNextFocusWindow(!k.IsShift);
                return;
            }

            // 5. 焦点丢失/悬空时的安全自愈兜底（按方向键或回车立即将焦点恢复至中央歌曲列表）
            if (GetFocusedWindowIndex(focused) == -1 && (k == Key.CursorUp || k == Key.CursorDown || k == Key.CursorLeft || k == Key.CursorRight || k == Key.Enter))
            {
                _songListView.SetFocusToList();
                Application.Invoke(UpdateFrameBorderHighlights);
            }

            // 6. 只有未在搜索框内打字时，按 F3 或 '/' 才作为激活搜索框的快捷键
            if (k == Key.F3 || k.AsRune.Value == '/')
            {
                k.Handled = true;
                _searchField.CanFocus = true;
                _isSearchActive = true;
                _searchField.SetFocus();
                return;
            }

            char c = char.ToUpperInvariant((char)k.AsRune.Value);

            if (k == Key.Esc)
            {
                k.Handled = true;
                if (_isImmersiveMode)
                {
                    ApplyImmersiveMode(false);
                    return;
                }
                if (_isNowPlayingViewActive)
                {
                    CloseNowPlayingView();
                }
                else if (_navigationStack.Count > 0)
                {
                    PopNavigationSnapshot();
                }
                else if (_artistAlbumDetailView.Visible)
                {
                    ShowLyricView();
                }
                else if (_currentDrilldownPlaylist != null)
                {
                    await LoadPlaylistsAsync();
                }
                else if (_currentDrilldownAlbum != null)
                {
                    await LoadFavoriteAlbumsAsync();
                }
                return;
            }

            if (c == 'Q')
            {
                k.Handled = true;
                ShowExitConfirmDialog();
                return;
            }

            if (c == 'V')
            {
                k.Handled = true;
                ToggleNowPlayingView();
                return;
            }

            if (c == 'P')
            {
                k.Handled = true;
                ToggleImmersiveMode();
                return;
            }

            if (k == Key.Space)
            {
                k.Handled = true;
                if (_activeSong != null && !_player.IsPlaying && _player.TotalDurationSeconds <= 0)
                {
                    await PlaySongAsync(_activeSong, UserSession.Current.LastPlaybackPositionSeconds);
                }
                else
                {
                    await _player.TogglePauseAsync();
                    UpdatePlayerStatus();
                }
                return;
            }

            if (c == 'O')
            {
                k.Handled = true;
                TogglePlaybackMode();
                return;
            }

            if (c == 'M')
            {
                k.Handled = true;
                ToggleMute();
                return;
            }

            if (c == 'S')
            {
                k.Handled = true;
                await HandleSmartFavoriteAsync();
                return;
            }

            if (c == 'T')
            {
                k.Handled = true;
                ToggleTranslation();
                return;
            }

            if (c == 'J')
            {
                k.Handled = true;
                if (_currentViewMode != ViewMode.GuessRecommend)
                {
                    await PlayPrevInCurrentListAsync();
                }
                else
                {
                    _controlBar.UpdateStatus("[电台模式] 电台模式不支持上一首");
                }
                return;
            }

            if (c == 'L')
            {
                k.Handled = true;
                if (_currentViewMode == ViewMode.GuessRecommend)
                {
                    await PlayNextRadioTrackAsync();
                }
                else
                {
                    await PlayNextInCurrentListAsync();
                }
                return;
            }

            if (k == Key.Home)
            {
                k.Handled = true;
                _songListView.ScrollToTop();
                return;
            }

            if (k.AsRune.Value == '+' || k.AsRune.Value == '=')
            {
                k.Handled = true;
                AdjustVolume(5);
                return;
            }

            if (k.AsRune.Value == '-')
            {
                k.Handled = true;
                AdjustVolume(-5);
                return;
            }
        };

        // 绑定播放器回调
        _player.PositionUpdated += pos =>
        {
            Application.Invoke(() =>
            {
                UpdateProgress(pos);
                UpdateLyrics(pos);
            });
        };

        _player.PlaybackFinished += () =>
        {
            Application.Invoke(async () =>
            {
                if (_currentViewMode == ViewMode.GuessRecommend)
                {
                    // 电台模式：单曲播放结束后自动平滑跳至下一首
                    await PlayNextRadioTrackAsync();
                    return;
                }

                if (_songListView.Songs.Count > 0 && _activeSong != null)
                {
                    if (_currentPlaybackMode == PlaybackMode.SingleLoop)
                    {
                        // 单曲循环：原地重新播放该曲
                        await PlaySongAsync(_activeSong, 0);
                        return;
                    }

                    await PlayNextInCurrentListAsync(isAutoPlayback: true);
                }
            });
        };

        // 递归解除全部子控件对 Space 和 Tab 的默认拦截（TextField 除外），确保全局快捷键与视窗循环流转顺畅
        UnbindSpaceKey(this);
        UnbindTabKeys(this);

        // 递归应用 Miku 现代色彩调色板至所有容器与组件
        MikuTheme.ApplyTo(this, MikuTheme.Base);
        _songListView.ApplyInnerScheme(MikuTheme.Base);
        _sidebarList.SetScheme(MikuTheme.Base);
        _lyricListView.SetScheme(MikuTheme.Lyric);
        MikuTheme.ApplyTo(_controlBar, MikuTheme.PlayerBar);

        // 统一监听各视窗焦点变化，动态高亮获焦激活面板的边框
        _sidebarList.HasFocusChanged += (s, e) => Application.Invoke(UpdateFrameBorderHighlights);
        _songListView.HasFocusChanged += (s, e) => Application.Invoke(UpdateFrameBorderHighlights);
        _songListView.InnerFocusChanged += (s, e) => Application.Invoke(UpdateFrameBorderHighlights);
        _lyricListView.HasFocusChanged += (s, e) => Application.Invoke(UpdateFrameBorderHighlights);
        _artistAlbumDetailView.HasFocusChanged += (s, e) => Application.Invoke(UpdateFrameBorderHighlights);
        _controlBar.HasFocusChanged += (s, e) => Application.Invoke(UpdateFrameBorderHighlights);
        _searchField.HasFocusChanged += (s, e) =>
        {
            if (!_searchField.HasFocus)
            {
                _isSearchActive = false;
                _searchField.CanFocus = false;
            }
            Application.Invoke(UpdateFrameBorderHighlights);
        };

        UpdateFrameBorderHighlights();

        Application.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
        {
            _isSearchActive = false;
            _searchField.CanFocus = false;
            _songListView.SetFocusToList();
            UpdateFrameBorderHighlights();
            if (!UserSession.Current.IsLoggedIn)
            {
                ShowLoginDialog();
            }
            return false;
        });
        _songListView.SetFocusToList();

        // 后台预热收藏曲目 ID 缓存，确保首次播放曲目即能精准呈现 [已收藏] / [收藏] 状态
        if (UserSession.Current.IsLoggedIn)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var favRes = await QqMusicApi.GetFavoriteSongsAsync(1, 200);
                    lock (_favoriteSongMids)
                    {
                        foreach (var s in favRes.Songs)
                        {
                            if (!string.IsNullOrEmpty(s.Mid)) _favoriteSongMids.Add(s.Mid);
                            if (s.Id > 0) _favoriteSongIds.Add(s.Id);
                        }
                    }
                    if (_activeSong != null)
                    {
                        var isFav = (!string.IsNullOrEmpty(_activeSong.Mid) && _favoriteSongMids.Contains(_activeSong.Mid)) ||
                                    (_activeSong.Id > 0 && _favoriteSongIds.Contains(_activeSong.Id));
                        Application.Invoke(() => _controlBar.SetFavoriteStatus(isFav));
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("MainWindow", $"Failed to preload favorite songs cache: {ex.Message}");
                }
            });
        }
    }

    private string GetUserStatusText()
    {
        if (UserSession.Current.IsLoggedIn)
        {
            var name = string.IsNullOrEmpty(UserSession.Current.Nick) ? UserSession.Current.Uin : UserSession.Current.Nick;
            var vipSuffix = UserSession.Current.IsVip ? " [VIP]" : "";
            return $"账号: {name}{vipSuffix}";
        }
        return "未登录 (按 L 登录)";
    }

    private void SwitchNextFocusWindow(bool forward)
    {
        var focused = Application.Navigation?.GetFocused();
        int currentWindow = GetFocusedWindowIndex(focused);
        if (currentWindow == -1)
        {
            currentWindow = _currentFocusedWindowIndex;
        }

        int windowCount = _isImmersiveMode ? 3 : 4;
        int nextWindow;
        if (forward)
        {
            nextWindow = (currentWindow + 1) % windowCount;
        }
        else
        {
            nextWindow = (currentWindow - 1 + windowCount) % windowCount;
        }

        SetFocusToWindow(nextWindow);
    }

    private void ToggleImmersiveMode()
    {
        ApplyImmersiveMode(!_isImmersiveMode);
    }

    private void ApplyImmersiveMode(bool enable)
    {
        _isImmersiveMode = enable;

        // 1. 上方搜索框与账号状态栏显隐
        _searchLabel.Visible = !enable;
        _searchField.Visible = !enable;
        _userStatusBtn.Visible = !enable;

        // 2. 下方控制栏与快捷键提示栏显隐
        _controlBar.Visible = !enable;
        _hotkeyHintLabel.Visible = !enable;

        // 3. 主视窗尺寸自适应：沉浸时自第 0 行顶格起，高度延伸至铺满最底行
        int topY = enable ? 0 : 1;
        var fillHeight = enable ? Dim.Fill(0) : Dim.Fill(5);

        _sidebarFrame.Y = topY;
        _sidebarFrame.Height = fillHeight;

        _songListView.Y = topY;
        _songListView.Height = fillHeight;

        _lyricFrame.Y = topY;
        _lyricFrame.Height = fillHeight;

        _sidebarTitleLabel.Y = topY;
        _songListTitleLabel.Y = topY;
        _lyricTitleLabel.Y = topY;

        UpdateImmersiveButtonHighlight();

        if (enable)
        {
            TriggerImmersiveActivity();
            StartImmersiveTimer();
        }
        else
        {
            StopImmersiveTimer();
            _lyricTransBtn.Visible = true;
            _lyricImmersiveBtn.Visible = true;
        }

        _nowPlayingView.SetImmersiveState(enable);

        if (enable && _currentFocusedWindowIndex == 3)
        {
            SetFocusToWindow(1);
        }

        SetNeedsDraw();
        Application.Invoke(UpdateFrameBorderHighlights);
    }

    public void TriggerImmersiveActivity()
    {
        _lastImmersiveActivityTick = Environment.TickCount64;
        if (_isImmersiveMode)
        {
            if (!_lyricTransBtn.Visible || !_lyricImmersiveBtn.Visible)
            {
                _lyricTransBtn.Visible = true;
                _lyricImmersiveBtn.Visible = true;
                _lyricFrame.SetNeedsDraw();
            }
        }
    }

    private void StartImmersiveTimer()
    {
        StopImmersiveTimer();
        _lastImmersiveActivityTick = Environment.TickCount64;
        _immersiveActivityTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            if (_isImmersiveMode)
            {
                if (Environment.TickCount64 - _lastImmersiveActivityTick > 3000)
                {
                    if (_lyricTransBtn.Visible || _lyricImmersiveBtn.Visible)
                    {
                        _lyricTransBtn.Visible = false;
                        _lyricImmersiveBtn.Visible = false;
                        _lyricFrame.SetNeedsDraw();
                    }
                }
            }
            return _isImmersiveMode;
        });
    }

    private void StopImmersiveTimer()
    {
        if (_immersiveActivityTimerToken != null)
        {
            Application.RemoveTimeout(_immersiveActivityTimerToken);
            _immersiveActivityTimerToken = null;
        }
    }

    private void UpdateImmersiveButtonHighlight()
    {
        if (_lyricImmersiveBtn == null) return;
        if (_isImmersiveMode)
        {
            _lyricImmersiveBtn.SetScheme(new Scheme
            {
                Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None),
                Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None),
                HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
            });
        }
        else
        {
            _lyricImmersiveBtn.SetScheme(new Scheme
            {
                Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None),
                Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None),
                HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None)
            });
        }
        _lyricImmersiveBtn.SetNeedsDraw();
    }

    private int GetFocusedWindowIndex(View? focused)
    {
        if (focused == null) return -1;

        for (var v = focused; v != null; v = v.SuperView)
        {
            if (v == _sidebarList || v == _sidebarFrame) return 0;
            if (v == _songListView) return 1;
            if (v == _lyricFrame || v == _lyricListView || v == _artistAlbumDetailView) return 2;
            if (v == _controlBar) return 3;
        }
        return -1;
    }

    private void SetFocusToWindow(int windowIndex)
    {
        _isSearchActive = false;
        _currentFocusedWindowIndex = windowIndex;
        switch (windowIndex)
        {
            case 0:
                _sidebarList.SetFocus();
                break;
            case 1:
                _songListView.SetFocusToList();
                break;
            case 2:
                if (_artistAlbumDetailView.Visible)
                {
                    _artistAlbumDetailView.SetFocusToDesc();
                }
                else
                {
                    _lyricListView.SetFocus();
                }
                break;
            case 3:
                _controlBar.SetFocusToBar();
                break;
            default:
                _songListView.SetFocusToList();
                break;
        }
        Application.Invoke(UpdateFrameBorderHighlights);
    }

    private void UpdateFrameBorderHighlights()
    {
        var focused = Application.Navigation?.GetFocused();
        int focusedWindow = GetFocusedWindowIndex(focused);
        if (focusedWindow == -1)
        {
            focusedWindow = _currentFocusedWindowIndex;
        }
        else
        {
            _currentFocusedWindowIndex = focusedWindow;
        }

        bool sidebarFocused = (focusedWindow == 0);
        bool songListFocused = (focusedWindow == 1);
        bool lyricFocused = (focusedWindow == 2);
        bool controlFocused = (focusedWindow == 3);

        _sidebarFrame.SetScheme(sidebarFocused ? MikuTheme.FrameBorderActive : MikuTheme.FrameBorderDim);
        _sidebarFrame.SetNeedsDraw();

        _songListView.SetScheme(songListFocused ? MikuTheme.FrameBorderActive : MikuTheme.FrameBorderDim);
        _songListView.SetNeedsDraw();

        _lyricFrame.SetScheme(lyricFocused ? MikuTheme.FrameBorderActive : MikuTheme.FrameBorderDim);
        _lyricFrame.SetNeedsDraw();
        if (_artistAlbumDetailView.Visible)
        {
            _artistAlbumDetailView.SetActiveBorder(lyricFocused);
        }

        _controlBar.SetScheme(controlFocused ? MikuTheme.FrameBorderActive : MikuTheme.FrameBorderDim);
        _controlBar.SetNeedsDraw();

        _sidebarTitleLabel?.SetNeedsDraw();
        _songListTitleLabel?.SetNeedsDraw();
        _lyricTitleLabel?.SetNeedsDraw();
    }

    public void UpdateLyricTitle(string text)
    {
        Application.Invoke(() =>
        {
            _lyricTitleLabel.Text = string.IsNullOrEmpty(text) ? "┤歌词├" : $"┤{text}├";
            _lyricTitleLabel.SetNeedsDraw();
            _lyricFrame.SetNeedsDraw();
        });
    }

    private static void UnbindSpaceKey(View view)
    {
        if (view is not TextField)
        {
            view.KeyBindings.Remove(Key.Space);
        }
        foreach (var sub in view.SubViews)
        {
            UnbindSpaceKey(sub);
        }
    }

    private static void UnbindTabKeys(View view)
    {
        if (view is not TextField)
        {
            view.KeyBindings.Remove(Key.Tab);
            view.KeyBindings.Remove(Key.Tab.WithShift);
        }
        foreach (var sub in view.SubViews)
        {
            UnbindTabKeys(sub);
        }
    }

    private void SetupMprisService()
    {
        _mprisService.PlayPauseHandler = () =>
        {
            Application.Invoke(async () =>
            {
                if (_activeSong != null && !_player.IsPlaying && _player.TotalDurationSeconds <= 0)
                {
                    await PlaySongAsync(_activeSong, UserSession.Current.LastPlaybackPositionSeconds);
                }
                else
                {
                    await _player.TogglePauseAsync();
                    UpdatePlayerStatus();
                }
            });
            return Task.CompletedTask;
        };

        _mprisService.PlayHandler = () =>
        {
            Application.Invoke(async () =>
            {
                if (!_player.IsPlaying)
                {
                    if (_activeSong != null && _player.TotalDurationSeconds <= 0)
                    {
                        await PlaySongAsync(_activeSong, UserSession.Current.LastPlaybackPositionSeconds);
                    }
                    else
                    {
                        await _player.TogglePauseAsync();
                        UpdatePlayerStatus();
                    }
                }
            });
            return Task.CompletedTask;
        };

        _mprisService.PauseHandler = () =>
        {
            Application.Invoke(async () =>
            {
                if (_player.IsPlaying)
                {
                    await _player.TogglePauseAsync();
                    UpdatePlayerStatus();
                }
            });
            return Task.CompletedTask;
        };

        _mprisService.StopHandler = () =>
        {
            Application.Invoke(async () =>
            {
                await _player.StopAsync();
                UpdatePlayerStatus();
            });
            return Task.CompletedTask;
        };

        _mprisService.NextHandler = () =>
        {
            Application.Invoke(async () =>
            {
                if (_currentViewMode == ViewMode.GuessRecommend)
                {
                    await PlayNextRadioTrackAsync();
                }
                else
                {
                    await PlayNextInCurrentListAsync();
                }
            });
            return Task.CompletedTask;
        };

        _mprisService.PreviousHandler = () =>
        {
            Application.Invoke(async () =>
            {
                if (_currentViewMode != ViewMode.GuessRecommend)
                {
                    await PlayPrevInCurrentListAsync();
                }
            });
            return Task.CompletedTask;
        };

        _mprisService.SeekHandler = offsetSec =>
        {
            Application.Invoke(async () =>
            {
                var target = Math.Clamp(_player.CurrentPositionSeconds + offsetSec, 0, _player.TotalDurationSeconds > 0 ? _player.TotalDurationSeconds : 3600);
                await _player.SeekAsync(target);
                _mprisService.EmitSeeked(target);
            });
            return Task.CompletedTask;
        };

        _mprisService.SetPositionHandler = targetSec =>
        {
            Application.Invoke(async () =>
            {
                var target = Math.Clamp(targetSec, 0, _player.TotalDurationSeconds > 0 ? _player.TotalDurationSeconds : 3600);
                await _player.SeekAsync(target);
                _mprisService.EmitSeeked(target);
            });
            return Task.CompletedTask;
        };

        _mprisService.VolumeSetHandler = vol0to1 =>
        {
            Application.Invoke(() =>
            {
                int volPercent = (int)Math.Round(vol0to1 * 100);
                _player.SetVolume(volPercent);
                if (volPercent > 0)
                {
                    _preMuteVolume = volPercent;
                }
                UserSession.Current.Volume = volPercent;
                UserSession.Current.Save();
                _controlBar.UpdateVolume(volPercent, volPercent == 0);
                _mprisService.UpdateVolume(volPercent);
            });
        };

        _mprisService.LoopStatusSetHandler = loopStatus =>
        {
            Application.Invoke(() =>
            {
                var newMode = PlaybackModeHelper.FromMpris(loopStatus, _currentPlaybackMode == PlaybackMode.Shuffle);
                SetPlaybackMode(newMode);
            });
        };

        _mprisService.ShuffleSetHandler = shuffle =>
        {
            Application.Invoke(() =>
            {
                var (ls, _) = _currentPlaybackMode.ToMpris();
                var newMode = PlaybackModeHelper.FromMpris(ls, shuffle);
                SetPlaybackMode(newMode);
            });
        };

        _mprisService.QuitHandler = () =>
        {
            Application.Invoke(() =>
            {
                UserSession.Current.Save();
                _player.Dispose();
                _mprisService.Dispose();
                Application.RequestStop();
            });
        };

        Task.Run(async () =>
        {
            await _mprisService.StartAsync();
            _mprisService.UpdatePlaybackMode(_currentPlaybackMode);
            _mprisService.UpdateVolume(_player.Volume);
            if (_activeSong != null)
            {
                _mprisService.UpdateSong(_activeSong);
                _mprisService.UpdatePlaybackStatus(_player.IsPlaying);
            }
        });
    }

    private void TogglePlaybackMode()
    {
        var next = _currentPlaybackMode.Next();
        SetPlaybackMode(next);
        _controlBar.UpdateStatus($"[播放模式: {next.GetName()}]");
    }

    private void SetPlaybackMode(PlaybackMode mode)
    {
        _currentPlaybackMode = mode;
        UserSession.Current.PlaybackMode = mode;
        UserSession.Current.Save();
        _controlBar.UpdatePlaybackMode(mode);
        _mprisService.UpdatePlaybackMode(mode);
    }

    private long _lastToggleNowPlayingTicks = 0;

    private void ToggleNowPlayingView()
    {
        var now = Environment.TickCount64;
        if (now - _lastToggleNowPlayingTicks < 350)
        {
            return;
        }
        _lastToggleNowPlayingTicks = now;

        Application.Invoke(() =>
        {
            if (_isNowPlayingViewActive)
            {
                CloseNowPlayingView();
            }
            else
            {
                OpenNowPlayingView();
            }
        });
    }

    private void OpenNowPlayingView()
    {
        _isNowPlayingViewActive = true;
        _sidebarFrame.Visible = false;
        _songListView.Visible = false;
        _lyricFrame.Visible = false;
        _searchLabel.Visible = false;
        _searchField.Visible = false;
        _userStatusBtn.Visible = false;

        _nowPlayingView.SetSong(_activeSong, AudioQualityHelper.GetBadge(_actualQualityTier));
        _nowPlayingView.SetLyrics(_currentLyrics, _showTranslation);
        _nowPlayingView.SetTranslationState(_showTranslation);
        _nowPlayingView.SetImmersiveState(_isImmersiveMode);
        _nowPlayingView.OnActivated();
        SetNeedsDraw();
    }

    private void CloseNowPlayingView()
    {
        _isNowPlayingViewActive = false;
        _nowPlayingView.OnDeactivated();

        _sidebarFrame.Visible = true;
        _songListView.Visible = true;
        _lyricFrame.Visible = true;
        _searchLabel.Visible = !_isImmersiveMode;
        _searchField.Visible = !_isImmersiveMode;
        _userStatusBtn.Visible = !_isImmersiveMode;

        _isSearchActive = false;
        _songListView.SetFocusToList();
        UpdateFrameBorderHighlights();
        if (_artistAlbumDetailView.Visible)
        {
            _artistAlbumDetailView.OnActivated();
        }
        SetNeedsDraw();
    }
}
