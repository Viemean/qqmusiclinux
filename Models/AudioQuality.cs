namespace QQMusic.Tui.Models;

public enum AudioQualityTier
{
    HiRes,
    SQ,
    HQ,
    Standard
}

public static class AudioQualityHelper
{
    public static string GetBadge(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.HiRes => "Hi-Res",
        AudioQualityTier.SQ => "SQ",
        AudioQualityTier.HQ => "HQ",
        AudioQualityTier.Standard => "标准",
        _ => "标准"
    };

    public static string GetPrefix(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.HiRes => "RS01",
        AudioQualityTier.SQ => "F000",
        AudioQualityTier.HQ => "M800",
        AudioQualityTier.Standard => "M500",
        _ => "M500"
    };

    public static string GetExtension(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.HiRes => ".flac",
        AudioQualityTier.SQ => ".flac",
        AudioQualityTier.HQ => ".mp3",
        AudioQualityTier.Standard => ".mp3",
        _ => ".mp3"
    };

    public static string GetDefaultSpec(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.HiRes => "24bit / 96kHz",
        AudioQualityTier.SQ => "16bit / 44.1kHz",
        AudioQualityTier.HQ => "320kbps",
        AudioQualityTier.Standard => "128kbps",
        _ => "128kbps"
    };

    public static AudioQualityTier Parse(string? name)
    {
        if (string.IsNullOrEmpty(name)) return AudioQualityTier.SQ;
        if (name.Contains("Hi-Res", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.HiRes;
        if (name.Contains("SQ", StringComparison.OrdinalIgnoreCase) || name.Contains("flac", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.SQ;
        if (name.Contains("HQ", StringComparison.OrdinalIgnoreCase) || name.Contains("320", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.HQ;
        return AudioQualityTier.Standard;
    }
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
