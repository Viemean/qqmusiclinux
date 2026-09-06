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

    private nint _pipeline;
    private readonly Lock _lock = new();
    private bool _disposed;

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
            gst_init(0, 0);
            AppLogger.Info("GstPlayer", "GStreamer initialized successfully (in-process)");

            lock (_lock)
            {
                _pipeline = gst_element_factory_make("playbin", "qqmusic_playbin");
                if (_pipeline != 0)
                {
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

    public Task PlayAsync(string url, double duration, double startPosition = 0)
    {
        lock (_lock)
        {
            if (_disposed || _pipeline == 0) return Task.CompletedTask;

            TotalDurationSeconds = duration;
            CurrentPositionSeconds = startPosition;
            IsPlaying = true;

            // 停掉前一段播放
            gst_element_set_state(_pipeline, GST_STATE_NULL);

            // 配置新音频流 URL 与音量（若为本地路径则转为 file:// 规范 URI）
            var playUri = url;
            if (!url.Contains("://") && File.Exists(url))
            {
                playUri = new Uri(Path.GetFullPath(url)).AbsoluteUri;
            }
            gst_util_set_object_arg(_pipeline, "uri", playUri);
            gst_util_set_object_arg(_pipeline, "volume", (Volume / 100.0).ToString("F2", CultureInfo.InvariantCulture));

            // 开始播放
            gst_element_set_state(_pipeline, GST_STATE_PLAYING);
            AppLogger.Info("GstPlayer", $"Playback started for URL: {url}");

            if (startPosition > 0.5)
            {
                var seekNs = (long)(startPosition * 1_000_000_000.0);
                // 异步延迟后再定位播放位置
                _ = Task.Run(async () =>
                {
                    await Task.Delay(60);
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

        return Task.CompletedTask;
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

    public Task StopAsync()
    {
        lock (_lock)
        {
            if (_disposed || _pipeline == 0) return Task.CompletedTask;

            gst_element_set_state(_pipeline, GST_STATE_NULL);
            IsPlaying = false;
            CurrentPositionSeconds = 0;
            AppLogger.Info("GstPlayer", "Playback stopped");
        }

        return Task.CompletedTask;
    }

    public Task SeekAsync(double seconds)
    {
        lock (_lock)
        {
            if (_disposed || _pipeline == 0) return Task.CompletedTask;

            CurrentPositionSeconds = Math.Clamp(seconds, 0, TotalDurationSeconds > 0 ? TotalDurationSeconds : 3600);
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

    private async Task PollingLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            if (_disposed) break;
            if (!IsPlaying || _pipeline == 0) continue;

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
                    PositionUpdated?.Invoke(sec);

                    if (TotalDurationSeconds > 0 && sec >= TotalDurationSeconds - 0.5)
                    {
                        PlaybackFinished?.Invoke();
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

            if (_pipeline != 0)
            {
                gst_element_set_state(_pipeline, GST_STATE_NULL);
                gst_object_unref(_pipeline);
                _pipeline = 0;
            }
        }
    }

    [LibraryImport(LibGst)]
    private static partial void gst_init(nint argc, nint argv);

    [LibraryImport(LibGst, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint gst_element_factory_make(string factoryname, string name);

    [LibraryImport(LibGst, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void gst_util_set_object_arg(nint obj, string name, string value);

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
    private static partial void gst_object_unref(nint obj);
}
