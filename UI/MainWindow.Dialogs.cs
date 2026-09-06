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

namespace QQMusic.Tui.UI;

public sealed partial class MainWindow
{
    private void ShowLoginDialog()
    {
        var dlg = new LoginDialog(() =>
        {
            Application.Invoke(() =>
            {
                UpdateTopRightButtonsLayout();
            });
        });
        Application.Run(dlg);
        UpdateTopRightButtonsLayout();
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

    private static Scheme TransparentDialogScheme { get; } = new Scheme
    {
        Normal    = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Terminal.Gui.Drawing.Color.None),
        Focus     = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, MikuTheme.QqGreenDark),
        HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuPinkAccent, Terminal.Gui.Drawing.Color.None),
        HotFocus  = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, MikuTheme.MikuPinkAccent),
        Disabled  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Terminal.Gui.Drawing.Color.None),
        Highlight = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Terminal.Gui.Drawing.Color.None),
        Active    = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
        ReadOnly  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Terminal.Gui.Drawing.Color.None),
        Editable  = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, Terminal.Gui.Drawing.Color.None)
    };

    private void ShowExitConfirmDialog()
    {
        bool confirmed = false;
        int dlgW = 46;
        int dlgH = 8;
        var dlg = new Dialog
        {
            Title = "退出确认",
            Width = dlgW,
            Height = dlgH,
            Y = Pos.Center()
        };

        if (_isNowPlayingViewActive)
        {
            // 在播放界面下：移动到右侧歌词视窗水平中心，避开左侧封面图层
            dlg.X = Pos.Percent(74) - (dlgW / 2);
        }
        else
        {
            dlg.X = Pos.Center();
        }

        dlg.SetScheme(TransparentDialogScheme);

        var msg = new Label
        {
            Text = "确定要退出 QQ 音乐吗？",
            X = Pos.Center(),
            Y = 1
        };
        msg.SetScheme(TransparentDialogScheme);
        dlg.Add(msg);

        // 底部居中对称摆放按钮，参考多歌手选择弹窗规范，拉开充裕间距防止字符挤压
        var yesBtn = new Button
        {
            Text = "确定 (Enter)",
            X = Pos.Center() - 14,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        yesBtn.SetScheme(TransparentDialogScheme);
        yesBtn.Accepting += (s, e) =>
        {
            confirmed = true;
            Application.RequestStop(dlg);
        };

        var noBtn = new Button
        {
            Text = "取消 (Esc)",
            X = Pos.Center() + 2,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        noBtn.SetScheme(TransparentDialogScheme);
        noBtn.Accepting += (s, e) =>
        {
            Application.RequestStop(dlg);
        };

        dlg.Add(yesBtn, noBtn);

        dlg.KeyDown += (s, k) =>
        {
            if (k == Key.Y || k.AsRune.Value == 'y' || k.AsRune.Value == 'Y' ||
                k == Key.Enter || k.AsRune.Value == '\r' || k.AsRune.Value == '\n')
            {
                k.Handled = true;
                confirmed = true;
                Application.RequestStop(dlg);
            }
            else if (k == Key.N || k.AsRune.Value == 'n' || k.AsRune.Value == 'N' ||
                     k == Key.Esc || k.AsRune.Value == 'q' || k.AsRune.Value == 'Q')
            {
                k.Handled = true;
                Application.RequestStop(dlg);
            }
        };

        MikuTheme.ApplyTo(dlg, TransparentDialogScheme);
        yesBtn.SetFocus();

        Application.Run(dlg);

        if (confirmed)
        {
            UserSession.Current.Save();
            _mprisService.Dispose();
            _player.Dispose();
            _standaloneWebServer?.Dispose();
            Application.RequestStop();
        }
    }

    private bool _isAudioRecognitionActive = false;

    private void ShowAudioRecognitionDialog()
    {
        if (_isAudioRecognitionActive) return;
        _isAudioRecognitionActive = true;

        try
        {
            Song? selectedSong = null;
            using var dlg = new AudioRecognitionDialog(song =>
            {
                selectedSong = song;
            }, inLyricArea: _isNowPlayingViewActive);

            Application.Run(dlg);

            // 等待 Dialog 完全退栈并从界面销毁后再触发播放与切换沉浸界面
            if (selectedSong != null)
            {
                _ = Task.Run(async () =>
                {
                    await PlaySongAsync(selectedSong, 0);
                    Application.Invoke(OpenNowPlayingView);
                });
            }
        }
        finally
        {
            _isAudioRecognitionActive = false;
        }
    }

    private void ShowWebStatusDialog(string url)
    {
        var dlg = new Dialog
        {
            Title = "Web 协同服务配置",
            Width = 64,
            Height = 11
        };

        string GetAddressText(int port)
        {
            var lanIp = WebPlaybackServer.GetLocalLanIp();
            return lanIp != null
                ? $"服务地址: http://0.0.0.0:{port}/ ({lanIp})"
                : $"服务地址: http://0.0.0.0:{port}/";
        }

        var addrLabel = new Label
        {
            Text = GetAddressText(_webServerPort),
            X = Pos.Center(),
            Y = 1,
            TextAlignment = Alignment.Center
        };

        var portLabel = new Label
        {
            Text = "服务端口:",
            X = 3,
            Y = 3
        };

        var portField = new TextField
        {
            Text = _webServerPort.ToString(),
            X = 13,
            Y = 3,
            Width = 9
        };
        portField.SetScheme(TransparentDialogScheme);

        var portHintLabel = new Label
        {
            Text = "(按回车直接应用)",
            X = 24,
            Y = 3
        };

        var tuiAudioLabel = new Label
        {
            Text = "TUI音频:",
            X = 3,
            Y = 5
        };

        var tuiAudioBtn = new Button
        {
            Text = _isTuiAudioDisabled ? "已禁用 (清理进程，仅Web播放) [T]" : "已开启 (本地硬件输出) [T]",
            X = 13,
            Y = 5,
            ShadowStyle = ShadowStyles.None
        };
        tuiAudioBtn.SetScheme(TransparentDialogScheme);

        void UpdateTuiAudioBtn()
        {
            tuiAudioBtn.Text = _isTuiAudioDisabled ? "已禁用 (清理进程，仅Web播放) [T]" : "已开启 (本地硬件输出) [T]";
        }

        void DoToggleTuiAudio()
        {
            _ = SetTuiAudioDisabledAsync(!_isTuiAudioDisabled);
            UpdateTuiAudioBtn();
        }

        tuiAudioBtn.Accepting += (s, e) => DoToggleTuiAudio();

        void DoApplyPort()
        {
            var portStr = portField.Text?.ToString()?.Trim();
            if (int.TryParse(portStr, out int p) && p >= 1024 && p <= 65535)
            {
                if (p != _webServerPort)
                {
                    _webServerPort = p;
                    RestartStandaloneWebServer(p);
                    addrLabel.Text = GetAddressText(_webServerPort);
                    _controlBar.UpdateStatus($"Web服务已迁移至端口: {_webServerPort}");
                }
            }
            else
            {
                _controlBar.UpdateStatus("端口无效，请输入 1024~65535 范围端口");
            }
        }

        portField.Accepting += (s, e) => DoApplyPort();
        portField.KeyDown += (s, k) =>
        {
            if (k == Key.Enter || k.AsRune.Value == '\r' || k.AsRune.Value == '\n')
            {
                k.Handled = true;
                DoApplyPort();
            }
        };

        var openBtn = new Button
        {
            Text = "浏览器打开 (B)",
            X = Pos.Center() - 19,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        openBtn.SetScheme(TransparentDialogScheme);

        void DoOpenBrowser()
        {
            try
            {
                string openTarget = $"http://127.0.0.1:{_webServerPort}/";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = openTarget,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch {}
            Application.RequestStop(dlg);
        }

        openBtn.Accepting += (s, e) => DoOpenBrowser();

        var stopBtn = new Button
        {
            Text = "关闭服务 (S)",
            X = Pos.Center() - 1,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        stopBtn.SetScheme(TransparentDialogScheme);
        if (_player is WebPlayer)
        {
            stopBtn.Enabled = false;
        }

        void DoStopServer()
        {
            if (_player is WebPlayer)
            {
                _controlBar.UpdateStatus("当前运行在 WebPlayer 模式，无法单独关闭服务");
                return;
            }
            StopStandaloneWebServer();
            Application.RequestStop(dlg);
        }

        stopBtn.Accepting += (s, e) => DoStopServer();

        var closeBtn = new Button
        {
            Text = "返回 (Esc)",
            X = Pos.Center() + 14,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        closeBtn.SetScheme(TransparentDialogScheme);
        closeBtn.Accepting += (s, e) => Application.RequestStop(dlg);

        dlg.Add(addrLabel, portLabel, portField, portHintLabel, tuiAudioLabel, tuiAudioBtn, openBtn, stopBtn, closeBtn);

        dlg.KeyDown += (s, k) =>
        {
            if (k == Key.B || k.AsRune.Value == 'b' || k.AsRune.Value == 'B')
            {
                k.Handled = true;
                DoOpenBrowser();
            }
            else if (k == Key.S || k.AsRune.Value == 's' || k.AsRune.Value == 'S')
            {
                k.Handled = true;
                DoStopServer();
            }
            else if (k == Key.T || k.AsRune.Value == 't' || k.AsRune.Value == 'T')
            {
                k.Handled = true;
                DoToggleTuiAudio();
            }
            else if (k == Key.Esc || k.AsRune.Value == 'q' || k.AsRune.Value == 'Q')
            {
                k.Handled = true;
                Application.RequestStop(dlg);
            }
        };

        MikuTheme.ApplyTo(dlg, TransparentDialogScheme);
        closeBtn.SetFocus();
        Application.Run(dlg);
    }
}
