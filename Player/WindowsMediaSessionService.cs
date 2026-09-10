using System.Runtime.CompilerServices;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

#if WINDOWS
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;
#endif

namespace QQMusic.Tui.Player;

/// <summary>
/// Windows 10/11 System Media Transport Controls integration.
/// A silent WinRT MediaPlayer owns the SMTC session while GStreamer remains the audio engine.
/// </summary>
public sealed class WindowsMediaSessionService : IDisposable
{
#if WINDOWS
    private MediaPlayer? _mediaPlayer;
    private MediaPlaybackCommandManager? _commandManager;
#endif
    private bool _disposed;

    public Func<Task>? PlayHandler { get; set; }
    public Func<Task>? PauseHandler { get; set; }
    public Func<Task>? StopHandler { get; set; }
    public Func<Task>? NextHandler { get; set; }
    public Func<Task>? PreviousHandler { get; set; }
    public Func<double, Task>? SetPositionHandler { get; set; }

    public Task StartAsync()
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;

        try
        {
            _mediaPlayer = new MediaPlayer
            {
                IsMuted = true
            };
            _commandManager = _mediaPlayer.CommandManager;
            _commandManager.IsEnabled = true;
            _commandManager.PlayBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            _commandManager.PauseBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            _commandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            _commandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            _commandManager.PlayReceived += OnPlayReceived;
            _commandManager.PauseReceived += OnPauseReceived;
            _commandManager.NextReceived += OnNextReceived;
            _commandManager.PreviousReceived += OnPreviousReceived;
            _commandManager.PositionReceived += OnPositionReceived;
            AppLogger.Info("WindowsMediaSession", "Windows SMTC session started");
        }
        catch (Exception ex)
        {
            AppLogger.Error("WindowsMediaSession", "Failed to start Windows SMTC session", ex);
        }
#endif
        return Task.CompletedTask;
    }

    public void UpdatePlaybackStatus(bool isPlaying)
    {
#if WINDOWS
        if (_mediaPlayer == null) return;
        _mediaPlayer.SystemMediaTransportControls.PlaybackStatus = isPlaying
            ? MediaPlaybackStatus.Playing
            : MediaPlaybackStatus.Paused;
#endif
    }

    public void UpdateSong(Song? song, string? coverPath = null)
    {
#if WINDOWS
        if (_mediaPlayer == null) return;
        var controls = _mediaPlayer.SystemMediaTransportControls;
        var updater = controls.DisplayUpdater;
        if (song == null)
        {
            updater.ClearAll();
            updater.Update();
            controls.PlaybackStatus = MediaPlaybackStatus.Stopped;
            return;
        }

        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = song.Title;
        updater.MusicProperties.Artist = song.Artist;
        updater.MusicProperties.AlbumTitle = song.Album;
        updater.Thumbnail = CreateThumbnail(song, coverPath);
        updater.Update();
        UpdateTimeline(0, song.Duration);
#endif
    }

    public void UpdateCover(Song? song, string? coverPath)
    {
#if WINDOWS
        if (_mediaPlayer == null || song == null) return;
        var updater = _mediaPlayer.SystemMediaTransportControls.DisplayUpdater;
        updater.Thumbnail = CreateThumbnail(song, coverPath);
        updater.Update();
#endif
    }

    public void UpdatePosition(double positionSeconds, double durationSeconds)
    {
#if WINDOWS
        UpdateTimeline(positionSeconds, durationSeconds);
#endif
    }

#if WINDOWS
    private void UpdateTimeline(double positionSeconds, double durationSeconds)
    {
        var controls = _mediaPlayer?.SystemMediaTransportControls;
        if (controls == null || durationSeconds <= 0) return;

        var duration = TimeSpan.FromSeconds(durationSeconds);
        var position = TimeSpan.FromSeconds(Math.Clamp(positionSeconds, 0, durationSeconds));
        controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            Position = position,
            MaxSeekTime = duration,
            EndTime = duration
        });
    }

    private static RandomAccessStreamReference? CreateThumbnail(Song song, string? coverPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(coverPath))
            {
                var uri = coverPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(coverPath)
                    : new Uri(Path.GetFullPath(coverPath));
                return RandomAccessStreamReference.CreateFromUri(uri);
            }
            if (!string.IsNullOrEmpty(song.AlbumMid))
            {
                return RandomAccessStreamReference.CreateFromUri(
                    new Uri($"https://y.qq.com/music/photo_new/T002R1200x1200M000{song.AlbumMid}.jpg?max_age=2592000"));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("WindowsMediaSession", $"Failed to set album artwork: {ex.Message}");
        }
        return null;
    }

    private void OnPlayReceived(MediaPlaybackCommandManager sender, MediaPlaybackCommandManagerPlayReceivedEventArgs args)
    {
        args.Handled = true;
        Invoke(PlayHandler);
    }

    private void OnPauseReceived(MediaPlaybackCommandManager sender, MediaPlaybackCommandManagerPauseReceivedEventArgs args)
    {
        args.Handled = true;
        Invoke(PauseHandler);
    }

    private void OnNextReceived(MediaPlaybackCommandManager sender, MediaPlaybackCommandManagerNextReceivedEventArgs args)
    {
        args.Handled = true;
        Invoke(NextHandler);
    }

    private void OnPreviousReceived(MediaPlaybackCommandManager sender, MediaPlaybackCommandManagerPreviousReceivedEventArgs args)
    {
        args.Handled = true;
        Invoke(PreviousHandler);
    }

    private void OnPositionReceived(MediaPlaybackCommandManager sender, MediaPlaybackCommandManagerPositionReceivedEventArgs args)
    {
        args.Handled = true;
        if (SetPositionHandler != null) _ = SetPositionHandler(args.Position.TotalSeconds);
    }
#endif

    private static void Invoke(Func<Task>? handler)
    {
        if (handler != null) _ = handler();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
#if WINDOWS
        if (_commandManager != null)
        {
            _commandManager.PlayReceived -= OnPlayReceived;
            _commandManager.PauseReceived -= OnPauseReceived;
            _commandManager.NextReceived -= OnNextReceived;
            _commandManager.PreviousReceived -= OnPreviousReceived;
            _commandManager.PositionReceived -= OnPositionReceived;
        }
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
        _commandManager = null;
#endif
    }
}
