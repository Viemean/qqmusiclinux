using QQMusic.Tui.Models;
using QQMusic.Tui.Services;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Player;

/// <summary>
/// 轻量 Web 协同播放器实现（TUI 负责曲库/解析/会话调度，手机浏览器负责 HTML5 硬件音频输出或纯遥控）
/// </summary>
public sealed class WebPlayer : IPlayer
{
    private readonly WebPlaybackServer _server = new();
    private readonly int _preferredPort;
    private readonly bool _initialAudioEnabled;
    private readonly Lock _lock = new();

    private CancellationTokenSource? _virtualTickerCts;
    private bool _disposed;

    public bool IsPlaying { get; private set; }
    public double CurrentPositionSeconds { get; private set; }
    public double TotalDurationSeconds { get; private set; }
    public int Volume { get; private set; } = 80;
    public bool AudioOutputEnabled => _server.AudioOutputEnabled;
    public string Url => _server.LocalUrl;
    public int Port => _server.Port;

    public event Action<double>? PositionUpdated;
    public event Action? PlaybackFinished;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? TogglePlayRequested;

    public WebPlayer(int preferredPort = 9999, bool initialAudioEnabled = true)
    {
        _preferredPort = preferredPort;
        _initialAudioEnabled = initialAudioEnabled;
    }

    public void Initialize()
    {
        _server.NextRequested += () => NextRequested?.Invoke();
        _server.PreviousRequested += () => PreviousRequested?.Invoke();
        _server.TogglePlayRequested += () => TogglePlayRequested?.Invoke();
        _server.PlaybackEnded += () =>
        {
            StopVirtualTicker();
            PlaybackFinished?.Invoke();
        };
        _server.SeekRequested += sec => _ = SeekAsync(sec);
        _server.VolumeRequested += vol => SetVolume(vol);
        _server.ProgressReported += (pos, dur) =>
        {
            CurrentPositionSeconds = pos;
            if (dur > 0) TotalDurationSeconds = dur;
            PositionUpdated?.Invoke(pos);
        };
        _server.AudioOutputToggled += enabled =>
        {
            AppLogger.Info("WebPlayer", $"Web audio output toggled: {(enabled ? "Enabled" : "Disabled")}");
            lock (_lock)
            {
                if (IsPlaying)
                {
                    if (!enabled)
                    {
                        StartVirtualTicker();
                    }
                    else
                    {
                        StopVirtualTicker();
                    }
                }
            }
        };

        var ok = _server.Start(_preferredPort, _initialAudioEnabled);
        if (ok)
        {
            AppLogger.Info("WebPlayer", $"WebPlayer initialized successfully at {_server.LocalUrl}");
        }
        else
        {
            AppLogger.Error("WebPlayer", "Failed to start underlying WebPlaybackServer");
        }
    }

    public Task PlayAsync(string url, double duration, double startPosition = 0)
    {
        lock (_lock)
        {
            if (_disposed) return Task.CompletedTask;

            TotalDurationSeconds = duration;
            CurrentPositionSeconds = startPosition;
            IsPlaying = true;

            _server.CurrentPlayUrl = url;
            _server.TotalDurationSeconds = duration;
            _server.CurrentPositionSeconds = startPosition;
            _server.IsPlaying = true;
            _server.BroadcastState("play");

            AppLogger.Info("WebPlayer", $"PlayAsync: url='{url}', duration={duration:F1}s, startPos={startPosition:F1}s, audioEnabled={AudioOutputEnabled}");

            if (!AudioOutputEnabled)
            {
                StartVirtualTicker();
            }
            else
            {
                StopVirtualTicker();
            }
        }

        return Task.CompletedTask;
    }

    public Task TogglePauseAsync()
    {
        lock (_lock)
        {
            if (_disposed) return Task.CompletedTask;

            IsPlaying = !IsPlaying;
            _server.IsPlaying = IsPlaying;
            _server.BroadcastState(IsPlaying ? "resume" : "pause");
            AppLogger.Info("WebPlayer", $"TogglePauseAsync: IsPlaying={IsPlaying}");

            if (IsPlaying)
            {
                if (!AudioOutputEnabled) StartVirtualTicker();
            }
            else
            {
                StopVirtualTicker();
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_lock)
        {
            if (_disposed) return Task.CompletedTask;

            IsPlaying = false;
            CurrentPositionSeconds = 0;
            _server.IsPlaying = false;
            _server.CurrentPositionSeconds = 0;
            _server.BroadcastState("stop");
            StopVirtualTicker();
            AppLogger.Info("WebPlayer", "Playback stopped");
        }

        return Task.CompletedTask;
    }

    public Task SeekAsync(double seconds)
    {
        lock (_lock)
        {
            if (_disposed) return Task.CompletedTask;

            CurrentPositionSeconds = Math.Clamp(seconds, 0, TotalDurationSeconds > 0 ? TotalDurationSeconds : 3600);
            _server.CurrentPositionSeconds = CurrentPositionSeconds;
            _server.BroadcastState("seek");
            AppLogger.Info("WebPlayer", $"Seek to {CurrentPositionSeconds:F1}s");
        }

        PositionUpdated?.Invoke(CurrentPositionSeconds);
        return Task.CompletedTask;
    }

    public void SetVolume(int vol)
    {
        Volume = Math.Clamp(vol, 0, 100);
        _server.Volume = Volume;
        _server.BroadcastState("volume");
    }

    public void UpdateCurrentSong(Song? song)
    {
        _server.CurrentSong = song;
        _server.BroadcastState("song_change");
    }

    public void UpdateCurrentLyrics(List<LyricLine>? lyrics)
    {
        _server.CurrentLyrics = lyrics;
        _server.BroadcastState("lyrics_change");
    }

    private void StartVirtualTicker()
    {
        StopVirtualTicker();
        _virtualTickerCts = new CancellationTokenSource();
        var ct = _virtualTickerCts.Token;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                bool shouldFinish = false;
                double pos = 0;
                lock (_lock)
                {
                    if (!IsPlaying || AudioOutputEnabled) break;
                    CurrentPositionSeconds += 0.5;
                    pos = CurrentPositionSeconds;
                    _server.CurrentPositionSeconds = pos;
                    if (TotalDurationSeconds > 0 && CurrentPositionSeconds >= TotalDurationSeconds - 0.5)
                    {
                        shouldFinish = true;
                    }
                }

                PositionUpdated?.Invoke(pos);

                if (shouldFinish)
                {
                    PlaybackFinished?.Invoke();
                    break;
                }
            }
        }, ct);
    }

    private void StopVirtualTicker()
    {
        try
        {
            _virtualTickerCts?.Cancel();
        }
        catch {}
        _virtualTickerCts = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            StopVirtualTicker();
            _server.Dispose();
        }
    }
}
