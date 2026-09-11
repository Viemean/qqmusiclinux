using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
using Attribute = Terminal.Gui.Drawing.Attribute;
using Color = Terminal.Gui.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;

namespace QQMusic.Tui.UI;

public sealed partial class MainWindow
{
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
        _recognizeBtn.Visible = !enable;
        _webBtn.Visible = !enable;

        // 2. 下方控制栏与快捷栏显隐
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
        _lastUserActivityTick = Environment.TickCount64;
        _lastImmersiveActivityTick = Environment.TickCount64;
        if (_isImmersiveMode)
        {
            bool isLocalOrWebDav = _activeSong != null && (_activeSong.IsLocal || _activeSong.IsWebDav);
            if (!_lyricTransBtn.Visible || !_lyricImmersiveBtn.Visible || (isLocalOrWebDav && !_lyricMatchBtn.Visible))
            {
                _lyricTransBtn.Visible = true;
                _lyricImmersiveBtn.Visible = true;
                if (isLocalOrWebDav)
                {
                    _lyricMatchBtn.Visible = true;
                }
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
                    if (_lyricTransBtn.Visible || _lyricImmersiveBtn.Visible || _lyricMatchBtn.Visible)
                    {
                        _lyricTransBtn.Visible = false;
                        _lyricImmersiveBtn.Visible = false;
                        _lyricMatchBtn.Visible = false;
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

    private void UpdateLyricMatchButtonHighlight()
    {
        if (_lyricMatchBtn == null) return;
        bool isLocalOrWebDav = _activeSong != null && (_activeSong.IsLocal || _activeSong.IsWebDav);
        _lyricMatchBtn.Visible = isLocalOrWebDav;
        _lyricMatchBtn.Text = "[Y] 匹配";
        bool isMatched = IsCurrentSongLyricMatched(_activeSong);
        var color = isMatched ? MikuTheme.QqGreenLight : MikuTheme.MikuTextMuted;
        var attr = new Terminal.Gui.Drawing.Attribute(color, Color.None);
        _lyricMatchBtn.SetScheme(new Scheme
        {
            Normal = attr,
            Focus = attr,
            HotNormal = attr,
            HotFocus = attr,
            Highlight = attr,
            Disabled = attr
        });
        _lyricMatchBtn.SetNeedsDraw();
    }

    private void UpdateImmersiveButtonHighlight()
    {
        if (_lyricImmersiveBtn == null) return;
        var color = _isImmersiveMode ? MikuTheme.QqGreenLight : MikuTheme.MikuTextMuted;
        var attr = new Terminal.Gui.Drawing.Attribute(color, Color.None);
        _lyricImmersiveBtn.SetScheme(new Scheme
        {
            Normal = attr,
            Focus = attr,
            HotNormal = attr,
            HotFocus = attr,
            Highlight = attr,
            Disabled = attr
        });
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
            if (v == _nowPlayingView) return 4;
        }
        return -1;
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
        PlaybackQueueService.Instance.Mode = mode;
        UserSession.Current.PlaybackMode = mode;
        UserSession.Current.Save();
        _controlBar.UpdatePlaybackMode(mode);
        _mprisService.UpdatePlaybackMode(mode);
        if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
        {
            _standaloneWebServer.CurrentPlaybackMode = mode;
            _standaloneWebServer.BroadcastState("mode_change");
        }
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
        _recognizeBtn.Visible = false;
        _webBtn.Visible = false;
        _hotkeyHintLabel.Visible = !_isImmersiveMode;

        _nowPlayingView.SetSong(_activeSong, AudioQualityHelper.GetBadge(_actualQualityTier));
        _nowPlayingView.SetLyrics(_currentLyrics, _showTranslation);
        _nowPlayingView.SetTranslationState(_showTranslation);
        _nowPlayingView.SetLyricMatchedState(IsCurrentSongLyricMatched(_activeSong));
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
        _recognizeBtn.Visible = !_isImmersiveMode;
        _webBtn.Visible = !_isImmersiveMode;
        _hotkeyHintLabel.Visible = !_isImmersiveMode;

        _isSearchActive = false;
        _songListView.SetFocusToList();
        UpdateFrameBorderHighlights();
        UpdateLyricMatchButtonHighlight();
        if (_artistAlbumDetailView.Visible)
        {
            _artistAlbumDetailView.OnActivated();
        }
        SetNeedsDraw();
    }

    private void EnterAodMode()
    {
        _isAodMode = true;
        _songListView.SetMarqueePaused(true);

        _sidebarFrame.Visible = false;
        _songListView.Visible = false;
        _lyricFrame.Visible = false;
        _searchLabel.Visible = false;
        _searchField.Visible = false;
        _userStatusBtn.Visible = false;
        _recognizeBtn.Visible = false;
        _webBtn.Visible = false;
        _controlBar.Visible = false;
        _hotkeyHintLabel.Visible = false;
        _sidebarTitleLabel.Visible = false;
        _songListTitleLabel.Visible = false;
        _lyricTitleLabel.Visible = false;

        // 若处于沉浸式大封面播放或写真详情，显式调用注销并清空终端 Kitty 图像协议缓冲区
        if (_nowPlayingView.Visible)
        {
            _nowPlayingView.OnDeactivated();
        }
        else
        {
            _nowPlayingView.Visible = false;
        }

        if (_artistAlbumDetailView.Visible)
        {
            _artistAlbumDetailView.OnDeactivated();
        }
        else
        {
            _artistAlbumDetailView.Visible = false;
        }

        TerminalImageHelper.ClearImages();

        _aodView.UpdateSong(_activeSong);
        _aodView.Visible = true;
        _aodView.SetFocus();
        SetNeedsDraw();
        AppLogger.Info("MainWindow", "Entered AOD background display mode");
    }

    private void ExitAodMode()
    {
        _isAodMode = false;
        _aodView.Visible = false;
        _songListView.SetMarqueePaused(false);

        if (_isNowPlayingViewActive)
        {
            _nowPlayingView.Visible = true;
            _nowPlayingView.OnActivated();
        }
        else
        {
            _sidebarFrame.Visible = true;
            _songListView.Visible = true;
            _lyricFrame.Visible = true;
            _searchLabel.Visible = !_isImmersiveMode;
            _searchField.Visible = !_isImmersiveMode;
            _userStatusBtn.Visible = !_isImmersiveMode;
            _recognizeBtn.Visible = !_isImmersiveMode;
            _webBtn.Visible = !_isImmersiveMode;
            _sidebarTitleLabel.Visible = true;
            _songListTitleLabel.Visible = true;
            _lyricTitleLabel.Visible = true;
            if (_artistAlbumDetailView.Visible)
            {
                _artistAlbumDetailView.OnActivated();
            }
            _songListView.SetFocusToList();
        }

        _controlBar.Visible = !_isImmersiveMode;
        _hotkeyHintLabel.Visible = !_isImmersiveMode;

        UpdatePlayerStatus();
        UpdateFrameBorderHighlights();
        SetNeedsDraw();
        AppLogger.Info("MainWindow", "Exited AOD background display mode");
    }

    private async Task HandleRealEscapeKeyAsync()
    {
        if (_isImmersiveMode)
        {
            ApplyImmersiveMode(false);
            return;
        }
        if (_isNowPlayingViewActive)
        {
            CloseNowPlayingView();
        }
        else if (_currentViewMode == ViewMode.Search && _searchCategory != null && _searchOverview != null)
        {
            _searchCategory = null;
            _hasMoreSearchResults = false;
            RenderSearchOverview(_searchOverview);
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
        else if (_currentViewMode == ViewMode.WebDav && !_isWebDavFlatMode && _webDavPathHistory.Count > 0)
        {
            await NavigateUpWebDavFolderAsync();
        }
        else
        {
            EnterAodMode();
        }
    }


}
