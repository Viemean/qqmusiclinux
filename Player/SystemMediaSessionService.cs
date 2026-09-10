using QQMusic.Tui.Models;

namespace QQMusic.Tui.Player;

/// <summary>
/// One application-facing media-session surface backed by MPRIS2, Windows SMTC,
/// or macOS Now Playing according to the current operating system.
/// </summary>
public sealed class SystemMediaSessionService : IDisposable
{
    private readonly MprisService? _linux;
    private readonly WindowsMediaSessionService? _windows;
    private readonly MacMediaSessionService? _mac;

    public SystemMediaSessionService()
    {
        if (OperatingSystem.IsLinux()) _linux = new MprisService();
        else if (OperatingSystem.IsWindows()) _windows = new WindowsMediaSessionService();
        else if (OperatingSystem.IsMacOS()) _mac = new MacMediaSessionService();
    }

    public Func<Task>? PlayPauseHandler
    {
        set
        {
            if (_linux != null) _linux.PlayPauseHandler = value;
            if (_mac != null) _mac.PlayPauseHandler = value;
        }
    }

    public Func<Task>? PlayHandler
    {
        set
        {
            if (_linux != null) _linux.PlayHandler = value;
            if (_windows != null) _windows.PlayHandler = value;
            if (_mac != null) _mac.PlayHandler = value;
        }
    }

    public Func<Task>? PauseHandler
    {
        set
        {
            if (_linux != null) _linux.PauseHandler = value;
            if (_windows != null) _windows.PauseHandler = value;
            if (_mac != null) _mac.PauseHandler = value;
        }
    }

    public Func<Task>? StopHandler
    {
        set
        {
            if (_linux != null) _linux.StopHandler = value;
            if (_windows != null) _windows.StopHandler = value;
            if (_mac != null) _mac.StopHandler = value;
        }
    }

    public Func<Task>? NextHandler
    {
        set
        {
            if (_linux != null) _linux.NextHandler = value;
            if (_windows != null) _windows.NextHandler = value;
            if (_mac != null) _mac.NextHandler = value;
        }
    }

    public Func<Task>? PreviousHandler
    {
        set
        {
            if (_linux != null) _linux.PreviousHandler = value;
            if (_windows != null) _windows.PreviousHandler = value;
            if (_mac != null) _mac.PreviousHandler = value;
        }
    }

    public Func<double, Task>? SeekHandler
    {
        set
        {
            if (_linux != null) _linux.SeekHandler = value;
        }
    }

    public Func<double, Task>? SetPositionHandler
    {
        set
        {
            if (_linux != null) _linux.SetPositionHandler = value;
            if (_windows != null) _windows.SetPositionHandler = value;
            if (_mac != null) _mac.SetPositionHandler = value;
        }
    }

    public Action<double>? VolumeSetHandler { set { if (_linux != null) _linux.VolumeSetHandler = value; } }
    public Action<string>? LoopStatusSetHandler { set { if (_linux != null) _linux.LoopStatusSetHandler = value; } }
    public Action<bool>? ShuffleSetHandler { set { if (_linux != null) _linux.ShuffleSetHandler = value; } }
    public Action? QuitHandler { set { if (_linux != null) _linux.QuitHandler = value; } }

    public Task StartAsync() =>
        _linux?.StartAsync() ?? _windows?.StartAsync() ?? _mac?.StartAsync() ?? Task.CompletedTask;

    public void UpdatePlaybackStatus(bool isPlaying)
    {
        _linux?.UpdatePlaybackStatus(isPlaying);
        _windows?.UpdatePlaybackStatus(isPlaying);
        _mac?.UpdatePlaybackStatus(isPlaying);
    }

    public void UpdateSong(Song? song, string? coverPath = null)
    {
        CurrentSong = song;
        _linux?.UpdateSong(song, coverPath);
        _windows?.UpdateSong(song, coverPath);
        _mac?.UpdateSong(song, coverPath);
    }

    public void UpdateCover(string? coverPath)
    {
        _linux?.UpdateCover(coverPath);
        _windows?.UpdateCover(CurrentSong, coverPath);
        _mac?.UpdateCover(coverPath);
    }

    private Song? CurrentSong { get; set; }

    public void UpdateVolume(int volumePercent) => _linux?.UpdateVolume(volumePercent);
    public void UpdatePlaybackMode(PlaybackMode mode) => _linux?.UpdatePlaybackMode(mode);

    public void UpdatePosition(double seconds, double durationSeconds = 0)
    {
        _linux?.UpdatePosition(seconds);
        _windows?.UpdatePosition(seconds, durationSeconds > 0 ? durationSeconds : CurrentSong?.Duration ?? 0);
        _mac?.UpdatePosition(seconds);
    }

    public void EmitSeeked(double seconds)
    {
        _linux?.EmitSeeked(seconds);
        _windows?.UpdatePosition(seconds, CurrentSong?.Duration ?? 0);
        _mac?.UpdatePosition(seconds, force: true);
    }

    public void Dispose()
    {
        _linux?.Dispose();
        _windows?.Dispose();
        _mac?.Dispose();
    }
}
