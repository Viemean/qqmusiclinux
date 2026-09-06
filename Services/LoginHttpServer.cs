using System.Net;
using System.Net.Sockets;
using System.Text;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

/// <summary>
/// 扫码登录本地 HTTP 服务
/// </summary>
public sealed class LoginHttpServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private byte[]? _qrBytes;
    private int _qrVersion;
    private string _currentStatus = "正在初始化二维码...";
    private bool _isSuccess;
    private string _userNick = "";
    private readonly object _lock = new();
    private bool _isDisposed;

    public event Func<Task>? RefreshRequested;

    public int Port { get; private set; }
    public string LocalUrl => Port > 0 ? $"http://127.0.0.1:{Port}/" : "";
    public string LanUrl => Port > 0 ? $"http://{WebPlaybackServer.GetLocalLanIp() ?? "127.0.0.1"}:{Port}/" : "";
    public string DisplayUrl => LanUrl;
    public string Url => DisplayUrl;
    public bool IsRunning => _listener != null && !_isDisposed && (_cts?.IsCancellationRequested == false);

    /// <summary>
    /// 启动本地 HTTP 服务
    /// </summary>
    /// <param name="initialQrBytes">初始二维码图像数据（可为空）</param>
    public bool Start(byte[]? initialQrBytes)
    {
        lock (_lock)
        {
            if (_isDisposed) return false;
            if (IsRunning)
            {
                if (initialQrBytes != null)
                {
                    UpdateQrCode(initialQrBytes);
                }
                return true;
            }

            _qrBytes = initialQrBytes;
            _cts = new CancellationTokenSource();

            // 优先尝试常用端口 9898，若被占用则回退至系统动态分配可用端口 (端口 0)
            int preferredPort = 9898;
            try
            {
                _listener = new TcpListener(IPAddress.Any, preferredPort);
                _listener.Start();
                Port = preferredPort;
                AppLogger.Info("LoginHttpServer", $"Started HTTP server on 0.0.0.0:{Port}");
            }
            catch (Exception ex)
            {
                AppLogger.Info("LoginHttpServer", $"Preferred port {preferredPort} unavailable ({ex.Message}), falling back to ephemeral port");
                try
                {
                    _listener = new TcpListener(IPAddress.Any, 0);
                    _listener.Start();
                    Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                    AppLogger.Info("LoginHttpServer", $"Started HTTP server on dynamic port {Port} (0.0.0.0)");
                }
                catch (Exception fallbackEx)
                {
                    AppLogger.Error("LoginHttpServer", "Failed to start HTTP listener on dynamic port", fallbackEx);
                    _listener = null;
                    return false;
                }
            }

            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return true;
        }
    }

    /// <summary>
    /// 动态更新二维码图像字节流
    /// </summary>
    public void UpdateQrCode(byte[]? qrBytes)
    {
        lock (_lock)
        {
            _qrBytes = qrBytes;
            _qrVersion++;
            if (qrBytes != null && _currentStatus == "正在初始化二维码...")
            {
                _currentStatus = "等待手机扫码...";
            }
        }
    }

    /// <summary>
    /// 更新当前扫码流程状态（供网页端同步提示）
    /// </summary>
    public void UpdateStatus(string status, bool isSuccess = false, string nick = "")
    {
        lock (_lock)
        {
            _currentStatus = status;
            _isSuccess = isSuccess;
            if (!string.IsNullOrEmpty(nick))
            {
                _userNick = nick;
            }
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
                    AppLogger.Error("LoginHttpServer", "AcceptTcpClient exception", ex);
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 5000;

            try
            {
                byte[] buffer = new byte[2048];
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (bytesRead <= 0) return;

                string request = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                string[] lines = request.Split("\r\n");
                if (lines.Length == 0) return;

                string firstLine = lines[0];
                string[] parts = firstLine.Split(' ');
                if (parts.Length < 2) return;

                string method = parts[0].ToUpperInvariant();
                string rawPath = parts[1];
                string path = rawPath.Split('?')[0];

                if (method != "GET" && method != "HEAD" && method != "POST")
                {
                    await SendResponseAsync(stream, 405, "Method Not Allowed", "text/plain", "Method Not Allowed", ct).ConfigureAwait(false);
                    return;
                }

                if (path == "/refresh" && method == "POST")
                {
                    if (RefreshRequested != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await RefreshRequested.Invoke(); }
                            catch (Exception ex) { AppLogger.Error("LoginHttpServer", "RefreshRequested handler error", ex); }
                        });
                    }
                    await SendResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", "{\"ok\":true}", ct).ConfigureAwait(false);
                }
                else if (path == "/" || path == "/index.html")
                {
                    string html = LoadHtmlPage();
                    await SendResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", html, ct).ConfigureAwait(false);
                }
                else if (path == "/status")
                {
                    string statusJson;
                    lock (_lock)
                    {
                        bool isReady = _qrBytes != null && _qrBytes.Length > 0;
                        string safeStatus = _currentStatus.Replace("\"", "\\\"");
                        string safeNick = _userNick.Replace("\"", "\\\"");
                        statusJson = $"{{\"ready\":{(isReady ? "true" : "false")},\"success\":{(_isSuccess ? "true" : "false")},\"status\":\"{safeStatus}\",\"nick\":\"{safeNick}\",\"version\":{_qrVersion}}}";
                    }
                    await SendResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", statusJson, ct).ConfigureAwait(false);
                }
                else if (path == "/qr.png")
                {
                    byte[]? imageBytes;
                    lock (_lock)
                    {
                        imageBytes = _qrBytes;
                    }

                    if (imageBytes != null && imageBytes.Length > 0)
                    {
                        await SendBinaryResponseAsync(stream, 200, "OK", "image/png", imageBytes, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendResponseAsync(stream, 404, "Not Found", "text/plain", "QR code not ready", ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    await SendResponseAsync(stream, 404, "Not Found", "text/plain", "Not Found", ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Debug("LoginHttpServer", $"Client handling ended: {ex.Message}");
            }
        }
    }

    private static async Task SendResponseAsync(NetworkStream stream, int statusCode, string statusText, string contentType, string content, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(content);
        string headers = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                         $"Content-Type: {contentType}\r\n" +
                         $"Content-Length: {body.Length}\r\n" +
                         $"Connection: close\r\n" +
                         $"Access-Control-Allow-Origin: *\r\n" +
                         $"Cache-Control: no-cache, no-store, must-revalidate\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), ct).ConfigureAwait(false);
        await stream.WriteAsync(body.AsMemory(0, body.Length), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task SendBinaryResponseAsync(NetworkStream stream, int statusCode, string statusText, string contentType, byte[] body, CancellationToken ct)
    {
        string headers = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                         $"Content-Type: {contentType}\r\n" +
                         $"Content-Length: {body.Length}\r\n" +
                         $"Connection: close\r\n" +
                         $"Access-Control-Allow-Origin: *\r\n" +
                         $"Cache-Control: no-cache, no-store, must-revalidate\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), ct).ConfigureAwait(false);
        await stream.WriteAsync(body.AsMemory(0, body.Length), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string LoadHtmlPage()
    {
        var filePath = ResolveWwwFilePath("login.html");
        if (filePath != null)
        {
            try
            {
                return File.ReadAllText(filePath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Error("LoginHttpServer", "Failed to read www/login.html", ex);
            }
        }

        // 极简 fallback 页面
        return """
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head><meta charset="UTF-8"><title>QQ音乐扫码登录</title><meta name="viewport" content="width=device-width,initial-scale=1"></head>
            <body style="background:#121212;color:#eee;text-align:center;padding:2rem;font-family:sans-serif;">
              <h2>QQ音乐 扫码登录</h2>
              <p><img id="qr" src="/qr.png" style="width:220px;height:220px;background:#fff;border-radius:8px;"></p>
              <p id="msg" style="color:#aaa;">正在加载二维码...</p>
              <button onclick="fetch('/refresh',{method:'POST'})" style="background:#31c27c;color:#fff;border:none;padding:8px 16px;border-radius:4px;cursor:pointer;">刷新二维码</button>
              <script>
                let v = -1;
                setInterval(async () => {
                  try {
                    const r = await fetch('/status');
                    const d = await r.json();
                    if (d.status) document.getElementById('msg').innerText = d.status;
                    if (d.ready && d.version !== v) {
                      v = d.version;
                      document.getElementById('qr').src = '/qr.png?v=' + d.version + '&t=' + Date.now();
                    }
                  } catch(e){}
                }, 1500);
              </script>
            </body>
            </html>
            """;
    }

    private static string? ResolveWwwFilePath(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        var p1 = Path.Combine(baseDir, "www", fileName);
        if (File.Exists(p1)) return p1;

        var curDir = Directory.GetCurrentDirectory();
        var p2 = Path.Combine(curDir, "www", fileName);
        if (File.Exists(p2)) return p2;

        var p3 = Path.Combine("/usr/share/qqmusic-tui/www", fileName);
        if (File.Exists(p3)) return p3;

        return null;
    }

    /// <summary>
    /// 停止服务并释放网络资源
    /// </summary>
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

            try
            {
                _listener.Stop();
                AppLogger.Info("LoginHttpServer", $"Stopped HTTP server on port {Port}");
            }
            catch (Exception ex)
            {
                AppLogger.Error("LoginHttpServer", "Error stopping listener", ex);
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
