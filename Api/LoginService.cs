using System.Net;
using System.Text.RegularExpressions;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Api;

public sealed partial class LoginService
{
    private static readonly HttpClientHandler s_handler = new()
    {
        UseCookies = false,
        AllowAutoRedirect = false
    };

    private static readonly HttpClient s_http = new(s_handler)
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly string s_loginReferer = "https://xui.ptlogin2.qq.com/cgi-bin/xlogin?appid=716027609&daid=383&style=33&login_text=%E7%99%BB%E5%BD%95&hide_title_bar=1&hide_border=1&target=self&s_url=https%3A%2F%2Fgraph.qq.com%2Foauth2.0%2Flogin_jump&pt_3rd_aid=100497308";

    static LoginService()
    {
        s_http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        s_http.DefaultRequestHeaders.Referrer = new Uri(s_loginReferer);
    }

    public record QrCodeResult(byte[] PngBytes, List<string> AsciiLines, string QrSig, int PtqrToken);

    public record PollStatus(int Code, string Message, string? RedirectUrl, string? Nick);

    /// <summary>
    /// 获取 QQ 扫码登录二维码
    /// </summary>
    public static async Task<QrCodeResult?> FetchQrCodeAsync(CancellationToken ct = default)
    {
        try
        {
            var random = Random.Shared.NextDouble();
            var url = $"https://ssl.ptlogin2.qq.com/ptqrshow?appid=716027609&e=2&l=M&s=3&d=72&v=4&t={random:F6}&daid=383&pt_3rd_aid=100497308";
            AppLogger.Info("LoginService", $"Fetching QR code from {url}");

            using var resp = await s_http.GetAsync(url, ct).ConfigureAwait(false);
            AppLogger.Debug("LoginService", $"FetchQrCode response status: {resp.StatusCode}");

            if (!resp.IsSuccessStatusCode)
            {
                AppLogger.Error("LoginService", $"FetchQrCode failed with HTTP status {resp.StatusCode}");
                return null;
            }

            string qrsig = "";
            if (resp.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var c in cookies)
                {
                    AppLogger.Debug("LoginService", $"FetchQrCode Set-Cookie: {c}");
                    var match = QrSigRegex().Match(c);
                    if (match.Success)
                    {
                        qrsig = match.Groups[1].Value;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(qrsig))
            {
                AppLogger.Error("LoginService", "Failed to find qrsig in Set-Cookie headers!");
            }
            else
            {
                AppLogger.Info("LoginService", $"Extracted qrsig length: {qrsig.Length}");
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            AppLogger.Debug("LoginService", $"Received PNG bytes length: {bytes.Length}");

            // 保存一份到 /tmp 供外部图像查看器打开
            try
            {
                await File.WriteAllBytesAsync("/tmp/qqmusic_login_qr.png", bytes, ct).ConfigureAwait(false);
                AppLogger.Info("LoginService", "Saved QR image to /tmp/qqmusic_login_qr.png");
            }
            catch (Exception ex)
            {
                AppLogger.Error("LoginService", "Error saving /tmp/qqmusic_login_qr.png", ex);
            }

            var asciiLines = PngQrReader.DecodePngToBlockText(bytes);
            int ptqrToken = HashPtqrToken(qrsig);
            AppLogger.Info("LoginService", $"Calculated ptqrToken: {ptqrToken}");

            return new QrCodeResult(bytes, asciiLines, qrsig, ptqrToken);
        }
        catch (Exception ex)
        {
            AppLogger.Error("LoginService", "FetchQrCodeAsync exception", ex);
            return null;
        }
    }

    /// <summary>
    /// 轮询二维码扫码状态
    /// </summary>
    public static async Task<PollStatus> PollQrStatusAsync(string qrsig, int ptqrToken, CancellationToken ct = default)
    {
        try
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var url = $"https://ssl.ptlogin2.qq.com/ptqrlogin?u1=https%3A%2F%2Fgraph.qq.com%2Foauth2.0%2Flogin_jump&ptqrtoken={ptqrToken}&ptredirect=0&h=1&t=1&g=1&from_ui=1&ptlang=2052&action=0-0-{ts}&js_ver=240905&js_type=1&login_sig=&pt_uistyle=40&aid=716027609&daid=383&pt_3rd_aid=100497308";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Cookie", $"qrsig={qrsig};");
            req.Headers.Referrer = new Uri(s_loginReferer);

            using var resp = await s_http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            AppLogger.Debug("LoginService", $"Poll HTTP status: {resp.StatusCode}, raw: {text.Trim()}");

            // 使用超高容错单引号匹配解析 ptuiCB 参数
            var matches = SingleQuoteRegex().Matches(text);
            if (matches.Count < 5)
            {
                AppLogger.Error("LoginService", $"Cannot parse ptuiCB, matches count: {matches.Count}, text: {text}");
                return new PollStatus(-1, "响应解析异常", null, null);
            }

            var codeStr = matches[0].Groups[1].Value;
            var redirectUrl = matches[2].Groups[1].Value;
            var msg = matches[4].Groups[1].Value;
            var nick = matches.Count >= 6 ? matches[5].Groups[1].Value : "";

            int code = int.TryParse(codeStr, out var c) ? c : -1;
            AppLogger.Info("LoginService", $"Parsed poll status - Code: {code}, Msg: '{msg}', Nick: '{nick}', RedirectUrl: '{redirectUrl}'");

            if (code == 0)
            {
                AppLogger.Info("LoginService", "QR scan confirmed! Proceeding to exchange cookies via check_sig...");
                await ExchangeCheckSigCookiesAsync(redirectUrl, qrsig, nick, ct).ConfigureAwait(false);
            }

            return new PollStatus(code, msg, redirectUrl, nick);
        }
        catch (OperationCanceledException)
        {
            AppLogger.Debug("LoginService", "PollQrStatus canceled");
            return new PollStatus(-999, "操作已取消", null, null);
        }
        catch (Exception ex)
        {
            AppLogger.Error("LoginService", "PollQrStatusAsync exception", ex);
            return new PollStatus(-1, $"请求失败: {ex.Message}", null, null);
        }
    }

    /// <summary>
    /// 请求 check_sig 换取最终登录 Session Cookie
    /// </summary>
    private static async Task ExchangeCheckSigCookiesAsync(string redirectUrl, string qrsig, string nick, CancellationToken ct)
    {
        var cookieDict = new Dictionary<string, string>();
        string uin = "";

        // 1. 尝试从 redirectUrl 的 query 参数中直接提取 uin
        if (!string.IsNullOrEmpty(redirectUrl))
        {
            var uinMatch = UinQueryRegex().Match(redirectUrl);
            if (uinMatch.Success)
            {
                uin = uinMatch.Groups[1].Value.TrimStart('o');
                AppLogger.Info("LoginService", $"Extracted uin from redirectUrl query: {uin}");
            }
        }

        // 2. 发起 check_sig 请求
        if (!string.IsNullOrEmpty(redirectUrl))
        {
            try
            {
                AppLogger.Info("LoginService", $"Requesting check_sig URL: {redirectUrl}");
                using var checkReq = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
                checkReq.Headers.Add("Cookie", $"qrsig={qrsig};");

                using var checkResp = await s_http.SendAsync(checkReq, ct).ConfigureAwait(false);
                AppLogger.Info("LoginService", $"check_sig response status: {checkResp.StatusCode}");

                if (checkResp.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var sc in setCookies)
                    {
                        AppLogger.Debug("LoginService", $"check_sig Set-Cookie: {sc}");
                        if (sc.Contains("1970 00:00:00") || sc.Contains("Max-Age=0")) continue;

                        var parts = sc.Split(';')[0].Split('=', 2);
                        if (parts.Length == 2)
                        {
                            var k = parts[0].Trim();
                            var v = parts[1].Trim();
                            if (!string.IsNullOrEmpty(v))
                            {
                                cookieDict[k] = v;
                                if (k.Equals("uin", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(uin))
                                {
                                    uin = v.TrimStart('o');
                                }
                            }
                        }
                    }
                }

                // 尝试跟进 302 跳转提取更多凭据
                if (checkResp.Headers.Location != null)
                {
                    var jumpUrl = checkResp.Headers.Location.ToString();
                    AppLogger.Info("LoginService", $"Following check_sig redirect: {jumpUrl}");
                    var cookieHeader = string.Join("; ", cookieDict.Select(kv => $"{kv.Key}={kv.Value}"));
                    using var jumpReq = new HttpRequestMessage(HttpMethod.Get, jumpUrl);
                    jumpReq.Headers.Add("Cookie", cookieHeader);
                    using var jumpResp = await s_http.SendAsync(jumpReq, ct).ConfigureAwait(false);
                    if (jumpResp.Headers.TryGetValues("Set-Cookie", out var jumpCookies))
                    {
                        foreach (var sc in jumpCookies)
                        {
                            if (sc.Contains("1970 00:00:00") || sc.Contains("Max-Age=0")) continue;
                            var parts = sc.Split(';')[0].Split('=', 2);
                            if (parts.Length == 2 && !string.IsNullOrEmpty(parts[1].Trim()))
                            {
                                cookieDict[parts[0].Trim()] = parts[1].Trim();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("LoginService", "Exception during check_sig exchange", ex);
            }
        }

        // 提取 skey / p_skey
        string skey = cookieDict.TryGetValue("skey", out var s) ? s : "";
        if (string.IsNullOrEmpty(skey) && cookieDict.TryGetValue("p_skey", out var ps))
        {
            skey = ps;
        }

        // 补充 QQ 音乐必要 Cookie
        if (!string.IsNullOrEmpty(uin))
        {
            cookieDict["uin"] = uin;
            cookieDict["qqmusic_uin"] = uin;
        }
        if (!string.IsNullOrEmpty(skey))
        {
            cookieDict["qqmusic_key"] = skey;
        }

        AppLogger.Info("LoginService", $"Finalizing Base Session -> Uin: '{uin}', Nick: '{nick}', skey length: {skey.Length}, total cookies: {cookieDict.Count}");

        // 写入当前基础会话
        UserSession.Current.Uin = uin;
        UserSession.Current.Nick = string.IsNullOrWhiteSpace(nick) ? (string.IsNullOrEmpty(uin) ? "已登录用户" : uin) : nick;
        UserSession.Current.MusicKey = skey;
        UserSession.Current.Cookies = cookieDict;
        UserSession.Current.IsVip = false;
        UserSession.Current.Save();

        // 自动触发第二阶段：向 QQ 互联申请 OAuth2 Code 并向 QQ 音乐换取官方 musickey 完整凭据
        AppLogger.Info("LoginService", "Starting Phase 2: Automatically exchanging OAuth2 Code for full QQ Music VIP musickey...");
        await ExchangeMusicKeyByOAuthAsync(cookieDict, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 确保当前登录会话拥有官方专属 musickey (qm_keyst)，若缺失则自动通过 p_skey 换票
    /// </summary>
    public static async Task<bool> EnsureMusicKeyAsync(CancellationToken ct = default)
    {
        if (UserSession.Current.Cookies.TryGetValue("qm_keyst", out var mk) && !string.IsNullOrEmpty(mk))
        {
            return true;
        }

        if (UserSession.Current.Cookies.TryGetValue("p_skey", out var pskey) && !string.IsNullOrEmpty(pskey))
        {
            AppLogger.Info("LoginService", "EnsureMusicKeyAsync: Detected missing qm_keyst, attempting automatic OAuth2 exchange...");
            return await ExchangeMusicKeyByOAuthAsync(UserSession.Current.Cookies, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// 第二阶段：自动通过 OAuth2 换取 QQ 音乐官方专属 musickey 完整 VIP Cookie
    /// </summary>
    public static async Task<bool> ExchangeMusicKeyByOAuthAsync(Dictionary<string, string> cookieDict, CancellationToken ct = default)
    {
        try
        {
            if (!cookieDict.TryGetValue("p_skey", out var p_skey) || string.IsNullOrEmpty(p_skey))
            {
                AppLogger.Error("LoginService", "ExchangeMusicKeyByOAuthAsync: p_skey not found in cookieDict");
                return false;
            }

            var gtk = GetACSRFToken(p_skey);
            AppLogger.Info("LoginService", $"Calculated g_tk: {gtk} for p_skey");

            // 1. POST https://graph.qq.com/oauth2.0/authorize
            var authUrl = "https://graph.qq.com/oauth2.0/authorize";
            using var authReq = new HttpRequestMessage(HttpMethod.Post, authUrl);

            var cookieHeader = string.Join("; ", cookieDict.Select(kv => $"{kv.Key}={kv.Value}"));
            authReq.Headers.Add("Cookie", cookieHeader);
            authReq.Headers.Referrer = new Uri("https://graph.qq.com/oauth2.0/show?which=Login&display=pc&response_type=code&client_id=100497308&redirect_uri=https%3A%2F%2Fy.qq.com%2Fwk_v17%2Fcommon_login.html%3Ftype%3DQQ%26%26redirect%3D&state=y_new.top.pop.logout&display=pc&scope=get_user_info");

            var formData = new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = "100497308",
                ["redirect_uri"] = "https://y.qq.com/wk_v17/common_login.html?type=QQ&&redirect=",
                ["scope"] = "get_user_info",
                ["state"] = "y_new.top.pop.logout",
                ["switch"] = "",
                ["from_ptlogin"] = "1",
                ["src"] = "1",
                ["update_auth"] = "1",
                ["openapi"] = "8090_1010_1030_1050",
                ["g_tk"] = gtk.ToString()
            };
            authReq.Content = new FormUrlEncodedContent(formData);

            using var authResp = await s_http.SendAsync(authReq, ct).ConfigureAwait(false);
            AppLogger.Info("LoginService", $"OAuth authorize response status: {authResp.StatusCode}");

            string code = "";
            if (authResp.Headers.Location != null)
            {
                var loc = authResp.Headers.Location.ToString();
                AppLogger.Info("LoginService", $"OAuth authorize redirect location: {loc}");
                var codeMatch = CodeQueryRegex().Match(loc);
                if (codeMatch.Success)
                {
                    code = codeMatch.Groups[1].Value;
                }
            }
            else
            {
                var body = await authResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                AppLogger.Debug("LoginService", $"OAuth authorize body: {(body.Length > 200 ? body[..200] : body)}");
                var codeMatch = CodeQueryRegex().Match(body);
                if (codeMatch.Success)
                {
                    code = codeMatch.Groups[1].Value;
                }
            }

            if (string.IsNullOrEmpty(code))
            {
                AppLogger.Error("LoginService", "Failed to obtain OAuth code from authorize endpoint");
                return false;
            }

            AppLogger.Info("LoginService", $"Successfully captured OAuth code: {code}");

            // 2. 调用 u.y.qq.com 官方接口换取 QQ 音乐专属 musickey
            var musicLoginUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";
            var payload = $"{{\"comm\":{{\"ct\":19,\"cv\":1,\"tmeLoginType\":\"1\"}},\"login\":{{\"module\":\"QQConnectLogin.LoginServer\",\"method\":\"QQLogin\",\"param\":{{\"onlyNeedAccessToken\":0,\"forceRefreshToken\":0,\"appid\":100497308,\"code\":\"{code}\"}}}}}}";

            using var loginReq = new HttpRequestMessage(HttpMethod.Post, musicLoginUrl);
            loginReq.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var loginResp = await s_http.SendAsync(loginReq, ct).ConfigureAwait(false);
            var loginRespJson = await loginResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            AppLogger.Info("LoginService", $"QQLogin response: {loginRespJson}");

            using var doc = System.Text.Json.JsonDocument.Parse(loginRespJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("login", out var loginObj) &&
                loginObj.TryGetProperty("data", out var loginData))
            {
                string musicid = "";
                if (loginData.TryGetProperty("str_musicid", out var smid))
                {
                    musicid = smid.GetString() ?? "";
                }
                else if (loginData.TryGetProperty("musicid", out var mid))
                {
                    musicid = mid.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? mid.GetInt64().ToString()
                        : (mid.GetString() ?? "");
                }

                var musickey = loginData.TryGetProperty("musickey", out var mk) && mk.ValueKind == System.Text.Json.JsonValueKind.String
                    ? mk.GetString() ?? "" : "";
                var openid = loginData.TryGetProperty("openid", out var op) && op.ValueKind == System.Text.Json.JsonValueKind.String
                    ? op.GetString() ?? "" : "";
                var accessToken = loginData.TryGetProperty("access_token", out var at) && at.ValueKind == System.Text.Json.JsonValueKind.String
                    ? at.GetString() ?? "" : "";
                var unionid = loginData.TryGetProperty("unionid", out var un) && un.ValueKind == System.Text.Json.JsonValueKind.String
                    ? un.GetString() ?? "" : "";

                AppLogger.Info("LoginService", $"Obtained QQ Music official credentials: musicid={musicid}, musickey length={musickey.Length}");

                if (!string.IsNullOrEmpty(musickey))
                {
                    cookieDict["musicid"] = musicid;
                    cookieDict["uin"] = musicid;
                    cookieDict["qqmusic_uin"] = musicid;
                    cookieDict["qqmusic_key"] = musickey;
                    cookieDict["qm_keyst"] = musickey;
                    cookieDict["qqmusic_version"] = "17";
                    cookieDict["qqmusic_miniversion"] = "70";
                    cookieDict["tmeLoginType"] = "1";
                    if (!string.IsNullOrEmpty(openid)) cookieDict["psrf_qqopenid"] = openid;
                    if (!string.IsNullOrEmpty(accessToken)) cookieDict["psrf_qqaccess_token"] = accessToken;
                    if (!string.IsNullOrEmpty(unionid)) cookieDict["psrf_qqunionid"] = unionid;

                    UserSession.Current.Uin = musicid;
                    UserSession.Current.MusicKey = musickey;
                    UserSession.Current.IsVip = true;
                    UserSession.Current.Cookies = cookieDict;
                    UserSession.Current.Save();
                    AppLogger.Info("LoginService", $"Full QQ Music VIP cookies successfully stored to UserSession! Total cookies: {cookieDict.Count}");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("LoginService", "Exception in ExchangeMusicKeyByOAuthAsync", ex);
        }
        return false;
    }

    public static int GetACSRFToken(string p_skey)
    {
        var hash = 5381;
        for (int i = 0; i < p_skey.Length; i++)
        {
            hash += (hash << 5) + (int)p_skey[i];
        }
        return hash & 0x7fffffff;
    }

    /// <summary>
    /// 手动导入 Cookie 字符串
    /// </summary>
    public static bool ImportCookieString(string rawCookie)
    {
        if (string.IsNullOrWhiteSpace(rawCookie)) return false;

        AppLogger.Info("LoginService", $"Importing raw cookie (length: {rawCookie.Length})");
        var dict = new Dictionary<string, string>();
        var pairs = rawCookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var p in pairs)
        {
            var idx = p.IndexOf('=');
            if (idx > 0)
            {
                var k = p[..idx].Trim();
                var v = p[(idx + 1)..].Trim();
                dict[k] = v;
            }
        }

        string uin = "";
        if (dict.TryGetValue("uin", out var u)) uin = u.TrimStart('o');
        else if (dict.TryGetValue("qqmusic_uin", out var qu)) uin = qu.TrimStart('o');
        else if (dict.TryGetValue("musicid", out var mu)) uin = mu;

        string key = "";
        if (dict.TryGetValue("qqmusic_key", out var qk)) key = qk;
        else if (dict.TryGetValue("skey", out var sk)) key = sk;
        else if (dict.TryGetValue("qm_keyst", out var qmk)) key = qmk;

        if (string.IsNullOrEmpty(uin) && string.IsNullOrEmpty(key))
        {
            AppLogger.Error("LoginService", "ImportCookieString failed: neither uin nor key found in cookie string");
            return false;
        }

        UserSession.Current.Uin = uin;
        UserSession.Current.Nick = $"用户_{uin}";
        UserSession.Current.MusicKey = key;
        UserSession.Current.Cookies = dict;
        UserSession.Current.Save();

        AppLogger.Info("LoginService", $"Cookie imported successfully: Uin={uin}");
        return true;
    }

    public static void Logout()
    {
        AppLogger.Info("LoginService", "User requested logout");
        UserSession.Current.Clear();
    }

    public static int HashPtqrToken(string qrsig)
    {
        int e = 0;
        for (int i = 0; i < qrsig.Length; i++)
        {
            e += (e << 5) + (int)qrsig[i];
        }
        return 2147483647 & e;
    }

    [GeneratedRegex(@"qrsig=([^;]+)")]
    private static partial Regex QrSigRegex();

    [GeneratedRegex(@"'([^']*)'")]
    private static partial Regex SingleQuoteRegex();

    [GeneratedRegex(@"[?&]uin=([^&]+)")]
    private static partial Regex UinQueryRegex();

    [GeneratedRegex(@"[?&]code=([^&#\s""']+)")]
    private static partial Regex CodeQueryRegex();
}
