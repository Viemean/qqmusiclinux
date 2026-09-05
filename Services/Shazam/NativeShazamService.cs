using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace QQMusic.Tui.Services.Shazam;

/// <summary>
/// 纯 C# 原生 Apple Shazam 云端音频指纹识别客户端
/// 彻底摆脱 Python 解释器与外部工具链，毫秒级指纹提取并直连苹果官方云端接口
/// </summary>
public static class NativeShazamService
{
    private static readonly HttpClient s_httpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        EnableMultipleHttp2Connections = true
    })
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    static NativeShazamService()
    {
        s_httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (iPad; U; CPU OS 4_3_3 like Mac OS X; en-us) AppleWebKit/533.17.9 (KHTML, like Gecko) Mobile/8J2");
        s_httpClient.DefaultRequestHeaders.Add("X-Shazam-Platform", "IPHONE");
        s_httpClient.DefaultRequestHeaders.Add("X-Shazam-AppVersion", "14.1.0");
        s_httpClient.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,ja;q=0.8");
    }

    /// <summary>
    /// 识别本地 WAV 音频切片文件
    /// </summary>
    public static async Task<(bool Success, string Title, string Artist, string Album, string Error)> RecognizeWavAsync(
        string wavFilePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(wavFilePath))
        {
            return (false, "", "", "", $"音频切片不存在: {wavFilePath}");
        }

        try
        {
            var pcmSamples = await ReadWavPcmSamplesAsync(wavFilePath, cancellationToken);
            return await RecognizePcmSamplesAsync(pcmSamples, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return (false, "", "", "", "识别已取消");
        }
        catch (Exception ex)
        {
            return (false, "", "", "", $"读取音频异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 纯内存识别 16000Hz 16-bit 单声道 PCM 样本 (0 磁盘 I/O，0 临时文件，极致性能)
    /// </summary>
    public static async Task<(bool Success, string Title, string Artist, string Album, string Error)> RecognizePcmSamplesAsync(
        ReadOnlyMemory<short> pcmSamples,
        CancellationToken cancellationToken = default)
    {
        if (pcmSamples.Length < 16000 * 2) // 少于 2 秒直接跳过
        {
            return (false, "", "", "", "音频样本过短，请等待累积更多音频");
        }

        try
        {
            // 毫秒级原生提取 Shazam 音频特征签名 (纯内存运算，< 5ms)
            var sw = Stopwatch.StartNew();
            var sig = ShazamAlgorithm.CreateSignatureFromPcm(pcmSamples.Span);
            var uri = sig.EncodeToUri();
            var sigMs = sw.ElapsedMilliseconds;

            // 3. 构建苹果 Shazam 云端发现接口请求体
            var uuid1 = Guid.NewGuid().ToString().ToUpper();
            var uuid2 = Guid.NewGuid().ToString().ToUpper();
            var url = $"https://amp.shazam.com/discovery/v5/zh-CN/CN/iphone/-/tag/{uuid1}/{uuid2}?sync=true&webv3=true&sampling=true&connected=&shazamapiversion=v3&sharehub=true&hubv5minorversion=v5.1&hidelb=true&video=v3";

            int sampleMs = (int)(sig.NumberSamples * 1000.0 / sig.SampleRateHz);
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var jsonString = $$"""
            {
              "timezone": "Asia/Shanghai",
              "signature": {
                "uri": "{{uri}}",
                "samplems": {{sampleMs}}
              },
              "timestamp": {{timestamp}},
              "context": {},
              "geolocation": {}
            }
            """;

            var jsonContent = new StringContent(jsonString, Encoding.UTF8, "application/json");
            using var response = await s_httpClient.PostAsync(url, jsonContent, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (false, "", "", "", $"Shazam 云端服务响应错误: {(int)response.StatusCode}");
            }

            var respJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(respJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object)
            {
                return (false, "", "", "", "未能匹配到对应歌曲，请靠近声源或尝试其他片段");
            }

            var title = track.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "" : "";
            var artist = track.TryGetProperty("subtitle", out var aProp) ? aProp.GetString() ?? "" : "";
            var album = "";

            // 从 sections metadata 中解析专辑名称
            if (track.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
            {
                foreach (var sec in sections.EnumerateArray())
                {
                    if (sec.TryGetProperty("type", out var st) && st.GetString() == "SONG" &&
                        sec.TryGetProperty("metadata", out var metaArr) && metaArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in metaArr.EnumerateArray())
                        {
                            var mTitle = m.TryGetProperty("title", out var mt) ? mt.GetString() ?? "" : "";
                            if (mTitle.Equals("album", StringComparison.OrdinalIgnoreCase) ||
                                mTitle.Equals("专辑", StringComparison.OrdinalIgnoreCase))
                            {
                                album = m.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                                break;
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(album)) break;
                }
            }

            // 获取 Apple Track ID
            string? appleTrackId = null;
            if (track.TryGetProperty("hub", out var hub) &&
                hub.TryGetProperty("actions", out var actions) &&
                actions.ValueKind == JsonValueKind.Array)
            {
                foreach (var act in actions.EnumerateArray())
                {
                    if (act.TryGetProperty("id", out var idProp))
                    {
                        appleTrackId = idProp.GetString();
                        if (!string.IsNullOrEmpty(appleTrackId)) break;
                    }
                }
            }
            if (string.IsNullOrEmpty(appleTrackId) && track.TryGetProperty("albumadamid", out var adamId))
            {
                appleTrackId = adamId.GetString();
            }

            // 原语种本地化智能校正：
            // 若歌名是纯 ASCII 罗马音 (无任何中文、日文汉字或假名)，且存在 Apple ID，
            // 则尝试通过 iTunes 本地化元数据反查当地原语言歌名 (如将 "Koioto to Amazora" 校正为 "恋音と雨空")
            if (!string.IsNullOrEmpty(appleTrackId) && IsPureAscii(title))
            {
                var localized = await TryFetchAppleLocalizedNameAsync(appleTrackId, cancellationToken);
                if (!string.IsNullOrEmpty(localized.Title)) title = localized.Title;
                if (!string.IsNullOrEmpty(localized.Artist)) artist = localized.Artist;
                if (!string.IsNullOrEmpty(localized.Album)) album = localized.Album;
            }

            return (true, title, artist, album, "");
        }
        catch (OperationCanceledException)
        {
            return (false, "", "", "", "识别已取消");
        }
        catch (Exception ex)
        {
            return (false, "", "", "", $"原生识曲异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 判断是否全为 ASCII 字符 (用于识别罗马音)
    /// </summary>
    private static bool IsPureAscii(string text)
    {
        foreach (char c in text)
        {
            if (c > 127) return false;
        }
        return true;
    }

    /// <summary>
    /// 轻量查询 iTunes 官方原语种元数据 (超时限制 1.0 秒，避免阻塞)
    /// </summary>
    private static async Task<(string Title, string Artist, string Album)> TryFetchAppleLocalizedNameAsync(
        string appleTrackId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(1.2));

            var url = $"https://itunes.apple.com/lookup?id={appleTrackId}&country=JP";
            var resp = await s_httpClient.GetStringAsync(url, cts.Token);
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    var trackName = item.TryGetProperty("trackName", out var tn) ? tn.GetString() : null;
                    var artistName = item.TryGetProperty("artistName", out var an) ? an.GetString() : null;
                    var collectionName = item.TryGetProperty("collectionName", out var cn) ? cn.GetString() : null;

                    if (!string.IsNullOrEmpty(trackName))
                    {
                        return (trackName, artistName ?? "", collectionName ?? "");
                    }
                }
            }
        }
        catch
        {
            // 忽略非关键的本地化元数据查询超时
        }
        return ("", "", "");
    }

    /// <summary>
    /// 高性能异步读取 WAV PCM 16-bit 样本
    /// </summary>
    private static async Task<short[]> ReadWavPcmSamplesAsync(string wavFilePath, CancellationToken cancellationToken)
    {
        byte[] bytes;
        await using (var fs = new FileStream(wavFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bytes = new byte[fs.Length];
            int read = await fs.ReadAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);
            if (read < bytes.Length)
            {
                Array.Resize(ref bytes, read);
            }
        }

        if (bytes.Length < 44) return Array.Empty<short>();

        // 解析 RIFF WAV Header 找到 data chunk
        int dataOffset = 12;
        int dataLength = 0;
        while (dataOffset + 8 <= bytes.Length)
        {
            var chunkId = Encoding.ASCII.GetString(bytes, dataOffset, 4);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(dataOffset + 4, 4));
            dataOffset += 8;

            if (chunkId == "data")
            {
                dataLength = Math.Min(chunkSize, bytes.Length - dataOffset);
                break;
            }
            dataOffset += chunkSize;
        }

        if (dataLength <= 0 || dataOffset >= bytes.Length)
        {
            // 兜底：假设前 44 字节为头，后续均为数据
            dataOffset = 44;
            dataLength = bytes.Length - 44;
        }

        int sampleCount = dataLength / 2;
        var samples = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataOffset + i * 2, 2));
        }

        return samples;
    }
}
