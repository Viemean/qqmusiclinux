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
public sealed class NowPlayingView : View
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

    /// <summary>
    /// 处理 Tab 键焦点切换：仅在交互项目（歌手/专辑）与底部控制台之间切换
    /// </summary>
    public void HandleTabNavigation(bool forward)
    {
        TriggerImmersiveActivity();
        TriggerInteractiveActivity();

        if (_isImmersiveMode || !TerminalImageHelper.IsImageSupported)
        {
            // 沉浸模式或无图全宽歌词模式下（封面容器已隐藏）：直接流转至底部控制栏
            FocusControlBarRequested?.Invoke();
            return;
        }

        // 普通模式（有底栏）：在交互项目与底部控制台之间轮转
        if (_artistLink.HasFocus)
        {
            if (forward)
            {
                _albumLink.SetFocus();
                FocusChangedNotification?.Invoke();
            }
            else
            {
                FocusControlBarRequested?.Invoke();
            }
        }
        else if (_albumLink.HasFocus)
        {
            if (forward)
            {
                FocusControlBarRequested?.Invoke();
            }
            else
            {
                _artistLink.SetFocus();
                FocusChangedNotification?.Invoke();
            }
        }
        else
        {
            // 当前焦点在底栏或外部刚切入
            if (forward)
            {
                _artistLink.SetFocus();
            }
            else
            {
                _albumLink.SetFocus();
            }
            FocusChangedNotification?.Invoke();
        }
    }

    public void SetSong(Song? song, string qualityBadge)
    {
        _currentSong = song;
        if (song == null)
        {
            _coverFilePath = null;
            _artistLink.SetText("");
            _songTitleLabel.Text = "";
            _albumLink.SetText("");
            _songInfoContainer.Visible = false;
            _matchLyricBtn.Visible = false;
            TerminalImageHelper.ClearImages();
            return;
        }

        bool isLocalOrWebDav = song.IsLocal || song.IsWebDav;
        _matchLyricBtn.Visible = isLocalOrWebDav;

        _artistLink.SetText(string.IsNullOrWhiteSpace(song.Artist) ? "未知歌手" : song.Artist);
        _songTitleLabel.Text = song.Title ?? "未知曲目";
        _albumLink.SetText(string.IsNullOrWhiteSpace(song.Album) ? "未知专辑" : song.Album);
        _songInfoContainer.Visible = true;
        _coverFilePath = null;

        if (TerminalImageHelper.IsImageSupported)
        {
            _ = Task.Run(async () =>
            {
                var coverPath = await TerminalImageHelper.EnsureSongCoverAsync(song);

                if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
                {
                    _coverFilePath = coverPath;
                    Application.Invoke(() =>
                    {
                        if (Visible)
                        {
                            RenderCoverIfVisible();
                        }
                    });
                }
            });
        }
    }

    public void SetLyrics(List<LyricLine> lyrics, bool showTranslation)
    {
        _currentLyrics = lyrics ?? new List<LyricLine>();
        _showTranslation = showTranslation;
        _currentActiveLyricIndex = -1;
        RefreshLyrics();
    }

    public void UpdatePlaybackTime(double currentSec)
    {
        if (_currentLyrics.Count == 0) return;

        var currentTs = TimeSpan.FromSeconds(currentSec);
        int activeIndex = -1;

        for (int i = 0; i < _currentLyrics.Count; i++)
        {
            if (_currentLyrics[i].Timestamp <= currentTs)
            {
                activeIndex = i;
            }
            else
            {
                break;
            }
        }

        if (activeIndex != _currentActiveLyricIndex)
        {
            _currentActiveLyricIndex = activeIndex;
            _lyricListView.SetNeedsDraw();
        }

        if (activeIndex >= 0 && _lyricLineToFirstItemIndex.TryGetValue(activeIndex, out int targetListItemIdx))
        {
            var sourceCount = _lyricListView.Source?.Count ?? 0;
            if (targetListItemIdx >= 0 && targetListItemIdx < sourceCount)
            {
                // 仅在用户 5 秒内未进行手动翻阅浏览时，自动推进滚动
                bool isUserBrowsing = (Environment.TickCount64 - _lastUserLyricScrollTick < 5000);
                if (!isUserBrowsing)
                {
                    try
                    {
                        if (_lyricListView.SelectedItem != targetListItemIdx)
                        {
                            _lyricListView.SelectedItem = targetListItemIdx;
                        }

                        int viewH = _lyricListView.Viewport.Height;
                        if (viewH > 0)
                        {
                            int targetTop = Math.Max(0, targetListItemIdx - (viewH / 2));
                            if (_lyricListView.Viewport.Y != targetTop)
                            {
                                _lyricListView.Viewport = new Rectangle(
                                    _lyricListView.Viewport.X,
                                    targetTop,
                                    _lyricListView.Viewport.Width,
                                    _lyricListView.Viewport.Height
                                );
                            }
                        }
                    }
                    catch {}
                }
                _lyricScrollBar?.UpdateMetrics(sourceCount, _lyricListView.Viewport.Height, _lyricListView.Viewport.Y);
            }
        }
    }

    private void RefreshLyrics()
    {
        int viewW = _lyricListView.Viewport.Width > 0 ? _lyricListView.Viewport.Width : 40;
        int usableWidth = Math.Max(10, viewW - 2);

        if (_currentLyrics.Count == 0)
        {
            var emptyMsg = MainWindow.CenterLyricText("暂无歌词", usableWidth);
            _lyricListView.SetSource(new ObservableCollection<string> { emptyMsg });
            _lyricItemToLineIndex.Clear();
            _lyricLineToFirstItemIndex.Clear();
            return;
        }

        var displayLines = new List<string>();
        _lyricItemToLineIndex.Clear();
        _lyricLineToFirstItemIndex.Clear();

        int viewH = _lyricListView.Viewport.Height > 0 ? _lyricListView.Viewport.Height : 15;
        int padLines = Math.Max(2, (viewH / 2) - 1);

        for (int p = 0; p < padLines; p++)
        {
            displayLines.Add("");
            _lyricItemToLineIndex.Add(-1);
        }

        for (int i = 0; i < _currentLyrics.Count; i++)
        {
            var l = _currentLyrics[i];
            _lyricLineToFirstItemIndex[i] = displayLines.Count;

            var origWrapped = MainWindow.WrapLyricText(l.Text, usableWidth);
            foreach (var oLine in origWrapped)
            {
                displayLines.Add(MainWindow.CenterLyricText(oLine, usableWidth));
                _lyricItemToLineIndex.Add(i);
            }

            if (_showTranslation && !string.IsNullOrWhiteSpace(l.Trans))
            {
                var transWrapped = MainWindow.WrapLyricText(l.Trans, usableWidth);
                foreach (var tLine in transWrapped)
                {
                    displayLines.Add(MainWindow.CenterLyricText(tLine, usableWidth));
                    _lyricItemToLineIndex.Add(i);
                }
            }

            displayLines.Add("");
            _lyricItemToLineIndex.Add(-1);
        }

        for (int p = 0; p < padLines; p++)
        {
            displayLines.Add("");
            _lyricItemToLineIndex.Add(-1);
        }

        _lyricListView.SetSource(new ObservableCollection<string>(displayLines));
        _lyricScrollBar?.UpdateMetrics(displayLines.Count, _lyricListView.Viewport.Height, _lyricListView.Viewport.Y);
    }

    public void OnActivated()
    {
        Visible = true;
        SetFocus();
        _lastRenderCols = Viewport.Width;
        _lastRenderRows = Viewport.Height;
        RefreshLyrics();

        if (_isImmersiveMode)
        {
            _artistLink.SetInteractiveEnabled(false);
            _albumLink.SetInteractiveEnabled(false);
            StartImmersiveTimer();
        }
        else
        {
            _artistLink.SetInteractiveEnabled(true);
            _albumLink.SetInteractiveEnabled(true);
        }

        Application.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
        {
            if (Visible)
            {
                RenderCoverIfVisible();
            }
            return false;
        });
    }

    public void OnDeactivated()
    {
        Visible = false;
        if (_resizeTimerToken != null)
        {
            Application.RemoveTimeout(_resizeTimerToken);
            _resizeTimerToken = null;
        }
        StopImmersiveTimer();
        TerminalImageHelper.ClearImages();
    }

    public void OnWindowResized()
    {
        if (!Visible) return;
        TerminalImageHelper.ClearImages();
        if (_resizeTimerToken != null)
        {
            Application.RemoveTimeout(_resizeTimerToken);
            _resizeTimerToken = null;
        }
        _resizeTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(80), () =>
        {
            _resizeTimerToken = null;
            if (Visible)
            {
                RenderCoverIfVisible();
                RefreshLyrics();
            }
            return false;
        });
    }

    public void SetTranslationState(bool enabled)
    {
        _showTranslation = enabled;
        UpdateTransButtonHighlight();
        RefreshLyrics();
    }

    private void UpdateTransButtonHighlight()
    {
        if (_transBtn == null) return;
        var color = _showTranslation ? MikuTheme.QqGreenLight : MikuTheme.MikuTextMuted;
        var attr = new Attribute(color, Color.None);
        _transBtn.SetScheme(new Scheme
        {
            Normal = attr,
            Focus = attr,
            HotNormal = attr,
            HotFocus = attr,
            Highlight = attr,
            Disabled = attr
        });
        _transBtn.SetNeedsDraw();
    }

    public void SetLyricMatchedState(bool isMatched)
    {
        _isLyricMatched = isMatched;
        UpdateMatchLyricButtonHighlight();
    }

    private void UpdateMatchLyricButtonHighlight()
    {
        if (_matchLyricBtn == null) return;
        _matchLyricBtn.Text = "[Y] 匹配";
        var color = _isLyricMatched ? MikuTheme.QqGreenLight : MikuTheme.MikuTextMuted;
        var attr = new Attribute(color, Color.None);
        _matchLyricBtn.SetScheme(new Scheme
        {
            Normal = attr,
            Focus = attr,
            HotNormal = attr,
            HotFocus = attr,
            Highlight = attr,
            Disabled = attr
        });
        _matchLyricBtn.SetNeedsDraw();
    }

    private void RenderCoverIfVisible()
    {
        if (!Visible || !TerminalImageHelper.IsImageSupported || string.IsNullOrEmpty(_coverFilePath) || !File.Exists(_coverFilePath))
        {
            return;
        }

        try
        {
            var origin = _coverContainer.FrameToScreen();
            int col = Math.Max(1, origin.X);
            int row = Math.Max(1, origin.Y);
            int containerCols = Math.Max(10, _coverContainer.Viewport.Width);
            int containerRows = Math.Max(6, _coverContainer.Viewport.Height);

            // 预留底部 5 行用于展示歌曲信息（歌手-歌曲名、留空行、专辑名），保持留白呼吸感
            int availableRowsForCover = Math.Max(4, containerRows - 5);
            int maxRowsByHeight = Math.Max(4, (int)(availableRowsForCover * 0.92));
            int maxRowsByWidth = Math.Max(4, (int)((containerCols * 0.85) / 2));
            int targetRows = Math.Max(4, Math.Min(maxRowsByHeight, maxRowsByWidth));
            int targetCols = targetRows * 2;

            // 水平居中
            int colOffset = Math.Max(1, (containerCols - targetCols) / 2);
            // 垂直居中于可用区域
            int rowOffset = Math.Max(1, (availableRowsForCover - targetRows) / 2);

            int renderCol = col + colOffset;
            int renderRow = Math.Max(1, row + rowOffset);

            TerminalImageHelper.RenderKittyImage(_coverFilePath, renderCol, renderRow, targetCols, targetRows);

            // 严格对齐：底部信息容器 X 坐标与封面起始列完全相同（colOffset），保持绝对左对齐
            UpdateSongInfoLayout(colOffset, targetCols, rowOffset + targetRows + 1);
        }
        catch
        {
            // 容错处理
        }
    }

    private void UpdateSongInfoLayout(int colOffset, int targetCols, int topRow)
    {
        if (_songInfoContainer == null) return;

        // 与封面始终保持绝对左对齐
        _songInfoContainer.X = colOffset;
        _songInfoContainer.Y = topRow;
        _songInfoContainer.Width = targetCols;
        _songInfoContainer.Height = 3;

        // 第 0 行：歌手 - 歌曲名
        _artistLink.X = 0;
        _artistLink.Y = 0;

        _hyphenLabel.X = Pos.Right(_artistLink);
        _hyphenLabel.Y = 0;

        _songTitleLabel.X = Pos.Right(_hyphenLabel);
        _songTitleLabel.Y = 0;
        _songTitleLabel.Width = Dim.Fill();

        // 第 1 行：留空间隔一行

        // 第 2 行：专辑名字（间隔一行距离）
        _albumLink.X = 0;
        _albumLink.Y = 2;
        _albumLink.Width = Dim.Fill();

        _songInfoContainer.SetNeedsDraw();
    }

    public void SetImmersiveState(bool enabled)
    {
        _isImmersiveMode = enabled;
        Height = enabled ? Dim.Fill(0) : Dim.Fill(5);
        UpdateImmersiveButtonHighlight();

        if (enabled)
        {
            TriggerImmersiveActivity();
            TriggerInteractiveActivity();
            StartImmersiveTimer();
            // 沉浸模式下自动取消选中歌手/专辑，需要取消沉浸模式才可以选择
            _artistLink.SetInteractiveEnabled(false);
            _albumLink.SetInteractiveEnabled(false);
            SetFocus();
            FocusChangedNotification?.Invoke();
        }
        else
        {
            StopImmersiveTimer();
            _transBtn.Visible = true;
            _immersiveBtn.Visible = true;
            if (_currentSong != null && (_currentSong.IsLocal || _currentSong.IsWebDav))
            {
                _matchLyricBtn.Visible = true;
            }
            _isInteractiveHighlightSuppressed = false;
            // 退出沉浸模式后恢复可选择
            _artistLink.SetInteractiveEnabled(true);
            _albumLink.SetInteractiveEnabled(true);
            _artistLink.SetHighlightSuppressed(false);
            _albumLink.SetHighlightSuppressed(false);
        }
        SetNeedsDraw();
    }

    private void UpdateImmersiveButtonHighlight()
    {
        if (_immersiveBtn == null) return;
        var color = _isImmersiveMode ? MikuTheme.QqGreenLight : MikuTheme.MikuTextMuted;
        var attr = new Attribute(color, Color.None);
        _immersiveBtn.SetScheme(new Scheme
        {
            Normal = attr,
            Focus = attr,
            HotNormal = attr,
            HotFocus = attr,
            Highlight = attr,
            Disabled = attr
        });
        _immersiveBtn.SetNeedsDraw();
    }

    public void TriggerImmersiveActivity()
    {
        _lastImmersiveActivityTick = Environment.TickCount64;
        bool isLocalOrWebDav = _currentSong != null && (_currentSong.IsLocal || _currentSong.IsWebDav);
        if (!_transBtn.Visible || !_immersiveBtn.Visible || (isLocalOrWebDav && !_matchLyricBtn.Visible))
        {
            _transBtn.Visible = true;
            _immersiveBtn.Visible = true;
            if (isLocalOrWebDav)
            {
                _matchLyricBtn.Visible = true;
            }
            SetNeedsDraw();
        }
    }

    public void TriggerInteractiveActivity()
    {
        _lastInteractiveActivityTick = Environment.TickCount64;
        if (_isInteractiveHighlightSuppressed)
        {
            _isInteractiveHighlightSuppressed = false;
            _artistLink.SetHighlightSuppressed(false);
            _albumLink.SetHighlightSuppressed(false);
        }
    }

    private void StartImmersiveTimer()
    {
        StopImmersiveTimer();
        _lastImmersiveActivityTick = Environment.TickCount64;
        _lastInteractiveActivityTick = Environment.TickCount64;
        _immersiveTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            if (_isImmersiveMode && Visible)
            {
                var now = Environment.TickCount64;

                // 1. 浮动功能按钮（译/沉浸/匹配）3 秒无操作自动隐藏
                if (now - _lastImmersiveActivityTick > 3000)
                {
                    if (_transBtn.Visible || _immersiveBtn.Visible || _matchLyricBtn.Visible)
                    {
                        _transBtn.Visible = false;
                        _immersiveBtn.Visible = false;
                        _matchLyricBtn.Visible = false;
                        SetNeedsDraw();
                    }
                }

                // 2. 交互项高亮 3 秒无操作自动隐藏
                if (!_isInteractiveHighlightSuppressed &&
                    _lastInteractiveActivityTick > 0 &&
                    now - _lastInteractiveActivityTick > 3000)
                {
                    _isInteractiveHighlightSuppressed = true;
                    _artistLink.SetHighlightSuppressed(true);
                    _albumLink.SetHighlightSuppressed(true);
                }
            }
            return _isImmersiveMode && Visible;
        });
    }

    private void StopImmersiveTimer()
    {
        if (_immersiveTimerToken != null)
        {
            Application.RemoveTimeout(_immersiveTimerToken);
            _immersiveTimerToken = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopImmersiveTimer();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// 无装饰符号（无括号）、未获焦呈纯净灰度、获焦呈现翡翠绿的轻量交互标签控件
/// 仅在双击（LeftButtonDoubleClicked）或回车/空格时执行激活，单击仅做获焦，防止误触
/// </summary>
public sealed class InteractiveLinkView : Label
{
    private string _text;
    private bool _isHighlightSuppressed;

    public int ContentWidth { get; private set; }

    public event Action? LinkSelected;
    public event Action? NavigateNextRequested;
    public event Action? NavigatePrevRequested;

    public InteractiveLinkView(string initialText)
    {
        _text = initialText;
        CanFocus = true;
        TabStop = TabBehavior.TabGroup;
        Height = 1;
        UpdateMetrics();

        MouseEvent += (s, m) =>
        {
            if (!CanFocus) return; // 沉浸模式下禁用一切交互与选中

            // 单击仅获焦，双击才执行激活跳转（防止误触）
            if (m.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked))
            {
                SetFocus();
                LinkSelected?.Invoke();
                m.Handled = true;
                return;
            }

            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                SetFocus();
                m.Handled = true;
                return;
            }
        };

        KeyDown += (s, k) =>
        {
            if (!CanFocus) return;

            if (k == Key.Enter || k.AsRune.Value == '\r' || k.AsRune.Value == '\n')
            {
                LinkSelected?.Invoke();
                k.Handled = true;
                return;
            }

            if (k == Key.CursorRight || k == Key.CursorDown)
            {
                NavigateNextRequested?.Invoke();
                k.Handled = true;
                return;
            }

            if (k == Key.CursorLeft || k == Key.CursorUp)
            {
                NavigatePrevRequested?.Invoke();
                k.Handled = true;
                return;
            }
        };

        HasFocusChanged += (s, e) =>
        {
            UpdateVisualScheme();
        };
    }

    public void SetText(string text)
    {
        _text = text;
        UpdateMetrics();
    }

    public void SetInteractiveEnabled(bool enabled)
    {
        CanFocus = enabled;
        TabStop = enabled ? TabBehavior.TabGroup : TabBehavior.NoStop;
        UpdateVisualScheme();
    }

    public void SetHighlightSuppressed(bool suppressed)
    {
        if (_isHighlightSuppressed != suppressed)
        {
            _isHighlightSuppressed = suppressed;
            UpdateVisualScheme();
        }
    }

    private void UpdateMetrics()
    {
        Text = _text;
        ContentWidth = MainWindow.GetDisplayWidth(_text);
        Width = ContentWidth;
        UpdateVisualScheme();
    }

    private void UpdateVisualScheme()
    {
        bool showActive = CanFocus && HasFocus && !_isHighlightSuppressed;
        if (showActive)
        {
            SetScheme(new Scheme
            {
                Normal = new Attribute(MikuTheme.QqGreenPrimary, Color.None),
                Focus = new Attribute(MikuTheme.QqGreenPrimary, Color.None),
                HotNormal = new Attribute(MikuTheme.QqGreenPrimary, Color.None)
            });
        }
        else
        {
            SetScheme(new Scheme
            {
                Normal = new Attribute(MikuTheme.QqTextLyricDim, Color.None),
                Focus = new Attribute(MikuTheme.QqTextLyricDim, Color.None),
                HotNormal = new Attribute(MikuTheme.QqTextLyricDim, Color.None)
            });
        }
        SetNeedsDraw();
    }
}
