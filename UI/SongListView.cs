using System.Collections.ObjectModel;
using System.Text;
using Rectangle = System.Drawing.Rectangle;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Models;

namespace QQMusic.Tui.UI;

/// <summary>
/// 中央歌曲列表视窗：
/// 1. 宽窗口等比自适应列宽（歌名~48%、歌手~22%、专辑~30%），消除右侧留白；
/// 2. 无“提示:”前缀纯净展示；
/// 3. 超长曲目信息丝滑跑马灯（Marquee）循环平滑滚动；
/// 4. 鼠标悬浮与无限向下滚动加载。
/// </summary>
public enum SongSubColumn
{
    Title,
    Artist,
    Album
}

public sealed partial class SongListView : FrameView
{
    private readonly ListView _listView;
    private readonly List<Song> _songs = [];
    private readonly List<string> _customItems = [];

    public event Func<Song, Task>? SongAccepted;
    public event Func<Task>? LoadMoreRequested;
    public event Action<Song>? ArtistClicked;
    public event Action<Song>? AlbumClicked;

    public IReadOnlyList<Song> Songs => _songs;
    public int? SelectedItem => _listView.SelectedItem;
    public bool IsInnerListFocused => _listView.HasFocus;
    public event EventHandler<HasFocusEventArgs>? InnerFocusChanged
    {
        add => _listView.HasFocusChanged += value;
        remove => _listView.HasFocusChanged -= value;
    }

    private SongSubColumn _focusedSubColumn = SongSubColumn.Title;
    public SongSubColumn FocusedSubColumn => _focusedSubColumn;
    private int _titleColWidth = 20;
    private int _artistColWidth = 12;
    private int _albumColWidth = 14;
    private int _lastHighlightRow = -1;
    private readonly ThinScrollBarView _scrollBar = new();

    private int _lastWidth = -1;
    private object? _viewportResizeTimerToken;
    private object? _loadMoreDebounceToken;
    private string _currentFullTitle = "歌曲列表 (就绪)";
    private string _lastDispatchedTitle = "";
    private int _marqueeOffset = 0;
    private bool _isMarqueePaused = false;
    private object? _timeoutToken;

    public void SetMarqueePaused(bool paused)
    {
        _isMarqueePaused = paused;
    }

    private Func<int, Task>? _customItemAccepted;
    private Action<int>? _customItemSelectionChanged;
    private string? _playingSongMid;
    private bool _isUpdatingDisplay = false;

    private bool _isRadioMode;
    private Song? _currentRadioSong;
    private string _currentRadioQuality = "[SQ]";
    private int _currentRadioPlayedCount = 1;
    private readonly List<string> _radioDisplayLines = [];

    private readonly Label _scrollTopBtn;
    private readonly Label _locatePlayingBtn;

    private long _lastClickTick = 0;
    private int _lastClickRow = -1;
    private int _lastClickCol = -1;

    public event Action<string>? StatusNotification;
    public event Action<string>? DisplayTitleChanged;
    public event Action? Clicked;
    public event Action<bool>? TabNavigationRequested;

    public SongListView()
    {
        Title = "";
        Width = Dim.Percent(58);
        Height = Dim.Fill(5);
        CanFocus = true;
        TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;

        _listView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true
        };
        _listView.KeyBindings.Remove(Key.Space);
        SetScheme(MikuTheme.FrameBorderDim);
        _listView.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                if (!_listView.HasFocus)
                {
                    _listView.SetFocus();
                }
                Clicked?.Invoke();
            }
        };
        MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                if (!_listView.HasFocus)
                {
                    _listView.SetFocus();
                }
                Clicked?.Invoke();
            }
        };

        _listView.SetSource(new ObservableCollection<string>());

        _listView.Accepted += async (s, e) =>
        {
            if (_isRadioMode) return;

            var idx = _listView.SelectedItem ?? -1;
            if (_customItemAccepted != null && idx >= 0)
            {
                await _customItemAccepted.Invoke(idx);
                return;
            }

            if (idx >= 0 && idx < _songs.Count)
            {
                var curSong = _songs[idx];
                if (_focusedSubColumn == SongSubColumn.Artist)
                {
                    if (!curSong.IsLocal)
                    {
                        ArtistClicked?.Invoke(curSong);
                        return;
                    }
                }
                else if (_focusedSubColumn == SongSubColumn.Album)
                {
                    if (!curSong.IsLocal && !string.IsNullOrWhiteSpace(curSong.AlbumMid))
                    {
                        AlbumClicked?.Invoke(curSong);
                        return;
                    }
                }

                if (SongAccepted != null)
                {
                    await SongAccepted.Invoke(curSong);
                }
            }
        };

        // 当前播放歌曲高亮字体颜色（电台卡片与歌单列表区分渲染）
        _listView.RowRender += (s, e) =>
        {
            if (_isRadioMode)
            {
                if (e.Row >= 0 && e.Row < _radioDisplayLines.Count)
                {
                    var text = _radioDisplayLines[e.Row];
                    if (text.Contains("[ 个性电台 · 猜你喜欢 ]"))
                    {
                        e.RowAttribute = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None);
                    }
                    else if (text.Contains("+--") || text.Contains("[ 快捷操作指南 ]"))
                    {
                        e.RowAttribute = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None);
                    }
                    else if (text.Contains("曲名:") || text.Contains("歌手:") || text.Contains("专辑:"))
                    {
                        e.RowAttribute = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Color.None);
                    }
                    else if (text.Contains("[Space]") || text.Contains("[ D ]") || text.Contains("[ R ]") || text.Contains("[ ] / N ]"))
                    {
                        e.RowAttribute = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextSub, Color.None);
                    }
                    else
                    {
                        e.RowAttribute = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None);
                    }
                }
                return;
            }

            if (_songs.Count > 0 && e.Row >= 0 && e.Row < _songs.Count)
            {
                if (!string.IsNullOrEmpty(_playingSongMid) && _songs[e.Row].Mid == _playingSongMid)
                {
                    if (_listView.SelectedItem == e.Row)
                    {
                        // 正在播放且被光标选中：发光浅薄荷绿字 + 清晰海青高光底色
                        e.RowAttribute = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark);
                    }
                    else
                    {
                        // 正在播放但未被光标选中：纯正翡翠绿高亮字（与歌词高亮完全对齐）
                        e.RowAttribute = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None);
                    }
                    return;
                }
            }
        };

        // 监听列表选中项变动：同步更新曲目详情、二级高亮显示并在接近底部时触发分页加载
        _listView.ValueChanged += (s, e) =>
        {
            if (_isRadioMode || _isUpdatingDisplay) return;

            var current = e.NewValue ?? _listView.SelectedItem ?? 0;
            if (_customItemSelectionChanged != null && current >= 0)
            {
                _customItemSelectionChanged.Invoke(current);
            }

            UpdateFocusedRowDisplay();
            UpdateSubColumnTitle(current);
            _scrollBar.UpdateMetrics(_songs.Count, _listView.Viewport.Height, _listView.Viewport.Y);
            CheckTriggerLoadMore();
        };

        _listView.KeyDown += (s, k) =>
        {
            if (k == Key.Tab || k.AsRune.Value == '\t' || k.ToString().Contains("Tab"))
            {
                k.Handled = true;
                TabNavigationRequested?.Invoke(!k.IsShift);
                return;
            }

            if (_isRadioMode) return;

            if (k == Key.CursorLeft)
            {
                k.Handled = true;
                if (_focusedSubColumn == SongSubColumn.Album)
                {
                    _focusedSubColumn = SongSubColumn.Artist;
                    UpdateFocusedRowDisplay();
                    UpdateSubColumnTitle();
                }
                else if (_focusedSubColumn == SongSubColumn.Artist)
                {
                    _focusedSubColumn = SongSubColumn.Title;
                    UpdateFocusedRowDisplay();
                    UpdateSubColumnTitle();
                }
                return;
            }

            if (k == Key.CursorRight)
            {
                k.Handled = true;
                if (_focusedSubColumn == SongSubColumn.Title)
                {
                    _focusedSubColumn = SongSubColumn.Artist;
                    UpdateFocusedRowDisplay();
                    UpdateSubColumnTitle();
                }
                else if (_focusedSubColumn == SongSubColumn.Artist)
                {
                    _focusedSubColumn = SongSubColumn.Album;
                    UpdateFocusedRowDisplay();
                    UpdateSubColumnTitle();
                }
                return;
            }

            if (k == Key.PageUp)
            {
                k.Handled = true;
                PageUpList();
                return;
            }

            if (k == Key.PageDown)
            {
                k.Handled = true;
                PageDownList();
                return;
            }

            if (k == Key.CursorDown || k == Key.CursorUp)
            {
                _scrollBar.TriggerActivity();
                CheckTriggerLoadMore();
            }
            else if (k == Key.Home)
            {
                ScrollToTop();
            }
        };

        _listView.MouseEvent += (s, m) =>
        {
            if (_isRadioMode) return;

            if (m.Flags.HasFlag(MouseFlags.WheeledDown))
            {
                CheckTriggerLoadMore();
            }

            // 用户要求：改成双击才进入歌手/专辑界面，单击用于常规选行以杜绝误触
            if (m.Position.HasValue && (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked)))
            {
                var clickedRow = _listView.Viewport.Y + m.Position.Value.Y;
                int clickX = m.Position.Value.X;
                bool isDoubleClick = m.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked);

                if (!isDoubleClick && m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
                {
                    var now = Environment.TickCount64;
                    if (now - _lastClickTick < 400 && _lastClickRow == clickedRow && Math.Abs(clickX - _lastClickCol) < 8)
                    {
                        isDoubleClick = true;
                        _lastClickTick = 0;
                    }
                    else
                    {
                        _lastClickTick = now;
                        _lastClickRow = clickedRow;
                        _lastClickCol = clickX;

                        int artistStart = 4 + _titleColWidth + 2;
                        int artistEnd = artistStart + _artistColWidth;
                        int albumStart = artistEnd + 2;
                        if (clickX >= artistStart && clickX < artistEnd)
                        {
                            _focusedSubColumn = SongSubColumn.Artist;
                        }
                        else if (clickX >= albumStart)
                        {
                            _focusedSubColumn = SongSubColumn.Album;
                        }
                        else
                        {
                            _focusedSubColumn = SongSubColumn.Title;
                        }
                        UpdateFocusedRowDisplay();
                        UpdateSubColumnTitle(clickedRow);
                    }
                }

                if (isDoubleClick && clickedRow >= 0 && clickedRow < _songs.Count)
                {
                    var clickedSong = _songs[clickedRow];

                    int totalWidth = _listView.Viewport.Width > 0 ? _listView.Viewport.Width : (Viewport.Width > 0 ? Viewport.Width - 2 : 80);
                    int remain = Math.Max(30, totalWidth - 13);
                    int titleColWidth = Math.Max(16, (int)Math.Round(remain * 0.46));
                    int artistColWidth = Math.Max(10, (int)Math.Round(remain * 0.24));

                    int artistStart = 4 + titleColWidth + 2;
                    int artistEnd = artistStart + artistColWidth;
                    int albumStart = artistEnd + 2;

                    if (clickX >= artistStart && clickX < artistEnd)
                    {
                        if (!clickedSong.IsLocal)
                        {
                            ArtistClicked?.Invoke(clickedSong);
                            m.Handled = true;
                            return;
                        }
                    }
                    else if (clickX >= albumStart)
                    {
                        if (!clickedSong.IsLocal && !string.IsNullOrWhiteSpace(clickedSong.AlbumMid))
                        {
                            AlbumClicked?.Invoke(clickedSong);
                            m.Handled = true;
                            return;
                        }
                    }
                }
            }

            // 鼠标悬浮其上时，即刻在顶部 Frame 标题提示显示完整歌名、歌手与专辑全称（跑马灯支持）
            if (m.Position.HasValue)
            {
                var hoverIndex = _listView.Viewport.Y + m.Position.Value.Y;
                if (hoverIndex >= 0 && hoverIndex < _songs.Count)
                {
                    var hSong = _songs[hoverIndex];
                    var albumText = string.IsNullOrWhiteSpace(hSong.Album) ? "单曲" : hSong.Album;
                    SetMarqueeTitle($"《{hSong.Title}》 歌手: {hSong.Artist}  专辑: {albumText}");
                }
            }
        };

        _listView.ViewportChanged += (s, e) =>
        {
            if (!_isRadioMode)
            {
                if (_loadMoreDebounceToken != null)
                {
                    Application.RemoveTimeout(_loadMoreDebounceToken);
                    _loadMoreDebounceToken = null;
                }
                _loadMoreDebounceToken = Application.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
                {
                    _loadMoreDebounceToken = null;
                    CheckTriggerLoadMore();
                    return false;
                });
            }
        };

        // 监听视图视口变化，自适应动态重算列宽或电台卡片排版 (150ms 防抖，防手机软键盘收起展开高频闪烁)
        ViewportChanged += (s, e) =>
        {
            int curW = _listView.Viewport.Width > 0 ? _listView.Viewport.Width : Viewport.Width;
            if (curW > 0 && curW != _lastWidth)
            {
                _lastWidth = curW;
                if (_viewportResizeTimerToken != null)
                {
                    Application.RemoveTimeout(_viewportResizeTimerToken);
                    _viewportResizeTimerToken = null;
                }
                _viewportResizeTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
                {
                    _viewportResizeTimerToken = null;
                    if (_isRadioMode && _currentRadioSong != null)
                    {
                        RefreshRadioDisplay();
                    }
                    else if (_songs.Count > 0)
                    {
                        RefreshDisplayList();
                    }
                    return false;
                });
            }
        };

        Add(_listView);

        // 右下角悬浮操作图标：[▲] 返回顶部 与 [●] 定位到当前播放歌曲（精致括号包装，中间保留 1 行适中间隔）
        _scrollTopBtn = new Label
        {
            Text = "[▲]",
            X = Pos.AnchorEnd(5),
            Y = Pos.AnchorEnd(4),
            Width = 3,
            Height = 1,
            CanFocus = false,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _scrollTopBtn.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None),
            Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
            HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
        });
        _scrollTopBtn.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) ||
                m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                ScrollToTop();
                m.Handled = true;
            }
        };
        Add(_scrollTopBtn);

        _locatePlayingBtn = new Label
        {
            Text = "[●]",
            X = Pos.AnchorEnd(5),
            Y = Pos.AnchorEnd(2),
            Width = 3,
            Height = 1,
            CanFocus = false,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _locatePlayingBtn.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None),
            Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
            HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
        });
        _locatePlayingBtn.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) ||
                m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                LocatePlayingSong();
                m.Handled = true;
            }
        };
        Add(_locatePlayingBtn);

        _scrollBar.X = Pos.AnchorEnd(1);
        _scrollBar.Y = 0;
        _scrollBar.Height = Dim.Fill();
        _scrollBar.ScrollPositionChanged += targetRow =>
        {
            if (_songs.Count > 0)
            {
                int clamped = Math.Clamp(targetRow, 0, _songs.Count - 1);
                _listView.SelectedItem = clamped;
                _listView.Viewport = new Rectangle(_listView.Viewport.X, clamped, _listView.Viewport.Width, _listView.Viewport.Height);
                UpdateSubColumnTitle(clamped);
            }
        };
        Add(_scrollBar);

        // 启动跑马灯定时器（300ms 刷新一次）
        _timeoutToken = Application.AddTimeout(TimeSpan.FromMilliseconds(300), OnMarqueeTick);
    }

    public Song? GetSelectedSong()
    {
        if (_isRadioMode)
        {
            return _currentRadioSong;
        }

        var idx = _listView.SelectedItem ?? -1;
        if (idx >= 0 && idx < _songs.Count)
        {
            return _songs[idx];
        }
        return null;
    }

    public void RemoveSong(Song song)
    {
        if (_isRadioMode) return;

        int idx = _songs.IndexOf(song);
        if (idx >= 0)
        {
            _songs.RemoveAt(idx);
            RefreshDisplayList();
            if (_songs.Count > 0)
            {
                _listView.SelectedItem = Math.Min(idx, _songs.Count - 1);
            }
        }
    }

    public void SetCustomItems(List<string> items, string title, Func<int, Task> onAccepted, Action<int>? onSelectionChanged = null)
    {
        _isRadioMode = false;
        _currentRadioSong = null;
        _radioDisplayLines.Clear();
        _scrollTopBtn.Visible = true;
        _locatePlayingBtn.Visible = true;
        _songs.Clear();
        _customItems.Clear();
        _customItems.AddRange(items);
        _customItemAccepted = onAccepted;
        _customItemSelectionChanged = onSelectionChanged;
        _listView.Viewport = new Rectangle(_listView.Viewport.X, 0, _listView.Viewport.Width, _listView.Viewport.Height);
        _listView.SetSource(new ObservableCollection<string>(_customItems));
        _listView.SelectedItem = _customItems.Count > 0 ? 0 : null;
        _scrollBar.UpdateMetrics(_customItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
        SetMarqueeTitle(title);

        if (items.Count > 0 && onSelectionChanged != null)
        {
            onSelectionChanged(0);
        }
    }

    public void AppendCustomItems(List<string> newItems, string statusTitle)
    {
        _isRadioMode = false;
        _currentRadioSong = null;
        _radioDisplayLines.Clear();
        _scrollTopBtn.Visible = true;
        _locatePlayingBtn.Visible = true;

        int prevSelected = _listView.SelectedItem ?? 0;
        int prevViewportY = _listView.Viewport.Y;

        _customItems.AddRange(newItems);
        _listView.SetSource(new ObservableCollection<string>(_customItems));

        if (prevSelected >= 0 && prevSelected < _customItems.Count)
        {
            _listView.SelectedItem = prevSelected;
        }
        else if (_customItems.Count > 0)
        {
            _listView.SelectedItem = 0;
        }
        else
        {
            _listView.SelectedItem = null;
        }
        if (prevViewportY > 0)
        {
            _listView.Viewport = new Rectangle(_listView.Viewport.X, prevViewportY, _listView.Viewport.Width, _listView.Viewport.Height);
        }
        _scrollBar.UpdateMetrics(_customItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
        SetMarqueeTitle(statusTitle);
    }

    public void SetSongs(List<Song> songs, string statusTitle)
    {
        _isRadioMode = false;
        _currentRadioSong = null;
        _radioDisplayLines.Clear();
        _scrollTopBtn.Visible = true;
        _locatePlayingBtn.Visible = true;
        _customItemAccepted = null;
        _customItemSelectionChanged = null;
        _customItems.Clear();
        _songs.Clear();
        _listView.Viewport = new Rectangle(_listView.Viewport.X, 0, _listView.Viewport.Width, _listView.Viewport.Height);
        _songs.AddRange(songs);
        RefreshDisplayList();
        SetMarqueeTitle(statusTitle);
    }

    public void AppendSongs(List<Song> newSongs, string statusTitle)
    {
        _isRadioMode = false;
        _currentRadioSong = null;
        _radioDisplayLines.Clear();
        _scrollTopBtn.Visible = true;
        _locatePlayingBtn.Visible = true;
        _songs.AddRange(newSongs);
        RefreshDisplayList();
        SetMarqueeTitle(statusTitle);
    }

    public void SetMessage(string message, string title)
    {
        _isRadioMode = false;
        _currentRadioSong = null;
        _radioDisplayLines.Clear();
        _scrollTopBtn.Visible = true;
        _locatePlayingBtn.Visible = true;
        _customItemAccepted = null;
        _songs.Clear();
        _customItems.Clear();
        _listView.SetSource(new ObservableCollection<string> { message });
        SetMarqueeTitle(title);
    }

    public void SetRadioCard(Song song, string qualityBadge, int playedCount)
    {
        _isRadioMode = true;
        _currentRadioSong = song;
        _currentRadioQuality = string.IsNullOrWhiteSpace(qualityBadge) ? "[标准]" : qualityBadge;
        _currentRadioPlayedCount = Math.Max(1, playedCount);
        _customItemAccepted = null;
        _customItems.Clear();
        _songs.Clear();
        _playingSongMid = song.Mid;

        _scrollTopBtn.Visible = false;
        _locatePlayingBtn.Visible = false;

        SetMarqueeTitle($"[电台] 猜你喜欢 · 《{song.Title}》 - {song.Artist}");
        RefreshRadioDisplay();
    }
}
