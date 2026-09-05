using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.UI;

public sealed partial class MainWindow
{
    private void ShowLoginDialog()
    {
        var dlg = new LoginDialog(() =>
        {
            Application.Invoke(() =>
            {
                _userStatusBtn.Text = GetUserStatusText();
            });
        });
        Application.Run(dlg);
        _userStatusBtn.Text = GetUserStatusText();
    }

    private void ShowQualityDialog()
    {
        if (_activeSong != null && _activeSong.IsLocal)
        {
            _controlBar.UpdateStatus($"[本地音乐] 当前为本地音频规格 ({_activeSong.Quality})，无需切换在线音质");
            return;
        }

        var dlg = new QualityDialog(_activeSong, _preferredQualityTier, async (newTier, option) =>
        {
            _preferredQualityTier = newTier;
            _actualQualityTier = option?.Tier ?? newTier;
            UserSession.Current.PreferredQuality = AudioQualityHelper.GetBadge(newTier);
            UserSession.Current.Save();

            Application.Invoke(() =>
            {
                _controlBar.UpdateQuality(AudioQualityHelper.GetBadge(_actualQualityTier));
                if (_currentViewMode == ViewMode.GuessRecommend && _activeSong != null)
                {
                    _songListView.SetRadioCard(_activeSong, AudioQualityHelper.GetBadge(_actualQualityTier), _radioPlayedCount);
                }
            });

            if (_activeSong != null && _player.IsPlaying)
            {
                var currentPos = _player.CurrentPositionSeconds;
                Application.Invoke(() =>
                {
                    _controlBar.UpdateStatus($"正在切换至 [{AudioQualityHelper.GetBadge(newTier)}] 音质...");
                });

                string? playUrl = option?.PlayUrl;
                string qualityName = option?.Badge ?? AudioQualityHelper.GetBadge(newTier);

                if (string.IsNullOrEmpty(playUrl))
                {
                    var res = await QqMusicApi.GetPlayUrlForTierAsync(_activeSong.Mid, _activeSong.EffectiveMediaMid, newTier);
                    playUrl = res.Url;
                    qualityName = res.Quality;
                    _actualQualityTier = res.Tier;
                }

                if (!string.IsNullOrEmpty(playUrl))
                {
                    _activeSong.Quality = qualityName;
                    await _player.PlayAsync(playUrl, _activeSong.Duration, currentPos);
                    Application.Invoke(UpdatePlayerStatus);
                }
                else
                {
                    Application.Invoke(() =>
                    {
                        _controlBar.UpdateStatus($"[切换失败] {_activeSong.Title} 暂无 {AudioQualityHelper.GetBadge(newTier)} 音源");
                    });
                }
            }
        });
        Application.Run(dlg);
    }

    private void ShowDownloadDialog()
    {
        var targetSong = _activeSong ?? _songListView.GetSelectedSong();
        if (targetSong == null)
        {
            _controlBar.UpdateStatus("[下载提示] 当前暂无播放或选中的曲目");
            return;
        }

        if (targetSong.IsLocal)
        {
            _controlBar.UpdateStatus("[本地音乐] 当前曲目已在本地磁盘，无需下载");
            return;
        }

        var dlg = new QualityDialog(targetSong, _preferredQualityTier, (newTier, option) =>
        {
            _ = Task.Run(() => DownloadSongAsync(targetSong, newTier, option));
        }, customTitle: "歌曲下载");
        Application.Run(dlg);
    }

    private async Task DownloadSongAsync(Song song, AudioQualityTier tier, QualityOption? option)
    {
        try
        {
            string tierBadge = option?.Badge ?? AudioQualityHelper.GetBadge(tier);
            Application.Invoke(() => _controlBar.UpdateStatus($"[正在解析] 正在解析《{song.Title}》的 {tierBadge} 下载链接..."));

            string? playUrl = option?.PlayUrl;
            AudioQualityTier actualTier = option?.Tier ?? tier;

            if (string.IsNullOrEmpty(playUrl))
            {
                var res = await QqMusicApi.GetPlayUrlForTierAsync(song.Mid, song.EffectiveMediaMid, tier);
                playUrl = res.Url;
                actualTier = res.Tier;
            }

            if (string.IsNullOrEmpty(playUrl))
            {
                Application.Invoke(() => _controlBar.UpdateStatus($"[下载失败] 《{song.Title}》({tierBadge}) 未能获取下载链接 (可能需要 VIP)"));
                return;
            }

            string musicDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrEmpty(musicDir) || !Directory.Exists(musicDir))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                musicDir = Path.Combine(home, "Music");
                if (!Directory.Exists(musicDir))
                {
                    string zhMusic = Path.Combine(home, "音乐");
                    musicDir = Directory.Exists(zhMusic) ? zhMusic : Path.Combine(home, "Music");
                }
            }
            string targetDir = Path.Combine(musicDir, "qqmusic");
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            string ext = (actualTier == AudioQualityTier.HiRes || actualTier == AudioQualityTier.SQ) ? ".flac" : ".mp3";
            string safeArtist = string.Join("_", song.Artist.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            string safeTitle = string.Join("_", song.Title.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            string fileName = $"{safeArtist} - {safeTitle}{ext}";
            string targetPath = Path.Combine(targetDir, fileName);

            Application.Invoke(() => _controlBar.UpdateStatus($"[正在下载] {fileName} ..."));

            using var client = new System.Net.Http.HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36");
            client.DefaultRequestHeaders.Add("Referer", "https://y.qq.com/");

            using var resp = await client.GetAsync(playUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            if (resp.IsSuccessStatusCode)
            {
                await using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs);
                Application.Invoke(() => _controlBar.UpdateStatus($"[下载完成] 已保存至: {fileName} (音乐/qqmusic)"));
            }
            else
            {
                Application.Invoke(() => _controlBar.UpdateStatus($"[下载失败] HTTP 响应码: {resp.StatusCode}"));
            }
        }
        catch (Exception ex)
        {
            Application.Invoke(() => _controlBar.UpdateStatus($"[下载异常] {ex.Message}"));
        }
    }

    private void ShowExitConfirmDialog()
    {
        bool confirmed = false;
        var dlg = new Dialog
        {
            Title = "退出确认",
            Width = 46,
            Height = 7
        };
        dlg.SetScheme(MikuTheme.Dialog);

        var msg = new Label
        {
            Text = "确定要退出 QQ 音乐吗？",
            X = Pos.Center(),
            Y = 1
        };
        dlg.Add(msg);

        var yesBtn = new Button
        {
            Text = "确定 (Y/Enter)",
            X = Pos.Center() - 12,
            Y = 3,
            ShadowStyle = ShadowStyles.None
        };
        yesBtn.Accepting += (s, e) =>
        {
            confirmed = true;
            Application.RequestStop();
        };
        dlg.Add(yesBtn);

        var noBtn = new Button
        {
            Text = "取消 (Esc)",
            X = Pos.Center() + 4,
            Y = 3,
            ShadowStyle = ShadowStyles.None
        };
        noBtn.Accepting += (s, e) =>
        {
            Application.RequestStop();
        };
        dlg.Add(noBtn);

        dlg.KeyDown += (s, k) =>
        {
            if (k == Key.Y || k.AsRune.Value == 'y' || k.AsRune.Value == 'Y')
            {
                k.Handled = true;
                confirmed = true;
                Application.RequestStop();
            }
            else if (k == Key.N || k.AsRune.Value == 'n' || k.AsRune.Value == 'N' || k == Key.Esc)
            {
                k.Handled = true;
                Application.RequestStop();
            }
        };

        Application.Run(dlg);

        if (confirmed)
        {
            UserSession.Current.Save();
            _mprisService.Dispose();
            _player.Dispose();
            Application.RequestStop();
        }
    }
}
