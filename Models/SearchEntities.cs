namespace QQMusic.Tui.Models;

public enum SearchCategory
{
    Song,
    Singer,
    Album,
    Playlist,
    Lyric
}

public sealed record SearchPage<T>(List<T> Items, int Total, bool HasMore);

public sealed record SearchOverview(
    string Query,
    SearchPage<Song> Songs,
    SearchPage<SearchSinger> Singers,
    SearchPage<SearchAlbum> Albums,
    SearchPage<SearchPlaylist> Playlists,
    SearchPage<Song> LyricSongs,
    List<string> RelatedWords
);

public sealed record SearchSinger(
    long Id,
    string Mid,
    string Name,
    string PicUrl,
    int SongCount,
    int AlbumCount
);

public sealed record SearchAlbum(
    long Id,
    string Mid,
    string Name,
    string Artist,
    string ArtistMid,
    int SongCount,
    string PublishDate,
    string PicUrl
);

public sealed record SearchPlaylist(
    long Tid,
    string Name,
    string Creator,
    int SongCount,
    long ListenCount,
    string PicUrl,
    string Description
)
{
    public Playlist ToPlaylist() => new(0, Name, SongCount, Tid, IsFav: true, PicUrl);
}

