using Terminal.Gui.App;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.UI;

public sealed partial class MainWindow
{
    /// <summary>
    /// 进入“猜你喜欢”视窗：若当前已经在播放电台流，则平滑恢复电台卡片展示而不打断播放；否则启动全新电台流
    /// </summary>
    private async Task ResumeOrStartGuessRadioAsync()
    {
        _currentViewMode = ViewMode.GuessRecommend;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        if (!UserSession.Current.IsLoggedIn)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("请按 L 键登录后体验“猜你喜欢”个性化音乐电台", "猜你喜欢 (未登录)");
                _controlBar.UpdateStatus("[猜你喜欢] 请先按 L 登录账号以获取个性化推荐");
            });
            return;
        }

        // 如果电台队列已就绪，且当前播放曲目属于电台队列，直接恢复电台并打开沉浸式播放界面
        if (_radioQueue.Count > 0 && _radioIndex < _radioQueue.Count && _activeSong != null && _radioQueue.Any(s => s.Mid == _activeSong.Mid))
        {
            Application.Invoke(() =>
            {
                _songListView.SetRadioCard(_activeSong, AudioQualityHelper.GetBadge(_actualQualityTier), _radioPlayedCount);
                OpenNowPlayingView();
            });
            return;
        }

        // 否则重新启动电台流
        await StartGuessRadioAsync();
    }

    /// <summary>
    /// 启动“猜你喜欢”个性化音乐电台（流模式）：进入即播放首曲，直接切入沉浸式大图歌词播放界面
    /// </summary>
    private async Task StartGuessRadioAsync()
    {
        _currentViewMode = ViewMode.GuessRecommend;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        if (!UserSession.Current.IsLoggedIn)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("请按 L 键登录后体验“猜你喜欢”个性化音乐电台", "猜你喜欢 (未登录)");
                _controlBar.UpdateStatus("[猜你喜欢] 请先按 L 登录账号以获取个性化推荐");
            });
            return;
        }

        _radioQueue.Clear();
        _radioIndex = 0;
        _radioPlayedCount = 1;

        Application.Invoke(() =>
        {
            _songListView.SetMessage("正在根据您的音乐品味连接个性化电台...", "猜你喜欢 (连接中)");
            _controlBar.UpdateStatus("[个性电台] 正在连接 QQ 音乐猜你喜欢电台流...");
        });

        var songs = await QqMusicApi.GetGuessRecommendSongsAsync(5);
        if (songs.Count == 0)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("未能获取到电台推荐歌曲，请按 L 检查登录状态或按 R 重试", "猜你喜欢: 0 首");
                _controlBar.UpdateStatus("[电台提示] 未能获取到推荐曲目，可按 R 重新连接");
            });
            return;
        }

        _radioQueue.AddRange(songs);
        var firstSong = _radioQueue[0];

        // 立即播放首曲并无缝呈现沉浸式播放大界面
        await PlaySongAsync(firstSong);
        Application.Invoke(() =>
        {
            _songListView.SetRadioCard(firstSong, AudioQualityHelper.GetBadge(_actualQualityTier), _radioPlayedCount);
            _controlBar.UpdateStatus($"[电台启播] 猜你喜欢第 01 首: 《{firstSong.Title}》 - {firstSong.Artist}");
            OpenNowPlayingView();
        });

        // 若初始歌曲少于或等于 3 首，后台静默补充
        if (_radioQueue.Count <= 3)
        {
            _ = Task.Run(PrefetchNextRadioBatchAsync);
        }
    }

    /// <summary>
    /// 电台模式：跳至下一首（单曲播完或按 ] / N / D 触发）
    /// </summary>
    private async Task PlayNextRadioTrackAsync()
    {
        if (_radioQueue.Count == 0)
        {
            await StartGuessRadioAsync();
            return;
        }

        _radioIndex++;
        if (_radioIndex >= _radioQueue.Count)
        {
            Application.Invoke(() =>
            {
                _controlBar.UpdateStatus("[个性电台] 正在缓冲下一批个性化推荐歌曲...");
            });
            await PrefetchNextRadioBatchAsync();
            if (_radioIndex >= _radioQueue.Count)
            {
                _radioIndex = 0;
            }
        }

        if (_radioIndex < _radioQueue.Count)
        {
            _radioPlayedCount++;
            var nextSong = _radioQueue[_radioIndex];
            await PlaySongAsync(nextSong);
            Application.Invoke(() =>
            {
                _songListView.SetRadioCard(nextSong, AudioQualityHelper.GetBadge(_actualQualityTier), _radioPlayedCount);
                _controlBar.UpdateStatus($"[电台切歌] 猜你喜欢第 {_radioPlayedCount:D2} 首: 《{nextSong.Title}》 - {nextSong.Artist}");
            });

            if (_radioQueue.Count - _radioIndex <= 3)
            {
                _ = Task.Run(PrefetchNextRadioBatchAsync);
            }
        }
    }

    /// <summary>
    /// 后台静默预拉取电台歌曲，杜绝并发调用 API 冲突并去重追加
    /// </summary>
    private async Task PrefetchNextRadioBatchAsync()
    {
        if (_isRadioPrefetching) return;
        _isRadioPrefetching = true;
        try
        {
            var moreSongs = await QqMusicApi.GetGuessRecommendSongsAsync(5);
            if (moreSongs.Count > 0)
            {
                lock (_radioQueue)
                {
                    var existingMids = new HashSet<string>(_radioQueue.Select(s => s.Mid));
                    foreach (var s in moreSongs)
                    {
                        if (!string.IsNullOrEmpty(s.Mid) && !existingMids.Contains(s.Mid))
                        {
                            _radioQueue.Add(s);
                            existingMids.Add(s.Mid);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainWindow", $"PrefetchNextRadioBatchAsync error: {ex.Message}");
        }
        finally
        {
            _isRadioPrefetching = false;
        }
    }
}
