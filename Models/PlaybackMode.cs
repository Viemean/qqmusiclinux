namespace QQMusic.Tui.Models;

/// <summary>
/// 播放循环与随机模式
/// </summary>
public enum PlaybackMode
{
    /// <summary>
    /// 列表循环 (首尾循环)
    /// </summary>
    ListLoop = 0,

    /// <summary>
    /// 单曲循环 (结束后重播该曲)
    /// </summary>
    SingleLoop = 1,

    /// <summary>
    /// 随机播放 (列表内洗牌抽取)
    /// </summary>
    Shuffle = 2,

    /// <summary>
    /// 顺序播放 (末尾停止)
    /// </summary>
    Sequential = 3
}

public static class PlaybackModeHelper
{
    public static string GetBadge(this PlaybackMode mode) => mode switch
    {
        PlaybackMode.ListLoop => "列表",
        PlaybackMode.SingleLoop => "单曲",
        PlaybackMode.Shuffle => "随机",
        PlaybackMode.Sequential => "顺序",
        _ => "列表"
    };

    public static string GetName(this PlaybackMode mode) => mode switch
    {
        PlaybackMode.ListLoop => "列表循环",
        PlaybackMode.SingleLoop => "单曲循环",
        PlaybackMode.Shuffle => "随机播放",
        PlaybackMode.Sequential => "顺序播放",
        _ => "列表循环"
    };

    public static PlaybackMode Next(this PlaybackMode mode) => mode switch
    {
        PlaybackMode.ListLoop => PlaybackMode.SingleLoop,
        PlaybackMode.SingleLoop => PlaybackMode.Shuffle,
        PlaybackMode.Shuffle => PlaybackMode.Sequential,
        PlaybackMode.Sequential => PlaybackMode.ListLoop,
        _ => PlaybackMode.ListLoop
    };

    public static (string LoopStatus, bool Shuffle) ToMpris(this PlaybackMode mode) => mode switch
    {
        PlaybackMode.ListLoop => ("Playlist", false),
        PlaybackMode.SingleLoop => ("Track", false),
        PlaybackMode.Shuffle => ("Playlist", true),
        PlaybackMode.Sequential => ("None", false),
        _ => ("Playlist", false)
    };

    public static PlaybackMode FromMpris(string loopStatus, bool shuffle)
    {
        if (shuffle) return PlaybackMode.Shuffle;
        return loopStatus switch
        {
            "Track" => PlaybackMode.SingleLoop,
            "None" => PlaybackMode.Sequential,
            _ => PlaybackMode.ListLoop
        };
    }
}
