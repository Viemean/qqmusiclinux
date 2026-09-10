using System;
using System.Collections.Generic;
using System.Linq;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;
using QQMusic.Tui.Services.Shazam;
using Xunit;

namespace QQMusic.Tui.Tests;

public class ShazamAlgorithmTests
{
    [Theory]
    [InlineData(1.8)]
    [InlineData(2.5)]
    [InlineData(3.2)]
    [InlineData(5.0)]
    [InlineData(7.5)]
    public void CreateSignature_VariousLengths_GeneratesPeaks(double seconds)
    {
        int sampleRate = 16000;
        int count = (int)(seconds * sampleRate);
        short[] samples = new short[count];

        // 混入多个频率 (440Hz, 880Hz, 1500Hz, 3000Hz) 模拟音乐泛音
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / sampleRate;
            double v = Math.Sin(2 * Math.PI * 440 * t) * 8000
                     + Math.Sin(2 * Math.PI * 880 * t) * 6000
                     + Math.Sin(2 * Math.PI * 1500 * t) * 4000
                     + Math.Sin(2 * Math.PI * 3000 * t) * 3000;
            samples[i] = (short)v;
        }

        var sig = ShazamAlgorithm.CreateSignatureFromPcm(samples);
        int totalPeaks = sig.BandPeaks.Values.Sum(p => p.Count);
        var uri = sig.EncodeToUri();

        Assert.True(sig.NumberSamples > 0);
        Assert.NotNull(uri);
        Assert.StartsWith("data:audio/vnd.shazam.sig;base64,", uri);
        Assert.True(totalPeaks > 0);
    }

    [Fact]
    public void FindBestMatchedSong_FromCandidates_MatchesHighestSimilarityOffline()
    {
        // 纯离线单元测试：构造包含精确匹配项、部分同名但不同歌手项、以及无关歌曲的候选列表
        var candidates = new List<Song>
        {
            new Song("001_unrelated", "夜曲", "周杰伦", "十一月的萧邦", 226),
            new Song("002g7Bfv0Ri1Qi", "恋音と雨空 (恋歌与雨天)", "Lefty Hand Cream", "1LDK", 260),
            new Song("003_cover", "恋音と雨空", "AAA", "GOLD SYMPHONY", 310),
            new Song("004_other", "晴天", "周杰伦", "叶惠美", 269)
        };

        var matched = AudioRecognitionService.FindBestMatchedSong(
            "恋音と雨空",
            "Lefty hand cream",
            "1LDK",
            candidates);

        Assert.NotNull(matched);
        Assert.Equal("002g7Bfv0Ri1Qi", matched.Mid);
        Assert.Equal("Lefty Hand Cream", matched.Artist);
        Assert.Contains("恋音と雨空", matched.Title);
    }
}
