using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.UI;

/// <summary>
/// 底部现代化微边框播放控制栏
/// 包含当前曲目/音质档位、双语歌词[译]切换、音量调节(步进/滚轮/静音)与高精度进度条
/// </summary>
public sealed partial class PlayerControlBar : FrameView
{
    private readonly Label _nowPlayingLabel;
    private readonly Button _favBtn;
    private readonly Button _shareBtn;
    private readonly Button _qualityBtn;
    private readonly Button _downloadBtn;
    private readonly Button _modeBtn;
    private readonly Button _volumeDecBtn;
    private readonly Button _volumeBtn;
    private readonly Button _volumeIncBtn;
    private readonly Button _prevBtn;
    private readonly Button _playPauseBtn;
    private readonly Button _nextBtn;
    private readonly Label _progressLabel;

    private long _lastMuteClickTicks;
    private bool _showTranslation = true;
    private bool _hasTranslation = false;
    private bool _isFavorite = false;
    private bool _isLocalMode = false;
    private Song? _currentSong;
    private string _persistentPlaybackStatus = "暂无播放曲目";
    private object? _temporaryStatusTimeout;

    private int _focusedControlIndex = 5;
    private bool _isAdjustingProgress = false;
    private double _currentPositionSeconds = 0;
    private double _totalDurationSeconds = 0;
    private double _currentPercent = 0;
    private TimeSpan _curTime = TimeSpan.Zero;
    private TimeSpan _totalTime = TimeSpan.Zero;

    public bool IsLocalMode => _isLocalMode;
    public Song? CurrentSong => _currentSong;

    public event Action? FavoriteClicked;
    public event Action? ShareClicked;
    public event Action? QualityClicked;
    public event Action? DownloadClicked;
    public event Action? ModeClicked;
    public event Action? PrevClicked;
    public event Action? PlayPauseClicked;
    public event Action? NextClicked;
    public event Action? NowPlayingClicked;
    public event Action<double>? SeekRequested;
    public event Action<int>? VolumeAdjustRequested;
    public event Action? VolumeMuteToggled;
    public event Action? ExitRequested;
    public event Action? Clicked;
    public event Action<bool>? TabNavigationRequested;

    public bool ShowTranslation => _showTranslation;
    public bool IsFavorite => _isFavorite;

    public void SetFocusToBar()
    {
        if (_focusedControlIndex == 0 && !_isAdjustingProgress)
        {
            _focusedControlIndex = 5;
        }
        SetFocus();
        UpdateControlHighlight();
    }

    public PlayerControlBar()
    {
        Title = "";
        X = 0;
        Y = Pos.AnchorEnd(5);
        Width = Dim.Fill();
        Height = 4;
        CanFocus = true;
        TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        KeyBindings.Remove(Key.Tab);
        KeyBindings.Remove(Key.Tab.WithShift);

        // 1. 左侧曲目信息：统一展示为“歌手 - 歌曲名字 - 专辑名字”，支持按列独立点击下钻
        _nowPlayingLabel = new Label
        {
            Text = "暂无播放曲目",
            X = 1,
            Y = 0,
            Width = Dim.Fill(36)
        };
        _nowPlayingLabel.CanFocus = false;
        _nowPlayingLabel.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _nowPlayingLabel.KeyBindings.Remove(Key.Space);
        _nowPlayingLabel.SetScheme(MikuTheme.PlayerBar);
        _nowPlayingLabel.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                HandleInfoClick(m.Position.HasValue ? m.Position.Value.X : 0);
                m.Handled = true;
            }
        };
        Add(_nowPlayingLabel);

        // 控制栏鼠标点击激活焦点与事件转发
        MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                if (!HasFocus)
                {
                    SetFocusToBar();
                }
                Clicked?.Invoke();
            }
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                if (m.Position.HasValue)
                {
                    var pos = m.Position.Value;
                    if (pos.Y == 0 && pos.X < Math.Max(10, Viewport.Width - 36))
                    {
                        HandleInfoClick(Math.Max(0, pos.X - 1));
                        m.Handled = true;
                    }
                }
            }
        };

        // 2. 第 0 行右侧控制区：
        // 音质按钮 [ SQ ] -> 音量减 [ - ] -> 音量数值 [ 100% ] -> 音量加 [ + ]（无需任何多余文字）

        _volumeIncBtn = new Button
        {
            Text = "+",
            X = Pos.AnchorEnd(5),
            Y = 0,
            CanFocus = false,
            ShadowStyle = ShadowStyles.None
        };
        _volumeIncBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _volumeIncBtn.KeyBindings.Remove(Key.Space);
        _volumeIncBtn.Accepting += (s, e) =>
        {
            _focusedControlIndex = 11;
            UpdateControlHighlight();
            VolumeAdjustRequested?.Invoke(10);
        };
        Add(_volumeIncBtn);

        _volumeBtn = new Button
        {
            Text = "80%",
            X = Pos.AnchorEnd(13),
            Y = 0,
            CanFocus = false,
            ShadowStyle = ShadowStyles.None
        };
        _volumeBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _volumeBtn.KeyBindings.Remove(Key.Space);
        _volumeBtn.Accepting += (s, e) =>
        {
            _focusedControlIndex = 10;
            UpdateControlHighlight();
            VolumeMuteToggled?.Invoke();
        };
        _volumeBtn.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.WheeledUp))
            {
                _focusedControlIndex = 10;
                UpdateControlHighlight();
                VolumeAdjustRequested?.Invoke(5);
                m.Handled = true;
            }
            else if (m.Flags.HasFlag(MouseFlags.WheeledDown))
            {
                _focusedControlIndex = 10;
                UpdateControlHighlight();
                VolumeAdjustRequested?.Invoke(-5);
                m.Handled = true;
            }
            else if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) ||
                     m.Flags.HasFlag(MouseFlags.LeftButtonPressed) ||
                     m.Flags.HasFlag(MouseFlags.MiddleButtonClicked) ||
                     m.Flags.HasFlag(MouseFlags.MiddleButtonPressed))
            {
                _focusedControlIndex = 10;
                UpdateControlHighlight();
                var now = Environment.TickCount64;
                if (now - _lastMuteClickTicks > 250)
                {
                    _lastMuteClickTicks = now;
                    VolumeMuteToggled?.Invoke();
                }
                m.Handled = true;
            }
        };
        Add(_volumeBtn);

        _volumeDecBtn = new Button
        {
            Text = "-",
            X = Pos.AnchorEnd(19),
            Y = 0,
            CanFocus = false,
            ShadowStyle = ShadowStyles.None
        };
        _volumeDecBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _volumeDecBtn.KeyBindings.Remove(Key.Space);
        _volumeDecBtn.Accepting += (s, e) =>
        {
            _focusedControlIndex = 9;
            UpdateControlHighlight();
            VolumeAdjustRequested?.Invoke(-10);
        };
        Add(_volumeDecBtn);

        // 下载按钮 [ 下载 ] - 放置在第 0 行音量减左侧（间距 1 格）
        _downloadBtn = new Button
        {
            Text = "下载",
            X = Pos.AnchorEnd(28),
            Y = 0,
            CanFocus = false,
            ShadowStyle = ShadowStyles.None
        };
        _downloadBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _downloadBtn.KeyBindings.Remove(Key.Space);
        _downloadBtn.Accepting += (s, e) =>
        {
            _focusedControlIndex = 8;
            UpdateControlHighlight();
            if (!_isLocalMode)
            {
                DownloadClicked?.Invoke();
            }
        };
        Add(_downloadBtn);

        // 音质按钮 (Hi-Res / SQ / HQ / 标准) - 紧凑放置在下载按钮左侧（间距 1 格）
        _qualityBtn = new Button
        {
            Text = "SQ",
            X = Pos.AnchorEnd(35),
            Y = 0,
            CanFocus = false,
            ShadowStyle = ShadowStyles.None
        };
        _qualityBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _qualityBtn.KeyBindings.Remove(Key.Space);
        _qualityBtn.Accepting += (s, e) =>
        {
            _focusedControlIndex = 7;
            UpdateControlHighlight();
            QualityClicked?.Invoke();
        };
        Add(_qualityBtn);

        // 3. 第 1 行：
        // 左侧：加长高精度进度条 (30格) + 紧邻右侧的 [ 收藏 ] 按钮
        // 右侧：[ 随机 ] + [ 上一首 ] + [ 暂停 ] + [ 下一首 ]

        _progressLabel = new Label
        {
            Text = "[00:00 / 00:00]  [------------------------------]  0%",
            X = 1,
            Y = 1,
            Width = 58
        };
        _progressLabel.SetScheme(MikuTheme.PlayerBar);
        _progressLabel.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                if (m.Position.HasValue)
                {
                    int clickX = m.Position.Value.X;
                    const int barStart = 18; // "[00:00 / 00:00]  [".Length
                    const int barLen = 30;
                    if (clickX >= barStart && clickX <= barStart + barLen)
                    {
                        double ratio = Math.Clamp((double)(clickX - barStart) / barLen, 0.0, 1.0);
                        SeekRequested?.Invoke(ratio);
                        m.Handled = true;
                    }
                }
            }
        };
        Add(_progressLabel);

        // 收藏/已收藏 按钮 (快捷键 S) - 放置在歌曲进度条百分比右侧
        _favBtn = new Button
        {
            Text = "收藏",
            X = Pos.Right(_progressLabel) + 1,
            Y = 1,
            CanFocus = false,
            ShadowStyle = ShadowStyles.None
        };
        _favBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _favBtn.KeyBindings.Remove(Key.Space);
        _favBtn.Accepting += (s, e) =>
        {
            _focusedControlIndex = 1;
            UpdateControlHighlight();
            if (!_isLocalMode)
            {
                FavoriteClicked?.Invoke();
            }
        };
        Add(_favBtn);

        _nextBtn = new Button
        {
            Text = "[L] 下一首",
            NoDecorations = true,
            X = Pos.AnchorEnd(12),
            Y = 1,
            CanFocus = false,
            ShadowStyle = ShadowStyles.None
        };
        _nextBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _nextBtn.KeyBindings.Remove(Key.Space);
        _nextBtn.Accepting += (s, e) =>
        {
            _focusedControlIndex = 6;
            UpdateControlHighlight();
            NextClicked?.Invoke();
        };
        Add(_nextBtn);

        _playPauseBtn = new Button
        {
            Text = "播放",
            X = Pos.AnchorEnd(22),
            Y = 1,
            CanFocus = false,
            ShadowStyle = ShadowStyles.None
        };
        _playPauseBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _playPauseBtn.KeyBindings.Remove(Key.Space);
        _playPauseBtn.Accepting += (s, e) =>
        {
            _focusedControlIndex = 5;
            UpdateControlHighlight();
            PlayPauseClicked?.Invoke();
        };
        Add(_playPauseBtn);

        _prevBtn = new Button
        {
            Text = "[J] 上一首",
            NoDecorations = true,
            X = Pos.AnchorEnd(34),
            Y = 1,
            CanFocus = false,
            ShadowStyle = ShadowStyles.None
        };
        _prevBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _prevBtn.KeyBindings.Remove(Key.Space);
        _prevBtn.Accepting += (s, e) =>
        {
            _focusedControlIndex = 4;
            UpdateControlHighlight();
            PrevClicked?.Invoke();
        };
        Add(_prevBtn);

        // 播放循环模式按钮 (快捷键 O) - 放在 [上一首] 按钮左侧
        _modeBtn = new Button
        {
            Text = "[O] 随机",
            NoDecorations = true,
            X = Pos.AnchorEnd(44),
            Y = 1,
            CanFocus = false,
            ShadowStyle = ShadowStyles.None
        };
        _modeBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _modeBtn.KeyBindings.Remove(Key.Space);
        _modeBtn.Accepting += (s, e) =>
        {
            _focusedControlIndex = 3;
            UpdateControlHighlight();
            ModeClicked?.Invoke();
        };
        Add(_modeBtn);

        // 分享按钮 - 放在 [播放模式] 按钮左侧
        _shareBtn = new Button
        {
            Text = "分享",
            X = Pos.AnchorEnd(54),
            Y = 1,
            CanFocus = false,
            ShadowStyle = ShadowStyles.None
        };
        _shareBtn.TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        _shareBtn.KeyBindings.Remove(Key.Space);
        _shareBtn.Accepting += (s, e) =>
        {
            _focusedControlIndex = 2;
            UpdateControlHighlight();
            if (!_isLocalMode)
            {
                ShareClicked?.Invoke();
            }
        };
        Add(_shareBtn);

        HasFocusChanged += (s, e) =>
        {
            if (!HasFocus)
            {
                _isAdjustingProgress = false;
            }
            else
            {
                if (_focusedControlIndex < 0 || _focusedControlIndex > 11)
                {
                    _focusedControlIndex = 5;
                }
            }
            UpdateControlHighlight();
        };

        KeyDown += HandleKeyDown;
    }

    private void HandleKeyDown(object? sender, Key k)
    {
        if (_isAdjustingProgress)
        {
            if (k == Key.CursorLeft)
            {
                k.Handled = true;
                double targetSec = Math.Max(0, _currentPositionSeconds - 10);
                double ratio = _totalDurationSeconds > 0 ? targetSec / _totalDurationSeconds : 0;
                _currentPositionSeconds = targetSec;
                SeekRequested?.Invoke(ratio);
                RenderProgressLabel();
                return;
            }
            if (k == Key.CursorRight)
            {
                k.Handled = true;
                double targetSec = Math.Min(_totalDurationSeconds, _currentPositionSeconds + 10);
                double ratio = _totalDurationSeconds > 0 ? targetSec / _totalDurationSeconds : 1.0;
                _currentPositionSeconds = targetSec;
                SeekRequested?.Invoke(ratio);
                RenderProgressLabel();
                return;
            }
            if (k == Key.Esc || k == Key.Enter)
            {
                k.Handled = true;
                _isAdjustingProgress = false;
                UpdateControlHighlight();
                return;
            }
            return;
        }

        if (k == Key.Esc)
        {
            k.Handled = true;
            ExitRequested?.Invoke();
            return;
        }

        if (k == Key.Tab || k.ToString().Contains("Tab"))
        {
            k.Handled = true;
            TabNavigationRequested?.Invoke(!k.IsShift);
            return;
        }

        if (k == Key.CursorLeft)
        {
            k.Handled = true;
            NavigatePreviousControl();
            return;
        }
        if (k == Key.CursorRight)
        {
            k.Handled = true;
            NavigateNextControl();
            return;
        }
        if (k == Key.CursorUp || k == Key.CursorDown)
        {
            k.Handled = true;
            ToggleRowControl();
            return;
        }
        if (k == Key.Enter || k == Key.Space)
        {
            k.Handled = true;
            ActivateCurrentControl();
            return;
        }
    }

}
