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
    private async Task HandleGlobalKeyDownAsync(Key k)
    {
            // 0. ANSI 1004 Focus Reporting 状态机与失焦/后台按键拦截
            // ESC 键可能是独立物理按压，也可能是 \x1b[O (Focus Out) 或 \x1b[I (Focus In) 的前导字符
            if (k == Key.Esc)
            {
                k.Handled = true;
                _lastEscRcvTick = Environment.TickCount64;
                _sawBracketAfterEsc = false;

                int currentEscId = Interlocked.Increment(ref _escSequenceCounter);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(25);
                    if (Volatile.Read(ref _escSequenceCounter) == currentEscId && !_sawBracketAfterEsc)
                    {
                        Application.Invoke(async () =>
                        {
                            if (!IsTerminalWindowFocused)
                            {
                                return;
                            }

                            // 1. 若当前处于任何弹窗（Dialog/Window/Modal）中，立即单次关闭该弹窗退出
                            if (Application.TopRunnableView != null && Application.TopRunnableView != this)
                            {
                                if (Application.TopRunnableView is LoginDialog loginDlg)
                                {
                                    loginDlg.CloseSelf();
                                }
                                else if (Application.TopRunnableView is IRunnable runnable)
                                {
                                    Application.RequestStop(runnable);
                                }
                                else
                                {
                                    Application.RequestStop();
                                }
                                return;
                            }

                            // 1.5. 列表即时查找浮窗处于显示状态时，按 Esc 优先收起
                            if (_quickSearchBar.Visible)
                            {
                                _quickSearchBar.Dismiss();
                                return;
                            }

                            // 2. 搜索框处于激活状态时，按 Esc 退出搜索模式恢复焦点
                            if (_isSearchActive || _searchField.HasFocus)
                            {
                                _isSearchActive = false;
                                _searchField.CanFocus = false;
                                _songListView?.SetFocusToList();
                                Application.Invoke(UpdateFrameBorderHighlights);
                                return;
                            }

                            // 3. 主界面真实 Esc 处理
                            await HandleRealEscapeKeyAsync();
                        });
                    }
                });
                return;
            }

            // 检查是否紧随 Esc 之后的 '['
            if (Environment.TickCount64 - _lastEscRcvTick < 100 && k.AsRune.Value == '[')
            {
                _sawBracketAfterEsc = true;
                k.Handled = true;
                return;
            }

            // 检查是否紧随 Esc [ 之后的 Focus 字符 ('O' 为 Focus Out, 'I' 为 Focus In)
            if (_sawBracketAfterEsc && Environment.TickCount64 - _lastEscRcvTick < 150)
            {
                _sawBracketAfterEsc = false;
                k.Handled = true;
                char fc = char.ToUpperInvariant((char)k.AsRune.Value);
                if (fc == 'O')
                {
                    IsTerminalWindowFocused = false;
                }
                else if (fc == 'I')
                {
                    IsTerminalWindowFocused = true;
                }
                return;
            }

            // 终端窗口失焦（被桌面其它窗口覆盖或处于后台）时，丢弃所有后续按键，不响应任何操作
            if (!IsTerminalWindowFocused)
            {
                k.Handled = true;
                return;
            }

            _lastUserActivityTick = Environment.TickCount64;
            TriggerImmersiveActivity();

            // 1. 若当前处于播放列表抽屉弹窗（QueueDrawerDialog）中，顶层直连分发快捷键，避免子控件字符搜索吞噬
            if (Application.TopRunnableView is QueueDrawerDialog queueDrawer)
            {
                bool isD = k == Key.DeleteChar || k == Key.D || k == Key.D.WithShift ||
                           k.AsRune.Value == 'd' || k.AsRune.Value == 'D' ||
                           k.ToString().Equals("d", StringComparison.OrdinalIgnoreCase) ||
                           k.ToString().Equals("Key.D", StringComparison.OrdinalIgnoreCase);
                if (isD)
                {
                    k.Handled = true;
                    queueDrawer.RemoveCurrentSelectedItem();
                    return;
                }

                bool isC = k == Key.C || k == Key.C.WithShift ||
                           k.AsRune.Value == 'c' || k.AsRune.Value == 'C' ||
                           k.ToString().Equals("c", StringComparison.OrdinalIgnoreCase) ||
                           k.ToString().Equals("Key.C", StringComparison.OrdinalIgnoreCase);
                if (isC)
                {
                    k.Handled = true;
                    queueDrawer.ClearUpcomingSongs();
                    return;
                }

                if (k == Key.Enter || k.AsRune.Value == '\r' || k.AsRune.Value == '\n')
                {
                    k.Handled = true;
                    queueDrawer.PlayCurrentSelectedItem();
                    return;
                }

                return;
            }

            // 若当前处于其它弹窗（Dialog/Modal）中，不拦截按键，交由弹窗处理
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

            // 2.5. AOD 后台息屏模式：任意键唤醒恢复主界面（按 Q/q 仍触发确认退出）
            if (_isAodMode)
            {
                if (k.AsRune.Value == 'q' || k.AsRune.Value == 'Q')
                {
                    k.Handled = true;
                    ShowExitConfirmDialog();
                    return;
                }

                k.Handled = true;
                ExitAodMode();
                return;
            }

            // 3. 搜索框处于激活打字状态：全部交由 _searchField.KeyDown 独立处理，避免双重并发触发
            if (_searchField.HasFocus && _isSearchActive)
            {
                return;
            }

            // 4. 全局 Tab 与 Shift+Tab 流转
            if (k == Key.Tab || k.AsRune.Value == '\t' || k.ToString().Contains("Tab"))
            {
                k.Handled = true;
                _isSearchActive = false;
                _searchField.CanFocus = false;
                if (_isNowPlayingViewActive)
                {
                    _nowPlayingView.HandleTabNavigation(!k.IsShift);
                    return;
                }
                SwitchNextFocusWindow(!k.IsShift);
                return;
            }

            // 5. 焦点丢失/悬空时的安全自愈兜底（按方向键或回车立即将焦点恢复至中央歌曲列表）
            if (GetFocusedWindowIndex(focused) == -1 && (k == Key.CursorUp || k == Key.CursorDown || k == Key.CursorLeft || k == Key.CursorRight || k == Key.Enter))
            {
                _songListView.SetFocusToList();
                Application.Invoke(UpdateFrameBorderHighlights);
            }

            // 6. 只有未在搜索框内打字时，按 F3 或 '/' 才作为激活搜索框的快捷键（大播放界面下禁止唤出搜索）
            if (k == Key.F3 || k.AsRune.Value == '/')
            {
                if (_isNowPlayingViewActive) return;
                k.Handled = true;
                _searchField.CanFocus = true;
                _isSearchActive = true;
                _searchField.SetFocus();
                return;
            }

            if (k.IsCtrl && char.ToUpperInvariant((char)k.AsRune.Value) == 'J')
            {
                if (_isNowPlayingViewActive) return;
                k.Handled = true;
                await ShowSearchSuggestionsAsync();
                return;
            }

            char c = char.ToUpperInvariant((char)k.AsRune.Value);


            if (_currentViewMode == ViewMode.ArtistDetail && !_isSearchActive)
            {
                if (k == Key.D1 || c == '1')
                {
                    k.Handled = true;
                    await ToggleSingerSubModeAsync();
                    return;
                }
                if (k == Key.D2 || c == '2')
                {
                    k.Handled = true;
                    await ToggleSingerSongOrderAsync();
                    return;
                }
                if (k == Key.D3 || c == '3')
                {
                    k.Handled = true;
                    await ToggleSingerFavoriteAsync();
                    return;
                }
            }

            if (_currentViewMode == ViewMode.LocalMusic && !_isSearchActive)
            {
                if (c == 'A')
                {
                    k.Handled = true;
                    ShowAddFolderDialog();
                    return;
                }
                if (c == 'R')
                {
                    k.Handled = true;
                    await RescanLocalMusicAsync();
                    return;
                }
                if (c == 'F')
                {
                    k.Handled = true;
                    ShowFolderManageDialog();
                    return;
                }
            }

            if (_currentViewMode == ViewMode.WebDav && !_isSearchActive)
            {
                if (c == 'F')
                {
                    k.Handled = true;
                    ShowWebdavManageDialog();
                    return;
                }
                if (c == 'D')
                {
                    k.Handled = true;
                    await ToggleWebDavViewModeAsync();
                    return;
                }
                if (c == 'A')
                {
                    k.Handled = true;
                    await ImportCurrentWebDavFolderAsync();
                    return;
                }
                if (c == 'R')
                {
                    k.Handled = true;
                    await RefreshWebDavAsync();
                    return;
                }
                if (c == 'S')
                {
                    k.Handled = true;
                    await ScanWebDavMetadataAsync();
                    return;
                }
                if (k == Key.Backspace)
                {
                    k.Handled = true;
                    await NavigateUpWebDavFolderAsync();
                    return;
                }
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
                await TogglePlayOrPauseAsync();
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

            if (c == 'R')
            {
                if (_isNowPlayingViewActive) return;
                k.Handled = true;
                ShowAudioRecognitionDialog();
                return;
            }

            if (c == 'W')
            {
                if (_isNowPlayingViewActive) return;
                k.Handled = true;
                HandleWebButtonClicked();
                return;
            }

            bool isU = c == 'U' || k == Key.U || k == Key.U.WithShift ||
                       k.ToString().Equals("u", StringComparison.OrdinalIgnoreCase) ||
                       k.ToString().Equals("Key.U", StringComparison.OrdinalIgnoreCase);
            if (isU)
            {
                if (_isNowPlayingViewActive) return;
                k.Handled = true;
                ShowLoginDialog();
                return;
            }

            if (c == 'N')
            {
                if (_isNowPlayingViewActive) return;
                k.Handled = true;
                if (_currentViewMode != ViewMode.GuessRecommend && _songListView.Songs.Count > 0)
                {
                    int curIdx = _songListView.SelectedItem ?? -1;
                    if (curIdx >= 0 && curIdx < _songListView.Songs.Count)
                    {
                        var song = _songListView.Songs[curIdx];
                        PlaybackQueueService.Instance.InsertNext(song);
                        _controlBar?.UpdateStatus($"下一首将播放: {song.Title} - {song.Artist}");
                    }
                }
                return;
            }

            if (c == 'E')
            {
                if (_isNowPlayingViewActive) return;
                k.Handled = true;
                ShowQueueDrawerDialog();
                return;
            }

            if (c == 'G')
            {
                if (_isNowPlayingViewActive) return;
                k.Handled = true;
                ToggleQuickSearch();
                return;
            }

            if (c == 'B')
            {
                k.Handled = true;
                ToggleDesktopNotification();
                return;
            }

            if (c == 'Y')
            {
                k.Handled = true;
                await MatchOrRestoreLyricAsync();
                return;
            }

            if (c == 'X')
            {
                k.Handled = true;
                await HandleExportSongAsync();
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
    }

    private void SwitchNextFocusWindow(bool forward)
    {
        if (_isNowPlayingViewActive)
        {
            _nowPlayingView.HandleTabNavigation(forward);
            return;
        }

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
            case 4:
                _nowPlayingView.HandleTabNavigation(true);
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
                await TogglePlayOrPauseAsync();
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
                try
                {
                    _standaloneWebServer?.Stop();
                    _standaloneWebServer?.Dispose();
                    _standaloneWebServer = null;
                }
                catch {}
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


}
