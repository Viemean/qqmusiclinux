using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services.QqAudioRecognition;

/// <summary>
/// QQ 音乐官方优图听歌识曲服务门面
/// </summary>
public static class QqMusicRecognitionService
{
    /// <summary>
    /// 当前环境中是否有可用的 QAFP 特征提取通道（检测官方微型 Runner）
    /// </summary>
    public static bool IsAvailable => QafpNativeRunner.IsAvailable;

    /// <summary>
    /// 识别 16000Hz PCM 采样切片并直通 QQ 音乐曲库
    /// </summary>
    public static async Task<RecognitionResult?> RecognizePcmSamplesAsync(
        short[] pcm16k, 
        QafpWorkerSession? workerSession = null, 
        CancellationToken cancellationToken = default)
    {
        if (pcm16k == null || pcm16k.Length < (int)(16000 * 2.0))
        {
            return null;
        }

        try
        {
            // 1. 16000Hz 降采样到 8000Hz (单声道 2 点低通移动平均)
            short[] pcm8k = Downsample16kTo8k(pcm16k);

            // 2. 提取 QAFP 特征（优先通过长连接 Worker 管道零开销提取，回退到一次性独立进程）
            QafpFeature? feature = null;
            if (workerSession != null && workerSession.IsReady)
            {
                feature = workerSession.Extract(pcm8k);
            }

            if (feature == null)
            {
                AppLogger.Force("QqMusicRecognitionService", $"Extracting QAFP feature via standalone runner, samples: {pcm8k.Length}, native runner available: {QafpNativeRunner.IsAvailable}");
                feature = QafpNativeRunner.Extract(pcm8k);
            }

            if (feature == null || feature.Data.Length == 0)
            {
                AppLogger.Force("QqMusicRecognitionService", "QAFP 特征提取失败或为空");
                return null;
            }

            // 3. 发起官方优图网络识别请求
            var response = await QqMusicRecognizeClient.SearchAsync(feature, cancellationToken);
            if (response.Success && response.Song != null)
            {
                AppLogger.Force("QqMusicRecognitionService", $"成功命中官方曲库: {response.Song.Title} - {response.Song.Artist} (Offset: {response.OffsetSeconds:F3}s)");
                return new RecognitionResult(
                    Success: true,
                    Title: response.Song.Title,
                    Artist: response.Song.Artist,
                    Album: response.Song.Album,
                    MatchedSong: response.Song,
                    ErrorMessage: ""
                );
            }

            if (!string.IsNullOrEmpty(response.ErrorMessage))
            {
                AppLogger.Force("QqMusicRecognitionService", $"云端识别失败: {response.ErrorMessage}");
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Force("QqMusicRecognitionService", $"识别链路异常: {ex}");
            return null;
        }
    }

    /// <summary>
    /// 16000Hz PCM 简单降采样至 8000Hz
    /// </summary>
    public static short[] Downsample16kTo8k(short[] pcm16k)
    {
        int targetLength = pcm16k.Length / 2;
        short[] pcm8k = new short[targetLength];
        for (int i = 0; i < targetLength; i++)
        {
            // 2 点均值平滑，有效滤除奈奎斯特混叠
            int sum = pcm16k[i * 2] + pcm16k[i * 2 + 1];
            pcm8k[i] = (short)(sum / 2);
        }
        return pcm8k;
    }
}
