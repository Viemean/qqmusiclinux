using System.Text.Json;

namespace QQMusic.Tui.Models;

public sealed class UserSession
{
    private static readonly string s_configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "qqmusic-tui"
    );
    private static readonly string s_configPath = Path.Combine(s_configDir, "session.json");

    public static UserSession Current { get; private set; } = new();

    public bool IsLoggedIn => !string.IsNullOrEmpty(Uin);

    public string Uin { get; set; } = "";
    public string Nick { get; set; } = "";
    public bool IsVip { get; set; } = false;
    public string MusicKey { get; set; } = "";
    public string EncryptedUin { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public int VipLevel { get; set; } = 0;
    public int MusicLevel { get; set; } = 0;
    public string VipExpireAt { get; set; } = "";
    public string PreferredQuality { get; set; } = "SQ";
    public int Volume { get; set; } = 80;
    public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.ListLoop;
    public double LastPlaybackPositionSeconds { get; set; } = 0;
    public Song? LastPlayedSong { get; set; } = null;
    public Dictionary<string, string> Cookies { get; set; } = [];
    public HashSet<string> FavoriteSingers { get; set; } = [];

    public static void Load()
    {
        try
        {
            if (!File.Exists(s_configPath)) return;

            var json = File.ReadAllText(s_configPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var session = new UserSession();
            if (root.TryGetProperty("uin", out var u)) session.Uin = u.GetString() ?? "";
            if (root.TryGetProperty("nick", out var n)) session.Nick = n.GetString() ?? "";
            if (root.TryGetProperty("is_vip", out var v)) session.IsVip = v.GetBoolean();
            if (root.TryGetProperty("music_key", out var k)) session.MusicKey = k.GetString() ?? "";
            if (root.TryGetProperty("preferred_quality", out var q)) session.PreferredQuality = q.GetString() ?? "SQ";
            if (root.TryGetProperty("encrypted_uin", out var eu)) session.EncryptedUin = eu.GetString() ?? "";
            if (root.TryGetProperty("avatar_url", out var avatar)) session.AvatarUrl = avatar.GetString() ?? "";
            if (root.TryGetProperty("vip_level", out var vipLevel) && vipLevel.TryGetInt32(out int vipLevelValue)) session.VipLevel = vipLevelValue;
            if (root.TryGetProperty("music_level", out var musicLevel) && musicLevel.TryGetInt32(out int musicLevelValue)) session.MusicLevel = musicLevelValue;
            if (root.TryGetProperty("vip_expire_at", out var vipExpire)) session.VipExpireAt = vipExpire.GetString() ?? "";
            if (root.TryGetProperty("volume", out var volProp) && volProp.TryGetInt32(out int vVal))
            {
                session.Volume = Math.Clamp(vVal, 0, 100);
            }
            if (root.TryGetProperty("playback_mode", out var pmProp))
            {
                var pmStr = pmProp.GetString();
                if (Enum.TryParse<PlaybackMode>(pmStr, true, out var parsedMode))
                {
                    session.PlaybackMode = parsedMode;
                }
            }
            if (root.TryGetProperty("last_position", out var posProp) && posProp.TryGetDouble(out double pVal))
            {
                session.LastPlaybackPositionSeconds = Math.Max(0, pVal);
            }
            if (root.TryGetProperty("last_song", out var songProp) && songProp.ValueKind == JsonValueKind.Object)
            {
                string sMid = songProp.TryGetProperty("mid", out var sm) ? sm.GetString() ?? "" : "";
                string sTitle = songProp.TryGetProperty("title", out var st) ? st.GetString() ?? "" : "";
                string sArtist = songProp.TryGetProperty("artist", out var sa) ? sa.GetString() ?? "" : "";
                string sAlbum = songProp.TryGetProperty("album", out var sal) ? sal.GetString() ?? "" : "";
                int sDuration = songProp.TryGetProperty("duration", out var sd) ? sd.GetInt32() : 0;
                string sMediaMid = songProp.TryGetProperty("media_mid", out var smm) ? smm.GetString() ?? "" : "";
                long sId = songProp.TryGetProperty("id", out var si) ? si.GetInt64() : 0;

                if (!string.IsNullOrEmpty(sMid) && !string.IsNullOrEmpty(sTitle))
                {
                    session.LastPlayedSong = new Song(sMid, sTitle, sArtist, sAlbum, sDuration, sMediaMid, sId);
                }
            }

            if (root.TryGetProperty("cookies", out var cObj) && cObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in cObj.EnumerateObject())
                {
                    session.Cookies[prop.Name] = prop.Value.GetString() ?? "";
                }
            }

            if (root.TryGetProperty("favorite_singers", out var sArr) && sArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in sArr.EnumerateArray())
                {
                    var mid = elem.GetString();
                    if (!string.IsNullOrEmpty(mid)) session.FavoriteSingers.Add(mid);
                }
            }

            Current = session;
        }
        catch
        {
            Current = new UserSession();
        }
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(s_configDir))
            {
                Directory.CreateDirectory(s_configDir);
            }

            var cookiePairs = new List<string>();
            foreach (var kvp in Cookies)
            {
                cookiePairs.Add($"\"{JsonEscape(kvp.Key)}\":\"{JsonEscape(kvp.Value)}\"");
            }

            string lastSongJson = "null";
            if (LastPlayedSong != null)
            {
                lastSongJson = "{" +
                    $"\"mid\":\"{JsonEscape(LastPlayedSong.Mid)}\"," +
                    $"\"title\":\"{JsonEscape(LastPlayedSong.Title)}\"," +
                    $"\"artist\":\"{JsonEscape(LastPlayedSong.Artist)}\"," +
                    $"\"album\":\"{JsonEscape(LastPlayedSong.Album)}\"," +
                    $"\"duration\":{LastPlayedSong.Duration}," +
                    $"\"media_mid\":\"{JsonEscape(LastPlayedSong.MediaMid)}\"," +
                    $"\"id\":{LastPlayedSong.Id}" +
                    "}";
            }

            var singerList = new List<string>();
            foreach (var smid in FavoriteSingers)
            {
                singerList.Add($"\"{JsonEscape(smid)}\"");
            }

            var json = $"{{" +
                $"\"uin\":\"{JsonEscape(Uin)}\"," +
                $"\"nick\":\"{JsonEscape(Nick)}\"," +
                $"\"is_vip\":{(IsVip ? "true" : "false")}," +
                $"\"music_key\":\"{JsonEscape(MusicKey)}\"," +
                $"\"preferred_quality\":\"{JsonEscape(PreferredQuality)}\"," +
                $"\"encrypted_uin\":\"{JsonEscape(EncryptedUin)}\"," +
                $"\"avatar_url\":\"{JsonEscape(AvatarUrl)}\"," +
                $"\"vip_level\":{VipLevel}," +
                $"\"music_level\":{MusicLevel}," +
                $"\"vip_expire_at\":\"{JsonEscape(VipExpireAt)}\"," +
                $"\"volume\":{Volume}," +
                $"\"playback_mode\":\"{PlaybackMode}\"," +
                $"\"last_position\":{LastPlaybackPositionSeconds:F2}," +
                $"\"last_song\":{lastSongJson}," +
                $"\"cookies\":{{{string.Join(",", cookiePairs)}}}," +
                $"\"favorite_singers\":[{string.Join(",", singerList)}]" +
                $"}}";

            File.WriteAllText(s_configPath, json);
        }
        catch
        {
            // 忽略写入异常
        }
    }

    public void Clear()
    {
        Uin = "";
        Nick = "";
        IsVip = false;
        MusicKey = "";
        EncryptedUin = "";
        AvatarUrl = "";
        VipLevel = 0;
        MusicLevel = 0;
        VipExpireAt = "";
        Cookies.Clear();

        try
        {
            if (File.Exists(s_configPath))
            {
                File.Delete(s_configPath);
            }
        }
        catch
        {
        }
    }

    public string GetCookieHeader()
    {
        if (Cookies.Count == 0)
        {
            if (!string.IsNullOrEmpty(Uin))
            {
                return $"uin={Uin}; qqmusic_uin={Uin}; qqmusic_key={MusicKey}; qm_keyst={MusicKey};";
            }
            return "";
        }

        var sb = new System.Text.StringBuilder();
        foreach (var kv in Cookies)
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append("; ");
        }

        if (!Cookies.ContainsKey("qqmusic_uin") && !string.IsNullOrEmpty(Uin))
        {
            sb.Append("qqmusic_uin=").Append(Uin).Append("; ");
        }
        if (!Cookies.ContainsKey("qqmusic_key") && !string.IsNullOrEmpty(MusicKey))
        {
            sb.Append("qqmusic_key=").Append(MusicKey).Append("; ");
        }
        if (!Cookies.ContainsKey("qm_keyst") && !string.IsNullOrEmpty(MusicKey))
        {
            sb.Append("qm_keyst=").Append(MusicKey).Append("; ");
        }
        return sb.ToString();
    }

    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
