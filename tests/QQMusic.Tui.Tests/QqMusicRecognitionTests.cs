using QQMusic.Tui.Models;
using QQMusic.Tui.Services.QqAudioRecognition;
using Xunit;
using Xunit.Abstractions;

namespace QQMusic.Tui.Tests;

/// <summary>
/// QQ 音乐官方优图听歌识曲测试套件（基于微型 Native Runner 与自包含测试资产）
/// </summary>
public class QqMusicRecognitionTests
{
    private static readonly string TestDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
    private static readonly string TestPcmPath = Path.Combine(TestDataDir, "dongfengpo_8k.pcm");
    private static readonly string TestFeatPath = Path.Combine(TestDataDir, "dongfengpo_8k.pcm.feat");

    private readonly ITestOutputHelper _output;

    public QqMusicRecognitionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Downsample16kTo8k_CalculatesAverageCorrectly()
    {
        short[] pcm16k = [100, 200, -300, -100, 500, 700];
        var pcm8k = QqMusicRecognitionService.Downsample16kTo8k(pcm16k);

        Assert.Equal(3, pcm8k.Length);
        Assert.Equal(150, pcm8k[0]);
        Assert.Equal(-200, pcm8k[1]);
        Assert.Equal(600, pcm8k[2]);
    }

    [Fact]
    public void QafpNativeRunner_IsAvailable_ReturnsTrueWhenRunnerPresent()
    {
        bool available = QafpNativeRunner.IsAvailable;
        _output.WriteLine($"QafpNativeRunner.IsAvailable: {available}");
        _output.WriteLine($"Runner Path: {QafpNativeRunner.RunnerPath}");
        _output.WriteLine($"Sysroot Path: {QafpNativeRunner.SysrootPath}");

        if (!string.IsNullOrEmpty(QafpNativeRunner.RunnerPath) && File.Exists(QafpNativeRunner.RunnerPath))
        {
            Assert.True(available, "当 qafp_runner 与 sysroot 存在时，IsAvailable 必须为 true");
            Assert.True(QqMusicRecognitionService.IsAvailable, "QqMusicRecognitionService.IsAvailable 必须与 Runner 状态同步为 true");
        }
    }

    [Fact]
    public void QafpNativeRunner_Extract_SyntheticPcm_ProducesValidFingerprint()
    {
        if (!QafpNativeRunner.IsAvailable)
        {
            _output.WriteLine("[Skip] QafpNativeRunner 环境不可用");
            return;
        }

        // 构造 3.6 秒 (28800 点) 的 440Hz + 880Hz 双音合成 8000Hz PCM
        short[] pcm8k = new short[28800];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            double t = (double)i / 8000.0;
            double s1 = Math.Sin(2.0 * Math.PI * 440.0 * t);
            double s2 = Math.Sin(2.0 * Math.PI * 880.0 * t);
            pcm8k[i] = (short)((s1 + s2) * 10000);
        }

        var feature = QafpNativeRunner.Extract(pcm8k);

        Assert.NotNull(feature);
        Assert.True(feature.Data.Length > 20, $"特征字节长度应大于基本头部，实际: {feature.Data.Length}");
        Assert.Equal(3.6f, feature.Duration, precision: 1);
        _output.WriteLine($"Synthetic PCM feature extracted: {feature.Data.Length} bytes, duration: {feature.Duration}s");
    }

    [Fact]
    public void QafpNativeRunner_Extract_ShortOrEmptyPcm_HandlesSafely()
    {
        if (!QafpNativeRunner.IsAvailable)
        {
            return;
        }

        // 空数组
        var emptyFeat = QafpNativeRunner.Extract([]);
        Assert.Null(emptyFeat);

        // 过短音频 (0.2 秒 1600 点，不足最小分析窗 2 秒)
        short[] shortPcm = new short[1600];
        var shortFeat = QafpNativeRunner.Extract(shortPcm);
        Assert.Null(shortFeat);
    }

    [Fact]
    public async Task QafpNativeRunner_Extract_MatchesOfficialFeatureBitForBit()
    {
        Assert.True(File.Exists(TestPcmPath), $"测试 PCM 资产缺失: {TestPcmPath}");
        Assert.True(File.Exists(TestFeatPath), $"测试官方特征基准资产缺失: {TestFeatPath}");

        if (!QafpNativeRunner.IsAvailable)
        {
            _output.WriteLine("[Skip] QafpNativeRunner 环境不可用");
            return;
        }

        byte[] pcmBytes = await File.ReadAllBytesAsync(TestPcmPath);
        short[] pcm8k = new short[pcmBytes.Length / 2];
        Buffer.BlockCopy(pcmBytes, 0, pcm8k, 0, pcmBytes.Length);

        var feature = QafpNativeRunner.Extract(pcm8k);
        Assert.NotNull(feature);
        Assert.Equal(463, feature.Data.Length);

        byte[] officialBytes = await File.ReadAllBytesAsync(TestFeatPath);
        Assert.Equal(officialBytes, feature.Data);
        _output.WriteLine("Official feature matched bit-for-bit (100% byte identical)!");
    }

    [Fact]
    public async Task SearchAsync_WithValidFeature_ReturnsMatchedSong()
    {
        Assert.True(File.Exists(TestFeatPath), $"测试官方特征基准资产缺失: {TestFeatPath}");

        byte[] featData = await File.ReadAllBytesAsync(TestFeatPath);
        var feature = new QafpFeature(featData, 4.0f, 0, 0.0f);

        var result = await QqMusicRecognizeClient.SearchAsync(feature);
        if (!result.Success && (result.ErrorMessage?.Contains("已取消") == true || result.ErrorMessage?.Contains("网络异常") == true))
        {
            _output.WriteLine($"[Skip] 腾讯云端接口网络不可达或超时，跳过云端断言: {result.ErrorMessage}");
            return;
        }

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("东风破", result.Title);
        Assert.NotNull(result.Song);
        Assert.Equal("003uEbEr0jcW7c", result.Song.Mid);
        Assert.True(result.OffsetSeconds > 40.0 && result.OffsetSeconds < 50.0);
    }

    [Fact]
    public async Task QqMusicRecognitionService_RecognizePcmSamplesAsync_EndToEnd()
    {
        Assert.True(File.Exists(TestPcmPath), $"测试 PCM 资产缺失: {TestPcmPath}");

        if (!QqMusicRecognitionService.IsAvailable)
        {
            _output.WriteLine("[Skip] QqMusicRecognitionService 环境不可用");
            return;
        }

        // 将 8000Hz 样本模拟插值转换为 16000Hz（验证服务内部的 16k -> 8k 自动降采样及全流程）
        byte[] pcm8kBytes = await File.ReadAllBytesAsync(TestPcmPath);
        int sampleCount8k = pcm8kBytes.Length / 2;
        short[] pcm8k = new short[sampleCount8k];
        Buffer.BlockCopy(pcm8kBytes, 0, pcm8k, 0, pcm8kBytes.Length);

        short[] pcm16k = new short[sampleCount8k * 2];
        for (int i = 0; i < sampleCount8k; i++)
        {
            pcm16k[i * 2] = pcm8k[i];
            pcm16k[i * 2 + 1] = pcm8k[i];
        }

        // 调用顶层门面方法
        var recResult = await QqMusicRecognitionService.RecognizePcmSamplesAsync(pcm16k);
        if (recResult == null)
        {
            _output.WriteLine("[Skip] 网络波动或云端未能返回识别结果");
            return;
        }

        Assert.True(recResult.Success, recResult.ErrorMessage);
        Assert.Equal("东风破", recResult.Title);
        Assert.Equal("周杰伦", recResult.Artist);
        Assert.NotNull(recResult.MatchedSong);
        Assert.Equal("003uEbEr0jcW7c", recResult.MatchedSong.Mid);
        _output.WriteLine($"End-to-end recognition success: {recResult.Title} - {recResult.Artist}");
    }

    [Fact]
    public async Task QafpNativeRunner_WorkerSession_ContinuousExtraction_WorksConsistently()
    {
        if (!QafpNativeRunner.IsAvailable)
        {
            _output.WriteLine("[Skip] QafpNativeRunner 环境不可用");
            return;
        }

        byte[] pcmBytes = await File.ReadAllBytesAsync(TestPcmPath);
        short[] pcm8k = new short[pcmBytes.Length / 2];
        Buffer.BlockCopy(pcmBytes, 0, pcm8k, 0, pcmBytes.Length);

        using var session = QafpNativeRunner.StartWorkerSession();
        Assert.NotNull(session);
        Assert.True(session.IsReady);

        // 连续提取 3 次，验证长连接管道通信与 JNI Reset 状态复位正确性
        for (int i = 0; i < 3; i++)
        {
            var feat = session.Extract(pcm8k);
            Assert.NotNull(feat);
            Assert.Equal(463, feat.Data.Length);
            Assert.Equal(4.0f, feat.Duration, precision: 1);
        }
        _output.WriteLine("WorkerSession 连续 3 次提取通过，特征长度稳定一致 (463 bytes)");
    }
}
