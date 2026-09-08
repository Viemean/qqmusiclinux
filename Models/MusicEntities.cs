namespace QQMusic.Tui.Models;

/// <summary>
/// 专辑数据模型
/// </summary>
public sealed record Album(
    long Id,
    string Mid,
    string Title,
    string Artist,
    int SongCount,
    string CoverUrl = "",
    long PubTime = 0
);

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

/// <summary>
/// 歌手子模式（单曲或专辑）
/// </summary>
public enum SingerSubMode
{
    Songs,
    Albums
}

/// <summary>
/// 歌单数据模型
/// </summary>
public record Playlist(
    long DirId,
    string Name,
    int SongCount,
    long Tid = 0,
    bool IsFav = false,
    string PicUrl = ""
)
{
    public bool IsMyFavorite => DirId == 201;

    public string Title => Name;
    public int SongNum => SongCount;
    public bool IsCreated => !IsFav;

    public string DisplayTitle => IsMyFavorite
        ? $"[我喜欢] ({SongCount} 首)"
        : (IsFav ? $"[收藏] {Name} ({SongCount} 首)" : $"{Name} ({SongCount} 首)");
}
