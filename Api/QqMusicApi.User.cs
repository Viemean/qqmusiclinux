using System.Text;
using System.Text.Json;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Api;

public sealed partial class QqMusicApi
{
    public static async Task<bool> RefreshCurrentUserProfileAsync(CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn || string.IsNullOrWhiteSpace(UserSession.Current.MusicKey))
        {
            return false;
        }

        var uin = UserSession.Current.Uin;
        var escapedUin = JsonEncodedText.Encode(UserSession.Current.Uin).ToString();
        var escapedKey = JsonEncodedText.Encode(UserSession.Current.MusicKey).ToString();
        var loginType = GetCurrentLoginType();
        var payload = "{\"comm\":{\"ct\":11,\"cv\":14090008,\"v\":14090008,\"chid\":\"10003505\",\"tmeAppID\":\"qqmusic\",\"tmeLoginType\":" + loginType +
            ",\"qq\":\"" + escapedUin + "\",\"authst\":\"" + escapedKey + "\"}," +
            "\"profile\":{\"module\":\"music.UnifiedHomepage.UnifiedHomepageSrv\",\"method\":\"GetHomepageHeader\",\"param\":{\"uin\":\"" + escapedUin + "\",\"IsQueryTabDetail\":1}}," +
            "\"vip\":{\"module\":\"VipLogin.VipLoginInter\",\"method\":\"vip_login_base\",\"param\":{}}}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "QQMusic 14090008(android 14)");
            var cookieHeader = UserSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader)) req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

            using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var changed = false;
            if (TryGetResponseData(root, "profile", out var profileData) &&
                profileData.TryGetProperty("Info", out var info) &&
                info.TryGetProperty("BaseInfo", out var baseInfo))
            {
                changed |= SetString(baseInfo, "Name", value => UserSession.Current.Nick = value);
                changed |= SetString(baseInfo, "EncryptedUin", value => UserSession.Current.EncryptedUin = value);
                changed |= SetString(baseInfo, "Avatar", value => UserSession.Current.AvatarUrl = value);
            }

            if (TryGetResponseData(root, "vip", out var vipData))
            {
                if (vipData.TryGetProperty("identity", out var identity))
                {
                    var isVip = ReadInt(identity, "vip") > 0 || ReadInt(identity, "HugeVip") > 0 || ReadInt(vipData, "svip") > 0;
                    UserSession.Current.IsVip = isVip;
                    UserSession.Current.VipLevel = ReadInt(identity, "level");
                    UserSession.Current.VipExpireAt = ReadString(identity, "HugeVipEnd");
                    if (string.IsNullOrWhiteSpace(UserSession.Current.VipExpireAt))
                    {
                        UserSession.Current.VipExpireAt = ReadString(identity, "overdate");
                    }
                    changed = true;
                }
                if (vipData.TryGetProperty("userinfo", out var userInfo))
                {
                    UserSession.Current.MusicLevel = ReadInt(userInfo, "music_level");
                    changed = true;
                }
            }

            if (changed)
            {
                UserSession.Current.Save();
                AppLogger.Info("QqMusicApi", $"Account profile refreshed: {UserSession.Current.Nick}, VIP={UserSession.Current.IsVip}, VIP level={UserSession.Current.VipLevel}, music level={UserSession.Current.MusicLevel}");
            }
            return changed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("QqMusicApi", $"Failed to refresh account profile: {ex.Message}");
            return false;
        }
    }

    private static int GetCurrentLoginType()
    {
        if (UserSession.Current.Cookies.TryGetValue("tmeLoginType", out var loginType) && int.TryParse(loginType, out var parsed))
        {
            return parsed;
        }
        return UserSession.Current.MusicKey.StartsWith("W_X", StringComparison.Ordinal) ? 1 : 2;
    }

    private static bool TryGetResponseData(JsonElement root, string key, out JsonElement data)
    {
        data = default;
        return root.TryGetProperty(key, out var response) &&
               (!response.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || code.GetInt32() == 0) &&
               response.TryGetProperty("data", out data);
    }

    private static bool SetString(JsonElement source, string property, Action<string> setter)
    {
        var value = ReadString(source, property);
        if (string.IsNullOrWhiteSpace(value)) return false;
        setter(value);
        return true;
    }

    private static string ReadString(JsonElement source, string property)
    {
        if (!source.TryGetProperty(property, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
    }

    private static int ReadInt(JsonElement source, string property)
    {
        if (!source.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : 0;
    }
}
