using System.Buffers.Binary;

namespace QQMusic.Tui.Services.Shazam;

/// <summary>
/// Shazam 二进制音频签名编码器
/// 负责将提取出的频域星座图峰值打包为官方 48 字节 Header + TLV 数据块，并计算 CRC32 输出 Base64
/// </summary>
public sealed class ShazamSignature
{
    public const string DataUriPrefix = "data:audio/vnd.shazam.sig;base64,";

    public int SampleRateHz { get; set; } = 16000;
    public int NumberSamples { get; set; } = 0;
    public Dictionary<ShazamFrequencyBand, List<ShazamFrequencyPeak>> BandPeaks { get; } = new();

    public ShazamSignature()
    {
        BandPeaks[ShazamFrequencyBand.Hz250To520] = new List<ShazamFrequencyPeak>();
        BandPeaks[ShazamFrequencyBand.Hz520To1450] = new List<ShazamFrequencyPeak>();
        BandPeaks[ShazamFrequencyBand.Hz1450To3500] = new List<ShazamFrequencyPeak>();
        BandPeaks[ShazamFrequencyBand.Hz3500To5500] = new List<ShazamFrequencyPeak>();
    }

    /// <summary>
    /// 将签名编码为官方标准的二进制字节数组
    /// </summary>
    public byte[] EncodeToBinary()
    {
        using var contentsMs = new MemoryStream();
        Span<byte> passBytes = stackalloc byte[4];
        Span<byte> magBytes = stackalloc byte[2];
        Span<byte> binBytes = stackalloc byte[2];
        Span<byte> tlvHeader = stackalloc byte[8];

        // 按频段升序遍历打包 TLV
        var sortedBands = BandPeaks.Keys.OrderBy(b => (int)b).ToList();
        foreach (var band in sortedBands)
        {
            var peaks = BandPeaks[band];
            if (peaks.Count == 0) continue;

            // 确保峰值按 fftPassNumber 递增排序
            peaks.Sort((a, b) => a.FftPassNumber.CompareTo(b.FftPassNumber));

            using var peaksMs = new MemoryStream();
            int fftPassNumber = 0;

            foreach (var p in peaks)
            {
                int delta = p.FftPassNumber - fftPassNumber;
                if (delta >= 255)
                {
                    peaksMs.WriteByte(0xFF);
                    BinaryPrimitives.WriteInt32LittleEndian(passBytes, p.FftPassNumber);
                    peaksMs.Write(passBytes);
                    fftPassNumber = p.FftPassNumber;
                    delta = 0;
                }

                peaksMs.WriteByte((byte)delta);

                BinaryPrimitives.WriteUInt16LittleEndian(magBytes, (ushort)p.PeakMagnitude);
                peaksMs.Write(magBytes);

                BinaryPrimitives.WriteUInt16LittleEndian(binBytes, (ushort)p.CorrectedPeakFrequencyBin);
                peaksMs.Write(binBytes);

                fftPassNumber = p.FftPassNumber;
            }

            var peaksData = peaksMs.ToArray();
            int bandId = 0x60030040 + (int)band;

            // TLV Header (8 字节)
            BinaryPrimitives.WriteInt32LittleEndian(tlvHeader.Slice(0, 4), bandId);
            BinaryPrimitives.WriteInt32LittleEndian(tlvHeader.Slice(4, 4), peaksData.Length);
            contentsMs.Write(tlvHeader);
            contentsMs.Write(peaksData);

            // 4 字节对齐补齐
            int padding = (-peaksData.Length) & 3;
            for (int pad = 0; pad < padding; pad++)
            {
                contentsMs.WriteByte(0);
            }
        }

        var contentsBytes = contentsMs.ToArray();

        // 组装 Header 与固定引导块
        int sizeMinusHeader = 8 + contentsBytes.Length;
        byte[] fullBuffer = new byte[48 + sizeMinusHeader];

        // 写入固定 48 字节 Header
        BinaryPrimitives.WriteUInt32LittleEndian(fullBuffer.AsSpan(0, 4), 0xCAFE2580);
        // [4..8] 为 CRC32，稍后计算填入
        BinaryPrimitives.WriteUInt32LittleEndian(fullBuffer.AsSpan(8, 4), (uint)sizeMinusHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(fullBuffer.AsSpan(12, 4), 0x94119C00);
        // Void1: 16..28 保持 0
        // ShiftedSampleRateId: 16000Hz 对应 (3 << 27) = 0x18000000
        BinaryPrimitives.WriteUInt32LittleEndian(fullBuffer.AsSpan(28, 4), 0x18000000);
        // Void2: 32..40 保持 0
        // NumberSamplesPlusDividedSampleRate: number_samples + (int)(16000 * 0.24) = number_samples + 3840
        uint numSamplesPlusOffset = (uint)(NumberSamples + (int)(SampleRateHz * 0.24));
        BinaryPrimitives.WriteUInt32LittleEndian(fullBuffer.AsSpan(40, 4), numSamplesPlusOffset);
        // FixedValue: ((15 << 19) + 0x40000) = 0x007C0000
        BinaryPrimitives.WriteUInt32LittleEndian(fullBuffer.AsSpan(44, 4), 0x007C0000);

        // 紧接着 Header 的固定引导块 (8 字节)
        BinaryPrimitives.WriteUInt32LittleEndian(fullBuffer.AsSpan(48, 4), 0x40000000);
        BinaryPrimitives.WriteUInt32LittleEndian(fullBuffer.AsSpan(52, 4), (uint)sizeMinusHeader);

        // 写入 TLV 内容
        Buffer.BlockCopy(contentsBytes, 0, fullBuffer, 56, contentsBytes.Length);

        // 计算从偏移第 8 字节到末尾的 CRC32
        uint crc = ComputeCrc32(fullBuffer.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(fullBuffer.AsSpan(4, 4), crc);

        return fullBuffer;
    }

    /// <summary>
    /// 导出为 Base64 格式的 Data URI
    /// </summary>
    public string EncodeToUri()
    {
        var binary = EncodeToBinary();
        return DataUriPrefix + Convert.ToBase64String(binary);
    }

    #region 高性能 IEEE 802.3 CRC32 查表计算
    private static readonly uint[] s_crcTable = InitializeCrcTable();

    private static uint[] InitializeCrcTable()
    {
        var table = new uint[256];
        const uint polynomial = 0xEDB88320;
        for (uint i = 0; i < 256; i++)
        {
            uint entry = i;
            for (int j = 0; j < 8; j++)
            {
                entry = (entry & 1) != 0 ? (entry >> 1) ^ polynomial : entry >> 1;
            }
            table[i] = entry;
        }
        return table;
    }

    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            byte index = (byte)((crc & 0xFF) ^ data[i]);
            crc = (crc >> 8) ^ s_crcTable[index];
        }
        return crc ^ 0xFFFFFFFF;
    }
    #endregion
}
