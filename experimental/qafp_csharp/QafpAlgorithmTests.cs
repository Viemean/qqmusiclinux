using System.Text;
using Xunit;
using Xunit.Abstractions;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services.QqAudioRecognition;

namespace QQMusic.Tui.Tests.Experimental;

/// <summary>
/// QAFP 纯 C# 原生逆向算法实验单测（保留本地供后续研究推进）
/// </summary>
public class QafpAlgorithmTests
{
    private readonly ITestOutputHelper _output;

    public QafpAlgorithmTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void QafpAlgorithm_Extract_GeneratesValidBinaryFeature()
    {
        // 构造 3.6 秒 (28800 点) 的 440Hz + 880Hz 双音合成波
        short[] pcm8k = new short[28800];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            double t = (double)i / 8000.0;
            double s1 = Math.Sin(2.0 * Math.PI * 440.0 * t);
            double s2 = Math.Sin(2.0 * Math.PI * 880.0 * t);
            pcm8k[i] = (short)((s1 + s2) * 10000);
        }

        var feature = QafpAlgorithm.Extract(pcm8k);

        Assert.NotNull(feature);
        Assert.True(feature.Data.Length > 100);
        Assert.Equal(3.6f, feature.Duration, precision: 1);
    }

    [Fact]
    public void Pack9BitList_MatchesExpectedBitstream()
    {
        int[] values = [56, 113, 471]; // 3 个 9 比特整数
        var packed = QafpAlgorithm.Pack9BitList(values);

        // 3 * 9 = 27 bits -> ceil(27/8) = 4 bytes
        Assert.Equal(4, packed.Length);

        // 56 = 000111000, 113 = 001110001, 471 = 111010111
        // bitstream: 00011100 00011100 01111010 11100000 -> 0x1c 0x1c 0x7a 0xe0
        Assert.Equal(0x1c, packed[0]);
        Assert.Equal(0x1c, packed[1]);
        Assert.Equal(0x7a, packed[2]);
        Assert.Equal(0xe0, packed[3]);
    }

    [Fact]
    public async Task QafpAlgorithm_ExtractsBinaryFeatureFromPcmCorrectly()
    {
        string pcmPath = "/tmp/dongfengpo_8k.pcm";
        if (!File.Exists(pcmPath))
        {
            return;
        }

        byte[] rawPcm = await File.ReadAllBytesAsync(pcmPath);
        short[] pcm8k = new short[rawPcm.Length / 2];
        Buffer.BlockCopy(rawPcm, 0, pcm8k, 0, rawPcm.Length);

        var feature = QafpAlgorithm.Extract(pcm8k);
        Assert.NotNull(feature);

        byte[] officialBytes = await File.ReadAllBytesAsync("/tmp/dongfengpo_8k.pcm.feat");
        var (extFrames, extPeaks) = DecodeFrames(feature.Data);
        var (offFrames, offPeaks) = DecodeFrames(officialBytes);

        _output.WriteLine($"Extracted: {extFrames} frames, {extPeaks.Count} peaks");
        _output.WriteLine($"Official:  {offFrames} frames, {offPeaks.Count} peaks");

        var extSet = new HashSet<int>(extPeaks);
        int setOverlap = offPeaks.Count(p => extSet.Contains(p));
        _output.WriteLine($"Set overlap: {setOverlap}/{offPeaks.Count} ({setOverlap * 100.0 / offPeaks.Count:F1}%)");
        Assert.True(setOverlap >= offPeaks.Count * 0.6, $"Peak overlap {setOverlap}/{offPeaks.Count} should be >= 60%");
    }

    private static (int FrameCount, List<int> Peaks) DecodeFrames(byte[] data)
    {
        var peaks = new List<int>();
        int offset = 0;
        int frames = 0;
        while (offset + 4 <= data.Length)
        {
            ushort featCnt = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            ushort subbandCnt = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2));
            offset += 4 + subbandCnt * 6 + 2;

            int bitCnt = featCnt * 9;
            int byteCnt = (bitCnt + 7) / 8;
            if (offset + byteCnt > data.Length) break;

            var slice = data.AsSpan(offset, byteCnt);
            for (int p = 0; p < featCnt; p++)
            {
                int startBit = p * 9;
                int val = 0;
                for (int b = 0; b < 9; b++)
                {
                    int currBit = startBit + b;
                    int byteIndex = currBit / 8;
                    int bitIndex = 7 - (currBit % 8);
                    int bit = (slice[byteIndex] >> bitIndex) & 1;
                    val = (val << 1) | bit;
                }
                peaks.Add(val);
            }
            offset += byteCnt;
            frames++;
        }
        return (frames, peaks);
    }
}
