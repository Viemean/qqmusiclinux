using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

    private CancellationTokenSource? _playbackCts;
    private long _playbackSessionId;

    // 物理真实累计收听满 30 秒门限流式持久化缓存状态机
    private bool _hasTriggeredCacheForCurrentSong;
    private long _currentCacheSessionId;
    private CancellationTokenSource? _cachingCts;
    private string? _lastResolvedPlayUrl;
    private double _accumulatedPlaySeconds;
    private double _lastProgressSec;

    private Task PlaySongAsync(Song song) => PlaySongAsync(song, 0);

    private async Task PlaySongAsync(Song song, double startPosition = 0)
    {
        var previousCts = Interlocked.Exchange(ref _playbackCts, new CancellationTokenSource());
        try
        {
            previousCts?.Cancel();
            previousCts?.Dispose();
        }
        catch {}

        var currentCts = _playbackCts;
        var currentSession = Interlocked.Increment(ref _playbackSessionId);
        var ct = currentCts.Token;

        // 重置 30 秒收听缓存状态与任务
        var cacheSession = Interlocked.Increment(ref _currentCacheSessionId);
        try
        {
            _cachingCts?.Cancel();
            _cachingCts?.Dispose();
        }
        catch {}
        _cachingCts = new CancellationTokenSource();
        _hasTriggeredCacheForCurrentSong = false;
        _lastResolvedPlayUrl = null;
        _accumulatedPlaySeconds = 0.0;
        _lastProgressSec = startPosition;

        bool IsStale() => ct.IsCancellationRequested || Interlocked.Read(ref _playbackSessionId) != currentSession;

        _activeSong = song;
        PlaybackQueueService.Instance.SyncCurrentSong(song);
        _songListView.SetPlayingSong(song.Mid);

        // 电台模式下实时更新专属电台卡片
        if (_currentViewMode == ViewMode.GuessRecommend)
        {
            Application.Invoke(() =>
            {
                if (IsStale()) return;
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
                if (IsStale()) return;
                _songListView.SetSongs(recentSongs, $"最近播放: 共 {recentSongs.Count} 首 (按 D 移除历史)");
                _songListView.SetPlayingSong(song.Mid);
            });
        }

        // 联动底栏收藏状态与本地/WebDAV模式
        var isFav = !song.IsLocal && !song.IsWebDav && ((!string.IsNullOrEmpty(song.Mid) && _favoriteSongMids.Contains(song.Mid)) ||
                    (song.Id > 0 && _favoriteSongIds.Contains(song.Id)));
        Application.Invoke(() =>
        {
            if (IsStale()) return;
            _controlBar.SetCurrentSong(song);
            _controlBar.SetLocalMode(song.IsLocal || song.IsWebDav);
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
        if (IsStale()) return;

        _currentLyrics.Clear();
        _currentActiveLyricIndex = -1;

        Application.Invoke(() =>
        {
            if (IsStale()) return;
            _controlBar.UpdateStatus($"正在解析音源: {song.Title} - {song.Artist} ...");
            _controlBar.UpdateQuality(AudioQualityHelper.GetBadge(_preferredQualityTier));
            _lyricListView.SetSource(new ObservableCollection<string> { "正在加载歌词..." });
            try { _lyricListView.SelectedItem = 0; } catch {}
        });

        string? playUrl;
        List<LyricLine> lyrics;

        if (song.IsWebDav)
        {
            var server = WebDavService.GetActiveServer();
            if (server != null && !string.IsNullOrEmpty(song.WebDavHref))
            {
                var localCache = WebDavService.GetLocalCachePath(server, song.WebDavHref);
                // 1. 若本地完整缓存已存在且有效，直接秒开本地文件
                if (File.Exists(localCache) && new FileInfo(localCache).Length > 4096)
                {
                    playUrl = localCache;
                    song = WebDavService.EnrichSongMetadata(server, song, playUrl);
                    _hasTriggeredCacheForCurrentSong = true; // 已有本地完整缓存，无需延时缓存
                }
                else
                {
                    // 2. 本地尚未缓存：直接使用带凭据的流式直链 URL 秒级起播，无需等待几十兆音频下载完毕
                    Application.Invoke(() =>
                    {
                        if (IsStale()) return;
                        _controlBar.UpdateStatus($"[WebDAV] 正在直连音频流: {song.Title} ...");
                    });
                    playUrl = WebDavService.BuildStreamingUriWithAuth(server, song.WebDavHref);
                    AppLogger.Info("MainWindow.Playback", $"Streaming WebDAV audio: {song.Title} via {playUrl}");
                }
            }
            else
            {
                playUrl = song.LocalFilePath;
                if (!string.IsNullOrEmpty(playUrl) && File.Exists(playUrl) && server != null)
                {
                    song = WebDavService.EnrichSongMetadata(server, song, playUrl);
                }
            }

            if (IsStale()) return;

            _actualQualityTier = song.Quality.Contains("Hi-Res", StringComparison.OrdinalIgnoreCase) ? AudioQualityTier.HiRes
                               : song.Quality.Contains("SQ", StringComparison.OrdinalIgnoreCase) ? AudioQualityTier.SQ
                               : song.Quality.Contains("HQ", StringComparison.OrdinalIgnoreCase) ? AudioQualityTier.HQ
                               : AudioQualityTier.Standard;

            // 通过级联策略加载 WebDAV 内嵌歌词（本地缓存 → 内存缓存 → 远端 .lrc → Range 头部提取）
            lyrics = server != null && !string.IsNullOrEmpty(song.WebDavHref)
                ? await WebDavService.EnsureLyricsAsync(server, song).ConfigureAwait(false)
                : [];

            // 同步更新全局激活歌曲与控制栏/MPRIS/AOD 状态
            _activeSong = song;
            Application.Invoke(() =>
            {
                if (IsStale()) return;
                _controlBar.SetCurrentSong(song);
                _controlBar.UpdateStatus($"正在播放: {song.Title} - {song.Artist}");
                _aodView.UpdateSong(song);
            });
            _player.UpdateCurrentSong(song);
            _mprisService.UpdateSong(song);
        }
        else if (song.IsLocal)
        {
            playUrl = song.LocalFilePath;
            _actualQualityTier = song.Quality.Contains("Hi-Res", StringComparison.OrdinalIgnoreCase) ? AudioQualityTier.HiRes
                                : song.Quality.Contains("SQ", StringComparison.OrdinalIgnoreCase) ? AudioQualityTier.SQ
                                : song.Quality.Contains("HQ", StringComparison.OrdinalIgnoreCase) ? AudioQualityTier.HQ
                                : AudioQualityTier.Standard;
            lyrics = await QQMusic.Tui.Services.LocalMusicService.GetLyricsAsync(song);
            if (IsStale()) return;
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
                if (IsStale()) return;
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

                if (IsStale()) return;

                playUrl = url;
                _actualQualityTier = actualTier;
                if (!string.IsNullOrEmpty(quality))
                {
                    song.Quality = quality;
                }
                lyrics = await QqMusicApi.GetLyricsAsync(song.Mid);
                if (IsStale()) return;

                _lastResolvedPlayUrl = playUrl;
                // 优化：不再在起播时立即全量写盘，延后至连续收听满 30 秒后再触发后台缓存，前奏切歌不消耗任何全量带宽
            }
        }

        if (IsStale()) return;

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
            if (IsStale()) return;
            _hasTranslation = hasTrans;
            UpdateTranslationButtonHighlight();
            _controlBar.UpdateTranslationAvailability(hasTrans);
        });

        if (IsStale()) return;

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

            if (IsStale()) return;

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

            // 异步后台拉取/提取封面，就绪后立即向系统 MPRIS 发送 mpris:artUrl 并弹出桌面切歌通知
            _ = Task.Run(async () =>
            {
                if (IsStale()) return;
                string? cover = null;
                try
                {
                    cover = await TerminalImageHelper.EnsureSongCoverAsync(song).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(cover) && !IsStale())
                    {
                        _mprisService.UpdateCover(cover);
                    }
                }
                catch {}

                if (!IsStale())
                {
                    DesktopNotificationService.Instance.NotifySongSwitch(song, _actualQualityTier, cover);
                }
            });

            // 若为本地歌曲或 WebDAV 歌曲，且无歌词或缺少翻译歌词（仅在外文歌曲确实需要翻译时），后台自动尝试匹配在线歌词与双语翻译
            bool isLocalOrWebDav = song.IsLocal || song.IsWebDav;
            bool isNoLyrics = _currentLyrics.Count == 0 || (_currentLyrics.Count == 1 && _currentLyrics[0].Text == "暂无歌词");
            bool isMissingTrans = !hasTrans && LyricParser.NeedsTranslation(_currentLyrics);
            if (isLocalOrWebDav && !IsCurrentSongLyricMatched(song) && (isNoLyrics || isMissingTrans))
            {
                _ = Task.Run(async () =>
                {
                    if (IsStale()) return;
                    await AutoMatchLyricAsync(song, playUrl, isNoLyrics);
                });
            }

            Application.Invoke(() =>
            {
                if (IsStale()) return;
                UpdatePlayerStatus();
                _nowPlayingView.SetSong(song, AudioQualityHelper.GetBadge(_actualQualityTier));
                _nowPlayingView.SetLyrics(_currentLyrics, _showTranslation);
                _nowPlayingView.SetLyricMatchedState(IsCurrentSongLyricMatched(song));
                UpdateLyricMatchButtonHighlight();
            });
        }
        else
        {
            if (IsStale()) return;

            Application.Invoke(() =>
            {
                if (IsStale()) return;
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
                if (IsStale()) return;
                Application.Invoke(async () =>
                {
                    if (IsStale()) return;
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

        Application.Invoke(() =>
        {
            if (IsStale()) return;
            RefreshLyricListView();
        });

        // 启动后台平滑预热下一首曲目的音源与封面
        _ = Task.Run(PrefetchNextSongAsync);
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
        if (_activeSong == null) return;

        // 若曲目总时长原本未知（如未缓存的 WebDAV 流媒体），当底层 GStreamer 探测到真实流时长后动态回填
        if (_activeSong.Duration <= 0 && _player.TotalDurationSeconds > 0)
        {
            _activeSong.Duration = (int)Math.Round(_player.TotalDurationSeconds);
            if (_standaloneWebServer != null)
            {
                _standaloneWebServer.TotalDurationSeconds = _activeSong.Duration;
            }
            _mprisService.UpdateSong(_activeSong);
        }

        // 累计真实物理播放时长：仅在播放状态、进度向前自然推移（排除拖拽快进/跳跃）时累加
        if (_player.IsPlaying && currentSec > _lastProgressSec)
        {
            double delta = currentSec - _lastProgressSec;
            if (delta > 0 && delta <= 1.5)
            {
                _accumulatedPlaySeconds += delta;
            }
        }
        _lastProgressSec = currentSec;

        // 真实收听满 30 秒物理时长门限检测（或超短音频收听超 80%）
        if (!_hasTriggeredCacheForCurrentSong && !_activeSong.IsLocal)
        {
            bool reachThreshold = _accumulatedPlaySeconds >= 30.0 ||
                (_activeSong.Duration > 0 && _activeSong.Duration < 30.0 && _accumulatedPlaySeconds >= _activeSong.Duration * 0.8);
            if (reachThreshold)
            {
                _hasTriggeredCacheForCurrentSong = true;
                TriggerBackgroundCacheForSong(_activeSong, _currentCacheSessionId, _cachingCts?.Token ?? CancellationToken.None);
            }
        }

        // AOD 后台息屏模式：仅在后台同步 D-Bus 位置与防抖持久化，不触发前台界面控件重绘
        if (_isAodMode)
        {
            _mprisService.UpdatePosition(currentSec, _activeSong.Duration);
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
        var total = TimeSpan.FromSeconds(Math.Max(0, _activeSong.Duration));
        var progressPercent = _activeSong.Duration > 0 ? Math.Clamp(currentSec / _activeSong.Duration, 0, 1) : 0;

        _controlBar.UpdateProgress(cur, total, progressPercent);
        _mprisService.UpdatePosition(currentSec, _activeSong.Duration);

        UserSession.Current.LastPlaybackPositionSeconds = currentSec;
        UserSession.Current.LastPlayedSong = _activeSong;
        if (Environment.TickCount64 - _lastProgressSaveTick > 5000)
        {
            _lastProgressSaveTick = Environment.TickCount64;
            UserSession.Current.Save();
        }
    }

    /// <summary>
    /// 收听满 30 秒后异步启动后台持久化落盘缓存
    /// </summary>
    private void TriggerBackgroundCacheForSong(Song song, long session, CancellationToken ct)
    {
        if (song.IsLocal) return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (ct.IsCancellationRequested || session != Interlocked.Read(ref _currentCacheSessionId)) return;

                if (song.IsWebDav && !string.IsNullOrEmpty(song.WebDavHref))
                {
                    var server = WebDavService.GetActiveServer();
                    if (server != null)
                    {
                        var localCache = WebDavService.GetLocalCachePath(server, song.WebDavHref);
                        if (!File.Exists(localCache) || new FileInfo(localCache).Length <= 4096)
                        {
                            AppLogger.Info("MainWindow.Playback", $"[30s收听达成] 启动后台持久化缓存 WebDAV 曲目: {song.Title}");
                            var downloadedPath = await WebDavService.GetOrDownloadAudioAsync(server, song.WebDavHref, null, ct).ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(downloadedPath) && File.Exists(downloadedPath))
                            {
                                WebDavService.EnrichSongMetadata(server, song, downloadedPath);
                            }
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(song.Mid))
                {
                    var cachedPath = AudioCacheService.GetCachedAudioPath(song.Mid, _actualQualityTier);
                    if (string.IsNullOrEmpty(cachedPath) || !File.Exists(cachedPath))
                    {
                        if (!string.IsNullOrEmpty(_lastResolvedPlayUrl) && _lastResolvedPlayUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            AppLogger.Info("MainWindow.Playback", $"[30s收听达成] 启动后台持久化缓存在线曲目: {song.Title} ({_actualQualityTier})");
                            await AudioCacheService.CacheAudioAsync(song.Mid, _actualQualityTier, _lastResolvedPlayUrl, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 切歌取消正常
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MainWindow.Playback", $"Background caching task failed for {song.Title}: {ex.Message}");
            }
        }, ct);
    }


    private async Task CycleQualityTierAsync(bool allowHiRes = false)
    {
        if (_activeSong != null && (_activeSong.IsLocal || _activeSong.IsWebDav))
        {
            return;
        }

        // Web 端使用单按钮循环全部支持的在线音质。
        var currentIndex = AudioQualityHelper.GetSelectionIndex(_preferredQualityTier);
        if (currentIndex < 0) currentIndex = 0;
        var nextTier = AudioQualityHelper.SelectionOrder[(currentIndex + 1) % AudioQualityHelper.SelectionOrder.Count];
        await SwitchQualityTierAsync(nextTier);
    }

    private async Task SwitchQualityTierAsync(AudioQualityTier newTier)
    {
        _preferredQualityTier = newTier;
        UserSession.Current.PreferredQuality = AudioQualityHelper.GetBadge(newTier);
        UserSession.Current.Save();

        if (_activeSong != null && !_activeSong.IsLocal && !_activeSong.IsWebDav)
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
