using System.Collections.ObjectModel;
using System.Text;
using Rectangle = System.Drawing.Rectangle;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Player;
using QQMusic.Tui.Services;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.UI;

public sealed partial class MainWindow
{
    private record PrefetchedPlayInfo(string Url, string Quality, AudioQualityTier ActualTier, DateTimeOffset ExpireAt);
    private static readonly Dictionary<string, PrefetchedPlayInfo> s_prefetchedPlayUrls = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_prefetchLock = new();

    private Task PlaySongAsync(Song song) => PlaySongAsync(song, 0);

    private async Task PlaySongAsync(Song song, double startPosition = 0)
    {
        _activeSong = song;
        PlaybackQueueService.Instance.SyncCurrentSong(song);
        _songListView.SetPlayingSong(song.Mid);

        // 电台模式下实时更新专属电台卡片
        if (_currentViewMode == ViewMode.GuessRecommend)
        {
            Application.Invoke(() =>
            {
                _songListView.SetRadioCard(song, AudioQualityHelper.GetBadge(_actualQualityTier), _radioPlayedCount);
            });
        }

        // 写入本地最近播放历史并联动视图
        RecentPlayHistory.Add(song);
        if (_currentViewMode == ViewMode.RecentPlay)
        {
            var recentSongs = RecentPlayHistory.GetSongs();
            Application.Invoke(() =>
            {
                _songListView.SetSongs(recentSongs, $"最近播放: 共 {recentSongs.Count} 首 (按 D 移除历史)");
                _songListView.SetPlayingSong(song.Mid);
            });
        }

        // 联动底栏收藏状态与本地模式
        var isFav = !song.IsLocal && ((!string.IsNullOrEmpty(song.Mid) && _favoriteSongMids.Contains(song.Mid)) ||
                    (song.Id > 0 && _favoriteSongIds.Contains(song.Id)));
        Application.Invoke(() =>
        {
            _controlBar.SetCurrentSong(song);
            _controlBar.SetLocalMode(song.IsLocal);
            _controlBar.SetFavoriteStatus(isFav);
            _aodView.UpdateSong(song);
        });

        _player.UpdateCurrentSong(song);
        if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
        {
            _standaloneWebServer.CurrentSong = song;
            _standaloneWebServer.IsCurrentSongFavorite = isFav;
            _standaloneWebServer.ActualQualityTier = _actualQualityTier;
            _standaloneWebServer.PreferredQualityTier = _preferredQualityTier;
            _standaloneWebServer.CurrentPlayUrl = null;
            _currentPlayUrl = null;
            _standaloneWebServer.IsPlaying = false;
            _standaloneWebServer.CurrentPositionSeconds = 0;
            _standaloneWebServer.TotalDurationSeconds = song.Duration;
            _standaloneWebServer.BroadcastState("song_change");
        }

        // 切歌时停止播放并清空歌词，避免索引越界
        await _player.StopAsync();
        _currentLyrics.Clear();
        _currentActiveLyricIndex = -1;

        Application.Invoke(() =>
        {
            _controlBar.UpdateStatus($"正在解析音源: {song.Title} - {song.Artist} ...");
            _controlBar.UpdateQuality(AudioQualityHelper.GetBadge(_preferredQualityTier));
            _lyricListView.SetSource(new ObservableCollection<string> { "正在加载歌词..." });
            try { _lyricListView.SelectedItem = 0; } catch {}
        });

        string? playUrl;
        List<LyricLine> lyrics;

        if (song.IsLocal)
        {
            playUrl = song.LocalFilePath;
            _actualQualityTier = song.Quality.Contains("Hi-Res", StringComparison.OrdinalIgnoreCase) ? AudioQualityTier.HiRes
                               : song.Quality.Contains("SQ", StringComparison.OrdinalIgnoreCase) ? AudioQualityTier.SQ
                               : song.Quality.Contains("HQ", StringComparison.OrdinalIgnoreCase) ? AudioQualityTier.HQ
                               : AudioQualityTier.Standard;
            lyrics = await QQMusic.Tui.Services.LocalMusicService.GetLyricsAsync(song);
        }
        else
        {
            // 优先探测本地磁盘音频缓存，实现 0 网络往返秒开
            var cachedAudio = AudioCacheService.GetCachedAudioPath(song.Mid, _preferredQualityTier);
            if (!string.IsNullOrEmpty(cachedAudio))
            {
                playUrl = cachedAudio;
                _actualQualityTier = _preferredQualityTier;
                song.Quality = AudioQualityHelper.GetBadge(_preferredQualityTier);
                AppLogger.Info("MainWindow", $"Audio cache hit for {song.Title} ({_actualQualityTier}): {cachedAudio}");
                lyrics = await QqMusicApi.GetLyricsAsync(song.Mid);
            }
            else
            {
                var cacheKey = $"{song.Mid}_{(int)_preferredQualityTier}";
                PrefetchedPlayInfo? prefetched = null;
                lock (s_prefetchLock)
                {
                    if (s_prefetchedPlayUrls.Remove(cacheKey, out var p) && p.ExpireAt > DateTimeOffset.UtcNow)
                    {
                        prefetched = p;
                    }
                }

                string? url;
                string? quality;
                AudioQualityTier actualTier;

                if (prefetched != null)
                {
                    url = prefetched.Url;
                    quality = prefetched.Quality;
                    actualTier = prefetched.ActualTier;
                    AppLogger.Info("MainWindow", $"Prefetch cache hit for {song.Title} ({actualTier})");
                }
                else
                {
                    (url, quality, actualTier) = await QqMusicApi.GetPlayUrlForTierAsync(song.Mid, song.EffectiveMediaMid, _preferredQualityTier);
                }

                playUrl = url;
                _actualQualityTier = actualTier;
                if (!string.IsNullOrEmpty(quality))
                {
                    song.Quality = quality;
                }
                lyrics = await QqMusicApi.GetLyricsAsync(song.Mid);

                // 启动异步流式边播边存
                if (!string.IsNullOrEmpty(playUrl))
                {
                    _ = AudioCacheService.CacheAudioAsync(song.Mid, _actualQualityTier, playUrl);
                }
            }
        }

        _currentLyrics.Clear();
        _currentLyrics.AddRange(lyrics);
        _player.UpdateCurrentLyrics(lyrics);
        if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
        {
            _standaloneWebServer.CurrentLyrics = lyrics;
            _standaloneWebServer.BroadcastState("lyrics_change");
        }

        var hasTrans = LyricParser.HasTranslation(_currentLyrics);
        Application.Invoke(() =>
        {
            _hasTranslation = hasTrans;
            UpdateTranslationButtonHighlight();
            _controlBar.UpdateTranslationAvailability(hasTrans);
        });

        if (!string.IsNullOrEmpty(playUrl))
        {
            if (!_isTuiAudioDisabled)
            {
                try
                {
                    await _player.PlayAsync(playUrl, song.Duration, startPosition);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("MainWindow", $"Local audio output failed: {ex.Message}");
                }
            }
            else
            {
                _isWebPlaying = true;
                _webVirtualPosition = startPosition;
                StartWebVirtualTicker(song.Duration);
            }

            _currentPlayUrl = playUrl;
            if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
            {
                _standaloneWebServer.CurrentPlayUrl = playUrl;
                _standaloneWebServer.ActualQualityTier = _actualQualityTier;
                _standaloneWebServer.PreferredQualityTier = _preferredQualityTier;
                _standaloneWebServer.TotalDurationSeconds = song.Duration;
                _standaloneWebServer.CurrentPositionSeconds = startPosition;
                _standaloneWebServer.IsPlaying = true;
                _standaloneWebServer.IsCurrentSongFavorite = isFav;
                _standaloneWebServer.BroadcastState("play");
            }
            UserSession.Current.LastPlayedSong = song;
            UserSession.Current.LastPlaybackPositionSeconds = startPosition;
            UserSession.Current.Save();
            _mprisService.UpdateSong(song);
            _mprisService.UpdatePlaybackStatus(true);
            _mprisService.UpdateVolume(_player.Volume);
            _mprisService.UpdatePlaybackMode(_currentPlaybackMode);

            if (TerminalImageHelper.IsImageSupported)
            {
                // 异步后台拉取/提取封面，就绪后立即向系统 MPRIS 发送 mpris:artUrl (file://)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var cover = await TerminalImageHelper.EnsureSongCoverAsync(song);
                        if (!string.IsNullOrEmpty(cover))
                        {
                            _mprisService.UpdateCover(cover);
                        }
                    }
                    catch {}
                });
            }

            Application.Invoke(() =>
            {
                UpdatePlayerStatus();
                _nowPlayingView.SetSong(song, AudioQualityHelper.GetBadge(_actualQualityTier));
                _nowPlayingView.SetLyrics(_currentLyrics, _showTranslation);
            });
        }
        else
        {
            Application.Invoke(() =>
            {
                _controlBar.UpdateStatus($"[无法播放] {song.Title} - {song.Artist} (无可用音源或需 VIP，1.5秒后自动跳过)");
                _controlBar.UpdateQuality(AudioQualityHelper.GetBadge(_actualQualityTier));
                _nowPlayingView.SetSong(song, AudioQualityHelper.GetBadge(_actualQualityTier));
            });

            if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
            {
                _standaloneWebServer.IsPlaying = false;
                _standaloneWebServer.BroadcastState("pause");
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                Application.Invoke(async () =>
                {
                    if (_activeSong?.Mid == song.Mid)
                    {
                        if (_currentViewMode == ViewMode.GuessRecommend)
                        {
                            await PlayNextRadioTrackAsync();
                        }
                        else
                        {
                            await PlayNextInCurrentListAsync(isAutoPlayback: true);
                        }
                    }
                });
            });
        }

        Application.Invoke(RefreshLyricListView);

        // 启动后台平滑预热下一首曲目的音源与封面
        _ = Task.Run(PrefetchNextSongAsync);
    }

    private void ToggleTranslation()
    {
        _showTranslation = !_showTranslation;
        UpdateTranslationButtonHighlight();
        RefreshLyricListView();
        _nowPlayingView.SetTranslationState(_showTranslation);
        _nowPlayingView.SetLyrics(_currentLyrics, _showTranslation);
    }

    private void UpdateTranslationButtonHighlight()
    {
        if (_lyricTransBtn == null) return;
        // 只显示一个译字，通过是否高亮判断是否开启
        if (_showTranslation && _hasTranslation)
        {
            _lyricTransBtn.SetScheme(new Scheme
            {
                Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None),
                Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None),
                HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
            });
        }
        else
        {
            _lyricTransBtn.SetScheme(new Scheme
            {
                Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None),
                Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None),
                HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None)
            });
        }
        _lyricTransBtn.SetNeedsDraw();
    }

    private void RefreshLyricListView()
    {
        int viewW = _lyricListView.Viewport.Width > 0 ? _lyricListView.Viewport.Width : 40;
        int usableWidth = Math.Max(10, viewW - 2);

        if (_currentLyrics.Count == 0)
        {
            var emptyMsg = CenterLyricText("暂无歌词", usableWidth);
            _lyricListView.SetSource(new ObservableCollection<string> { emptyMsg });
            _lyricItemToLineIndex.Clear();
            _lyricLineToFirstItemIndex.Clear();
            return;
        }

        var showTrans = _showTranslation;
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

            // 2. 原文行（水平对称居中，智能断行对齐）
            var origWrapped = WrapLyricText(l.Text, usableWidth);
            foreach (var oLine in origWrapped)
            {
                displayLines.Add(CenterLyricText(oLine, usableWidth));
                _lyricItemToLineIndex.Add(i);
            }

            // 3. 翻译行（若启用且非空，紧贴原文下方，同样水平对称居中与智能断行）
            if (showTrans && !string.IsNullOrWhiteSpace(l.Trans))
            {
                var transWrapped = WrapLyricText(l.Trans, usableWidth);
                foreach (var tLine in transWrapped)
                {
                    displayLines.Add(CenterLyricText(tLine, usableWidth));
                    _lyricItemToLineIndex.Add(i);
                }
            }

            // 4. 句落之间插入单个空行，恢复自然舒适的一行呼吸间隔
            displayLines.Add("");
            _lyricItemToLineIndex.Add(-1);
        }

        // 5. 底部预留视口半高空行留白，确保最后一句歌词也能从容滚动至屏幕正中央
        for (int p = 0; p < padLines; p++)
        {
            displayLines.Add("");
            _lyricItemToLineIndex.Add(-1);
        }

        var prevIdx = _lyricListView.SelectedItem ?? 0;
        _lyricListView.SetSource(new ObservableCollection<string>(displayLines));
        if (prevIdx >= 0 && prevIdx < displayLines.Count)
        {
            try
            {
                _lyricListView.SelectedItem = prevIdx;
                int targetTop = Math.Max(0, prevIdx - (viewH / 2));
                _lyricListView.Viewport = new Rectangle(
                    _lyricListView.Viewport.X,
                    targetTop,
                    _lyricListView.Viewport.Width,
                    _lyricListView.Viewport.Height
                );
            }
            catch {}
        }
        _lyricScrollBar?.UpdateMetrics(displayLines.Count, _lyricListView.Viewport.Height, _lyricListView.Viewport.Y);
    }

    public static int GetDisplayWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int w = 0;
        foreach (var ch in text)
        {
            w += ch > 127 ? 2 : 1;
        }
        return w;
    }

    public static string CenterLyricText(string text, int targetWidth)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        int w = GetDisplayWidth(text);
        if (w >= targetWidth) return text;
        int pad = Math.Max(0, (targetWidth - w) / 2);
        return new string(' ', pad) + text;
    }

    public static List<string> WrapLyricText(string text, int maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return lines;
        if (maxWidth <= 4)
        {
            lines.Add(text);
            return lines;
        }

        if (GetDisplayWidth(text) <= maxWidth)
        {
            lines.Add(text);
            return lines;
        }

        var curLine = new StringBuilder();
        int curWidth = 0;
        var tokens = TokenizeText(text);

        foreach (var token in tokens)
        {
            int tokenWidth = GetDisplayWidth(token);
            if (curWidth + tokenWidth <= maxWidth)
            {
                curLine.Append(token);
                curWidth += tokenWidth;
            }
            else
            {
                if (curLine.Length > 0)
                {
                    var lineStr = curLine.ToString().Trim();
                    if (!string.IsNullOrEmpty(lineStr))
                    {
                        lines.Add(lineStr);
                    }
                    curLine.Clear();
                    curWidth = 0;
                }

                if (tokenWidth > maxWidth)
                {
                    foreach (var ch in token)
                    {
                        int chW = ch > 127 ? 2 : 1;
                        if (curWidth + chW > maxWidth)
                        {
                            var s = curLine.ToString().Trim();
                            if (!string.IsNullOrEmpty(s)) lines.Add(s);
                            curLine.Clear();
                            curWidth = 0;
                        }
                        curLine.Append(ch);
                        curWidth += chW;
                    }
                }
                else
                {
                    var trimmedToken = token.TrimStart();
                    if (!string.IsNullOrEmpty(trimmedToken))
                    {
                        curLine.Append(trimmedToken);
                        curWidth += GetDisplayWidth(trimmedToken);
                    }
                }
            }
        }

        if (curLine.Length > 0)
        {
            var lineStr = curLine.ToString().Trim();
            if (!string.IsNullOrEmpty(lineStr)) lines.Add(lineStr);
        }

        return lines.Count > 0 ? lines : new List<string> { text };
    }

    private static List<string> TokenizeText(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c > 127)
            {
                tokens.Add(c.ToString());
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                tokens.Add(" ");
                while (i + 1 < text.Length && char.IsWhiteSpace(text[i + 1])) i++;
                i++;
            }
            else
            {
                int start = i;
                while (i < text.Length && text[i] <= 127 && !char.IsWhiteSpace(text[i]))
                {
                    i++;
                }
                tokens.Add(text.Substring(start, i - start));
            }
        }
        return tokens;
    }

    private void UpdatePlayerStatus()
    {
        if (_activeSong == null)
        {
            _controlBar.SetCurrentSong(null);
            _controlBar.UpdateQuality(AudioQualityHelper.GetBadge(_actualQualityTier));
            _controlBar.UpdateVolume(_player.Volume, _player.Volume == 0);
            _controlBar.UpdatePlayingState(false);
            _mprisService.UpdatePlaybackStatus(false);
            return;
        }

        _controlBar.SetCurrentSong(_activeSong);
        _controlBar.UpdateQuality(AudioQualityHelper.GetBadge(_actualQualityTier));
        _controlBar.UpdateVolume(_player.Volume, _player.Volume == 0);
        bool isPlaying = _isTuiAudioDisabled ? _isWebPlaying : _player.IsPlaying;
        _controlBar.UpdatePlayingState(isPlaying);
        _mprisService.UpdatePlaybackStatus(isPlaying);
        if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
        {
            _standaloneWebServer.IsPlaying = isPlaying;
        }
    }

    private void AdjustVolume(int delta)
    {
        var newVol = Math.Clamp(_player.Volume + delta, 0, 100);
        _player.SetVolume(newVol);
        if (newVol > 0)
        {
            _preMuteVolume = newVol;
        }
        UserSession.Current.Volume = newVol;
        UserSession.Current.Save();
        _controlBar.UpdateVolume(newVol, newVol == 0);
        _mprisService.UpdateVolume(newVol);
    }

    private void ToggleMute()
    {
        if (_player.Volume > 0)
        {
            _preMuteVolume = _player.Volume;
            _player.SetVolume(0);
            UserSession.Current.Volume = 0;
            UserSession.Current.Save();
            _controlBar.UpdateVolume(0, true);
            _mprisService.UpdateVolume(0);
        }
        else
        {
            var restoreVol = _preMuteVolume > 0 ? _preMuteVolume : 80;
            _player.SetVolume(restoreVol);
            UserSession.Current.Volume = restoreVol;
            UserSession.Current.Save();
            _controlBar.UpdateVolume(restoreVol, false);
            _mprisService.UpdateVolume(restoreVol);
        }
    }

    private void UpdateProgress(double currentSec)
    {
        if (_activeSong == null || _activeSong.Duration <= 0) return;

        // AOD 后台息屏模式：仅在后台同步 D-Bus 位置与防抖持久化，不触发前台界面控件重绘
        if (_isAodMode)
        {
            _mprisService.UpdatePosition(currentSec);
            UserSession.Current.LastPlaybackPositionSeconds = currentSec;
            UserSession.Current.LastPlayedSong = _activeSong;
            if (Environment.TickCount64 - _lastProgressSaveTick > 5000)
            {
                _lastProgressSaveTick = Environment.TickCount64;
                UserSession.Current.Save();
            }
            return;
        }

        var cur = TimeSpan.FromSeconds(currentSec);
        var total = TimeSpan.FromSeconds(_activeSong.Duration);
        var progressPercent = Math.Clamp(currentSec / _activeSong.Duration, 0, 1);

        _controlBar.UpdateProgress(cur, total, progressPercent);
        _mprisService.UpdatePosition(currentSec);

        UserSession.Current.LastPlaybackPositionSeconds = currentSec;
        UserSession.Current.LastPlayedSong = _activeSong;
        if (Environment.TickCount64 - _lastProgressSaveTick > 5000)
        {
            _lastProgressSaveTick = Environment.TickCount64;
            UserSession.Current.Save();
        }
    }

    private void UpdateLyrics(double currentSec)
    {
        if (_isAodMode) return; // AOD 模式跳过歌词计算与渲染
        _nowPlayingView.UpdatePlaybackTime(currentSec);
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
                // 仅在用户未手动翻阅浏览且未获焦歌词视窗时，自动居中滚动
                bool isUserBrowsing = _lyricListView.HasFocus || (Environment.TickCount64 - _lastUserLyricScrollTick < 3000);
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
                    catch
                    {
                        // 忽略切歌过渡期的瞬态索引竞争
                    }
                }
                _lyricScrollBar?.UpdateMetrics(sourceCount, _lyricListView.Viewport.Height, _lyricListView.Viewport.Y);
            }
        }
    }

    private async Task CycleQualityTierAsync(bool allowHiRes = false)
    {
        // 循环切换音质：Web 端跳过 Hi-Res（Standard -> HQ -> SQ -> Standard）
        var nextTier = _preferredQualityTier switch
        {
            AudioQualityTier.Standard => AudioQualityTier.HQ,
            AudioQualityTier.HQ => AudioQualityTier.SQ,
            AudioQualityTier.SQ => allowHiRes ? AudioQualityTier.HiRes : AudioQualityTier.Standard,
            _ => AudioQualityTier.Standard
        };
        await SwitchQualityTierAsync(nextTier);
    }

    private async Task SwitchQualityTierAsync(AudioQualityTier newTier)
    {
        _preferredQualityTier = newTier;
        UserSession.Current.PreferredQuality = AudioQualityHelper.GetBadge(newTier);
        UserSession.Current.Save();

        if (_activeSong != null && !_activeSong.IsLocal)
        {
            double currentPos = _isTuiAudioDisabled ? _webVirtualPosition : _player.CurrentPositionSeconds;
            var (url, quality, actualTier) = await QqMusicApi.GetPlayUrlForTierAsync(_activeSong.Mid, _activeSong.EffectiveMediaMid, newTier);
            if (!string.IsNullOrEmpty(url))
            {
                _actualQualityTier = actualTier;
                _activeSong.Quality = quality;
                if (!_isTuiAudioDisabled)
                {
                    await _player.PlayAsync(url, _activeSong.Duration, currentPos);
                }
                Application.Invoke(() =>
                {
                    _controlBar.UpdateQuality(AudioQualityHelper.GetBadge(actualTier));
                    _nowPlayingView.SetSong(_activeSong, AudioQualityHelper.GetBadge(actualTier));
                    UpdatePlayerStatus();
                });
                _currentPlayUrl = url;
                if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
                {
                    _standaloneWebServer.CurrentPlayUrl = url;
                    _standaloneWebServer.ActualQualityTier = actualTier;
                    _standaloneWebServer.PreferredQualityTier = _preferredQualityTier;
                    _standaloneWebServer.CurrentPositionSeconds = currentPos;
                    _standaloneWebServer.BroadcastState("play");
                }
            }
        }
        else
        {
            Application.Invoke(() =>
            {
                _controlBar.UpdateQuality(AudioQualityHelper.GetBadge(_preferredQualityTier));
            });
            if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
            {
                _standaloneWebServer.PreferredQualityTier = _preferredQualityTier;
                _standaloneWebServer.ActualQualityTier = _preferredQualityTier;
                _standaloneWebServer.BroadcastState("quality_change");
            }
        }
    }

    private async Task PrefetchNextSongAsync()
    {
        try
        {
            // 延迟 2.5 秒，避免与当前歌曲的音源解码、歌词拉取争抢网络
            await Task.Delay(2500).ConfigureAwait(false);

            Song? nextSong = null;
            if (_currentViewMode == ViewMode.GuessRecommend)
            {
                if (_radioIndex + 1 < _radioQueue.Count)
                {
                    nextSong = _radioQueue[_radioIndex + 1];
                }
            }
            else
            {
                nextSong = PlaybackQueueService.Instance.PeekNextSong();
            }

            if (nextSong != null && !nextSong.IsLocal && !string.IsNullOrEmpty(nextSong.Mid))
            {
                // 1. 如果本地已有音频缓存，无需网络预热
                var cached = AudioCacheService.GetCachedAudioPath(nextSong.Mid, _preferredQualityTier);
                if (!string.IsNullOrEmpty(cached)) return;

                // 2. 预热本地封面
                _ = TerminalImageHelper.EnsureSongCoverAsync(nextSong);

                // 3. 预解析下一首音源链接并缓存
                var cacheKey = $"{nextSong.Mid}_{(int)_preferredQualityTier}";
                lock (s_prefetchLock)
                {
                    if (s_prefetchedPlayUrls.TryGetValue(cacheKey, out var item) && item.ExpireAt > DateTimeOffset.UtcNow)
                    {
                        return;
                    }
                }

                var (url, quality, actualTier) = await QqMusicApi.GetPlayUrlForTierAsync(nextSong.Mid, nextSong.EffectiveMediaMid, _preferredQualityTier).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(url))
                {
                    lock (s_prefetchLock)
                    {
                        s_prefetchedPlayUrls[cacheKey] = new PrefetchedPlayInfo(url, quality, actualTier, DateTimeOffset.UtcNow.AddMinutes(20));
                    }
                    AppLogger.Info("MainWindow", $"Prefetched next track audio URL successfully: {nextSong.Title} ({actualTier})");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("MainWindow", $"PrefetchNextSongAsync exception: {ex.Message}");
        }
    }
}
