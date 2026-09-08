using System;
using System.Linq;
using Xunit;
using QQMusic.Tui.Services.Shazam;

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
        
        // 输出调试信息 (通过 xunit test runner)
        Console.WriteLine($"Seconds: {seconds:F1}, TotalPeaks: {totalPeaks}, UriLength: {uri.Length}");
    }

    [Fact]
    public async System.Threading.Tasks.Task TestMatchWithQqMusic()
    {
        var songs = await QQMusic.Tui.Api.QqMusicApi.SearchAsync("恋音と雨空 Lefty Hand Cream", 1, 10);
        Assert.NotNull(songs);
        Assert.NotEmpty(songs);

        var matched = QQMusic.Tui.Services.AudioRecognitionService.FindBestMatchedSong(
            "恋音と雨空",
            "Lefty hand cream",
            "1LDK",
            songs);

        Console.WriteLine($"Best matched song: {matched?.Title} by {matched?.Artist} ({matched?.Mid})");
        Assert.NotNull(matched);
        Assert.Equal("002g7Bfv0Ri1Qi", matched.Mid);
    }
}
