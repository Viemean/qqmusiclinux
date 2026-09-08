using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Models;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Color = Terminal.Gui.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;

namespace QQMusic.Tui.UI;

/// <summary>
/// 全屏播放界面
/// 左侧：专辑图与歌曲信息（歌手、歌曲名、专辑）
/// 右侧：居中同步歌词
/// </summary>
public sealed partial class NowPlayingView : View
{
    private readonly View _coverContainer;
    private readonly Label _unsupportedLabel1;
    private readonly Label _unsupportedLabel2;
    private readonly Label _unsupportedLabel3;

    // 封面正下方与封面始终保持左对齐的歌曲信息展示区
    private readonly View _songInfoContainer;
    private readonly InteractiveLinkView _artistLink;
    private readonly Label _hyphenLabel;
    private readonly Label _songTitleLabel;
    private readonly InteractiveLinkView _albumLink;

    private readonly View _lyricContainer;
    private readonly ListView _lyricListView;
    private readonly ThinScrollBarView _lyricScrollBar;
    private readonly Label _transBtn;
    private readonly Label _immersiveBtn;
    private readonly Label _matchLyricBtn;

    private Song? _currentSong;
    private string? _coverFilePath;
    private List<LyricLine> _currentLyrics = new();
    private readonly List<int> _lyricItemToLineIndex = new();
    private readonly Dictionary<int, int> _lyricLineToFirstItemIndex = new();
    private int _currentActiveLyricIndex = -1;
    private long _lastUserLyricScrollTick;
    private bool _showTranslation = true;
    private bool _isImmersiveMode = false;
    private bool _isLyricMatched = false;
    private long _lastImmersiveActivityTick = 0;
    private long _lastInteractiveActivityTick = 0;
    private bool _isInteractiveHighlightSuppressed = false;
    private object? _immersiveTimerToken;
    private int _lastViewportWidth;
    private int _lastRenderCols;
    private int _lastRenderRows;
    private object? _resizeTimerToken;
    private object? _lyricResizeTimerToken;
    private long _lastTransClickTicks;
    private long _lastMatchClickTicks;

    public event Action? BackRequested;
    public event Action<TimeSpan>? SeekRequested;
    public event Action? ToggleTranslationRequested;
    public event Action? ToggleImmersiveRequested;
    public event Action? MatchLyricRequested;
    public event Action<Song>? ArtistDrilldownRequested;
    public event Action<Song>? AlbumDrilldownRequested;
    public event Action? FocusControlBarRequested;
    public event Action? LoginRequested;
    public event Action? FocusChangedNotification;
    public event Action? ShowQueueRequested;

    public NowPlayingView()
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill(5); // 留出底栏 5 行高度
        Visible = false;
        CanFocus = true;

        bool isImageSupported = TerminalImageHelper.IsImageSupported;

        // 1. 左侧面板：纯净无边框封面容器 (终端支持图片显示协议时显示并占 48% 宽度；不支持时完全隐藏)
        _coverContainer = new View
        {
            X = 0,
            Y = 0,
            Width = isImageSupported ? Dim.Percent(48) : 0,
            Height = Dim.Fill(),
            CanFocus = isImageSupported,
            Visible = isImageSupported
        };

        // 不支持图形协议时的提示标签 (在无图终端模式下直接隐藏左侧区域，无需占位显示)
        _unsupportedLabel1 = new Label
        {
            Text = "[ 当前终端不支持图形协议 ]",
            X = Pos.Center(),
            Y = Pos.Center() - 1,
            Visible = false
        };
        _unsupportedLabel1.SetScheme(MikuTheme.PlayerBar);

        _unsupportedLabel2 = new Label
        {
            Text = "建议切换支持 Kitty 图像协议的终端",
            X = Pos.Center(),
            Y = Pos.Center() + 1,
            Visible = false
        };
        _unsupportedLabel2.SetScheme(MikuTheme.Base);

        _unsupportedLabel3 = new Label
        {
            Text = "(如 Kitty / WezTerm / Ghostty)",
            X = Pos.Center(),
            Y = Pos.Center() + 2,
            Visible = false
        };
        _unsupportedLabel3.SetScheme(MikuTheme.Base);

        _coverContainer.Add(_unsupportedLabel1, _unsupportedLabel2, _unsupportedLabel3);

        // 封面下方歌曲信息展示区（两行纯净展示，歌手与专辑间隔一行距离，与封面始终保持绝对左对齐）
        // 第 0 行：歌手 - 歌曲名
        // 第 1 行：留空（间隔一行）
        // 第 2 行：专辑名字
        _songInfoContainer = new View
        {
            X = 1,
            Y = Pos.AnchorEnd(4),
            Width = Dim.Fill(2),
            Height = 3,
            CanFocus = true
        };

        _artistLink = new InteractiveLinkView("");
        _artistLink.X = 0;
        _artistLink.Y = 0;

        _hyphenLabel = new Label
        {
            Text = " - ",
            X = Pos.Right(_artistLink),
            Y = 0,
            Width = 3,
            Height = 1,
            CanFocus = false
        };
        _hyphenLabel.SetScheme(new Scheme
        {
            Normal = new Attribute(MikuTheme.QqTextLyricDim, Color.None)
        });

        _songTitleLabel = new Label
        {
            Text = "",
            X = Pos.Right(_hyphenLabel),
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            CanFocus = false
        };
        _songTitleLabel.SetScheme(new Scheme
        {
            Normal = new Attribute(Color.White, Color.None)
        });

        _albumLink = new InteractiveLinkView("");
        _albumLink.X = 0;
        _albumLink.Y = 2; // 间隔一行距离
        _albumLink.Width = Dim.Fill();

        _artistLink.LinkSelected += () =>
        {
            TriggerInteractiveActivity();
            if (_currentSong != null)
            {
                ArtistDrilldownRequested?.Invoke(_currentSong);
            }
        };
        _artistLink.NavigateNextRequested += () =>
        {
            _albumLink.SetFocus();
            TriggerInteractiveActivity();
            FocusChangedNotification?.Invoke();
        };

        _albumLink.LinkSelected += () =>
        {
            TriggerInteractiveActivity();
            if (_currentSong != null)
            {
                AlbumDrilldownRequested?.Invoke(_currentSong);
            }
        };
        _albumLink.NavigatePrevRequested += () =>
        {
            _artistLink.SetFocus();
            TriggerInteractiveActivity();
            FocusChangedNotification?.Invoke();
        };

        _artistLink.HasFocusChanged += (s, e) => FocusChangedNotification?.Invoke();
        _albumLink.HasFocusChanged += (s, e) => FocusChangedNotification?.Invoke();

        _songInfoContainer.Add(_artistLink, _hyphenLabel, _songTitleLabel, _albumLink);
        _coverContainer.Add(_songInfoContainer);
        Add(_coverContainer);

        // 2. 右侧面板：纯净无边框大视窗歌词 (无图终端模式下占满全宽并水平居中歌词)
        _lyricContainer = new View
        {
            X = isImageSupported ? Pos.Right(_coverContainer) : 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = false
        };

        _lyricListView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = false, // 歌词列表不抢占 Tab 焦点
            TabStop = TabBehavior.NoStop
        };
        _lyricListView.KeyBindings.Remove(Key.Space);
        _lyricListView.SetScheme(MikuTheme.Lyric);

        _lyricListView.ViewportChanged += (s, e) =>
        {
            int curW = _lyricListView.Viewport.Width;
            if (curW > 0 && curW != _lastViewportWidth)
            {
                _lastViewportWidth = curW;
                if (_lyricResizeTimerToken != null)
                {
                    Application.RemoveTimeout(_lyricResizeTimerToken);
                    _lyricResizeTimerToken = null;
                }
                _lyricResizeTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
                {
                    _lyricResizeTimerToken = null;
                    RefreshLyrics();
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
                    e.RowAttribute = new Attribute(MikuTheme.QqGreenPrimary, Color.None);
                    return;
                }
            }
            e.RowAttribute = new Attribute(MikuTheme.QqTextLyricDim, Color.None);
        };

        bool isInBottomRightButtonArea = false;
        _lyricListView.MouseEvent += (s, m) =>
        {
            int containerW = _lyricContainer.Viewport.Width;
            int containerH = _lyricContainer.Viewport.Height;
            if (containerW > 0 && containerH > 0 && m.Position is { } pos && pos.X >= containerW - 18 && pos.Y >= containerH - 3)
            {
                isInBottomRightButtonArea = true;
                m.Handled = true;
                return;
            }
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                isInBottomRightButtonArea = false;
            }
            if (m.Flags.HasFlag(MouseFlags.WheeledUp) || m.Flags.HasFlag(MouseFlags.WheeledDown) ||
                m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                _lastUserLyricScrollTick = Environment.TickCount64;
                TriggerImmersiveActivity();
                TriggerInteractiveActivity();
            }
        };

        _lyricListView.Accepting += (s, e) =>
        {
            if (isInBottomRightButtonArea)
            {
                e.Handled = true;
                return;
            }
            int selectedIdx = _lyricListView.SelectedItem ?? -1;
            if (selectedIdx >= 0 && selectedIdx < _lyricItemToLineIndex.Count)
            {
                int lineIdx = _lyricItemToLineIndex[selectedIdx];
                if (lineIdx >= 0 && lineIdx < _currentLyrics.Count)
                {
                    SeekRequested?.Invoke(_currentLyrics[lineIdx].Timestamp);
                }
            }
        };

        _lyricContainer.Add(_lyricListView);

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
                TriggerImmersiveActivity();
                TriggerInteractiveActivity();
            }
        };
        _lyricContainer.Add(_lyricScrollBar);

        // 歌词区右下角按钮排布（严格物理对齐）：
        // 上行：            [Y] 匹配
        // 下行：[T]译    [P] 全屏
        _transBtn = new Label
        {
            Text = "[T]译",
            X = Pos.AnchorEnd(16),
            Y = Pos.AnchorEnd(1),
            Width = 5,
            Height = 1,
            CanFocus = false,
            TabStop = TabBehavior.NoStop,
            HotKeySpecifier = (Rune)0
        };
        _transBtn.MouseEvent += (s, m) =>
        {
            TriggerImmersiveActivity();
            TriggerInteractiveActivity();
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                var now = Environment.TickCount64;
                if (now - _lastTransClickTicks > 200)
                {
                    _lastTransClickTicks = now;
                    ToggleTranslationRequested?.Invoke();
                }
                m.Handled = true;
            }
        };

        _immersiveBtn = new Label
        {
            Text = "[P] 全屏",
            X = Pos.AnchorEnd(9),
            Y = Pos.AnchorEnd(1),
            Width = 8,
            Height = 1,
            CanFocus = false,
            TabStop = TabBehavior.NoStop,
            HotKeySpecifier = (Rune)0
        };
        _immersiveBtn.MouseEvent += (s, m) =>
        {
            TriggerImmersiveActivity();
            TriggerInteractiveActivity();
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                ToggleImmersiveRequested?.Invoke();
                m.Handled = true;
            }
        };

        _matchLyricBtn = new Label
        {
            Text = "[Y] 匹配",
            X = Pos.AnchorEnd(9),
            Y = Pos.AnchorEnd(2),
            Width = 8,
            Height = 1,
            CanFocus = false,
            TabStop = TabBehavior.NoStop,
            HotKeySpecifier = (Rune)0,
            Visible = false
        };
        _matchLyricBtn.MouseEvent += (s, m) =>
        {
            TriggerImmersiveActivity();
            TriggerInteractiveActivity();
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                var now = Environment.TickCount64;
                if (now - _lastMatchClickTicks > 300)
                {
                    _lastMatchClickTicks = now;
                    MatchLyricRequested?.Invoke();
                }
                m.Handled = true;
            }
        };

        _lyricContainer.Add(_transBtn, _immersiveBtn, _matchLyricBtn);
        UpdateTransButtonHighlight();
        UpdateImmersiveButtonHighlight();
        UpdateMatchLyricButtonHighlight();

        Add(_lyricContainer);

        // 鼠标活动唤醒沉浸模式下自动隐藏的图标与交互高亮
        MouseEvent += (s, m) =>
        {
            TriggerImmersiveActivity();
            TriggerInteractiveActivity();
        };

        // 键盘快捷键监听：按 P 切换沉浸，按 Esc/V 退出沉浸或返回主界面
        KeyDown += (s, k) =>
        {
            TriggerImmersiveActivity();
            TriggerInteractiveActivity();

            var ch = char.ToUpperInvariant((char)k.AsRune.Value);
            if (ch == 'E')
            {
                ShowQueueRequested?.Invoke();
                k.Handled = true;
                return;
            }

            if (ch == 'Y')
            {
                MatchLyricRequested?.Invoke();
                k.Handled = true;
                return;
            }

            if (ch == 'T')
            {
                ToggleTranslationRequested?.Invoke();
                k.Handled = true;
                return;
            }

            if (ch == 'P')
            {
                ToggleImmersiveRequested?.Invoke();
                k.Handled = true;
                return;
            }

            if (ch == 'U')
            {
                LoginRequested?.Invoke();
                k.Handled = true;
                return;
            }

            if (k == Key.Esc)
            {
                if (_isImmersiveMode)
                {
                    ToggleImmersiveRequested?.Invoke();
                    k.Handled = true;
                    return;
                }
                BackRequested?.Invoke();
                k.Handled = true;
                return;
            }

            if (k == Key.V || ch == 'V')
            {
                BackRequested?.Invoke();
                k.Handled = true;
                return;
            }

            // Tab 键在交互项目与底栏之间流转
            if (k == Key.Tab || k.AsRune.Value == '\t' || k.ToString().Contains("Tab"))
            {
                HandleTabNavigation(!k.IsShift);
                k.Handled = true;
                return;
            }
        };

        // 响应终端窗口尺寸变动自适应
        ViewportChanged += (s, e) =>
        {
            if (Visible)
            {
                int curW = Viewport.Width;
                int curH = Viewport.Height;
                if (curW > 0 && curH > 0 && (curW != _lastRenderCols || curH != _lastRenderRows))
                {
                    _lastRenderCols = curW;
                    _lastRenderRows = curH;
                    OnWindowResized();
                }
            }
        };
    }


}
