namespace QQMusic.Tui.Models;

public enum AudioQualityTier
{
    HiRes = 0,
    SQ = 1,
    HQ = 2,
    Standard = 3,
    Master = 4,
    Premium = 5,
    Atmos51 = 6,
    Atmos71 = 7,
    Dolby = 8
}

public static class AudioQualityHelper
{
    public static string GetBadge(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.Master => "母带",
        AudioQualityTier.Premium => "臻品",
        AudioQualityTier.Atmos51 => "5.1",
        AudioQualityTier.Atmos71 => "7.1",
        AudioQualityTier.Dolby => "杜比",
        AudioQualityTier.HiRes => "Hi-Res",
        AudioQualityTier.SQ => "SQ",
        AudioQualityTier.HQ => "HQ",
        AudioQualityTier.Standard => "标准",
        _ => "标准"
    };

    public static string GetPrefix(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.Master => "AI00",
        AudioQualityTier.Premium => "Q000",
        AudioQualityTier.Atmos51 => "Q001",
        AudioQualityTier.Atmos71 => "Q003",
        AudioQualityTier.Dolby => "D004",
        AudioQualityTier.HiRes => "RS01",
        AudioQualityTier.SQ => "F000",
        AudioQualityTier.HQ => "M800",
        AudioQualityTier.Standard => "M500",
        _ => "M500"
    };

    public static string GetExtension(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.Master => ".flac",
        AudioQualityTier.Premium => ".flac",
        AudioQualityTier.Atmos51 => ".flac",
        AudioQualityTier.Atmos71 => ".ogg",
        AudioQualityTier.Dolby => ".mp4",
        AudioQualityTier.HiRes => ".flac",
        AudioQualityTier.SQ => ".flac",
        AudioQualityTier.HQ => ".mp3",
        AudioQualityTier.Standard => ".mp3",
        _ => ".mp3"
    };

    public static string GetDefaultSpec(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.Master => "24bit / 192kHz",
        AudioQualityTier.Premium => "臻品音质",
        AudioQualityTier.Atmos51 => "5.1 声道",
        AudioQualityTier.Atmos71 => "7.1 全景声",
        AudioQualityTier.Dolby => "Dolby Atmos",
        AudioQualityTier.HiRes => "24bit / 96kHz",
        AudioQualityTier.SQ => "16bit / 44.1kHz",
        AudioQualityTier.HQ => "320kbps",
        AudioQualityTier.Standard => "128kbps",
        _ => "128kbps"
    };

    public static AudioQualityTier Parse(string? name)
    {
        if (string.IsNullOrEmpty(name)) return AudioQualityTier.SQ;
        if (name.Contains("母带", StringComparison.OrdinalIgnoreCase) || name.Contains("Master", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.Master;
        if (name.Contains("5.1", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.Atmos51;
        if (name.Contains("7.1", StringComparison.OrdinalIgnoreCase) || name.Contains("全景声", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.Atmos71;
        if (name.Contains("杜比", StringComparison.OrdinalIgnoreCase) || name.Contains("Dolby", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.Dolby;
        if (name.Contains("臻品", StringComparison.OrdinalIgnoreCase) || name.Contains("Premium", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.Premium;
        if (name.Contains("Hi-Res", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.HiRes;
        if (name.Contains("SQ", StringComparison.OrdinalIgnoreCase) || name.Contains("flac", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.SQ;
        if (name.Contains("HQ", StringComparison.OrdinalIgnoreCase) || name.Contains("320", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.HQ;
        return AudioQualityTier.Standard;
    }

    public static IReadOnlyList<AudioQualityTier> SelectionOrder { get; } =
    [
        AudioQualityTier.Master,
        AudioQualityTier.Premium,
        AudioQualityTier.Atmos51,
        AudioQualityTier.Atmos71,
        AudioQualityTier.Dolby,
        AudioQualityTier.HiRes,
        AudioQualityTier.SQ,
        AudioQualityTier.HQ,
        AudioQualityTier.Standard
    ];

    public static int GetSelectionIndex(AudioQualityTier tier)
    {
        for (var i = 0; i < SelectionOrder.Count; i++)
        {
            if (SelectionOrder[i] == tier) return i;
        }
        return -1;
    }

    public static IReadOnlyList<AudioQualityTier> GetFallbackTiers(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.Master => [AudioQualityTier.HiRes, AudioQualityTier.SQ, AudioQualityTier.HQ, AudioQualityTier.Standard],
        AudioQualityTier.Premium or AudioQualityTier.Atmos51 or AudioQualityTier.Atmos71 or AudioQualityTier.Dolby =>
            [AudioQualityTier.HiRes, AudioQualityTier.SQ, AudioQualityTier.HQ, AudioQualityTier.Standard],
        AudioQualityTier.HiRes => [AudioQualityTier.SQ, AudioQualityTier.HQ, AudioQualityTier.Standard],
        AudioQualityTier.SQ => [AudioQualityTier.HQ, AudioQualityTier.Standard],
        AudioQualityTier.HQ => [AudioQualityTier.Standard],
        _ => []
    };

    public static string GetQualityName(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.Master => "臻品母带",
        AudioQualityTier.Premium => "臻品音质",
        AudioQualityTier.Atmos51 => "臻品全景声 5.1",
        AudioQualityTier.Atmos71 => "臻品全景声 7.1",
        AudioQualityTier.Dolby => "杜比全景声",
        AudioQualityTier.HiRes => "Hi-Res",
        AudioQualityTier.SQ => "SQ 无损",
        AudioQualityTier.HQ => "HQ 高品质",
        _ => "标准音质"
    };
}

public record QualityOption(
    AudioQualityTier Tier,
    string Badge,
    string Name,
    string Spec,
    string BitrateInfo,
    bool Available,
    string? PlayUrl = null
)
{
    public string DisplayText(bool isCurrent)
    {
        var mark = isCurrent ? " ✓" : "";
        var detail = string.IsNullOrEmpty(BitrateInfo) ? Spec : $"{Spec} {{{BitrateInfo}}}";
        var status = Available ? detail : $"{Spec} (无音源)";
        return $"{Name,-6}  {status}{mark}";
    }
}
