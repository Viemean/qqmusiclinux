namespace QQMusic.Tui.Models;

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
