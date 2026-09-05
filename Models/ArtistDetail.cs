namespace QQMusic.Tui.Models;

/// <summary>
/// 歌手详情数据模型
/// </summary>
public sealed record ArtistDetail(
    string Mid,
    long Id,
    string Name,
    string Brief,
    List<Song> Songs
);
