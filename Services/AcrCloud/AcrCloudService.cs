using System.Buffers.Binary;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QQMusic.Tui.Services.AcrCloud;

/// <summary>
/// ACRCloud 增强音频识别服务 (专注于同人、ACG、日漫原声带及小众音乐识别)
/// </summary>
public static class AcrCloudService
{
    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    /// <summary>
    /// 使用 ACRCloud 识别内存 PCM 样本
    /// </summary>
    public static async Task<(bool Success, string Title, string Artist, string Album, string Error)> RecognizePcmSamplesAsync(
        ReadOnlyMemory<short> pcmSamples,
        CancellationToken cancellationToken = default)
    {
        var config = AcrCloudConfig.Current;
        if (!config.IsConfigured)
        {
            return (false, "", "", "", "未配置 ACRCloud 密钥");
        }

        if (pcmSamples.Length < 16000 * 2)
        {
            return (false, "", "", "", "音频样本过短");
        }

        try
        {
            // 封装标准的 44 字节 WAV 格式供 ACRCloud 识别采样率与声道
            byte[] wavBytes = BuildWavData(pcmSamples.Span, 16000, 1);

            string host = string.IsNullOrWhiteSpace(config.Host) ? "identify-cn-north-1.acrcloud.cn" : config.Host.Trim();
            if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                host = "https://" + host;
            }
            string requestUrl = $"{host.TrimEnd('/')}/v1/identify";

            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            string stringToSign = $"POST\n/v1/identify\n{config.AccessKey}\naudio\n1\n{timestamp}";

            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(config.AccessSecret));
            string signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

            using var form = new MultipartFormDataContent();
            void AddField(string name, string val)
            {
                var c = new ByteArrayContent(Encoding.UTF8.GetBytes(val));
                c.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
                {
                    Name = $"\"{name}\""
                };
                c.Headers.ContentType = null;
                form.Add(c);
            }

            AddField("access_key", config.AccessKey);
            AddField("data_type", "audio");
            AddField("signature_version", "1");
            AddField("signature", signature);
            AddField("timestamp", timestamp);
            AddField("sample_bytes", wavBytes.Length.ToString());

            var audioContent = new ByteArrayContent(wavBytes);
            audioContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
            {
                Name = "\"sample\"",
                FileName = "\"sample.wav\""
            };
            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            form.Add(audioContent);

            using var response = await s_httpClient.PostAsync(requestUrl, form, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (false, "", "", "", $"ACRCloud HTTP 错误: {(int)response.StatusCode}");
            }

            var respJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(respJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) &&
                status.TryGetProperty("code", out var codeProp))
            {
                int code = codeProp.GetInt32();
                if (code == 0)
                {
                    if (root.TryGetProperty("metadata", out var metadata) &&
                        metadata.TryGetProperty("music", out var musicArr) &&
                        musicArr.ValueKind == JsonValueKind.Array && musicArr.GetArrayLength() > 0)
                    {
                        var firstMusic = musicArr[0];

                        // 置信度阈值判定 (低于 60 视为弱特征/无效噪声匹配)
                        int score = firstMusic.TryGetProperty("score", out var sProp) && sProp.TryGetInt32(out var sVal) ? sVal : 100;
                        if (score < 60)
                        {
                            return (false, "", "", "", "未能匹配到对应歌曲");
                        }

                        var title = firstMusic.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "" : "";
                        var album = "";
                        if (firstMusic.TryGetProperty("album", out var alObj) && alObj.TryGetProperty("name", out var alName))
                        {
                            album = alName.GetString() ?? "";
                        }

                        var artists = new List<string>();
                        if (firstMusic.TryGetProperty("artists", out var artArr) && artArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var art in artArr.EnumerateArray())
                            {
                                if (art.TryGetProperty("name", out var n))
                                {
                                    var nameStr = n.GetString();
                                    if (!string.IsNullOrEmpty(nameStr)) artists.Add(nameStr);
                                }
                            }
                        }
                        string artist = artists.Count > 0 ? string.Join("/", artists) : "";

                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            return (true, title, artist, album, "");
                        }
                    }
                }
                else
                {
                    string msg = status.TryGetProperty("msg", out var msgProp) ? msgProp.GetString() ?? "" : "";
                    return (false, "", "", "", string.IsNullOrWhiteSpace(msg) ? $"ACRCloud 错误 ({code})" : $"ACRCloud: {msg}");
                }
            }

            return (false, "", "", "", "未能匹配到对应歌曲");
        }
        catch (OperationCanceledException)
        {
            return (false, "", "", "", "识别已取消");
        }
        catch (Exception ex)
        {
            return (false, "", "", "", $"ACRCloud 识别异常: {ex.Message}");
        }
    }

    private static byte[] BuildWavData(ReadOnlySpan<short> pcmSamples, int sampleRate, short channels)
    {
        int byteRate = sampleRate * channels * 2;
        short blockAlign = (short)(channels * 2);
        int pcmLength = pcmSamples.Length * 2;
        byte[] wavBytes = new byte[44 + pcmLength];

        Encoding.ASCII.GetBytes("RIFF").CopyTo(wavBytes.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(4, 4), 36 + pcmLength);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wavBytes.AsSpan(8, 4));
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wavBytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(28, 4), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(32, 2), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wavBytes.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(40, 4), pcmLength);

        for (int i = 0; i < pcmSamples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(44 + i * 2, 2), pcmSamples[i]);
        }

        return wavBytes;
    }
}
