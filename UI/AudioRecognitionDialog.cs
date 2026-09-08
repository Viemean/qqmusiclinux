using System.Diagnostics;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;
using QQMusic.Tui.Services.AcrCloud;
using QQMusic.Tui.Utils;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace QQMusic.Tui.UI;

/// <summary>
/// 听歌识曲流式交互弹窗
/// 支持系统内录与麦克风切换、多阶段流式识别
/// </summary>
public sealed class AudioRecognitionDialog : Dialog
{
    private readonly Action<Song>? _onSongSelected;
    private CancellationTokenSource _cts = new();

    private readonly Label _statusLabel;
    private readonly Label _detailLabel1;
    private readonly Label _detailLabel2;
    private readonly Label _detailLabel3;
    private readonly Button _actionBtn;
    private readonly Button _sourceBtn;
    private readonly Button _keyBtn;
    private readonly Button _cancelBtn;

    // 记忆用户选择的录音源，避免每次打开弹窗时重置
    private static AudioRecordSource s_currentSource = AudioRecordSource.SystemInternal;
    private AudioRecordSource _currentSource = s_currentSource;
    private long _lastToggleTick = 0;
    private Song? _recognizedSong;
    private bool _isRecognized = false;
    private bool _isWorking = false;
    private bool _isDismissed = false;
    private AudioRecordingSession? _recordingSession;

    private static Scheme TransparentDialogScheme { get; } = new Scheme
    {
        Normal    = new Attribute(MikuTheme.MikuTextWhite, Color.None),
        Focus     = new Attribute(Color.White, MikuTheme.QqGreenDark),
        HotNormal = new Attribute(MikuTheme.MikuPinkAccent, Color.None),
        HotFocus  = new Attribute(Color.White, MikuTheme.MikuPinkAccent),
        Disabled  = new Attribute(MikuTheme.MikuTextMuted, Color.None),
        Highlight = new Attribute(MikuTheme.QqGreenPrimary, Color.None),
        Active    = new Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
        ReadOnly  = new Attribute(MikuTheme.MikuTextMuted, Color.None),
        Editable  = new Attribute(Color.White, Color.None)
    };

    private string GetCurrentSourceButtonText()
    {
        return _currentSource == AudioRecordSource.SystemInternal ? "内录 (T)" : "麦克风 (T)";
    }

    public AudioRecognitionDialog(Action<Song>? onSongSelected, bool inLyricArea = false)
    {
        _onSongSelected = onSongSelected;
        _currentSource = s_currentSource;

        // 预热网络连接池
        AudioRecognitionService.PreWarm();

        Title = "听歌识曲";
        int dlgW = 58;
        int dlgH = 11;
        Width = dlgW;
        Height = dlgH;
        Y = Pos.Center();

        if (inLyricArea)
        {
            X = Pos.Percent(50) + Pos.Percent(25) - (dlgW / 2);
        }
        else
        {
            X = Pos.Center();
        }

        SetScheme(TransparentDialogScheme);

        _statusLabel = new Label
        {
            Text = _currentSource == AudioRecordSource.SystemInternal
                ? "[系统内录] 音频识别中..."
                : "[麦克风] 音频识别中...",
            X = 3,
            Y = 1,
            Width = Dim.Fill(2)
        };
        _statusLabel.SetScheme(TransparentDialogScheme);

        _detailLabel1 = new Label
        {
            Text = "",
            X = 3,
            Y = 5,
            Width = Dim.Fill(2)
        };
        _detailLabel1.SetScheme(TransparentDialogScheme);

        _detailLabel2 = new Label
        {
            Text = "",
            X = 3,
            Y = 6,
            Width = Dim.Fill(2),
            Visible = false
        };
        _detailLabel2.SetScheme(TransparentDialogScheme);

        _detailLabel3 = new Label
        {
            Text = "",
            X = 3,
            Y = 7,
            Width = Dim.Fill(2),
            Visible = false
        };
        _detailLabel3.SetScheme(TransparentDialogScheme);

        _actionBtn = new Button
        {
            Text = "重试 (R)",
            X = 2,
            Y = Pos.AnchorEnd(1),
            NoDecorations = true,
            ShadowStyle = ShadowStyles.None
        };
        _actionBtn.SetScheme(TransparentDialogScheme);
        _actionBtn.Accepting += (s, e) => HandleActionTriggered();

        _sourceBtn = new Button
        {
            Text = GetCurrentSourceButtonText(),
            X = Pos.Right(_actionBtn) + 2,
            Y = Pos.AnchorEnd(1),
            NoDecorations = true,
            ShadowStyle = ShadowStyles.None,
            CanFocus = false
        };
        _sourceBtn.SetScheme(TransparentDialogScheme);
        _sourceBtn.KeyBindings.Clear(); // 移除按钮内置热键，统一由窗体 KeyDown 分发
        _sourceBtn.Accepting += (s, e) => ToggleAudioSource();

        _keyBtn = new Button
        {
            Text = "密钥 (K)",
            X = Pos.Right(_sourceBtn) + 2,
            Y = Pos.AnchorEnd(1),
            NoDecorations = true,
            ShadowStyle = ShadowStyles.None
        };
        _keyBtn.SetScheme(TransparentDialogScheme);
        _keyBtn.Accepting += (s, e) => ShowAcrCloudConfigDialog();

        _cancelBtn = new Button
        {
            Text = "取消 (Esc)",
            X = Pos.Right(_keyBtn) + 2,
            Y = Pos.AnchorEnd(1),
            NoDecorations = true,
            ShadowStyle = ShadowStyles.None
        };
        _cancelBtn.SetScheme(TransparentDialogScheme);
        _cancelBtn.Accepting += (s, e) => HandleCancel();

        Add(_statusLabel, _detailLabel1, _detailLabel2, _detailLabel3, _actionBtn, _sourceBtn, _keyBtn, _cancelBtn);

        KeyDown += (s, k) =>
        {
            if (_isDismissed)
            {
                k.Handled = true;
                return;
            }

            if (k == Key.Esc || k == Key.Q || k == Key.Q.WithShift)
            {
                k.Handled = true;
                HandleCancel();
                return;
            }

            if (k == Key.R || k == Key.R.WithShift)
            {
                k.Handled = true;
                if (_isRecognized)
                {
                    HandleActionTriggered();
                }
                else
                {
                    StartRecognitionProcess();
                }
                return;
            }

            if (k == Key.T || k == Key.T.WithShift)
            {
                k.Handled = true;
                if (!_isRecognized)
                {
                    ToggleAudioSource();
                }
                return;
            }

            if (k == Key.K || k == Key.K.WithShift)
            {
                k.Handled = true;
                if (!_isRecognized)
                {
                    ShowAcrCloudConfigDialog();
                }
                return;
            }
        };

        _cancelBtn.KeyBindings.Remove(Key.Space);

        MikuTheme.ApplyTo(this, TransparentDialogScheme);

        Application.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            if (!_isDismissed)
            {
                StartRecognitionProcess();
            }
            return false;
        });
    }

    private void ToggleAudioSource()
    {
        if (_isDismissed) return;
        var now = Environment.TickCount64;
        if (now - _lastToggleTick < 500)
        {
            return;
        }
        _lastToggleTick = now;

        var oldSource = _currentSource;
        _currentSource = _currentSource == AudioRecordSource.SystemInternal
            ? AudioRecordSource.Microphone
            : AudioRecordSource.SystemInternal;
        s_currentSource = _currentSource;
        _sourceBtn.Text = GetCurrentSourceButtonText();
        AppLogger.Force("AudioRecognitionDialog", $"User toggled audio source: {oldSource} -> {_currentSource}");

        StartRecognitionProcess();
    }

    private void HandleCancel()
    {
        if (_isDismissed) return;
        _isDismissed = true;
        StopAllProcesses();
        Application.RequestStop(this);
    }

    private void HandleActionTriggered()
    {
        if (_isDismissed) return;

        if (_isRecognized)
        {
            _isDismissed = true;
            var songToPlay = _recognizedSong;
            StopAllProcesses();
            if (songToPlay != null)
            {
                _onSongSelected?.Invoke(songToPlay);
            }
            Application.RequestStop(this);
            return;
        }

        StartRecognitionProcess();
    }

    private void StartRecognitionProcess()
    {
        StopAllProcesses();

        _cts = new CancellationTokenSource();
        _isWorking = true;
        _isRecognized = false;
        _recognizedSong = null;

        _statusLabel.Text = _currentSource == AudioRecordSource.SystemInternal
            ? "[系统内录] 音频识别中..."
            : "[麦克风] 音频识别中...";
        _statusLabel.Y = 1;
        _statusLabel.Visible = true;

        _detailLabel1.Text = "[░░░░░░░░░░░░░░░░] 0.0s / 15s";
        _detailLabel1.Y = 3;
        _detailLabel1.Visible = true;

        _detailLabel2.Text = "";
        _detailLabel2.Visible = false;
        _detailLabel3.Visible = false;

        _actionBtn.Text = "重试 (R)";
        _actionBtn.X = 2;
        _actionBtn.Visible = true;

        _sourceBtn.Text = GetCurrentSourceButtonText();
        _sourceBtn.X = Pos.Right(_actionBtn) + 2;
        _sourceBtn.Visible = true;

        _keyBtn.Text = "密钥 (K)";
        _keyBtn.X = Pos.Right(_sourceBtn) + 2;
        _keyBtn.Visible = true;

        _cancelBtn.Text = "取消 (Esc)";
        _cancelBtn.X = Pos.Right(_keyBtn) + 2;
        _cancelBtn.Visible = true;
        _cancelBtn.SetFocus();
        SetNeedsDraw();

        AppLogger.Force("AudioRecognitionDialog", $"Starting recognition process. FixedSource: {_currentSource}");

        // 检测系统内录输出可用性 (避免在无任何声卡输出时静音等待)
        if (_currentSource == AudioRecordSource.SystemInternal && !AudioDeviceHelper.HasInternalRecordDevice())
        {
            ShowDeviceUnavailable("未检测到可用的系统内录通道 (无声卡输出)", "请连接耳机/扬声器，或按 T 切换为麦克风外录");
            return;
        }

        _recordingSession = AudioRecordingService.StartRecordingSession(_currentSource);
        if (!_recordingSession.IsRunning)
        {
            ShowFailed("无法启动录音服务 (PulseAudio/PipeWire 连接失败)");
            return;
        }

        Task.Run(async () =>
        {
            var token = _cts.Token;
            var sw = Stopwatch.StartNew();
            const double totalSeconds = 15.0;
            const int intervalMs = 100;

            // 渐进切片检查时间点 (逐级推进识别)
            double[] sliceCheckpoints = [2.2, 3.5, 5.0, 6.8, 8.8, 11.2, 15.0];
            bool[] checkedSlices = new bool[sliceCheckpoints.Length];
            int inflightRequests = 0;

            try
            {
                while (sw.Elapsed.TotalSeconds < totalSeconds && !token.IsCancellationRequested && !_isRecognized)
                {
                    await Task.Delay(intervalMs, token);
                    var elapsed = sw.Elapsed.TotalSeconds;

                    // 刷新进度条
                    Application.Invoke(() =>
                    {
                        if (_isDismissed) return;
                        if (!_isRecognized && _isWorking)
                        {
                            int barLen = 16;
                            int filled = (int)((elapsed / totalSeconds) * barLen);
                            var bar = new string('■', Math.Clamp(filled, 0, barLen)) +
                                      new string('░', Math.Max(0, barLen - filled));
                            _detailLabel1.Text = $"[{bar}] {elapsed:F1}s / {totalSeconds:F0}s";
                            SetNeedsDraw();
                        }
                    });

                    // 检查到达切片点并触发比对
                    for (int i = 0; i < sliceCheckpoints.Length; i++)
                    {
                        if (elapsed >= sliceCheckpoints[i] && !checkedSlices[i])
                        {
                            checkedSlices[i] = true;
                            var samples = _recordingSession?.GetSnapshotSamples();
                            if (samples != null && samples.Length >= (int)(16000 * 1.8))
                            {
                                Interlocked.Increment(ref inflightRequests);
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        var result = await AudioRecognitionService.RecognizeAndMatchPcmAsync(samples, token);
                                        if (result.Success && !_isRecognized && !_isDismissed)
                                        {
                                            _isRecognized = true;
                                            Application.Invoke(() =>
                                            {
                                                if (_isDismissed) return;
                                                ShowSuccess(result);
                                            });
                                        }
                                    }
                                    finally
                                    {
                                        Interlocked.Decrement(ref inflightRequests);
                                    }
                                }, token);
                            }
                            break;
                        }
                    }
                }

                // 录音结束后等待在途请求返回
                if (!_isRecognized && !token.IsCancellationRequested && !_isDismissed)
                {
                    int waitTimeout = 35;
                    while (inflightRequests > 0 && waitTimeout-- > 0 && !_isRecognized && !token.IsCancellationRequested && !_isDismissed)
                    {
                        await Task.Delay(100, token);
                    }

                    if (!_isRecognized && !token.IsCancellationRequested && !_isDismissed)
                    {
                        Application.Invoke(() =>
                        {
                            if (_isDismissed) return;
                            ShowFailed("未能匹配到对应歌曲");
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!_isRecognized && !_isDismissed && !token.IsCancellationRequested)
                {
                    Application.Invoke(() =>
                    {
                        if (_isDismissed) return;
                        ShowFailed(ex.Message);
                    });
                }
            }
            finally
            {
                if (!_isRecognized)
                {
                    _recordingSession?.Dispose();
                    _recordingSession = null;
                }
            }
        });
    }

    private void ShowSuccess(RecognitionResult result)
    {
        if (_isDismissed) return;
        StopAllProcesses();
        _isWorking = false;
        _isRecognized = true;
        _recognizedSong = result.MatchedSong;

        AppLogger.Force("AudioRecognitionDialog", $"Recognition success: Title='{result.Title}', Artist='{result.Artist}', Source={_currentSource}");

        _statusLabel.Y = 0;
        _statusLabel.Text = "识别成功！已匹配：";

        _detailLabel1.Text = $"曲名: {result.Title}";
        _detailLabel1.Y = 2;
        _detailLabel1.Visible = true;

        _detailLabel2.Text = $"歌手: {result.Artist}";
        _detailLabel2.Y = 3;
        _detailLabel2.Visible = true;

        _detailLabel3.Text = string.IsNullOrWhiteSpace(result.Album) ? "" : $"专辑: {result.Album}";
        _detailLabel3.Y = 4;
        _detailLabel3.Visible = !string.IsNullOrWhiteSpace(result.Album);

        _sourceBtn.Visible = false;
        _keyBtn.Visible = false;

        if (_recognizedSong != null)
        {
            _actionBtn.Text = "立即播放 (Enter)";
            _actionBtn.X = Pos.Center() - 14;
            _actionBtn.Visible = true;
            _cancelBtn.Text = "关闭 (Esc)";
            _cancelBtn.X = Pos.Center() + 4;
            _cancelBtn.Visible = true;
            _actionBtn.SetFocus();
        }
        else
        {
            _actionBtn.Visible = false;
            _cancelBtn.Text = "确定 (Enter/Esc)";
            _cancelBtn.X = Pos.Center() - 8;
            _cancelBtn.Visible = true;
            _cancelBtn.SetFocus();
        }

        SetNeedsDraw();
    }

    private void ShowFailed(string message)
    {
        if (_isDismissed) return;
        StopAllProcesses();
        _isWorking = false;
        _isRecognized = false;
        _recognizedSong = null;

        AppLogger.Force("AudioRecognitionDialog", $"Recognition failed for Source={_currentSource}. Reason: {message}");

        _statusLabel.Text = _currentSource == AudioRecordSource.SystemInternal
            ? "[系统内录] 识曲失败 (未匹配到歌曲)"
            : "[麦克风] 识曲失败 (未匹配到歌曲)";
        _statusLabel.Y = 1;

        _detailLabel1.Text = _currentSource == AudioRecordSource.SystemInternal
            ? "建议调大系统音量，或按 T 切换为麦克风外录"
            : "建议靠近声源，或按 T 切换为系统内录";
        _detailLabel1.Y = 3;
        _detailLabel1.Visible = true;
        _detailLabel2.Visible = false;
        _detailLabel3.Visible = false;

        _actionBtn.Text = "重试 (R)";
        _actionBtn.X = 2;
        _actionBtn.Visible = true;

        _sourceBtn.Text = GetCurrentSourceButtonText();
        _sourceBtn.X = Pos.Right(_actionBtn) + 2;
        _sourceBtn.Visible = true;

        _keyBtn.Text = "密钥 (K)";
        _keyBtn.X = Pos.Right(_sourceBtn) + 2;
        _keyBtn.Visible = true;

        _cancelBtn.Text = "关闭 (Esc)";
        _cancelBtn.X = Pos.Right(_keyBtn) + 2;
        _cancelBtn.Visible = true;

        _actionBtn.SetFocus();
        SetNeedsDraw();
    }

    private void ShowDeviceUnavailable(string title, string hint)
    {
        StopAllProcesses();
        _isWorking = false;
        _isRecognized = false;
        _recognizedSong = null;

        AppLogger.Force("AudioRecognitionDialog", $"Device unavailable for Source={_currentSource}: {title} | {hint}");

        _statusLabel.Text = $"[设备不可用] {title}";
        _statusLabel.Y = 1;

        _detailLabel1.Text = hint;
        _detailLabel1.Y = 3;
        _detailLabel1.Visible = true;
        _detailLabel2.Visible = false;
        _detailLabel3.Visible = false;

        _actionBtn.Text = "重试 (R)";
        _actionBtn.X = 2;
        _actionBtn.Visible = true;

        _sourceBtn.Text = GetCurrentSourceButtonText();
        _sourceBtn.X = Pos.Right(_actionBtn) + 2;
        _sourceBtn.Visible = true;

        _keyBtn.Text = "密钥 (K)";
        _keyBtn.X = Pos.Right(_sourceBtn) + 2;
        _keyBtn.Visible = true;

        _cancelBtn.Text = "关闭 (Esc)";
        _cancelBtn.X = Pos.Right(_keyBtn) + 2;
        _cancelBtn.Visible = true;

        _sourceBtn.SetFocus();
        SetNeedsDraw();
    }

    private void ShowAcrCloudConfigDialog()
    {
        StopAllProcesses();

        var config = AcrCloudConfig.Current;
        var dlg = new Dialog
        {
            Title = "ACRCloud 密钥配置",
            Width = 64,
            Height = 13,
            X = Pos.Center(),
            Y = Pos.Center()
        };
        dlg.SetScheme(TransparentDialogScheme);

        var hostLbl = new Label { Text = "Host:", X = 2, Y = 1, Width = 15 };
        var hostField = new TextField { Text = config.Host, X = 17, Y = 1, Width = Dim.Fill(2) };

        var keyLbl = new Label { Text = "Access Key:", X = 2, Y = 3, Width = 15 };
        var keyField = new TextField { Text = config.AccessKey, X = 17, Y = 3, Width = Dim.Fill(2) };

        var secretLbl = new Label { Text = "Access Secret:", X = 2, Y = 5, Width = 15 };
        var secretField = new TextField { Text = config.AccessSecret, X = 17, Y = 5, Width = Dim.Fill(2), Secret = true };

        var tipLbl = new Label
        {
            Text = "提示: 注册 acrcloud.cn 即可获取每日免费额度",
            X = 2,
            Y = 7,
            Width = Dim.Fill(2)
        };
        tipLbl.SetScheme(TransparentDialogScheme);

        var saveBtn = new Button
        {
            Text = "保存 (Enter)",
            X = Pos.Center() - 11,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        saveBtn.SetScheme(TransparentDialogScheme);

        var cancelBtn = new Button
        {
            Text = "取消 (Esc)",
            X = Pos.Center() + 3,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        cancelBtn.SetScheme(TransparentDialogScheme);

        saveBtn.Accepting += (s, e) =>
        {
            config.Host = hostField.Text?.Trim() ?? "";
            config.AccessKey = keyField.Text?.Trim() ?? "";
            config.AccessSecret = secretField.Text?.Trim() ?? "";
            config.Save();
            Application.RequestStop(dlg);
            StartRecognitionProcess();
        };

        cancelBtn.Accepting += (s, e) =>
        {
            Application.RequestStop(dlg);
            StartRecognitionProcess();
        };

        dlg.Add(hostLbl, hostField, keyLbl, keyField, secretLbl, secretField, tipLbl, saveBtn, cancelBtn);
        MikuTheme.ApplyTo(dlg, TransparentDialogScheme);
        Application.Run(dlg);
    }

    private void StopAllProcesses()
    {
        try { _cts.Cancel(); } catch { }
        _recordingSession?.Dispose();
        _recordingSession = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopAllProcesses();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
