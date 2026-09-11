using System.Collections.ObjectModel;
using System.Text;
using Rectangle = System.Drawing.Rectangle;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Player;
using QQMusic.Tui.Services;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.UI;

/// <summary>
/// 主视窗：协调顶部搜索栏、左侧导航栏、中央歌曲列表、右侧歌词视窗与底部现代化控制栏
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly IPlayer _player;
    private readonly bool _isWebMode;
    private long _lastUserActivityTick = Environment.TickCount64;
    private object? _aodInactivityTimerToken;
    private readonly SystemMediaSessionService _mprisService;
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
    private readonly Button _recognizeBtn;
    private readonly Button _webBtn;
    private WebPlaybackServer? _standaloneWebServer;
    private string? _currentPlayUrl;
    private bool _isTuiAudioDisabled = false;
    private bool _isWebPlaying = false;
    private double _webVirtualPosition = 0;
    private object? _webVirtualTickerToken;
    private readonly NowPlayingView _nowPlayingView;
    private bool _isNowPlayingViewActive = false;
    private readonly AodView _aodView;
    private bool _isAodMode = false;
    private bool _isSearchActive = false;
    private readonly QuickSearchFloatingBar _quickSearchBar;
    private long _lastTransClickTicks;
    private long _lastMatchClickTicks;
    private readonly Label _lyricTransBtn;
    private readonly Label _lyricImmersiveBtn;
    private readonly Label _lyricMatchBtn;
    private bool _isImmersiveMode = false;
    private long _lastImmersiveActivityTick = 0;
    private object? _immersiveActivityTimerToken;
    private bool _showTranslation = true;
    private bool _hasTranslation = false;

    private readonly List<LyricLine> _currentLyrics = [];
    private readonly List<int> _lyricItemToLineIndex = [];
    private readonly Dictionary<int, int> _lyricLineToFirstItemIndex = [];
    private int _lastLyricViewportWidth = 0;
    private object? _lyricResizeTimerToken;
    private int _currentActiveLyricIndex = -1;
    private Song? _activeSong;
    private AudioQualityTier _preferredQualityTier;
    private AudioQualityTier _actualQualityTier;

    private string _lastSearchQuery = "";
    private int _searchCurrentPage = 1;
    private bool _isLoadingMore = false;
    private bool _hasMoreSearchResults = false;
    private bool _isSearching = false;
    private SearchOverview? _searchOverview;
    private SearchCategory? _searchCategory;
    private List<Song> _searchSongItems = [];
    private List<SearchSinger> _searchSingerItems = [];
    private List<SearchAlbum> _searchAlbumItems = [];
    private List<SearchPlaylist> _searchPlaylistItems = [];
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
        WebDav,
        Other
    }
    private ViewMode _currentViewMode = ViewMode.Other;

    // 猜你喜欢（个性电台流模式）动态预取队列与收听状态
    private readonly List<Song> _radioQueue = [];
    private int _radioIndex = 0;
    private int _radioPlayedCount = 0;
    private bool _isRadioPrefetching = false;

    // 终端窗口前后台焦点状态与 ANSI 1004 Focus Reporting 过滤状态机
    public static bool IsTerminalWindowFocused { get; private set; } = true;
    private long _lastEscRcvTick = 0;
    private bool _sawBracketAfterEsc = false;
    private int _escSequenceCounter = 0;

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
    private int _webServerPort = 9999;
    private long _lastUserLyricScrollTick = 0;
    private long _lastProgressSaveTick = 0;

    public MainWindow(IPlayer player, bool isWebMode = false)
    {
        _player = player;
        _isWebMode = isWebMode;

        if (_player is WebPlayer webPlayer)
        {
            _standaloneWebServer = webPlayer.Server;
            _webServerPort = webPlayer.Port;
            AttachWebServerEvents(_standaloneWebServer);
        }

        // 载入持久化登录凭证并在后台自动补齐官方 musickey
        UserSession.Load();
        _preferredQualityTier = AudioQualityHelper.Parse(UserSession.Current.PreferredQuality);
        _actualQualityTier = _preferredQualityTier;
        _currentPlaybackMode = UserSession.Current.PlaybackMode;
        PlaybackQueueService.Instance.Mode = _currentPlaybackMode;
        int initialVolume = UserSession.Current.Volume > 0 ? UserSession.Current.Volume : 80;
        _preMuteVolume = initialVolume;
        _player.SetVolume(initialVolume);

        _mprisService = new SystemMediaSessionService();
        SetupMprisService();

        Task.Run(async () =>
        {
            var keyReady = await LoginService.EnsureMusicKeyAsync();
            var profileReady = UserSession.Current.IsLoggedIn &&
                               await QqMusicApi.RefreshCurrentUserProfileAsync();
            if (keyReady || profileReady)
            {
                Application.Invoke(UpdateTopRightButtonsLayout);
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

        // 顶部用户状态按钮：通过点击或 U 快捷键唤起
        _userStatusBtn = new Button
        {
            Text = GetUserStatusText(),
            NoDecorations = true,
            Y = 0,
            ShadowStyle = ShadowStyles.None,
            CanFocus = false
        };
        _userStatusBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _userStatusBtn.KeyBindings.Remove(Key.Space);
        _userStatusBtn.Accepting += (s, e) => ShowLoginDialog();
        Add(_userStatusBtn);

        // 顶部听歌识曲按钮：位于账号按钮左侧
        _recognizeBtn = new Button
        {
            Text = "[R] 识曲",
            NoDecorations = true,
            Y = 0,
            ShadowStyle = ShadowStyles.None,
            CanFocus = false
        };
        _recognizeBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _recognizeBtn.KeyBindings.Remove(Key.Space);
        _recognizeBtn.Accepting += (s, e) => ShowAudioRecognitionDialog();
        Add(_recognizeBtn);

        // 顶部 Web 协同按钮：独立位于账号按钮右侧
        _webBtn = new Button
        {
            Text = "[W] Web",
            NoDecorations = true,
            Y = 0,
            ShadowStyle = ShadowStyles.None,
            CanFocus = false
        };
        _webBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _webBtn.KeyBindings.Remove(Key.Space);
        _webBtn.Accepting += (s, e) => HandleWebButtonClicked();
        Add(_webBtn);

        _searchField = new TextField
        {
            X = Pos.Right(_searchLabel) + 1,
            Y = 0,
            Width = Dim.Fill(36),
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

        // 初始化顶部按钮的独立防重叠自适应布局
        UpdateTopRightButtonsLayout();
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
            else if (k == Key.CursorDown || (k.IsCtrl && char.ToUpperInvariant((char)k.AsRune.Value) == 'J'))
            {
                k.Handled = true;
                await ShowSearchSuggestionsAsync();
            }
            else if (k == Key.Tab || k.ToString().Contains("Tab"))
            {
                k.Handled = true;
                _isSearchActive = false;
                _searchField.CanFocus = false;
                SwitchNextFocusWindow(!k.IsShift);
            }
            else if (k == Key.Esc || k == Key.CursorUp)
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
            "本地音乐",
            "WebDAV"
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
        _songListView.SongAccepted += async song =>
        {
            if (_currentViewMode != ViewMode.GuessRecommend && _songListView.Songs.Count > 0)
            {
                int curIdx = _songListView.SelectedItem ?? 0;
                if (curIdx < 0 || curIdx >= _songListView.Songs.Count)
                {
                    curIdx = _songListView.Songs.ToList().FindIndex(s => s.Mid == song.Mid);
                    if (curIdx < 0) curIdx = 0;
                }
                PlaybackQueueService.Instance.Mode = _currentPlaybackMode;
                PlaybackQueueService.Instance.SetQueue(_songListView.Songs, curIdx);
            }
            await PlaySongAsync(song);
        };
        _songListView.SongPlayNextRequested += song =>
        {
            PlaybackQueueService.Instance.InsertNext(song);
            _controlBar?.UpdateStatus($"下一首将播放: {song.Title} - {song.Artist}");
        };
        _songListView.TabNavigationRequested += forward => SwitchNextFocusWindow(forward);
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

        // 3.5. 主列表即时查找悬浮窗 (G 键触发) - 置于中央歌曲列表视窗内靠上居中，彻底消除与右侧分割线重叠
        _quickSearchBar = new QuickSearchFloatingBar
        {
            X = Pos.Center(),
            Y = 1
        };
        _quickSearchBar.SearchProvider = kw => _songListView.PerformInListSearch(kw);
        _quickSearchBar.RowSelected += rowIdx => _songListView.ScrollToAndSelectItem(rowIdx);
        _quickSearchBar.DismissRequested += () =>
        {
            _songListView.SetFocusToList();
        };
        _songListView.Add(_quickSearchBar);

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
            else if (idx == 8)
            {
                _isViewingPlaylistsList = false;
                _currentDrilldownPlaylist = null;
                _isViewingAlbumsList = false;
                _currentDrilldownAlbum = null;
                await LoadWebDavMusicAsync();
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
                if (_lyricResizeTimerToken != null)
                {
                    Application.RemoveTimeout(_lyricResizeTimerToken);
                    _lyricResizeTimerToken = null;
                }
                _lyricResizeTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
                {
                    _lyricResizeTimerToken = null;
                    RefreshLyricListView();
                    return false;
                });
            }
        };
        _lyricListView.RowRender += (s, e) =>
        {
            if (_currentActiveLyricIndex >= 0 && e.Row >= 0 && e.Row < _lyricItemToLineIndex.Count)
            {
                int lineIdx = _lyricItemToLineIndex[e.Row];
                if (lineIdx == _currentActiveLyricIndex)
                {
                    // 当前播放句：高亮显示
                    e.RowAttribute = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None);
                    return;
                }
            }

            // 未播放行与空行样式
            e.RowAttribute = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqTextLyricDim, Color.None);
        };
        bool isLyricMouseInButtonArea = false;
        _lyricListView.MouseEvent += (s, m) =>
        {
            int frameW = _lyricFrame.Viewport.Width;
            int frameH = _lyricFrame.Viewport.Height;
            if (frameW > 0 && frameH > 0 && m.Position is { } pos && pos.X >= frameW - 18 && pos.Y >= frameH - 3)
            {
                isLyricMouseInButtonArea = true;
                m.Handled = true;
                return;
            }
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                isLyricMouseInButtonArea = false;
            }
            if (m.Flags.HasFlag(MouseFlags.WheeledUp) || m.Flags.HasFlag(MouseFlags.WheeledDown) ||
                m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                _lastUserLyricScrollTick = Environment.TickCount64;
                SetFocusToWindow(2);
            }
        };
        _lyricFrame.MouseEvent += (s, m) =>
        {
            int frameW = _lyricFrame.Viewport.Width;
            int frameH = _lyricFrame.Viewport.Height;
            if (frameW > 0 && frameH > 0 && m.Position is { } pos && pos.X >= frameW - 18 && pos.Y >= frameH - 3)
            {
                isLyricMouseInButtonArea = true;
                m.Handled = true;
                return;
            }
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                isLyricMouseInButtonArea = false;
            }
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
            if (isLyricMouseInButtonArea) return;
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

        // 歌词界面右下角按钮排布（严格物理对齐）：
        // 上行：            [Y] 匹配
        // 下行：[T]译    [P] 全屏
        _lyricTransBtn = new Label
        {
            Text = "[T]译",
            X = Pos.AnchorEnd(16),
            Y = Pos.AnchorEnd(1),
            Width = 5,
            Height = 1,
            CanFocus = false,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop,
            HotKeySpecifier = (Rune)0
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

        _lyricImmersiveBtn = new Label
        {
            Text = "[P] 全屏",
            X = Pos.AnchorEnd(9),
            Y = Pos.AnchorEnd(1),
            Width = 8,
            Height = 1,
            CanFocus = false,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop,
            HotKeySpecifier = (Rune)0
        };
        _lyricImmersiveBtn.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                ToggleImmersiveMode();
                m.Handled = true;
            }
        };

        _lyricMatchBtn = new Label
        {
            Text = "[Y] 匹配",
            X = Pos.AnchorEnd(9),
            Y = Pos.AnchorEnd(2),
            Width = 8,
            Height = 1,
            CanFocus = false,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop,
            HotKeySpecifier = (Rune)0,
            Visible = false
        };
        _lyricMatchBtn.MouseEvent += async (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                var now = Environment.TickCount64;
                if (now - _lastMatchClickTicks > 300)
                {
                    _lastMatchClickTicks = now;
                    await MatchOrRestoreLyricAsync();
                }
                m.Handled = true;
            }
        };

        _lyricFrame.Add(_lyricTransBtn, _lyricImmersiveBtn, _lyricMatchBtn);
        UpdateTranslationButtonHighlight();
        UpdateImmersiveButtonHighlight();
        UpdateLyricMatchButtonHighlight();

        _artistAlbumDetailView = new ArtistAlbumDetailView
        {
            Visible = false
        };
        _artistAlbumDetailView.Clicked += () => SetFocusToWindow(2);
        _artistAlbumDetailView.TabNavigationRequested += forward => SwitchNextFocusWindow(forward);
        _artistAlbumDetailView.SubModeRequested += () => _ = ToggleSingerSubModeAsync();
        _artistAlbumDetailView.OrderRequested += () => _ = ToggleSingerSongOrderAsync();
        _artistAlbumDetailView.FavoriteRequested += () => _ = ToggleSingerFavoriteAsync();
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
            await TogglePlayOrPauseAsync();
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
        _controlBar.ShareClicked += HandleShareCurrentSong;
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
            double totalSec = _player.TotalDurationSeconds > 0
                ? _player.TotalDurationSeconds
                : (_activeSong?.Duration > 0 ? _activeSong.Duration : (_standaloneWebServer?.TotalDurationSeconds ?? 0));

            if (totalSec > 0)
            {
                var targetSec = Math.Clamp(ratio * totalSec, 0, totalSec);
                await SeekPlaybackPositionAsync(targetSec);
            }
        };
        _controlBar.UpdateVolume(initialVolume, initialVolume == 0);
        if (UserSession.Current.LastPlayedSong != null)
        {
            var lastSong = UserSession.Current.LastPlayedSong;
            _activeSong = lastSong;
            _controlBar.SetCurrentSong(lastSong);
            _controlBar.SetLocalMode(lastSong.IsLocal || lastSong.IsWebDav);
            _controlBar.UpdateQuality(AudioQualityHelper.GetBadge(_actualQualityTier));
            if (lastSong.Duration > 0 && UserSession.Current.LastPlaybackPositionSeconds > 0)
            {
                UpdateProgress(UserSession.Current.LastPlaybackPositionSeconds);
            }
            if (_standaloneWebServer != null)
            {
                _standaloneWebServer.CurrentSong = lastSong;
                _standaloneWebServer.TotalDurationSeconds = lastSong.Duration;
                _standaloneWebServer.CurrentPositionSeconds = UserSession.Current.LastPlaybackPositionSeconds;
                _standaloneWebServer.ActualQualityTier = _actualQualityTier;
                _standaloneWebServer.PreferredQualityTier = _preferredQualityTier;
                _standaloneWebServer.BroadcastState("sync");
            }
            UpdateLyricMatchButtonHighlight();
        }
        _controlBar.FavoriteClicked += async () =>
        {
            var targetSong = _activeSong ?? _songListView.GetSelectedSong();
            if (targetSong == null)
            {
                _controlBar.UpdateStatus("[操作提示] 当前暂无播放曲目，请先选择歌曲或点播");
                return;
            }
            if (targetSong.IsLocal || targetSong.IsWebDav)
            {
                _controlBar.UpdateStatus("本地/WebDAV 曲目不支持在线收藏");
                return;
            }
            await ToggleSongFavoriteAsync(targetSong);
        };
        Add(_controlBar);

        // 底部快捷键操作指南（独立放置在控制栏UI方框下方最底行，干净平整无边框干扰）
        _hotkeyHintLabel = new Label
        {
            Text = " [V]播放界面  [B]通知  [M]静音  [/]搜索  [E]队列  [G]查找  [N]插入",
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
            await SeekPlaybackPositionAsync(time.TotalSeconds);
        };
        _nowPlayingView.ToggleTranslationRequested += ToggleTranslation;
        _nowPlayingView.ToggleImmersiveRequested += ToggleImmersiveMode;
        _nowPlayingView.ArtistDrilldownRequested += HandleNowPlayingArtistClicked;
        _nowPlayingView.AlbumDrilldownRequested += (song) =>
        {
            CloseNowPlayingView();
            OnAlbumClicked(song);
        };
        _nowPlayingView.FocusControlBarRequested += () =>
        {
            SetFocusToWindow(3);
        };
        _nowPlayingView.FocusChangedNotification += () =>
        {
            Application.Invoke(UpdateFrameBorderHighlights);
        };
        _nowPlayingView.ShowQueueRequested += ShowQueueDrawerDialog;
        _nowPlayingView.MatchLyricRequested += async () => await MatchOrRestoreLyricAsync();
        _nowPlayingView.LoginRequested += ShowLoginDialog;
        Add(_nowPlayingView);
        _aodView = new AodView
        {
            Visible = false
        };
        Add(_aodView);
        if (_isWebMode)
        {
            _lastUserActivityTick = Environment.TickCount64;
            _aodInactivityTimerToken = Application.AddTimeout(TimeSpan.FromSeconds(1), () =>
            {
                if (!_isAodMode && Environment.TickCount64 - _lastUserActivityTick >= 15000)
                {
                    Application.Invoke(EnterAodMode);
                }
                return true;
            });
            if (_player is WebPlayer wp)
            {
                _controlBar.UpdateStatus($"Web播放已就绪: {wp.Url} (15秒无操作息屏)");
            }
        }
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

        // 鼠标活动唤醒沉浸模式下自动隐藏的图标与刷新无操作看门狗
        MouseEvent += (s, m) =>
        {
            IsTerminalWindowFocused = true;
            _lastUserActivityTick = Environment.TickCount64;
            TriggerImmersiveActivity();
        };

        // 全局顶层按键预捕获，除搜索框文字输入外，统一拦截分发全局播放与视图快捷键
        // 全局顶层按键预捕获，除搜索框文字输入外，统一拦截分发全局播放与视图快捷键
        Application.KeyDown += async (s, k) => await HandleGlobalKeyDownAsync(k);

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
            SetFocusToWindow(0);
            UpdateFrameBorderHighlights();
            if (!UserSession.Current.IsLoggedIn)
            {
                ShowLoginDialog();
            }
            return false;
        });
        SetFocusToWindow(0);

        // 后台预热收藏曲目 ID 缓存，用于更新收藏状态
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

    private async Task SeekPlaybackPositionAsync(double targetSec)
    {
        double totalSec = _player.TotalDurationSeconds > 0
            ? _player.TotalDurationSeconds
            : (_activeSong?.Duration > 0 ? _activeSong.Duration : (_standaloneWebServer?.TotalDurationSeconds ?? 0));

        if (totalSec > 0)
        {
            targetSec = Math.Clamp(targetSec, 0, totalSec);
        }

        if (_isTuiAudioDisabled)
        {
            _webVirtualPosition = targetSec;
            if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
            {
                _standaloneWebServer.CurrentPositionSeconds = targetSec;
                _standaloneWebServer.BroadcastState("seek");
            }
            Application.Invoke(() =>
            {
                UpdateProgress(targetSec);
                UpdateLyrics(targetSec);
            });
        }
        else
        {
            await _player.SeekAsync(targetSec);
            if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
            {
                _standaloneWebServer.CurrentPositionSeconds = targetSec;
                _standaloneWebServer.BroadcastState("seek");
            }
            Application.Invoke(() =>
            {
                UpdateProgress(targetSec);
                UpdateLyrics(targetSec);
            });
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {

            try
            {
                DisableWebAodWatchdog();
                if (_standaloneWebServer != null)
                {
                    if (_standaloneWebServer.IsRunning)
                    {
                        _standaloneWebServer.Stop();
                    }
                    _standaloneWebServer.Dispose();
                    _standaloneWebServer = null;
                }
            }
            catch {}

            try { _mprisService.Dispose(); } catch {}
            try { _player.Dispose(); } catch {}
        }
        base.Dispose(disposing);
    }
}
