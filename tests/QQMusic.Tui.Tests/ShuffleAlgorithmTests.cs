using System.Collections.Generic;
using System.Linq;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;
using Xunit;

namespace QQMusic.Tui.Tests;

public class ShuffleAlgorithmTests
{
    private static List<Song> CreateMockSongList(int count)
    {
        return Enumerable.Range(1, count)
            .Select(i => new Song($"mid_{i}", $"Song Title {i}", $"Artist {i}", $"Album {i}", 200, "", i))
            .ToList();
    }

    [Fact]
    public void ShuffleMode_GetNextSong_VisitsAllSongsWithoutImmediateDuplicate()
    {
        var queue = PlaybackQueueService.Instance;
        var songs = CreateMockSongList(10);

        queue.SetQueue(songs, startIndex: 0);
        queue.Mode = PlaybackMode.Shuffle;

        var visitedMids = new List<string> { queue.CurrentSong!.Mid };

        // 在包含 10 首歌的队列中，切歌 9 次应遍历完这一轮随机列表中的其余 9 首
        for (int i = 0; i < 9; i++)
        {
            var nextSong = queue.GetNextSong(isAutoPlayback: true);
            Assert.NotNull(nextSong);
            visitedMids.Add(nextSong.Mid);
        }

        // 单轮洗牌内不应产生重复歌曲
        Assert.Equal(10, visitedMids.Count);
        Assert.Equal(10, visitedMids.Distinct().Count());
    }

    [Fact]
    public void ShuffleMode_GetPrevSong_AllowsBacktrackingPlayedHistory()
    {
        var queue = PlaybackQueueService.Instance;
        var songs = CreateMockSongList(5);

        queue.SetQueue(songs, startIndex: 0);
        queue.Mode = PlaybackMode.Shuffle;

        var firstSong = queue.CurrentSong;
        var secondSong = queue.GetNextSong(isAutoPlayback: true);
        var thirdSong = queue.GetNextSong(isAutoPlayback: true);

        Assert.NotNull(firstSong);
        Assert.NotNull(secondSong);
        Assert.NotNull(thirdSong);

        // 回退到第二首
        var backToSecond = queue.GetPrevSong();
        Assert.NotNull(backToSecond);
        Assert.Equal(secondSong.Mid, backToSecond.Mid);

        // 再次回退到第一首
        var backToFirst = queue.GetPrevSong();
        Assert.NotNull(backToFirst);
        Assert.Equal(firstSong.Mid, backToFirst.Mid);
    }

    [Fact]
    public void PlaybackModeHelper_Next_TransitionsInCorrectOrder()
    {
        var mode = PlaybackMode.ListLoop;

        mode = mode.Next();
        Assert.Equal(PlaybackMode.SingleLoop, mode);

        mode = mode.Next();
        Assert.Equal(PlaybackMode.Shuffle, mode);

        mode = mode.Next();
        Assert.Equal(PlaybackMode.Sequential, mode);

        mode = mode.Next();
        Assert.Equal(PlaybackMode.ListLoop, mode);
    }
}
