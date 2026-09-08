using System;
using System.Collections.Generic;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.UI;

public sealed partial class PlayerControlBar
{
    private void NavigatePreviousControl()
    {
        do
        {
            _focusedControlIndex = (_focusedControlIndex - 1 + 12) % 12;
        } while (IsControlSkipped(_focusedControlIndex));

        UpdateControlHighlight();
    }

    private void NavigateNextControl()
    {
        do
        {
            _focusedControlIndex = (_focusedControlIndex + 1) % 12;
        } while (IsControlSkipped(_focusedControlIndex));

        UpdateControlHighlight();
    }

    private void ToggleRowControl()
    {
        // 0-6 为第1行控件，7-11 为第0行控件
        if (_focusedControlIndex <= 6)
        {
            _focusedControlIndex = 7;
        }
        else
        {
            _focusedControlIndex = 0;
        }

        if (IsControlSkipped(_focusedControlIndex))
        {
            NavigateNextControl();
        }
        else
        {
            UpdateControlHighlight();
        }
    }

    private bool IsControlSkipped(int index)
    {
        if (_isLocalMode && (index == 1 || index == 2 || index == 8)) // 收藏、分享 或 下载
        {
            return true;
        }
        return false;
    }

    private void ActivateCurrentControl()
    {
        switch (_focusedControlIndex)
        {
            case 0: // 进度条
                _isAdjustingProgress = true;
                UpdateControlHighlight();
                break;
            case 1: // 收藏
                if (!_isLocalMode) FavoriteClicked?.Invoke();
                break;
            case 2: // 分享
                if (!_isLocalMode) ShareClicked?.Invoke();
                break;
            case 3: // 循环模式
                ModeClicked?.Invoke();
                break;
            case 4: // 上一首
                PrevClicked?.Invoke();
                break;
            case 5: // 播放/暂停
                PlayPauseClicked?.Invoke();
                break;
            case 6: // 下一首
                NextClicked?.Invoke();
                break;
            case 7: // 音质
                QualityClicked?.Invoke();
                break;
            case 8: // 下载
                if (!_isLocalMode) DownloadClicked?.Invoke();
                break;
            case 9: // 音量 -
                VolumeAdjustRequested?.Invoke(-10);
                break;
            case 10: // 音量值 (静音)
                VolumeMuteToggled?.Invoke();
                break;
            case 11: // 音量 +
                VolumeAdjustRequested?.Invoke(10);
                break;
        }
    }

    private void UpdateControlHighlight()
    {
        var focusScheme = new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
            Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
            HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark)
        };
        var normalScheme = MikuTheme.PlayerBar;

        bool isFocused = HasFocus;

        _favBtn.SetScheme((isFocused && _focusedControlIndex == 1) ? focusScheme : (_isFavorite ? MikuTheme.FavoriteActive : normalScheme));
        _shareBtn.SetScheme((isFocused && _focusedControlIndex == 2) ? focusScheme : normalScheme);
        _modeBtn.SetScheme((isFocused && _focusedControlIndex == 3) ? focusScheme : normalScheme);
        _prevBtn.SetScheme((isFocused && _focusedControlIndex == 4) ? focusScheme : normalScheme);
        _playPauseBtn.SetScheme((isFocused && _focusedControlIndex == 5) ? focusScheme : normalScheme);
        _nextBtn.SetScheme((isFocused && _focusedControlIndex == 6) ? focusScheme : normalScheme);
        _qualityBtn.SetScheme((isFocused && _focusedControlIndex == 7) ? focusScheme : normalScheme);
        _downloadBtn.SetScheme((isFocused && _focusedControlIndex == 8) ? focusScheme : normalScheme);
        _volumeDecBtn.SetScheme((isFocused && _focusedControlIndex == 9) ? focusScheme : normalScheme);
        _volumeBtn.SetScheme((isFocused && _focusedControlIndex == 10) ? focusScheme : normalScheme);
        _volumeIncBtn.SetScheme((isFocused && _focusedControlIndex == 11) ? focusScheme : normalScheme);

        RenderProgressLabel();
        SetNeedsDraw();
    }

    private void RenderProgressLabel()
    {
        const int barLength = 30;
        var filled = (int)(_currentPercent * barLength);
        var bar = new string('=', Math.Max(0, filled - 1)) + (filled > 0 ? ">" : "") + new string('-', Math.Max(0, barLength - filled));

        string tail = "";
        if (HasFocus && _focusedControlIndex == 0)
        {
            tail = _isAdjustingProgress ? "  [◄/► 步进10s | Esc退出]" : "  [按回车调节]";
        }

        _progressLabel.Text = $"[{_curTime:mm\\:ss} / {_totalTime:mm\\:ss}]  [{bar}]  {(int)(_currentPercent * 100)}%{tail}";
        if (HasFocus && _focusedControlIndex == 0)
        {
            _progressLabel.SetScheme(new Scheme
            {
                Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, _isAdjustingProgress ? MikuTheme.QqGreenDark : Color.None),
                Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, _isAdjustingProgress ? MikuTheme.QqGreenDark : Color.None),
                HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, _isAdjustingProgress ? MikuTheme.QqGreenDark : Color.None)
            });
        }
        else
        {
            _progressLabel.SetScheme(MikuTheme.PlayerBar);
        }
    }

    public void UpdatePlaybackMode(PlaybackMode mode)
    {
        _modeBtn.Text = $"[O] {mode.GetBadge()}";
        SetNeedsLayout();
    }

    public void SetFavoriteStatus(bool isFavorite)
    {
        _isFavorite = isFavorite;
        _favBtn.Text = isFavorite ? "已收藏" : "收藏";
        if (isFavorite)
        {
            _favBtn.SetScheme(MikuTheme.FavoriteActive);
        }
        else
        {
            _favBtn.SetScheme(MikuTheme.PlayerBar);
        }
        SetNeedsLayout();
    }

    public void SetCurrentSong(Song? song)
    {
        _currentSong = song;
        if (song != null)
        {
            var albumText = string.IsNullOrWhiteSpace(song.Album) ? "单曲" : song.Album;
            _persistentPlaybackStatus = $"{song.Artist} - {song.Title} - {albumText}";
            SetLocalMode(song.IsLocal || song.IsWebDav);
        }
        else
        {
            _persistentPlaybackStatus = "暂无播放曲目";
            SetLocalMode(false);
        }

        if (_temporaryStatusTimeout == null)
        {
            _nowPlayingLabel.Text = _persistentPlaybackStatus;
            SetNeedsLayout();
        }
    }

    private void HandleInfoClick(int clickX)
    {
        // 用户要求：控制栏点击容易误触进入歌手/专辑界面，取消控制栏点击歌手/专辑界面的行为，统一触发全屏沉浸播放视窗
        NowPlayingClicked?.Invoke();
    }

    private static int GetDisplayWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int w = 0;
        foreach (var ch in text)
        {
            w += ch > 127 ? 2 : 1;
        }
        return w;
    }

    public void UpdateStatus(string text)
    {
        // 若为 [播放中]、[已暂停]、[空闲] 等常规播放状态，直接忽略前缀，保持显示“歌手 - 歌曲名字 - 专辑名字”
        if (text.StartsWith("[播放中]") || text.StartsWith("[已暂停]") || text.StartsWith("[空闲]"))
        {
            if (_temporaryStatusTimeout != null)
            {
                return;
            }
            _nowPlayingLabel.Text = _persistentPlaybackStatus;
            SetNeedsLayout();
            return;
        }

        // 其它业务操作提示：如 [收藏成功]、[取消收藏成功]、[操作提示] 等
        if (_temporaryStatusTimeout != null)
        {
            Application.RemoveTimeout(_temporaryStatusTimeout);
            _temporaryStatusTimeout = null;
        }

        _nowPlayingLabel.Text = text;
        SetNeedsLayout();

        // 倒计时 2500ms 后自动平滑恢复为当前的“歌手 - 歌曲名字 - 专辑名字”
        _temporaryStatusTimeout = Application.AddTimeout(TimeSpan.FromMilliseconds(2500), () =>
        {
            _temporaryStatusTimeout = null;
            _nowPlayingLabel.Text = _persistentPlaybackStatus;
            SetNeedsLayout();
            return false;
        });
    }

    public void SetLocalMode(bool isLocal)
    {
        _isLocalMode = isLocal;
        _downloadBtn.Visible = !isLocal;
        _favBtn.Visible = !isLocal;
        _shareBtn.Visible = !isLocal;
        UpdateQualityPosition();
        SetNeedsLayout();
    }

    private void UpdateQualityPosition()
    {
        int btnWidth = Math.Max(6, _qualityBtn.Text.Length + 4);
        int anchorOffset = _isLocalMode ? 19 : 28;
        _qualityBtn.X = Pos.AnchorEnd(anchorOffset + btnWidth + 1);
    }

    public void UpdateQuality(string qualityBadge)
    {
        _qualityBtn.Text = qualityBadge;
        UpdateQualityPosition();
        SetNeedsLayout();
    }

    public void UpdateVolume(int volume, bool isMute)
    {
        _volumeBtn.Text = (isMute || volume == 0) ? "0%" : $"{volume}%";
        SetNeedsLayout();
    }

    public void UpdateTranslationAvailability(bool hasTrans)
    {
        _hasTranslation = hasTrans;
    }

    public void ToggleTranslation()
    {
        _showTranslation = !_showTranslation;
    }

    public void UpdatePlayingState(bool isPlaying)
    {
        _playPauseBtn.Text = isPlaying ? "暂停" : "播放";
        SetNeedsLayout();
    }

    public void UpdateProgress(TimeSpan cur, TimeSpan total, double percent)
    {
        _curTime = cur;
        _totalTime = total;
        _currentPositionSeconds = cur.TotalSeconds;
        _totalDurationSeconds = total.TotalSeconds;
        _currentPercent = Math.Clamp(percent, 0, 1);

        RenderProgressLabel();
    }
}
