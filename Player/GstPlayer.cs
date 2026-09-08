using System.Globalization;
using System.Runtime.InteropServices;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Player;

public sealed partial class GstPlayer : IPlayer
{
    private const string LibGst = "libgstreamer-1.0.so.0";

    private const int GST_STATE_NULL = 1;
    private const int GST_STATE_READY = 2;
    private const int GST_STATE_PAUSED = 3;
    private const int GST_STATE_PLAYING = 4;
    private const int GST_FORMAT_TIME = 3;
    private const int GST_SEEK_FLAGS = 1 | 4; // GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_KEY_UNIT

    private const int GST_MESSAGE_EOS = 1;
    private const int GST_MESSAGE_ERROR = 2;
    private const int GST_MESSAGE_WARNING = 4;

    private nint _pipeline;
    private readonly Lock _lock = new();
    private bool _disposed;
    private bool _playbackFinishedTriggered;

    private string? _currentPlayUrl;
    private double _lastHealthyPosition;
    private int _watchdogStallCount;
    private int _retryCount;
    private bool _isRecovering;
    private CancellationTokenSource? _fadeCts;

    public bool IsAvailable => _pipeline != 0;
    public bool IsPlaying { get; private set; }
    public double CurrentPositionSeconds { get; private set; }
    public double TotalDurationSeconds { get; private set; }
    public int Volume { get; private set; } = 80;

    public event Action<double>? PositionUpdated;
    public event Action? PlaybackFinished;

    public void Initialize()
    {
        try
        {
            // 若用户未显式指定 GST_AUDIO_SINK，预设抗抖动环境变量以防底层 autoaudiosink 派生未捕获
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GST_AUDIO_SINK")))
            {
                Environment.SetEnvironmentVariable("GST_AUDIO_SINK", "pulsesink buffer-time=200000 latency-time=50000 client-name=\"QQMusic TUI\"");
            }

            gst_init(0, 0);
            AppLogger.Info("GstPlayer", "GStreamer initialized successfully (in-process)");

            lock (_lock)
            {
                _pipeline = gst_element_factory_make("playbin", "qqmusic_playbin");
                if (_pipeline != 0)
                {
                    ConfigureAudioSink(_pipeline);
                    SetVolume(Volume);
                    AppLogger.Info("GstPlayer", "GStreamer playbin pipeline created");
                }
                else
                {
                    AppLogger.Error("GstPlayer", "Failed to create GStreamer playbin pipeline");
                }
            }

            if (_pipeline != 0)
            {
                Task.Run(PollingLoopAsync);
            }
        }
        catch (DllNotFoundException dllEx)
        {
            AppLogger.Warn("GstPlayer", $"GStreamer library {LibGst} not found on this system: {dllEx.Message}");
            _pipeline = 0;
        }
        catch (Exception ex)
        {
            AppLogger.Error("GstPlayer", "Exception during GStreamer initialization", ex);
            _pipeline = 0;
        }
    }

    public async Task PlayAsync(string url, double duration, double startPosition = 0)
    {
        // 若当前正在发声，先执行 100ms 软淡出以消除爆音
        if (IsPlaying && !_isRecovering && Volume > 0)
        {
            await FadeOutAsync(100).ConfigureAwait(false);
        }

        lock (_lock)
        {
            if (_disposed || _pipeline == 0) return;

            _currentPlayUrl = url;
            TotalDurationSeconds = duration;
            CurrentPositionSeconds = startPosition;
            _lastHealthyPosition = startPosition;
            IsPlaying = true;
            _playbackFinishedTriggered = false;
            if (!_isRecovering)
            {
                _retryCount = 0;
                _watchdogStallCount = 0;
            }

            // 停掉前一段播放
            gst_element_set_state(_pipeline, GST_STATE_NULL);

            // 配置新音频流 URL 与初始音量（若为本地路径则转为 file:// 规范 URI）
            var playUri = url;
            if (!url.Contains("://") && File.Exists(url))
            {
                playUri = new Uri(Path.GetFullPath(url)).AbsoluteUri;
            }
            gst_util_set_object_arg(_pipeline, "uri", playUri);

            // 先以 0 音量启动播放，随后软淡入
            gst_util_set_object_arg(_pipeline, "volume", "0.00");
            gst_element_set_state(_pipeline, GST_STATE_PLAYING);
            AppLogger.Info("GstPlayer", $"Playback started for URL: {url}");

            if (startPosition > 0.5)
            {
                var seekNs = (long)(startPosition * 1_000_000_000.0);
                // 异步延迟后再定位播放位置
                _ = Task.Run(async () =>
                {
                    await Task.Delay(60).ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (!_disposed && _pipeline != 0)
                        {
                            gst_element_seek_simple(_pipeline, GST_FORMAT_TIME, GST_SEEK_FLAGS, seekNs);
                            AppLogger.Info("GstPlayer", $"Direct start position seeked to: {startPosition:F1}s");
                        }
                    }
                });
            }
        }

        // 启动 120ms 软淡入
        StartFadeIn(Volume, 120);
    }

    public Task TogglePauseAsync()
    {
        lock (_lock)
        {
            if (_disposed || _pipeline == 0) return Task.CompletedTask;

            if (IsPlaying)
            {
                gst_element_set_state(_pipeline, GST_STATE_PAUSED);
                IsPlaying = false;
                AppLogger.Info("GstPlayer", "Playback paused");
            }
            else
            {
                gst_element_set_state(_pipeline, GST_STATE_PLAYING);
                IsPlaying = true;
                AppLogger.Info("GstPlayer", "Playback resumed");
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (IsPlaying && Volume > 0)
        {
            await FadeOutAsync(100).ConfigureAwait(false);
        }

        lock (_lock)
        {
            if (_disposed || _pipeline == 0) return;

            gst_element_set_state(_pipeline, GST_STATE_NULL);
            IsPlaying = false;
            CurrentPositionSeconds = 0;
            _lastHealthyPosition = 0;
            _playbackFinishedTriggered = false;
            _watchdogStallCount = 0;
            _retryCount = 0;
            AppLogger.Info("GstPlayer", "Playback stopped");
        }
    }

    public Task SeekAsync(double seconds)
    {
        lock (_lock)
        {
            if (_disposed || _pipeline == 0) return Task.CompletedTask;

            CurrentPositionSeconds = Math.Clamp(seconds, 0, TotalDurationSeconds > 0 ? TotalDurationSeconds : 3600);
            _lastHealthyPosition = CurrentPositionSeconds;
            _watchdogStallCount = 0;
            if (TotalDurationSeconds > 0 && CurrentPositionSeconds < TotalDurationSeconds - 1.0)
            {
                _playbackFinishedTriggered = false;
            }
            var seekNs = (long)(CurrentPositionSeconds * 1_000_000_000.0);
            var ok = gst_element_seek_simple(_pipeline, GST_FORMAT_TIME, GST_SEEK_FLAGS, seekNs);
            AppLogger.Info("GstPlayer", $"Seek to {CurrentPositionSeconds:F1}s result: {ok}");
        }

        PositionUpdated?.Invoke(CurrentPositionSeconds);
        return Task.CompletedTask;
    }

    public void SetVolume(int vol)
    {
        Volume = Math.Clamp(vol, 0, 100);
        lock (_lock)
        {
            if (!_disposed && _pipeline != 0)
            {
                var volStr = (Volume / 100.0).ToString("F2", CultureInfo.InvariantCulture);
                gst_util_set_object_arg(_pipeline, "volume", volStr);
            }
        }
    }

    private async Task FadeOutAsync(int durationMs = 120)
    {
        try
        {
            _fadeCts?.Cancel();
            _fadeCts = new CancellationTokenSource();
            var ct = _fadeCts.Token;

            if (_pipeline == 0 || !IsPlaying || Volume <= 0) return;

            int steps = 6;
            int stepDelay = Math.Max(10, durationMs / steps);
            double currentVol = Volume / 100.0;

            for (int i = steps - 1; i >= 0; i--)
            {
                if (ct.IsCancellationRequested) break;
                lock (_lock)
                {
                    if (_disposed || _pipeline == 0) return;
                    double stepVol = currentVol * (i / (double)steps);
                    gst_util_set_object_arg(_pipeline, "volume", stepVol.ToString("F3", CultureInfo.InvariantCulture));
                }
                await Task.Delay(stepDelay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void StartFadeIn(int targetVolume, int durationMs = 120)
    {
        if (_pipeline == 0 || targetVolume <= 0) return;

        _fadeCts?.Cancel();
        _fadeCts = new CancellationTokenSource();
        var ct = _fadeCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                int steps = 6;
                int stepDelay = Math.Max(10, durationMs / steps);
                for (int i = 1; i <= steps; i++)
                {
                    await Task.Delay(stepDelay, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;
                    lock (_lock)
                    {
                        if (_disposed || _pipeline == 0 || !IsPlaying) return;
                        double stepVol = (targetVolume / 100.0) * (i / (double)steps);
                        gst_util_set_object_arg(_pipeline, "volume", stepVol.ToString("F3", CultureInfo.InvariantCulture));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        });
    }

    private void TriggerWatchdogRecovery()
    {
        if (_isRecovering || _disposed || !IsPlaying) return;

        if (string.IsNullOrEmpty(_currentPlayUrl) || !_currentPlayUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_retryCount >= 3)
        {
            AppLogger.Error("GstPlayer", "Watchdog recovery exhausted (3 attempts failed), triggering PlaybackFinished to skip track");
            _retryCount = 0;
            _watchdogStallCount = 0;
            PlaybackFinished?.Invoke();
            return;
        }

        _isRecovering = true;
        _retryCount++;
        int backoffSec = 1 << (_retryCount - 1); // 1s, 2s, 4s
        AppLogger.Warn("GstPlayer", $"Watchdog initiated reconnect attempt {_retryCount}/3 from {_lastHealthyPosition:F1}s after {backoffSec}s backoff");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(backoffSec * 1000).ConfigureAwait(false);
                if (!_disposed && IsPlaying && !string.IsNullOrEmpty(_currentPlayUrl))
                {
                    await PlayAsync(_currentPlayUrl, TotalDurationSeconds, _lastHealthyPosition).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("GstPlayer", "Watchdog reconnect encountered exception", ex);
            }
            finally
            {
                _isRecovering = false;
                _watchdogStallCount = 0;
            }
        });
    }

    private async Task PollingLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            if (_disposed) break;
            if (!IsPlaying || _pipeline == 0) continue;

            // 1. 监测 GStreamer Bus 异常
            nint bus = 0;
            lock (_lock)
            {
                if (!_disposed && _pipeline != 0)
                {
                    bus = gst_element_get_bus(_pipeline);
                }
            }
            if (bus != 0)
            {
                try
                {
                    nint msg = gst_bus_pop_filtered(bus, GST_MESSAGE_ERROR);
                    if (msg != 0)
                    {
                        AppLogger.Warn("GstPlayer", "Bus reported GST_MESSAGE_ERROR, triggering watchdog");
                        gst_object_unref(msg);
                        TriggerWatchdogRecovery();
                    }
                }
                finally
                {
                    gst_object_unref(bus);
                }
            }

            // 2. 轮询播放进度与断点看门狗
            try
            {
                long posNs = 0;
                bool success;
                lock (_lock)
                {
                    if (_disposed || _pipeline == 0) continue;
                    success = gst_element_query_position(_pipeline, GST_FORMAT_TIME, out posNs);
                }

                if (success && posNs >= 0)
                {
                    var sec = posNs / 1_000_000_000.0;
                    CurrentPositionSeconds = sec;

                    // 检测进度是否前进
                    if (sec > _lastHealthyPosition + 0.05)
                    {
                        // 若成功推进超过 2 秒且曾经重试过，重置重试计数
                        if (sec - _lastHealthyPosition >= 2.0 && _retryCount > 0)
                        {
                            _retryCount = 0;
                        }

                        _lastHealthyPosition = sec;
                        _watchdogStallCount = 0;
                    }
                    else if (IsPlaying && TotalDurationSeconds > 0 && sec < TotalDurationSeconds - 2.0)
                    {
                        // 正在播放但进度停滞
                        _watchdogStallCount++;
                        if (_watchdogStallCount >= 20) // 5 秒无进展
                        {
                            TriggerWatchdogRecovery();
                        }
                    }

                    PositionUpdated?.Invoke(sec);

                    if (TotalDurationSeconds > 0 && sec >= TotalDurationSeconds - 0.5)
                    {
                        bool shouldTrigger = false;
                        lock (_lock)
                        {
                            if (!_playbackFinishedTriggered)
                            {
                                _playbackFinishedTriggered = true;
                                shouldTrigger = true;
                            }
                        }

                        if (shouldTrigger)
                        {
                            PlaybackFinished?.Invoke();
                        }
                    }
                }
            }
            catch
            {
                // Ignored
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            _fadeCts?.Cancel();
            _fadeCts?.Dispose();

            if (_pipeline != 0)
            {
                gst_element_set_state(_pipeline, GST_STATE_NULL);
                gst_object_unref(_pipeline);
                _pipeline = 0;
            }
        }
    }

    private static void ConfigureAudioSink(nint pipeline)
    {
        try
        {
            var customEnv = Environment.GetEnvironmentVariable("GST_AUDIO_SINK");
            if (!string.IsNullOrWhiteSpace(customEnv) && !customEnv.StartsWith("pulsesink", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Info("GstPlayer", $"Using user-specified GST_AUDIO_SINK: {customEnv}");
                return;
            }

            // 优先配置 pulsesink 并注入抗抖动缓冲参数（200ms 缓冲与 50ms 周期，适应高负载游戏环境）
            var sink = gst_element_factory_make("pulsesink", "qqmusic_pulsesink");
            if (sink != 0)
            {
                gst_util_set_object_arg(sink, "client-name", "QQMusic TUI");
                gst_util_set_object_arg(sink, "buffer-time", "200000");
                gst_util_set_object_arg(sink, "latency-time", "50000");
                g_object_set(pipeline, "audio-sink", sink, 0);
                AppLogger.Info("GstPlayer", "Custom pulsesink configured (buffer-time=200000, latency-time=50000, client-name=QQMusic TUI)");
                return;
            }

            // 备用 pipewiresink 探测
            sink = gst_element_factory_make("pipewiresink", "qqmusic_pipewiresink");
            if (sink != 0)
            {
                gst_util_set_object_arg(sink, "client-name", "QQMusic TUI");
                g_object_set(pipeline, "audio-sink", sink, 0);
                AppLogger.Info("GstPlayer", "Custom pipewiresink configured (client-name=QQMusic TUI)");
                return;
            }

            AppLogger.Warn("GstPlayer", "Neither pulsesink nor pipewiresink available, falling back to default autoaudiosink");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("GstPlayer", $"Failed to configure custom audio-sink, fallback to default: {ex.Message}");
        }
    }

    private const string LibGObject = "libgobject-2.0.so.0";

    [LibraryImport(LibGst)]
    private static partial void gst_init(nint argc, nint argv);

    [LibraryImport(LibGst, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint gst_element_factory_make(string factoryname, string name);

    [LibraryImport(LibGst, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void gst_util_set_object_arg(nint obj, string name, string value);

    [LibraryImport(LibGObject, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void g_object_set(nint obj, string property_name, nint value, nint sentinel);

    [LibraryImport(LibGst)]
    private static partial int gst_element_set_state(nint element, int state);

    [LibraryImport(LibGst)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool gst_element_query_position(nint element, int format, out long cur);

    [LibraryImport(LibGst)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool gst_element_query_duration(nint element, int format, out long duration);

    [LibraryImport(LibGst)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool gst_element_seek_simple(nint element, int format, int flags, long seekPos);

    [LibraryImport(LibGst)]
    private static partial nint gst_element_get_bus(nint element);

    [LibraryImport(LibGst)]
    private static partial nint gst_bus_pop_filtered(nint bus, int message_type);

    [LibraryImport(LibGst)]
    private static partial void gst_object_unref(nint obj);
}
