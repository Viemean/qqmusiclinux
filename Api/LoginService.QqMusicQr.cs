using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Api;

public sealed partial class LoginService
{
    public static async Task<PollStatus> WaitForQqMusicQrLoginAsync(QrCodeResult qr, Action<PollStatus>? changed = null, CancellationToken ct = default)
    {
        if (qr.Type != QrLoginType.QqMusic) return new(QrLoginEvent.Error, -1, "二维码类型不是 QQ音乐");
        try
        {
            using var socket = await ConnectMqttAsync(qr.Identifier, ct).ConfigureAwait(false);
            await SubscribeMqttAsync(socket, qr.Identifier, ct).ConfigureAwait(false);
            changed?.Invoke(new(QrLoginEvent.Waiting, 408, "等待使用 QQ音乐 APP 扫码..."));
            Task<byte[]?> receiving = ReceiveMqttAsync(socket, ct);
            while (!ct.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(receiving, Task.Delay(TimeSpan.FromSeconds(25), ct)).ConfigureAwait(false);
                if (completed != receiving)
                {
                    await socket.SendAsync(new byte[] { 0xC0, 0 }, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                    continue;
                }
                var packet = await receiving.ConfigureAwait(false);
                if (packet == null) return new(QrLoginEvent.Error, -1, "QQ音乐登录连接已断开");
                receiving = ReceiveMqttAsync(socket, ct);
                if (packet[0] >> 4 != 3 || !TryParsePublish(packet, out var type, out var payload)) continue;

                var status = type switch
                {
                    "scanned" => new PollStatus(QrLoginEvent.Confirming, 404, "已扫码，请在 QQ音乐 APP 中确认授权..."),
                    "canceled" => new PollStatus(QrLoginEvent.Refused, 403, "已取消登录"),
                    "timeout" => new PollStatus(QrLoginEvent.Expired, 402, "二维码已失效"),
                    "loginFailed" => new PollStatus(QrLoginEvent.Error, -1, "QQ音乐登录失败"),
                    _ => null
                };
                if (status != null)
                {
                    changed?.Invoke(status);
                    if (status.Event != QrLoginEvent.Confirming) return status;
                    continue;
                }
                if (type != "cookies") continue;
                if (!ReadLoginCookies(payload, out var musicId, out var token)) return new(QrLoginEvent.Error, -1, "QQ音乐登录凭证响应不完整");
                bool saved = await ExchangeQqMusicCredentialAsync(qr.Identifier, musicId, token, ct).ConfigureAwait(false);
                var result = saved ? new PollStatus(QrLoginEvent.Done, 0, "登录成功") : new PollStatus(QrLoginEvent.Error, -1, "QQ音乐登录凭证交换失败");
                changed?.Invoke(result);
                return result;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLogger.Error("LoginService", "QQ Music MQTT login failed", ex);
            return new(QrLoginEvent.Error, -1, $"QQ音乐登录连接失败: {ex.Message}");
        }
        return new(QrLoginEvent.Error, -999, "操作已取消");
    }

    private static async Task<ClientWebSocket> ConnectMqttAsync(string id, CancellationToken ct)
    {
        string path = "/ws/handshake";
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol("mqtt");
            socket.Options.SetRequestHeader("Origin", "https://y.qq.com");
            socket.Options.SetRequestHeader("Referer", "https://y.qq.com/");
            socket.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 Chrome/128 Safari/537.36");
            await socket.ConnectAsync(new Uri($"wss://mu.y.qq.com{path}"), ct).ConfigureAwait(false);
            await SendConnectAsync(socket, id, ct).ConfigureAwait(false);
            var ack = await ReceiveMqttAsync(socket, ct).ConfigureAwait(false);
            if (ack == null || ack.Length < 5 || ack[0] >> 4 != 2) { socket.Dispose(); throw new InvalidDataException("MQTT CONNACK 无效"); }
            int offset = 1;
            _ = ReadVbi(ack, ref offset);
            offset++;
            int reason = ack[offset++];
            int propertyLength = ReadVbi(ack, ref offset);
            int end = offset + propertyLength;
            string server = "";
            while (offset < end)
            {
                int property = ack[offset++];
                if (property == 0x1C) server = ReadString(ack, ref offset); else SkipProperty(ack, ref offset, property);
            }
            if (reason == 0) return socket;
            socket.Dispose();
            if (reason is not (0x9C or 0x9D) || string.IsNullOrEmpty(server) || redirects == 3) throw new InvalidOperationException($"MQTT 连接被拒绝: 0x{reason:X2}");
            path = $"{path.TrimEnd('/')}/{server}";
        }
        throw new InvalidOperationException("MQTT 重定向过多");
    }

    private static async Task SendConnectAsync(ClientWebSocket socket, string id, CancellationToken ct)
    {
        var properties = new List<byte>();
        StringProperty(properties, 0x15, "pass");
        UserProperty(properties, "tmeAppID", "qqmusic");
        UserProperty(properties, "business", "management");
        UserProperty(properties, "hashTag", id);
        UserProperty(properties, "clientTag", "management.user");
        UserProperty(properties, "userID", id);
        var body = new List<byte>();
        WriteString(body, "MQTT");
        body.AddRange([5, 2, 0, 45]);
        WriteVbi(body, properties.Count);
        body.AddRange(properties);
        WriteString(body, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{Random.Shared.Next(1000, 10000)}");
        await SendPacketAsync(socket, 0x10, body, ct).ConfigureAwait(false);
    }

    private static async Task SubscribeMqttAsync(ClientWebSocket socket, string id, CancellationToken ct)
    {
        var properties = new List<byte>();
        UserProperty(properties, "authorization", "tmelogin");
        UserProperty(properties, "pubsub", "unicast");
        var body = new List<byte> { 0, 1 };
        WriteVbi(body, properties.Count);
        body.AddRange(properties);
        WriteString(body, $"management.qrcode_login/{id}");
        body.Add(0);
        await SendPacketAsync(socket, 0x82, body, ct).ConfigureAwait(false);
    }

    private static async Task SendPacketAsync(ClientWebSocket socket, byte header, List<byte> body, CancellationToken ct)
    {
        var packet = new List<byte>(body.Count + 5) { header };
        WriteVbi(packet, body.Count);
        packet.AddRange(body);
        await socket.SendAsync(packet.ToArray(), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReceiveMqttAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Binary) continue;
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return stream.ToArray();
        }
    }

    private static bool TryParsePublish(byte[] packet, out string type, out byte[] payload)
    {
        type = "";
        payload = [];
        try
        {
            int offset = 1;
            _ = ReadVbi(packet, ref offset);
            _ = ReadString(packet, ref offset);
            if (((packet[0] >> 1) & 3) > 0) offset += 2;
            int propertyLength = ReadVbi(packet, ref offset);
            int end = offset + propertyLength;
            while (offset < end)
            {
                int property = packet[offset++];
                if (property == 0x26)
                {
                    var key = ReadString(packet, ref offset);
                    var value = ReadString(packet, ref offset);
                    if (key == "type") type = value;
                }
                else SkipProperty(packet, ref offset, property);
            }
            payload = packet[offset..];
            return !string.IsNullOrEmpty(type);
        }
        catch { return false; }
    }

    private static bool ReadLoginCookies(byte[] payload, out string musicId, out string token)
    {
        musicId = "";
        token = "";
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("cookies", out var cookies)) return false;
            if (cookies.TryGetProperty("qqmusic_uin", out var uin) && uin.TryGetProperty("value", out var uv)) musicId = uv.GetString() ?? "";
            if (cookies.TryGetProperty("qqmusic_key", out var key) && key.TryGetProperty("value", out var kv)) token = kv.GetString() ?? "";
            return !string.IsNullOrEmpty(musicId) && !string.IsNullOrEmpty(token);
        }
        catch (JsonException) { return false; }
    }

    private static async Task<bool> ExchangeQqMusicCredentialAsync(string qrId, string musicId, string token, CancellationToken ct)
    {
        if (!long.TryParse(musicId, out var numericId)) return false;
        var id = JsonEncodedText.Encode(qrId).ToString();
        var key = JsonEncodedText.Encode(token).ToString();
        var payload = "{\"comm\":{\"ct\":11,\"cv\":14090008,\"v\":14090008,\"chid\":\"10003505\",\"tmeAppID\":\"qqmusic\",\"tmeLoginType\":6}," +
            "\"req_0\":{\"module\":\"music.login.LoginServer\",\"method\":\"Login\",\"param\":{\"musicid\":" + numericId + ",\"qrCodeID\":\"" + id + "\",\"token\":\"" + key + "\"}}}";
        return await ExchangeDirectCredentialAsync(payload, QrLoginType.QqMusic, ct).ConfigureAwait(false);
    }

    private static void UserProperty(List<byte> target, string key, string value)
    {
        target.Add(0x26);
        WriteString(target, key);
        WriteString(target, value);
    }

    private static void StringProperty(List<byte> target, byte property, string value)
    {
        target.Add(property);
        WriteString(target, value);
    }

    private static void WriteString(List<byte> target, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        target.Add((byte)(bytes.Length >> 8));
        target.Add((byte)bytes.Length);
        target.AddRange(bytes);
    }

    private static string ReadString(ReadOnlySpan<byte> source, ref int offset)
    {
        if (offset + 2 > source.Length) throw new InvalidDataException("MQTT 字符串长度缺失");
        int length = BinaryPrimitives.ReadUInt16BigEndian(source[offset..]);
        offset += 2;
        if (offset + length > source.Length) throw new InvalidDataException("MQTT 字符串数据不完整");
        var value = Encoding.UTF8.GetString(source.Slice(offset, length));
        offset += length;
        return value;
    }

    private static void WriteVbi(List<byte> target, int value)
    {
        do
        {
            int digit = value % 128;
            value /= 128;
            if (value > 0) digit |= 128;
            target.Add((byte)digit);
        } while (value > 0);
    }

    private static int ReadVbi(ReadOnlySpan<byte> source, ref int offset)
    {
        int multiplier = 1, value = 0;
        byte digit;
        do
        {
            if (offset >= source.Length || multiplier > 2097152) throw new InvalidDataException("MQTT 可变整数无效");
            digit = source[offset++];
            value += (digit & 127) * multiplier;
            multiplier *= 128;
        } while ((digit & 128) != 0);
        return value;
    }

    private static void SkipProperty(ReadOnlySpan<byte> source, ref int offset, int property)
    {
        switch (property)
        {
            case 0x01 or 0x17 or 0x19 or 0x24 or 0x25 or 0x28 or 0x29 or 0x2A: offset += 1; break;
            case 0x13 or 0x21 or 0x22 or 0x23: offset += 2; break;
            case 0x02 or 0x11 or 0x18 or 0x27: offset += 4; break;
            case 0x0B: _ = ReadVbi(source, ref offset); break;
            case 0x09 or 0x16:
                int length = BinaryPrimitives.ReadUInt16BigEndian(source[offset..]);
                offset += 2 + length;
                break;
            case 0x03 or 0x08 or 0x12 or 0x15 or 0x1A or 0x1C or 0x1F: _ = ReadString(source, ref offset); break;
            case 0x26: _ = ReadString(source, ref offset); _ = ReadString(source, ref offset); break;
            default: throw new InvalidDataException($"不支持的 MQTT 属性: 0x{property:X2}");
        }
        if (offset > source.Length) throw new InvalidDataException("MQTT 属性数据不完整");
    }
}
