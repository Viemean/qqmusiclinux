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
/// 类似 Electron 客户端风格的沉浸式全屏播放界面
/// 左侧：高清专辑原图（Kitty 原生图形协议）+ 歌曲名/歌手/专辑/音质徽标
/// 右侧：大视窗自动折行居中同步歌词
/// </summary>
public sealed class NowPlayingView : View
{
    private readonly View _coverContainer;
    private readonly Label _unsupportedLabel1;
    private readonly Label _unsupportedLabel2;
    private readonly Label _unsupportedLabel3;
    private readonly View _lyricContainer;
    private readonly ListView _lyricListView;
    private readonly ThinScrollBarView _lyricScrollBar;
    private readonly Label _transBtn;
    private readonly Label _immersiveBtn;

    private Song? _currentSong;
    private string? _coverFilePath;
    private List<LyricLine> _currentLyrics = new();
    private readonly List<int> _lyricItemToLineIndex = new();
    private readonly Dictionary<int, int> _lyricLineToFirstItemIndex = new();
    private int _currentActiveLyricIndex = -1;
    private long _lastUserLyricScrollTick;
    private bool _showTranslation = true;
    private bool _isImmersiveMode = false;
    private long _lastImmersiveActivityTick = 0;
    private object? _immersiveTimerToken;
    private int _lastViewportWidth;
    private int _lastRenderCols;
    private int _lastRenderRows;
    private object? _resizeTimerToken;
    private long _lastTransClickTicks;

    public event Action? BackRequested;
    public event Action<TimeSpan>? SeekRequested;
    public event Action? ToggleTranslationRequested;
    public event Action? ToggleImmersiveRequested;

    public NowPlayingView()
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill(5); // 留出底栏 5 行高度
        Visible = false;

        // 1. 左侧面板：纯净无边框超大封面容器
        _coverContainer = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(48),
            Height = Dim.Fill()
        };

        // 不支持图形协议时的专业提示
        _unsupportedLabel1 = new Label
        {
            Text = "[ 当前终端不支持图形协议 ]",
            X = Pos.Center(),
            Y = Pos.Center() - 1,
            Visible = !TerminalImageHelper.IsImageSupported
        };
        _unsupportedLabel1.SetScheme(MikuTheme.PlayerBar);

        _unsupportedLabel2 = new Label
        {
            Text = "建议切换支持 Kitty 图像协议的终端",
            X = Pos.Center(),
            Y = Pos.Center() + 1,
            Visible = !TerminalImageHelper.IsImageSupported
        };
        _unsupportedLabel2.SetScheme(MikuTheme.Base);

        _unsupportedLabel3 = new Label
        {
            Text = "(如 Kitty / WezTerm / Ghostty)",
            X = Pos.Center(),
            Y = Pos.Center() + 2,
            Visible = !TerminalImageHelper.IsImageSupported
        };
        _unsupportedLabel3.SetScheme(MikuTheme.Base);

        _coverContainer.Add(_unsupportedLabel1, _unsupportedLabel2, _unsupportedLabel3);
        Add(_coverContainer);

        // 2. 右侧面板：纯净无边框大视窗歌词
        _lyricContainer = new View
        {
            X = Pos.Right(_coverContainer),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        _lyricListView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        _lyricListView.KeyBindings.Remove(Key.Space);
        _lyricListView.SetScheme(MikuTheme.Lyric);

        _lyricListView.ViewportChanged += (s, e) =>
        {
            int curW = _lyricListView.Viewport.Width;
            if (curW > 0 && curW != _lastViewportWidth)
            {
                _lastViewportWidth = curW;
                Application.Invoke(RefreshLyrics);
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

        _lyricListView.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.WheeledUp) || m.Flags.HasFlag(MouseFlags.WheeledDown) ||
                m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                _lastUserLyricScrollTick = Environment.TickCount64;
                if (!_lyricListView.HasFocus) _lyricListView.SetFocus();
            }
        };

        _lyricListView.Accepting += (s, e) =>
        {
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
            }
        };
        _lyricContainer.Add(_lyricScrollBar);

        // 歌词区右下角纯净单字“译”与“沉浸”按钮（通过高亮/暗灰判断状态）
        _transBtn = new Label
        {
            Text = "译",
            X = Pos.AnchorEnd(8),
            Y = Pos.AnchorEnd(1),
            Width = 2,
            Height = 1,
            CanFocus = false,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _transBtn.MouseEvent += (s, m) =>
        {
            TriggerImmersiveActivity();
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
            Text = "沉浸",
            X = Pos.AnchorEnd(5),
            Y = Pos.AnchorEnd(1),
            Width = 4,
            Height = 1,
            CanFocus = false,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _immersiveBtn.MouseEvent += (s, m) =>
        {
            TriggerImmersiveActivity();
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                ToggleImmersiveRequested?.Invoke();
                m.Handled = true;
            }
        };

        _lyricContainer.Add(_transBtn, _immersiveBtn);
        UpdateTransButtonHighlight();
        UpdateImmersiveButtonHighlight();

        Add(_lyricContainer);

        // 鼠标活动唤醒沉浸模式下自动隐藏的图标
        MouseEvent += (s, m) =>
        {
            TriggerImmersiveActivity();
        };

        // 键盘快捷键监听：按 P 切换沉浸，按 Esc/V 退出沉浸或返回主界面
        KeyDown += (s, k) =>
        {
            TriggerImmersiveActivity();

            var ch = char.ToUpperInvariant((char)k.AsRune.Value);
            if (ch == 'P')
            {
                ToggleImmersiveRequested?.Invoke();
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

    public void SetSong(Song? song, string qualityBadge)
    {
        _currentSong = song;
        if (song == null)
        {
            _coverFilePath = null;
            TerminalImageHelper.ClearImages();
            return;
        }

        if (song != null)
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
        else
        {
            _coverFilePath = null;
            TerminalImageHelper.ClearImages();
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
                            // 保持当前歌词垂直居中滚动对齐（与主界面完全一致）
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

        // 1. 顶部预留视口半高空行留白，确保第一句歌词也能从容滚动至屏幕正中央
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

        // 2. 底部预留视口半高空行留白，确保最后一句歌词也能从容滚动至屏幕正中央
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
        _lyricListView?.SetFocus();
        _lastRenderCols = Viewport.Width;
        _lastRenderRows = Viewport.Height;
        RefreshLyrics();
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
        if (_showTranslation)
        {
            _transBtn.SetScheme(new Scheme
            {
                Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None),
                Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None),
                HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
            });
        }
        else
        {
            _transBtn.SetScheme(new Scheme
            {
                Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None),
                Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None),
                HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None)
            });
        }
        _transBtn.SetNeedsDraw();
    }

    private void RenderCoverIfVisible()
    {
        if (!Visible || !TerminalImageHelper.IsImageSupported || string.IsNullOrEmpty(_coverFilePath) || !File.Exists(_coverFilePath))
        {
            return;
        }

        try
        {
            // 获取 _coverContainer 的屏幕绝对坐标（纯净无边框，居中靠上更具呼吸感）
            var origin = _coverContainer.FrameToScreen();
            int col = Math.Max(1, origin.X);
            int row = Math.Max(1, origin.Y);
            int containerCols = Math.Max(10, _coverContainer.Viewport.Width);
            int containerRows = Math.Max(6, _coverContainer.Viewport.Height);

            // 封面尺寸适度缩小（约占视口高度 72%，比例协调，不压迫界面）
            int maxRowsByHeight = Math.Max(4, (int)(containerRows * 0.72));
            int maxRowsByWidth = Math.Max(4, (int)((containerCols * 0.82) / 2));
            int targetRows = Math.Max(4, Math.Min(maxRowsByHeight, maxRowsByWidth));
            int targetCols = targetRows * 2;

            // 水平居中
            int colOffset = Math.Max(1, (containerCols - targetCols) / 2);
            // 居中偏上（距离顶部留出 2~3 行，不顶格遮挡外框标题）
            int rowOffset = Math.Max(2, (containerRows - targetRows) / 4);

            int renderCol = col + colOffset;
            int renderRow = Math.Max(2, row + rowOffset);

            TerminalImageHelper.RenderKittyImage(_coverFilePath, renderCol, renderRow, targetCols, targetRows);
        }
        catch
        {
            // 容错处理
        }
    }

    public void SetImmersiveState(bool enabled)
    {
        _isImmersiveMode = enabled;
        Height = enabled ? Dim.Fill(0) : Dim.Fill(5);
        UpdateImmersiveButtonHighlight();

        if (enabled)
        {
            TriggerImmersiveActivity();
            StartImmersiveTimer();
        }
        else
        {
            StopImmersiveTimer();
            _transBtn.Visible = true;
            _immersiveBtn.Visible = true;
        }
        SetNeedsDraw();
    }

    private void UpdateImmersiveButtonHighlight()
    {
        if (_immersiveBtn == null) return;
        if (_isImmersiveMode)
        {
            _immersiveBtn.SetScheme(new Scheme
            {
                Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None),
                Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None),
                HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
            });
        }
        else
        {
            _immersiveBtn.SetScheme(new Scheme
            {
                Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None),
                Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None),
                HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None)
            });
        }
        _immersiveBtn.SetNeedsDraw();
    }

    public void TriggerImmersiveActivity()
    {
        _lastImmersiveActivityTick = Environment.TickCount64;
        if (!_transBtn.Visible || !_immersiveBtn.Visible)
        {
            _transBtn.Visible = true;
            _immersiveBtn.Visible = true;
            SetNeedsDraw();
        }
    }

    private void StartImmersiveTimer()
    {
        StopImmersiveTimer();
        _lastImmersiveActivityTick = Environment.TickCount64;
        _immersiveTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            if (_isImmersiveMode && Visible)
            {
                if (Environment.TickCount64 - _lastImmersiveActivityTick > 3000)
                {
                    if (_transBtn.Visible || _immersiveBtn.Visible)
                    {
                        _transBtn.Visible = false;
                        _immersiveBtn.Visible = false;
                        SetNeedsDraw();
                    }
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
