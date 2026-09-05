namespace QQMusic.Tui.Services.Shazam;

/// <summary>
/// Shazam 音频指纹频段分类定义
/// </summary>
public enum ShazamFrequencyBand
{
    Hz250To520 = 0,   // 250Hz - 520Hz  (ID: 0x60030040)
    Hz520To1450 = 1,  // 520Hz - 1450Hz (ID: 0x60030041)
    Hz1450To3500 = 2, // 1450Hz - 3500Hz (ID: 0x60030042)
    Hz3500To5500 = 3  // 3500Hz - 5500Hz (ID: 0x60030043)
}

/// <summary>
/// Shazam 频谱峰值描述
/// </summary>
public sealed class ShazamFrequencyPeak
{
    public int FftPassNumber { get; set; }
    public int PeakMagnitude { get; set; }
    public int CorrectedPeakFrequencyBin { get; set; }
    public int SampleRateHz { get; set; } = 16000;

    public ShazamFrequencyPeak(int fftPassNumber, int peakMagnitude, int correctedPeakFrequencyBin, int sampleRateHz = 16000)
    {
        FftPassNumber = fftPassNumber;
        PeakMagnitude = peakMagnitude;
        CorrectedPeakFrequencyBin = correctedPeakFrequencyBin;
        SampleRateHz = sampleRateHz;
    }
}
