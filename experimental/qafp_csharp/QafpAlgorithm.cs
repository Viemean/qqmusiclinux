using System.Buffers.Binary;
using System.Text;

namespace QQMusic.Tui.Services.QqAudioRecognition;

/// <summary>
/// 纯 C# 原生 QQ 音乐官方优图（QAFP）音频特征提取内核
/// 严格依照 QAFP 二进制协议规格实现：
/// - 采样基准：8000Hz, 16-bit Mono PCM
/// - 复合帧周期：0.9s（14 个 STFT 窗，14×512=7168 点推进）
/// - 短时变换：1024 点实数 FFT，512 点步长，Hamming 窗加权（0.54-0.46cos）
/// - 子频带结构：16 频带能量直方图量化（6 字节 / 频带）
/// - 特征压缩：时频局域峰值 9 比特大端位流序列化（Bitpacked Landmarks）
/// </summary>
public static class QafpAlgorithm
{
    private const int SampleRate = 8000;
    private const int WindowSize = 1024;
    private const int HopSize = 512;
    private const int WindowsPerFrame = 14;
    private const int SubbandWindowsPerFrame = 10;
    private const int SamplesPerFrame = 8000; // 官方复合帧固定 1.0 秒 (8000 样本)
    private const int HalfWindow = WindowSize / 2; // 512 bins (0 ~ 4000Hz)

    // 预计算 1024 点 Hamming 窗（反汇编确认：系数 0.54/-0.46）
    private static readonly float[] HammingWindow = InitializeHammingWindow(WindowSize);

    // 预计算 1024 点 Radix-2 FFT 旋转因子与位反转表
    private static readonly int[] BitRev = InitializeBitReversalTable(10); // 2^10 = 1024
    private static readonly float[] TwiddleCos = InitializeTwiddleCos(WindowSize);
    private static readonly float[] TwiddleSin = InitializeTwiddleSin(WindowSize);

    // 5 频带非线性峰值提取区间 (涵盖低频基频、中低频、人声共振峰、高频泛音、高频细节)
    private static readonly (int Start, int End)[] BandRanges =
    [
        (5, 40),     // 频带 0: 39 ~ 312 Hz (基频与打击乐)
        (41, 130),   // 频带 1: 320 ~ 1015 Hz (人声中低频)
        (131, 240),  // 频带 2: 1023 ~ 1875 Hz (人声主共振峰)
        (241, 360),  // 频带 3: 1882 ~ 2812 Hz (乐器与人声泛音)
        (361, 505)   // 频带 4: 2820 ~ 3945 Hz (高频打击与空气感)
    ];

    // 官方 32 听觉临界频带 (Traunmüller Bark Scale) 频域 bin 边界 (覆盖 0 ~ 4000Hz)
    private static readonly int[] BarkBinEdges =
    [
        0, 7, 14, 22, 29, 36, 43, 51, 58, 66,
        74, 82, 91, 100, 110, 120, 131, 142, 154, 167,
        181, 196, 212, 230, 249, 270, 293, 319, 348, 381,
        418, 461, 512
    ];

    /// <summary>
    /// 从 8000Hz 16-bit 单声道 PCM 样本中提取纯原生 QAFP 特征
    /// </summary>
    public static QafpFeature? Extract(short[] pcm8k)
    {
        if (pcm8k == null || pcm8k.Length < SamplesPerFrame)
        {
            return null;
        }

        float durationSeconds = (float)pcm8k.Length / SampleRate;
        int frameCount = pcm8k.Length / SamplesPerFrame;
        if (frameCount <= 0)
        {
            return null;
        }

        using var memoryStream = new MemoryStream();

        for (int frameIdx = 0; frameIdx < frameCount; frameIdx++)
        {
            int frameSampleStart = frameIdx * SamplesPerFrame;
            byte[] frameBytes = ProcessFrame(pcm8k, frameSampleStart);
            memoryStream.Write(frameBytes);
        }

        byte[] featureData = memoryStream.ToArray();
        return new QafpFeature(
            Data: featureData,
            Duration: durationSeconds,
            FeatureType: 0,
            Confidence: 0.0f
        );
    }

    private static byte[] ProcessFrame(short[] pcm8k, int startSample)
    {
        var allPeaks = new List<int>();

        // 1. 第一阶段：计算 14 个 STFT 窗口的频谱与能量分布
        float[,] magGrid = new float[WindowsPerFrame, HalfWindow + 1];
        float[] windowEnergies = new float[WindowsPerFrame];
        float totalFrameEnergy = 0f;

        float[] real = new float[WindowSize];
        float[] imag = new float[WindowSize];

        // 跨窗连续预加重 IIR 状态：避免每窗重置导致窗边缘高频脉冲假峰
        float preEmphasisState = (startSample > 0 && startSample < pcm8k.Length)
            ? pcm8k[startSample - 1]
            : 0f;

        for (int w = 0; w < WindowsPerFrame; w++)
        {
            int wStart = startSample + w * HopSize;
            int available = Math.Min(WindowSize, pcm8k.Length - wStart);

            // 预加重使用跨窗连续状态（历史样本从上一窗末尾继承）
            float prevSample = preEmphasisState;
            for (int i = 0; i < available; i++)
            {
                float current = pcm8k[wStart + i];
                float filtered = current - 0.97f * prevSample;
                prevSample = current;
                real[i] = filtered * HammingWindow[i];
                imag[i] = 0f;
            }
            // 更新下一窗的连续预加重状态（取本窗末尾样本）
            if (available > 0)
                preEmphasisState = pcm8k[wStart + available - 1];

            for (int i = available; i < WindowSize; i++)
            {
                real[i] = 0f;
                imag[i] = 0f;
            }

            PerformFft(real, imag);

            float wEnergy = 0f;
            for (int k = 0; k <= HalfWindow; k++)
            {
                float p = real[k] * real[k] + imag[k] * imag[k];
                float m = MathF.Sqrt(p);
                magGrid[w, k] = m;


                if (k >= 5 && k <= 505)
                {
                    wEnergy += m;
                }
            }
            windowEnergies[w] = wEnergy;
            totalFrameEnergy += wEnergy;
        }

        float avgWindowEnergy = (totalFrameEnergy / WindowsPerFrame) + 1e-6f;

        // 2. 第二阶段：基于动态能量配额与频带均值门限提取峰值
        for (int w = 0; w < WindowsPerFrame; w++)
        {
            float ratio = windowEnergies[w] / avgWindowEnergy;
            int quota = ratio switch
            {
                < 0.35f => 2,
                < 0.70f => 3,
                < 1.30f => 5,
                < 2.00f => 7,
                _ => 9
            };

            var windowCandidates = new List<(int Bin, float Mag)>();

            for (int b = 0; b < BandRanges.Length; b++)
            {
                var (bStart, bEnd) = BandRanges[b];
                float bandSum = 0f;
                int bandLen = bEnd - bStart;
                for (int k = bStart; k < bEnd; k++)
                {
                    bandSum += magGrid[w, k];
                }
                float bandMean = bandLen > 0 ? (bandSum / bandLen) : 0f;
                float threshold = 1.5f * bandMean;

                for (int k = bStart; k < bEnd; k++)
                {
                    float val = magGrid[w, k];
                    // 频域 ±2 bin 局域极大值 + 时间轴 NMS（w±1 弱抑制）
                    bool isPeak = val > threshold &&
                        val > magGrid[w, k - 1] && val > magGrid[w, k + 1] &&
                        val > magGrid[w, k - 2] && val > magGrid[w, k + 2];
                    if (isPeak && w > 0)
                        isPeak &= val >= magGrid[w - 1, k];
                    if (isPeak && w < WindowsPerFrame - 1)
                        isPeak &= val >= magGrid[w + 1, k];
                    if (isPeak)
                        windowCandidates.Add((k, val));
                }
            }

            // 窗口内依据能量降序选取前 quota 个显著峰值
            windowCandidates.Sort((x, y) => y.Mag.CompareTo(x.Mag));
            int takeCount = Math.Min(quota, windowCandidates.Count);
            for (int i = 0; i < takeCount; i++)
            {
                allPeaks.Add(windowCandidates[i].Bin);
            }
        }


        // 3. 计算 10 个 100ms 窗口下的 32 个均匀子频带对数能量 (覆盖整帧 1.0s / 8000 样本)
        var subbandEnergy = new float[32, SubbandWindowsPerFrame];
        var sbReal = new float[WindowSize];
        var sbImag = new float[WindowSize];

        for (int sw = 0; sw < SubbandWindowsPerFrame; sw++)
        {
            int swStart = startSample + sw * 800;
            int swAvail = Math.Min(WindowSize, pcm8k.Length - swStart);
            float prev = (swStart > 0) ? pcm8k[swStart - 1] : 0f;
            for (int i = 0; i < swAvail; i++)
            {
                float cur = pcm8k[swStart + i];
                sbReal[i] = (cur - 0.97f * prev) * HammingWindow[i];
                prev = cur;
                sbImag[i] = 0f;
            }
            for (int i = swAvail; i < WindowSize; i++)
            {
                sbReal[i] = 0f;
                sbImag[i] = 0f;
            }

            PerformFft(sbReal, sbImag);

            for (int s = 0; s < 32; s++)
            {
                float sum = 0f;
                int bStart = BarkBinEdges[s];
                int bEnd = BarkBinEdges[s + 1];
                for (int k = bStart; k < bEnd; k++)
                {
                    sum += MathF.Sqrt(sbReal[k] * sbReal[k] + sbImag[k] * sbImag[k]);
                }
                subbandEnergy[s, sw] = sum;
            }
        }

        // 计算 32 个子带在整帧的总能量，并通过频域局部极大值 (Local Maxima NMS) 筛选主导子带
        var bandTotals = new float[32];
        for (int s = 0; s < 32; s++)
        {
            float tot = 0f;
            for (int sw = 0; sw < SubbandWindowsPerFrame; sw++)
            {
                tot += subbandEnergy[s, sw];
            }
            bandTotals[s] = tot;
        }

        // 依据声学感知区间 (基频、共振峰、泛音、高频细节) 划分 6 个互不重叠宏观频段挑选代表子带
        var acousticIntervals = new (int Start, int End)[]
        {
            (0, 3),   // 低频基频
            (3, 8),   // 中低频
            (8, 14),  // 人声共振峰 1
            (14, 21), // 人声共振峰 2
            (21, 29), // 高频乐器
            (29, 32)  // 空气感细节
        };

        var selectedBands = new List<int> { 0 }; // 必定包含 0 号基频子带
        for (int i = 1; i < acousticIntervals.Length; i++)
        {
            var (iStart, iEnd) = acousticIntervals[i];
            int bestBand = iStart;
            float bestEnergy = -1f;
            for (int s = iStart; s < iEnd; s++)
            {
                if (bandTotals[s] > bestEnergy)
                {
                    bestEnergy = bandTotals[s];
                    bestBand = s;
                }
            }
            if (bestEnergy > bandTotals[0] * 0.02f)
            {
                selectedBands.Add(bestBand);
            }
        }

        selectedBands = selectedBands.Distinct().OrderBy(s => s).ToList();

        var activeSubbands = new List<(int SubbandId, int[] Levels)>();
        foreach (int s in selectedBands)
        {
            int[] levels = new int[SubbandWindowsPerFrame];
            float maxLog = float.MinValue;
            var logEnergies = new float[SubbandWindowsPerFrame];

            for (int sw = 0; sw < SubbandWindowsPerFrame; sw++)
            {
                float l = MathF.Log10(subbandEnergy[s, sw] + 1e-6f);
                logEnergies[sw] = l;
                if (l > maxLog) maxLog = l;
            }

            // 25dB 动态截断量化 (2.5 in log10)
            float floor = maxLog - 2.5f;

            for (int sw = 0; sw < SubbandWindowsPerFrame; sw++)
            {
                float val = logEnergies[sw];
                int lvl = (val <= floor)
                    ? 0
                    : Math.Clamp((int)((val - floor) / 2.5f * 15f), 1, 15);
                levels[sw] = lvl;
            }

            activeSubbands.Add((s, levels));
        }

        if (activeSubbands.Count == 0)
        {
            activeSubbands.Add((0, new int[SubbandWindowsPerFrame]));
        }

        // 4. 构建复合帧二进制结构
        ushort featureCount = (ushort)allPeaks.Count;
        ushort subbandCount = (ushort)activeSubbands.Count;

        using var frameStream = new MemoryStream();
        using var writer = new BinaryWriter(frameStream);

        // 帧头 (4 字节)
        writer.Write(BinaryPrimitives.ReverseEndianness(featureCount));
        writer.Write(BinaryPrimitives.ReverseEndianness(subbandCount));

        // 子频带直方图 (subbandCount * 6 字节: 1B ID + 5B (10 个 4-bit 对数能量量化值))
        foreach (var (subbandId, levels) in activeSubbands)
        {
            byte[] entry = PackSubbandEntry(subbandId, levels);
            writer.Write(entry);
        }

        // 特征打包区: 2 字节标记 + 9 比特位流
        writer.Write(BinaryPrimitives.ReverseEndianness(featureCount));
        byte[] bitpackedPeaks = Pack9BitList(allPeaks);
        writer.Write(bitpackedPeaks);

        return frameStream.ToArray();
    }

    /// <summary>
    /// 将 8 比特子频带 ID 与 10 个 4 比特量化级别打包为 48 比特（6 字节）
    /// </summary>
    private static byte[] PackSubbandEntry(int subbandId, int[] levels)
    {
        var result = new byte[6];
        result[0] = (byte)(subbandId & 0x1F); // 0~31
        for (int i = 0; i < 5; i++)
        {
            int high = (i * 2 < levels.Length) ? (levels[i * 2] & 0x0F) : 0;
            int low = (i * 2 + 1 < levels.Length) ? (levels[i * 2 + 1] & 0x0F) : 0;
            result[1 + i] = (byte)((high << 4) | low);
        }
        return result;
    }

    /// <summary>
    /// 严格按照大端 9 比特位流无缝打包特征频率索引
    /// </summary>
    public static byte[] Pack9BitList(IReadOnlyList<int> values)
    {
        if (values == null || values.Count == 0)
        {
            return Array.Empty<byte>();
        }

        int totalBits = values.Count * 9;
        int totalBytes = (totalBits + 7) / 8;
        byte[] buffer = new byte[totalBytes];

        int bitOffset = 0;
        foreach (int v in values)
        {
            int val = v & 0x1FF; // 9 bits

            for (int b = 8; b >= 0; b--)
            {
                int bit = (val >> b) & 1;
                if (bit == 1)
                {
                    int byteIdx = bitOffset / 8;
                    int shift = 7 - (bitOffset % 8);
                    buffer[byteIdx] |= (byte)(1 << shift);
                }
                bitOffset++;
            }
        }

        return buffer;
    }

    /// <summary>
    /// 1024 点实数 FFT 原地计算
    /// </summary>
    private static void PerformFft(float[] real, float[] imag)
    {
        // 1. 位反转置换
        for (int i = 0; i < WindowSize; i++)
        {
            int j = BitRev[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // 2. 蝶形运算
        for (int len = 2; len <= WindowSize; len <<= 1)
        {
            int halfLen = len >> 1;
            int step = WindowSize / len;

            for (int i = 0; i < WindowSize; i += len)
            {
                for (int j = 0; j < halfLen; j++)
                {
                    int k = j * step;
                    float c = TwiddleCos[k];
                    float s = TwiddleSin[k];

                    int uIdx = i + j;
                    int vIdx = i + j + halfLen;

                    float tr = real[vIdx] * c - imag[vIdx] * s;
                    float ti = real[vIdx] * s + imag[vIdx] * c;

                    real[vIdx] = real[uIdx] - tr;
                    imag[vIdx] = imag[uIdx] - ti;

                    real[uIdx] += tr;
                    imag[uIdx] += ti;
                }
            }
        }
    }

    // Hamming 窗：反汇编 0x69190=-0.46, 0x69198=0.54 直接确认系数
    private static float[] InitializeHammingWindow(int size)
    {
        float[] window = new float[size];
        for (int i = 0; i < size; i++)
        {
            window[i] = 0.54f - 0.46f * MathF.Cos(2f * MathF.PI * i / (size - 1));
        }
        return window;
    }

    private static int[] InitializeBitReversalTable(int bits)
    {
        int count = 1 << bits;
        int[] table = new int[count];
        for (int i = 0; i < count; i++)
        {
            int rev = 0;
            for (int j = 0; j < bits; j++)
            {
                if ((i & (1 << j)) != 0)
                {
                    rev |= (1 << (bits - 1 - j));
                }
            }
            table[i] = rev;
        }
        return table;
    }

    private static float[] InitializeTwiddleCos(int size)
    {
        float[] table = new float[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            table[i] = MathF.Cos(-2f * MathF.PI * i / size);
        }
        return table;
    }

    private static float[] InitializeTwiddleSin(int size)
    {
        float[] table = new float[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            table[i] = MathF.Sin(-2f * MathF.PI * i / size);
        }
        return table;
    }
}
