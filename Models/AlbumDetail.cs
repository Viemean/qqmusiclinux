namespace QQMusic.Tui.Models;

/// <summary>
/// 专辑详情数据模型
/// </summary>
public sealed record AlbumDetail(
    string Mid,
    string Name,
    string Artist,
    string PublishDate,
    string Company,
    string Description,
    List<Song> Songs
);
