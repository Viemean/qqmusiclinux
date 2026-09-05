namespace QQMusic.Tui.Models;

/// <summary>
/// 专辑数据模型
/// </summary>
/// <param name="Id">专辑数字 ID</param>
/// <param name="Mid">专辑 Mid 字符串唯一标识</param>
/// <param name="Title">专辑标题</param>
/// <param name="Artist">专辑所属歌手</param>
/// <param name="SongCount">包含歌曲数量</param>
/// <param name="CoverUrl">封面图片链接</param>
/// <param name="PubTime">发布时间戳或年份</param>
public sealed record Album(
    long Id,
    string Mid,
    string Title,
    string Artist,
    int SongCount,
    string CoverUrl = "",
    long PubTime = 0
);
