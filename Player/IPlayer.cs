using QQMusic.Tui.Models;

namespace QQMusic.Tui.Player;

/// <summary>
/// 播放器通用抽象接口（解耦原生 GStreamer 管道与轻量 Web 协同播放器）
/// </summary>
public interface IPlayer : IDisposable
{
    bool IsPlaying { get; }
    double CurrentPositionSeconds { get; }
    double TotalDurationSeconds { get; }
    int Volume { get; }

    event Action<double>? PositionUpdated;
    event Action? PlaybackFinished;

    void Initialize();
    Task PlayAsync(string url, double duration, double startPosition = 0);
    Task TogglePauseAsync();
    Task StopAsync();
    Task SeekAsync(double seconds);
    void SetVolume(int vol);

    /// <summary>
    /// 更新当前曲目元数据（供 Web 播放器向客户端同步展示封面与标题等信息）
    /// </summary>
    void UpdateCurrentSong(Song? song) {}

    /// <summary>
    /// 更新当前曲目歌词（供 Web 播放器向客户端同步动态歌词）
    /// </summary>
    void UpdateCurrentLyrics(List<LyricLine>? lyrics) {}
}
