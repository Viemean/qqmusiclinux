using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services.QqAudioRecognition;

/// <summary>
/// QQ 音乐官方优图听歌识曲响应实体
/// </summary>
public record QqMusicRecognizeResult(
    bool Success,
    string Title,
    string Artist,
    string Album,
    Song? Song = null,
    double OffsetSeconds = 0,
    string ErrorMessage = ""
);

/// <summary>
/// QAFP 指纹特征实体
/// </summary>
public record QafpFeature(
    byte[] Data,
    float Duration,
    int FeatureType = 0,
    float Confidence = 0.0f
);

/// <summary>
/// QQ 音乐官方优图听歌识曲 HTTP REST 客户端
/// </summary>
public static class QqMusicRecognizeClient
{
    private static readonly byte[] AesKey = Encoding.UTF8.GetBytes("spr_8a2cdeab7b81");
    private const string SignSalt = "spr_xiaomi_speed_androida45a1b";
    private const int QafpVersion = 201506;
    private const string Endpoint = "http://c.y.qq.com/youtu/humming/search";

    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    /// <summary>
    /// 使用 QAFP 特征直接向 QQ 音乐官方优图服务器发起识别请求
    /// </summary>
    public static async Task<QqMusicRecognizeResult> SearchAsync(QafpFeature feature, CancellationToken cancellationToken = default)
    {
        if (feature.Data == null || feature.Data.Length == 0)
        {
            return new QqMusicRecognizeResult(false, "", "", "", null, 0, "指纹特征数据为空");
        }

        try
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long sessionId = timestamp;
            int fpType = feature.FeatureType + 1;

            // 1. 生成时间戳签名 MD5
            string signStr = $"{SignSalt}{timestamp}";
            string veriStr = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(signStr)));

            // 2. 构造明文控制头 (以 \0 结尾)
            string header = $"v={QafpVersion}&source=spr_xiaomi_speed_android&time={timestamp}&veri_str={veriStr}&cmd=1&info={feature.Duration:F1},{feature.Data.Length},10306&type=0&session_id={sessionId}&feature_type={fpType}&confidence={feature.Confidence:F1}\0";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);

            // 3. 拼接 Payload: Header + FeatureBytes
            byte[] rawPayload = new byte[headerBytes.Length + feature.Data.Length];
            Buffer.BlockCopy(headerBytes, 0, rawPayload, 0, headerBytes.Length);
            Buffer.BlockCopy(feature.Data, 0, rawPayload, headerBytes.Length, feature.Data.Length);

            // 4. AES-ECB 加密
            byte[] encryptedBody;
            using (var aes = Aes.Create())
            {
                aes.Key = AesKey;
                aes.Mode = CipherMode.ECB;
                encryptedBody = aes.EncryptEcb(rawPayload, PaddingMode.PKCS7);
            }

            // 5. 构建 HTTP POST 请求
            string requestUrl = $"{Endpoint}?sessionid={sessionId}&recognizetype=1&fpType={fpType}";
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "MusicRecognition 34}(android 10)");
            request.Headers.TryAddWithoutValidation("AppId", "85");
            request.Headers.TryAddWithoutValidation("Cookie", "uin=; ct=3003; cv=10306; recognizetype=1");

            var content = new ByteArrayContent(encryptedBody);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
            request.Content = content;

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new QqMusicRecognizeResult(false, "", "", "", null, 0, $"HTTP 请求失败: {response.StatusCode}");
            }

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return ParseResponse(responseBytes);
        }
        catch (OperationCanceledException)
        {
            return new QqMusicRecognizeResult(false, "", "", "", null, 0, "识别请求已取消");
        }
        catch (Exception ex)
        {
            AppLogger.Force("QqMusicRecognizeClient", $"识别请求异常: {ex}");
            return new QqMusicRecognizeResult(false, "", "", "", null, 0, $"网络异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析服务器返回的 JSON 报文
    /// </summary>
    private static QqMusicRecognizeResult ParseResponse(byte[] jsonBytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;

            if (!root.TryGetProperty("ret", out var retProp) || retProp.GetInt32() != 0)
            {
                return new QqMusicRecognizeResult(false, "", "", "", null, 0, "服务器未命中歌曲特征");
            }

            // 提取时间偏移量 offset
            double offset = 0.0;
            if (root.TryGetProperty("results", out var resultsProp) && resultsProp.GetArrayLength() > 0)
            {
                var firstResult = resultsProp[0];
                if (firstResult.TryGetProperty("offset", out var offsetProp))
                {
                    if (offsetProp.ValueKind == JsonValueKind.String)
                    {
                        double.TryParse(offsetProp.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out offset);
                    }
                    else if (offsetProp.ValueKind == JsonValueKind.Number)
                    {
                        offset = offsetProp.GetDouble();
                    }
                }
            }

            // 提取歌曲元数据
            if (!root.TryGetProperty("songlist", out var songlistProp) || songlistProp.GetArrayLength() == 0)
            {
                return new QqMusicRecognizeResult(false, "", "", "", null, offset, "返回数据中未包含歌曲信息");
            }

            var songItem = songlistProp[0];
            string songMid = songItem.TryGetProperty("mid", out var midProp) ? midProp.GetString() ?? "" : "";
            string title = songItem.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(title) && songItem.TryGetProperty("name", out var nameProp))
            {
                title = nameProp.GetString() ?? "";
            }

            long id = songItem.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;
            int duration = songItem.TryGetProperty("interval", out var intProp) ? intProp.GetInt32() : 0;

            // 提取歌手
            string artist = "";
            var singers = new List<ArtistInfo>();
            if (songItem.TryGetProperty("singer", out var singerProp) && singerProp.ValueKind == JsonValueKind.Array)
            {
                var artistNames = new List<string>();
                foreach (var s in singerProp.EnumerateArray())
                {
                    string sName = s.TryGetProperty("name", out var snProp) ? snProp.GetString() ?? "" : "";
                    string sMid = s.TryGetProperty("mid", out var smProp) ? smProp.GetString() ?? "" : "";
                    long sId = s.TryGetProperty("id", out var sidProp) ? sidProp.GetInt64() : 0;
                    if (!string.IsNullOrEmpty(sName))
                    {
                        artistNames.Add(sName);
                        singers.Add(new ArtistInfo(sName, sMid, sId));
                    }
                }
                artist = string.Join(" / ", artistNames);
            }

            // 提取专辑
            string album = "";
            string albumMid = "";
            if (songItem.TryGetProperty("album", out var albumProp))
            {
                album = albumProp.TryGetProperty("name", out var anProp) ? anProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(album) && albumProp.TryGetProperty("title", out var atProp))
                {
                    album = atProp.GetString() ?? "";
                }
                albumMid = albumProp.TryGetProperty("mid", out var amProp) ? amProp.GetString() ?? "" : "";
            }

            // 提取 media_mid
            string mediaMid = "";
            if (songItem.TryGetProperty("file", out var fileProp) && fileProp.TryGetProperty("media_mid", out var mmProp))
            {
                mediaMid = mmProp.GetString() ?? "";
            }

            var song = new Song(
                Mid: songMid,
                Title: title,
                Artist: artist,
                Album: album,
                Duration: duration,
                MediaMid: mediaMid,
                Id: id,
                AlbumMid: albumMid
            )
            {
                Singers = singers
            };

            return new QqMusicRecognizeResult(true, title, artist, album, song, offset, "");
        }
        catch (Exception ex)
        {
            AppLogger.Force("QqMusicRecognizeClient", $"解析识别结果失败: {ex}");
            return new QqMusicRecognizeResult(false, "", "", "", null, 0, $"解析响应失败: {ex.Message}");
        }
    }
}
