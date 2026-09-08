using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using QQMusic.Tui.Models;
using QQMusic.Tui.UI;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

/// <summary>
/// Web 播放与协同控制服务
/// </summary>
public sealed partial class WebPlaybackServer : IDisposable
{
    private sealed class SseClient : IDisposable
    {
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public Channel<string> Channel { get; }
        public CancellationTokenSource Cts { get; }

        public SseClient(TcpClient client, NetworkStream stream, CancellationToken parentToken)
        {
            Client = client;
            Stream = stream;
            Channel = System.Threading.Channels.Channel.CreateBounded<string>(new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            Cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        }

        public void Dispose()
        {
            try { Cts.Cancel(); } catch { }
            try { Cts.Dispose(); } catch { }
            try { Stream.Dispose(); } catch { }
            try { Client.Dispose(); } catch { }
        }
    }

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private bool _isDisposed;

    private readonly List<SseClient> _sseClients = new();
    private readonly object _sseLock = new();

    public int Port { get; private set; }
    public string LocalUrl => Port > 0 ? $"http://0.0.0.0:{Port}/" : "";
    public string DisplayUrl => Port > 0 ? $"http://{GetLocalLanIp() ?? "127.0.0.1"}:{Port}/" : "";
    public bool IsRunning => _listener != null && !_isDisposed && (_cts?.IsCancellationRequested == false);

    public static string? GetLocalLanIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("223.5.5.5", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
            {
                return endPoint.Address.ToString();
            }
        }
        catch {}
        return null;
    }

    // 当前状态
    public Song? CurrentSong { get; set; }
    public List<LyricLine>? CurrentLyrics { get; set; }
    public string? CurrentPlayUrl { get; set; }
    public double TotalDurationSeconds { get; set; }
    public double CurrentPositionSeconds { get; set; }
    public bool IsPlaying { get; set; }
    public int Volume { get; set; } = 80;
    public bool AudioOutputEnabled { get; set; } = true;
    public bool IsCurrentSongFavorite { get; set; }
    public PlaybackMode CurrentPlaybackMode { get; set; } = PlaybackMode.ListLoop;
    public AudioQualityTier PreferredQualityTier { get; set; } = AudioQualityTier.SQ;
    public AudioQualityTier ActualQualityTier { get; set; } = AudioQualityTier.SQ;

    // 回调事件
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? TogglePlayRequested;
    public event Action? ToggleFavoriteRequested;
    public event Action? ToggleModeRequested;
    public event Action? ToggleQualityRequested;
    public event Action? PlaybackEnded;
    public event Action<double>? SeekRequested;
    public event Action<int>? VolumeRequested;
    public event Action<double, double>? ProgressReported;
    public event Action<bool>? AudioOutputToggled;

    public bool Start(int preferredPort = 9999, bool initialAudioOutput = true)
    {
        lock (_lock)
        {
            if (_isDisposed) return false;
            if (IsRunning) return true;

            AudioOutputEnabled = initialAudioOutput;
            _cts = new CancellationTokenSource();

            try
            {
                _listener = new TcpListener(IPAddress.Any, preferredPort);
                try { _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch {}
                _listener.Start(128);
                Port = preferredPort;
                AppLogger.Info("WebPlaybackServer", $"Started Web playback server on preferred port {Port}");
            }
            catch (Exception ex)
            {
                AppLogger.Info("WebPlaybackServer", $"Preferred port {preferredPort} unavailable ({ex.Message}), falling back to dynamic port");
                try
                {
                    _listener = new TcpListener(IPAddress.Any, 0);
                    try { _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch {}
                    _listener.Start(128);
                    Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                    AppLogger.Info("WebPlaybackServer", $"Started Web playback server on dynamic port {Port}");
                }
                catch (Exception fallbackEx)
                {
                    AppLogger.Error("WebPlaybackServer", "Failed to start Web playback listener on dynamic port", fallbackEx);
                    _listener = null;
                    return false;
                }
            }

            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return true;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    AppLogger.Error("WebPlaybackServer", "AcceptTcpClient exception", ex);
                    try
                    {
                        await Task.Delay(100, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            client.NoDelay = true;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            client.LingerState = new LingerOption(enable: false, seconds: 0);
        }
        catch {}

        bool keepAliveForSse = false;
        var stream = client.GetStream();

        try
        {
            byte[] buffer = new byte[4096];
            using var readTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            int bytesRead;
            try
            {
                bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readTimeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return;
            }

            if (bytesRead <= 0) return;

            string requestText = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var headerEnd = requestText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string headerPart = headerEnd >= 0 ? requestText[..headerEnd] : requestText;
            string bodyPart = headerEnd >= 0 && headerEnd + 4 < requestText.Length ? requestText[(headerEnd + 4)..] : "";

            string[] lines = headerPart.Split("\r\n");
            if (lines.Length == 0) return;

            string firstLine = lines[0];
            string[] parts = firstLine.Split(' ');
            if (parts.Length < 2) return;

            string method = parts[0].ToUpperInvariant();
            string rawPath = parts[1];
            string path = rawPath.Split('?')[0];

            // 提取 Range 请求头
            string? rangeHeader = null;
            foreach (var line in lines)
            {
                if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                {
                    rangeHeader = line["Range:".Length..].Trim();
                    break;
                }
            }

            if (method == "GET")
            {
                if (path == "/" || path == "/index.html")
                {
                    string html = StaticResourceHelper.LoadStaticText("index.html");
                    await SendResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", html, ct).ConfigureAwait(false);
                }
                else if (path == "/style.css")
                {
                    string css = StaticResourceHelper.LoadStaticText("style.css");
                    await SendResponseAsync(stream, 200, "OK", "text/css; charset=utf-8", css, ct).ConfigureAwait(false);
                }
                else if (path == "/app.js")
                {
                    string js = StaticResourceHelper.LoadStaticText("app.js");
                    await SendResponseAsync(stream, 200, "OK", "application/javascript; charset=utf-8", js, ct).ConfigureAwait(false);
                }
                else if (path == "/cover")
                {
                    await HandleCoverRequestAsync(stream, rawPath, ct).ConfigureAwait(false);
                }
                else if (path == "/stream/audio")
                {
                    await HandleAudioStreamAsync(stream, rangeHeader, ct).ConfigureAwait(false);
                }
                else if (path == "/api/events")
                {
                    keepAliveForSse = true;
                    await HandleSseEventsAsync(client, stream, ct).ConfigureAwait(false);
                    return;
                }
                else
                {
                    await SendResponseAsync(stream, 404, "Not Found", "text/plain", "Not Found", ct).ConfigureAwait(false);
                }
            }
                else if (method == "POST")
                {
                    if (path == "/api/action")
                    {
                        HandleApiAction(bodyPart);
                        await SendResponseAsync(stream, 200, "OK", "application/json", "{\"ok\":true}", ct).ConfigureAwait(false);
                    }
                    else if (path == "/api/toggle")
                    {
                        TogglePlayRequested?.Invoke();
                        await SendResponseAsync(stream, 200, "OK", "application/json", "{\"ok\":true}", ct).ConfigureAwait(false);
                    }
                    else if (path == "/api/favorite")
                    {
                        ToggleFavoriteRequested?.Invoke();
                        await SendResponseAsync(stream, 200, "OK", "application/json", "{\"ok\":true}", ct).ConfigureAwait(false);
                    }
                    else if (path == "/api/mode")
                    {
                        ToggleModeRequested?.Invoke();
                        await SendResponseAsync(stream, 200, "OK", "application/json", "{\"ok\":true}", ct).ConfigureAwait(false);
                    }
                    else if (path == "/api/quality")
                    {
                        ToggleQualityRequested?.Invoke();
                        await SendResponseAsync(stream, 200, "OK", "application/json", "{\"ok\":true}", ct).ConfigureAwait(false);
                    }
                    else if (path == "/api/next")
                    {
                        NextRequested?.Invoke();
                        await SendResponseAsync(stream, 200, "OK", "application/json", "{\"ok\":true}", ct).ConfigureAwait(false);
                    }
                    else if (path == "/api/previous" || path == "/api/prev")
                    {
                        PreviousRequested?.Invoke();
                        await SendResponseAsync(stream, 200, "OK", "application/json", "{\"ok\":true}", ct).ConfigureAwait(false);
                    }
                    else if (path == "/api/seek")
                    {
                        double targetPos = 0;
                        var queryIndex = rawPath.IndexOf("pos=", StringComparison.OrdinalIgnoreCase);
                        if (queryIndex >= 0)
                        {
                            var posStr = rawPath[(queryIndex + 4)..].Split('&')[0];
                            double.TryParse(posStr, System.Globalization.CultureInfo.InvariantCulture, out targetPos);
                        }
                        else if (!string.IsNullOrWhiteSpace(bodyPart) && bodyPart.Contains("position"))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(bodyPart);
                                if (doc.RootElement.TryGetProperty("position", out var pProp)) targetPos = pProp.GetDouble();
                            }
                            catch {}
                        }
                        SeekRequested?.Invoke(targetPos);
                        await SendResponseAsync(stream, 200, "OK", "application/json", "{\"ok\":true}", ct).ConfigureAwait(false);
                    }
                    else if (path == "/api/progress")
                    {
                        HandleApiProgress(bodyPart);
                        await SendResponseAsync(stream, 200, "OK", "application/json", "{\"ok\":true}", ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendResponseAsync(stream, 404, "Not Found", "text/plain", "Not Found", ct).ConfigureAwait(false);
                    }
                }
                else if (method == "OPTIONS")
                {
                    await SendCorsHeadersAsync(stream, ct).ConfigureAwait(false);
                }
                else
                {
                    await SendResponseAsync(stream, 405, "Method Not Allowed", "text/plain", "Method Not Allowed", ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Debug("WebPlaybackServer", $"Client socket handling finished: {ex.Message}");
            }
            finally
            {
                if (!keepAliveForSse)
                {
                    try { stream.Dispose(); } catch {}
                    try { client.Dispose(); } catch {}
                }
            }
        }

    private async Task HandleCoverRequestAsync(NetworkStream stream, string rawPath, CancellationToken ct)
    {
        string? mid = null;
        string? albumMid = null;

        var qIdx = rawPath.IndexOf('?');
        if (qIdx >= 0 && qIdx + 1 < rawPath.Length)
        {
            var qs = rawPath[(qIdx + 1)..].Split('&');
            foreach (var param in qs)
            {
                var kv = param.Split('=');
                if (kv.Length == 2)
                {
                    var k = kv[0];
                    var v = Uri.UnescapeDataString(kv[1]);
                    if (k.Equals("mid", StringComparison.OrdinalIgnoreCase)) mid = v;
                    else if (k.Equals("albumMid", StringComparison.OrdinalIgnoreCase)) albumMid = v;
                }
            }
        }

        string? coverFile = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(albumMid))
            {
                coverFile = await TerminalImageHelper.EnsureAlbumCoverAsync(albumMid).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(coverFile) && CurrentSong != null)
            {
                coverFile = await TerminalImageHelper.EnsureSongCoverAsync(CurrentSong).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(coverFile) && !string.IsNullOrWhiteSpace(mid))
            {
                var tempSong = new Song(mid, "", "", "", 0, AlbumMid: albumMid ?? "");
                coverFile = await TerminalImageHelper.EnsureSongCoverAsync(tempSong).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("WebPlaybackServer", $"EnsureSongCoverAsync exception: {ex.Message}");
        }

        if (!string.IsNullOrEmpty(coverFile) && File.Exists(coverFile))
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(coverFile, ct).ConfigureAwait(false);
                string contentType = coverFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                
                string headers = $"HTTP/1.1 200 OK\r\n" +
                                 $"Content-Type: {contentType}\r\n" +
                                 $"Content-Length: {bytes.Length}\r\n" +
                                 $"Cache-Control: public, max-age=86400\r\n" +
                                 $"Access-Control-Allow-Origin: *\r\n" +
                                 $"Connection: close\r\n\r\n";
                byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
                await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), ct).ConfigureAwait(false);
                await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("WebPlaybackServer", $"Failed to read cover file: {ex.Message}");
            }
        }

        await SendResponseAsync(stream, 404, "Not Found", "text/plain", "Cover not available", ct).ConfigureAwait(false);
    }

    private async Task HandleAudioStreamAsync(NetworkStream stream, string? rangeHeader, CancellationToken ct)
    {
        string? url = CurrentPlayUrl;
        if (string.IsNullOrEmpty(url))
        {
            await SendResponseAsync(stream, 404, "Not Found", "text/plain", "No audio URL", ct).ConfigureAwait(false);
            return;
        }

        // WebDAV 歌曲处理：若本地已有完整缓存，直接输出本地文件；否则服务端透明代理转发 WebDAV 流
        if (CurrentSong?.IsWebDav == true)
        {
            var server = WebDavService.GetActiveServer();
            if (server != null && !string.IsNullOrEmpty(CurrentSong.WebDavHref))
            {
                var localCache = WebDavService.GetLocalCachePath(server, CurrentSong.WebDavHref);
                if (File.Exists(localCache) && new FileInfo(localCache).Length > 4096)
                {
                    url = localCache;
                }
                else
                {
                    await ProxyWebDavStreamAsync(stream, server, CurrentSong.WebDavHref, rangeHeader, ct).ConfigureAwait(false);
                    return;
                }
            }
        }

        // 携带嵌入凭据的远程地址严禁直接重定向给浏览器
        if ((url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) && !url.Contains('@'))
        {
            await SendRedirectAsync(stream, url, ct).ConfigureAwait(false);
            return;
        }

        string filePath = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(url).LocalPath : url;
        if (!File.Exists(filePath))
        {
            await SendResponseAsync(stream, 404, "Not Found", "text/plain", "Local file not found", ct).ConfigureAwait(false);
            return;
        }

        var fileInfo = new FileInfo(filePath);
        long totalLength = fileInfo.Length;
        long start = 0;
        long end = totalLength - 1;
        bool isRange = false;

        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var rangeSpec = rangeHeader["bytes=".Length..].Trim();
            var parts = rangeSpec.Split('-');
            if (long.TryParse(parts[0], out var s))
            {
                start = Math.Clamp(s, 0, totalLength - 1);
            }
            if (parts.Length > 1 && long.TryParse(parts[1], out var e))
            {
                end = Math.Clamp(e, start, totalLength - 1);
            }
            isRange = true;
        }

        long contentLength = end - start + 1;
        string contentType = filePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) ? "audio/flac"
                           : filePath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? "audio/ogg"
                           : filePath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ? "audio/mp4"
                           : filePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? "audio/wav"
                           : "audio/mpeg";

        int statusCode = isRange ? 206 : 200;
        string statusText = isRange ? "Partial Content" : "OK";

        string headers = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                         $"Content-Type: {contentType}\r\n" +
                         $"Accept-Ranges: bytes\r\n" +
                         (isRange ? $"Content-Range: bytes {start}-{end}/{totalLength}\r\n" : "") +
                         $"Content-Length: {contentLength}\r\n" +
                         $"Access-Control-Allow-Origin: *\r\n" +
                         $"Connection: close\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), ct).ConfigureAwait(false);

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        if (start > 0)
        {
            fs.Seek(start, SeekOrigin.Begin);
        }

        byte[] chunk = new byte[64 * 1024];
        long remaining = contentLength;
        while (remaining > 0 && !ct.IsCancellationRequested)
        {
            int toRead = (int)Math.Min(chunk.Length, remaining);
            int bytesRead = await fs.ReadAsync(chunk.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (bytesRead <= 0) break;
            await stream.WriteAsync(chunk.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            remaining -= bytesRead;
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task ProxyWebDavStreamAsync(NetworkStream stream, WebDavServer server, string relativeHref, string? rangeHeader, CancellationToken ct)
    {
        try
        {
            using var resp = await WebDavService.OpenAudioStreamAsync(server, relativeHref, rangeHeader, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.PartialContent)
            {
                await SendResponseAsync(stream, (int)resp.StatusCode, resp.ReasonPhrase ?? "WebDAV Error", "text/plain", $"WebDAV server returned {resp.StatusCode}", ct).ConfigureAwait(false);
                return;
            }

            int statusCode = (int)resp.StatusCode;
            string statusText = resp.ReasonPhrase ?? (statusCode == 206 ? "Partial Content" : "OK");
            string contentType = resp.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
            long? contentLength = resp.Content.Headers.ContentLength;
            string? contentRange = resp.Content.Headers.ContentRange?.ToString();

            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {statusCode} {statusText}\r\n");
            sb.Append($"Content-Type: {contentType}\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            if (!string.IsNullOrEmpty(contentRange))
            {
                sb.Append($"Content-Range: {contentRange}\r\n");
            }
            if (contentLength.HasValue)
            {
                sb.Append($"Content-Length: {contentLength.Value}\r\n");
            }
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Connection: close\r\n\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), ct).ConfigureAwait(false);

            await using var remoteStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] chunk = new byte[64 * 1024];
            int bytesRead;
            while ((bytesRead = await remoteStream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false)) > 0)
            {
                if (ct.IsCancellationRequested) break;
                await stream.WriteAsync(chunk.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            }
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Warn("WebPlaybackServer", $"ProxyWebDavStreamAsync error: {ex.Message}");
        }
    }

    private static async Task SendResponseAsync(NetworkStream stream, int statusCode, string statusText, string contentType, string content, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(content);
        string headers = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                         $"Content-Type: {contentType}\r\n" +
                         $"Content-Length: {body.Length}\r\n" +
                         $"Access-Control-Allow-Origin: *\r\n" +
                         $"Connection: close\r\n" +
                         $"Cache-Control: no-cache, no-store, must-revalidate\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), ct).ConfigureAwait(false);
        await stream.WriteAsync(body.AsMemory(0, body.Length), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task SendRedirectAsync(NetworkStream stream, string locationUrl, CancellationToken ct)
    {
        string headers = "HTTP/1.1 302 Found\r\n" +
                         $"Location: {locationUrl}\r\n" +
                         "Content-Length: 0\r\n" +
                         "Access-Control-Allow-Origin: *\r\n" +
                         "Connection: close\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task SendCorsHeadersAsync(NetworkStream stream, CancellationToken ct)
    {
        string headers = "HTTP/1.1 204 No Content\r\n" +
                         "Access-Control-Allow-Origin: *\r\n" +
                         "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                         "Access-Control-Allow-Headers: Content-Type, Range\r\n" +
                         "Connection: close\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }



    public void Stop()
    {
        lock (_lock)
        {
            if (_listener == null) return;
            try
            {
                _cts?.Cancel();
            }
            catch {}

            lock (_sseLock)
            {
                foreach (var client in _sseClients)
                {
                    try { client.Dispose(); } catch {}
                }
                _sseClients.Clear();
            }

            try
            {
                _listener.Stop();
                AppLogger.Info("WebPlaybackServer", $"Stopped Web playback server on port {Port}");
            }
            catch (Exception ex)
            {
                AppLogger.Error("WebPlaybackServer", "Error stopping listener", ex);
            }
            finally
            {
                _listener = null;
                _cts?.Dispose();
                _cts = null;
                Port = 0;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Stop();
        }
    }
}
