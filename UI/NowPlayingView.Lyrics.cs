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

public sealed partial class NowPlayingView
{
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

}
