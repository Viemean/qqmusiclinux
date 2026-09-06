using Xunit;

namespace QQMusic.Tui.Tests;

public class ShuffleAlgorithmTests
{
    [Fact]
    public void TestFisherYatesShuffleIntegrity()
    {
        const int count = 50;
        var indices = new List<int>();
        for (int i = 0; i < count; i++) indices.Add(i);

        // 模拟洗牌
        for (int i = count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        // 1. 元素总数必须不变
        Assert.Equal(count, indices.Count);

        // 2. 必须包含 0 ~ count-1 的所有元素且无重复
        var distinctSet = new HashSet<int>(indices);
        Assert.Equal(count, distinctSet.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Contains(i, distinctSet);
        }
    }

    [Fact]
    public void TestShuffleCycleNoDuplicateWithinOneRound()
    {
        const int count = 30;
        var indices = new List<int>();
        for (int i = 0; i < count; i++) indices.Add(i);

        for (int i = count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        var playedWithinRound = new List<int>();
        for (int pointer = 0; pointer < count; pointer++)
        {
            var next = indices[pointer];
            Assert.DoesNotContain(next, playedWithinRound);
            playedWithinRound.Add(next);
        }

        Assert.Equal(count, playedWithinRound.Count);
    }
}
