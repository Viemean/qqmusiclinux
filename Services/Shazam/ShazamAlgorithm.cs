namespace QQMusic.Tui.Services.Shazam;

/// <summary>
/// Apple Shazam 官方指纹提取核心算法 (C# 原生实现)
/// 包含 2048 点 Hanning 窗、极速实数 FFT、时域与频域峰值扩展及局部最大值提取
/// </summary>
public sealed class ShazamAlgorithm
{
    private const int WindowSize = 2048;
    private const int HalfWindow = 1025;
    private const int StepSize = 128;
    private const int HistoryBufferSize = 256;

    // 预计算 Hanning 窗 (2050 点取中间 2048 个，去除端点 0)
    private static readonly float[] s_hanningWindow = InitializeHanningWindow();

    // 预计算 2048 点 Radix-2 FFT 旋转因子与位反转表
    private static readonly int[] s_bitRev = InitializeBitReversalTable(11);
    private static readonly float[] s_twiddleCos = InitializeTwiddleCos();
    private static readonly float[] s_twiddleSin = InitializeTwiddleSin();

    // 环形音频采样缓冲 (2048 个点)
    private readonly float[] _sampleRingBuffer = new float[WindowSize];
    private int _sampleBufferPos = 0;

    // 环形 FFT 功率谱历史 (256 × 1025)
    private readonly float[][] _fftOutputs = new float[HistoryBufferSize][];
    private int _fftOutputsPos = 0;

    // 环形峰值扩展历史 (256 × 1025)
    private readonly float[][] _spreadFftOutputs = new float[HistoryBufferSize][];
    private int _spreadPos = 0;
    private int _spreadNumWritten = 0;

    // 临时 FFT 实虚部计算缓存 (避免任何堆内存分配)
    private readonly float[] _re = new float[WindowSize];
    private readonly float[] _im = new float[WindowSize];

    public ShazamAlgorithm()
    {
        for (int i = 0; i < HistoryBufferSize; i++)
        {
            _fftOutputs[i] = new float[HalfWindow];
            _spreadFftOutputs[i] = new float[HalfWindow];
        }
    }

    /// <summary>
    /// 处理 16000Hz 单声道 16-bit PCM 音频，提取特征并填充到 ShazamSignature
    /// </summary>
    public static ShazamSignature CreateSignatureFromPcm(ReadOnlySpan<short> samples)
    {
        var algorithm = new ShazamAlgorithm();
        var signature = new ShazamSignature
        {
            SampleRateHz = 16000,
            NumberSamples = samples.Length
        };

        const int maxPeaks = 255;
        const double maxTimeSeconds = 3.1;
        int maxSamples = Math.Min(samples.Length, (int)(maxTimeSeconds * 16000));

        int offset = 0;
        while (offset + StepSize <= maxSamples)
        {
            algorithm.Process128Chunk(samples.Slice(offset, StepSize), signature);
            offset += StepSize;

            int totalPeaks = signature.BandPeaks.Values.Sum(p => p.Count);
            if (totalPeaks >= maxPeaks)
            {
                break;
            }
        }

        return signature;
    }

    private void Process128Chunk(ReadOnlySpan<short> chunk, ShazamSignature signature)
    {
        // 1. 写入采样环形缓冲
        for (int i = 0; i < StepSize; i++)
        {
            _sampleRingBuffer[(_sampleBufferPos + i) % WindowSize] = chunk[i];
        }
        _sampleBufferPos = (_sampleBufferPos + StepSize) % WindowSize;

        // 2. 按时间从旧到新提取 2048 样本并加 Hanning 窗
        for (int i = 0; i < WindowSize; i++)
        {
            int ringIdx = (_sampleBufferPos + i) % WindowSize;
            _re[i] = _sampleRingBuffer[ringIdx] * s_hanningWindow[i];
            _im[i] = 0f;
        }

        // 3. 执行 2048 点快速傅里叶变换 (FFT)
        ComputeFft(_re, _im);

        // 4. 计算归一化功率谱并写入 fftOutputs
        var currentFft = _fftOutputs[_fftOutputsPos];
        const float invScale = 1.0f / 131072.0f; // 1 / (1 << 17)
        for (int i = 0; i < HalfWindow; i++)
        {
            float power = (_re[i] * _re[i] + _im[i] * _im[i]) * invScale;
            currentFft[i] = Math.Max(power, 1e-10f);
        }

        _fftOutputsPos = (_fftOutputsPos + 1) % HistoryBufferSize;

        // 5. 执行 Peak Spreading
        DoPeakSpreading(currentFft);

        // 6. 若历史累积足够，执行 Peak 提取
        if (_spreadNumWritten >= 46)
        {
            DoPeakRecognition(signature);
        }
    }

    private void DoPeakSpreading(float[] originLastFft)
    {
        var spreadNew = new float[HalfWindow];

        // 频域局部极大值扩展 (宽度 3)
        for (int i = 0; i < HalfWindow - 3; i++)
        {
            spreadNew[i] = Math.Max(originLastFft[i], Math.Max(originLastFft[i + 1], originLastFft[i + 2]));
        }
        for (int i = HalfWindow - 3; i < HalfWindow; i++)
        {
            spreadNew[i] = originLastFft[i];
        }

        int i1 = (_spreadPos - 1 + HistoryBufferSize) % HistoryBufferSize;
        int i2 = (_spreadPos - 3 + HistoryBufferSize) % HistoryBufferSize;
        int i3 = (_spreadPos - 6 + HistoryBufferSize) % HistoryBufferSize;

        var s1 = _spreadFftOutputs[i1];
        var s2 = _spreadFftOutputs[i2];
        var s3 = _spreadFftOutputs[i3];

        // 时域向后级联更新历史极大值包络
        for (int k = 0; k < HalfWindow; k++)
        {
            float o = spreadNew[k];
            s1[k] = Math.Max(s1[k], o);
            s2[k] = Math.Max(s2[k], s1[k]);
            s3[k] = Math.Max(s3[k], s2[k]);
        }

        // 保存当前扩展帧到环形缓冲
        Array.Copy(spreadNew, _spreadFftOutputs[_spreadPos], HalfWindow);

        _spreadPos = (_spreadPos + 1) % HistoryBufferSize;
        _spreadNumWritten++;
    }

    private static readonly int[] s_neighborOffsets = [-10, -7, -4, -3, 1, 2, 5, 8];
    private static readonly int[] s_timeOffsets = [-53, -45, 165, 172, 179, 186, 193, 200, 214, 221, 228, 235, 242, 249];

    private void DoPeakRecognition(ShazamSignature signature)
    {
        int idx46 = (_fftOutputsPos - 46 + HistoryBufferSize) % HistoryBufferSize;
        int idx49 = (_spreadPos - 49 + HistoryBufferSize) % HistoryBufferSize;

        var fftMinus46 = _fftOutputs[idx46];
        var fftMinus49 = _spreadFftOutputs[idx49];

        const float minThreshold = 1.0f / 64.0f;

        for (int binPosition = 10; binPosition < 1015; binPosition++)
        {
            float centerVal = fftMinus46[binPosition];
            if (centerVal < minThreshold || centerVal < fftMinus49[binPosition - 1])
            {
                continue;
            }

            // 频域邻域极大值比较
            float maxNeighborInFftMinus49 = 0f;
            foreach (var offset in s_neighborOffsets)
            {
                float val = fftMinus49[binPosition + offset];
                if (val > maxNeighborInFftMinus49) maxNeighborInFftMinus49 = val;
            }

            if (centerVal <= maxNeighborInFftMinus49)
            {
                continue;
            }

            // 时域相邻帧极大值比较
            float maxNeighborOther = maxNeighborInFftMinus49;
            foreach (var otherOffset in s_timeOffsets)
            {
                int frameIdx = (_spreadPos + otherOffset) % HistoryBufferSize;
                if (frameIdx < 0) frameIdx += HistoryBufferSize;
                float val = _spreadFftOutputs[frameIdx][binPosition - 1];
                if (val > maxNeighborOther) maxNeighborOther = val;
            }

            if (centerVal <= maxNeighborOther)
            {
                continue;
            }

            // 确定为有效特征峰值！进行二次抛物线微调插值
            int fftPassNumber = _spreadNumWritten - 46;

            float valBefore = fftMinus46[binPosition - 1];
            float valAfter = fftMinus46[binPosition + 1];

            double peakMag = Math.Log(Math.Max(minThreshold, centerVal)) * 1477.3 + 6144.0;
            double peakMagBefore = Math.Log(Math.Max(minThreshold, valBefore)) * 1477.3 + 6144.0;
            double peakMagAfter = Math.Log(Math.Max(minThreshold, valAfter)) * 1477.3 + 6144.0;

            double peakVar1 = peakMag * 2.0 - peakMagBefore - peakMagAfter;
            double peakVar2 = (peakMagAfter - peakMagBefore) * 32.0 / (peakVar1 > 0 ? peakVar1 : 1.0);

            double correctedPeakFrequencyBin = binPosition * 64.0 + peakVar2;
            double frequencyHz = correctedPeakFrequencyBin * (16000.0 / 2.0 / 1024.0 / 64.0);

            ShazamFrequencyBand band;
            if (frequencyHz > 250 && frequencyHz < 520)
            {
                band = ShazamFrequencyBand.Hz250To520;
            }
            else if (frequencyHz >= 520 && frequencyHz < 1450)
            {
                band = ShazamFrequencyBand.Hz520To1450;
            }
            else if (frequencyHz >= 1450 && frequencyHz < 3500)
            {
                band = ShazamFrequencyBand.Hz1450To3500;
            }
            else if (frequencyHz >= 3500 && frequencyHz <= 5500)
            {
                band = ShazamFrequencyBand.Hz3500To5500;
            }
            else
            {
                continue;
            }

            signature.BandPeaks[band].Add(new ShazamFrequencyPeak(
                fftPassNumber,
                (int)peakMag,
                (int)correctedPeakFrequencyBin,
                16000
            ));
        }
    }

    #region 高性能 Radix-2 FFT 引擎与静态查找表
    private static void ComputeFft(float[] re, float[] im)
    {
        const int n = WindowSize;

        // 位反转置换
        for (int i = 0; i < n; i++)
        {
            int j = s_bitRev[i];
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        // 蝶形运算
        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1;
            int step = n / size;

            for (int i = 0; i < n; i += size)
            {
                for (int k = 0; k < half; k++)
                {
                    int tableIdx = k * step;
                    float wRe = s_twiddleCos[tableIdx];
                    float wIm = s_twiddleSin[tableIdx];

                    int uIdx = i + k;
                    int vIdx = i + k + half;

                    float uRe = re[uIdx];
                    float uIm = im[uIdx];
                    float vRe = re[vIdx] * wRe - im[vIdx] * wIm;
                    float vIm = re[vIdx] * wIm + im[vIdx] * wRe;

                    re[uIdx] = uRe + vRe;
                    im[uIdx] = uIm + vIm;
                    re[vIdx] = uRe - vRe;
                    im[vIdx] = uIm - vIm;
                }
            }
        }
    }

    private static float[] InitializeHanningWindow()
    {
        var window = new float[WindowSize];
        const double m = 2049.0;
        for (int i = 0; i < WindowSize; i++)
        {
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * (i + 1) / m));
        }
        return window;
    }

    private static int[] InitializeBitReversalTable(int bits)
    {
        int count = 1 << bits;
        var table = new int[count];
        for (int i = 0; i < count; i++)
        {
            int rev = 0;
            int n = i;
            for (int j = 0; j < bits; j++)
            {
                rev = (rev << 1) | (n & 1);
                n >>= 1;
            }
            table[i] = rev;
        }
        return table;
    }

    private static float[] InitializeTwiddleCos()
    {
        var table = new float[WindowSize / 2];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = MathF.Cos(-2f * MathF.PI * i / WindowSize);
        }
        return table;
    }

    private static float[] InitializeTwiddleSin()
    {
        var table = new float[WindowSize / 2];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = MathF.Sin(-2f * MathF.PI * i / WindowSize);
        }
        return table;
    }
    #endregion
}
