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
    private string _currentStatus = "正在初始化二维码...";
    private bool _isSuccess;
    private string _userNick = "";
    private readonly object _lock = new();
    private bool _isDisposed;

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

                if (method != "GET" && method != "HEAD")
                {
                    await SendResponseAsync(stream, 405, "Method Not Allowed", "text/plain", "Method Not Allowed", ct).ConfigureAwait(false);
                    return;
                }

                if (path == "/" || path == "/index.html")
                {
                    string html = BuildHtmlPage();
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
                        statusJson = $"{{\"ready\":{(isReady ? "true" : "false")},\"success\":{(_isSuccess ? "true" : "false")},\"status\":\"{safeStatus}\",\"nick\":\"{safeNick}\"}}";
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

    private static string BuildHtmlPage()
    {
        return """
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
              <meta charset="UTF-8">
              <meta name="viewport" content="width=device-width, initial-scale=1.0">
              <title>QQ音乐终端版 - 网页协同扫码登录</title>
              <style>
                * { box-sizing: border-box; margin: 0; padding: 0; }
                body {
                  background-color: #0f1412;
                  color: #e0e0e0;
                  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "PingFang SC", "Hiragino Sans GB", "Microsoft YaHei", sans-serif;
                  min-height: 100vh;
                  display: flex;
                  align-items: center;
                  justify-content: center;
                  padding: 20px;
                }
                .card {
                  background: linear-gradient(145deg, #18221d, #141c18);
                  border: 1px solid rgba(49, 194, 124, 0.25);
                  border-radius: 20px;
                  padding: 36px 32px;
                  max-width: 380px;
                  width: 100%;
                  text-align: center;
                  box-shadow: 0 16px 48px rgba(0, 0, 0, 0.6), 0 0 24px rgba(49, 194, 124, 0.1);
                }
                .logo-title {
                  color: #31c27c;
                  font-size: 22px;
                  font-weight: 800;
                  letter-spacing: 1px;
                  margin-bottom: 6px;
                }
                .sub-title {
                  color: #8c9b93;
                  font-size: 13px;
                  margin-bottom: 24px;
                }
                .qr-container {
                  position: relative;
                  width: 230px;
                  height: 230px;
                  margin: 0 auto 24px auto;
                  display: flex;
                  align-items: center;
                  justify-content: center;
                  background-color: #ffffff;
                  border-radius: 16px;
                  padding: 12px;
                  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.35);
                }
                .qr-image {
                  width: 100%;
                  height: 100%;
                  display: none;
                  image-rendering: pixelated;
                }
                .qr-loading {
                  color: #666;
                  font-size: 14px;
                  display: flex;
                  flex-direction: column;
                  align-items: center;
                  gap: 10px;
                }
                .spinner {
                  width: 32px;
                  height: 32px;
                  border: 3px solid rgba(49, 194, 124, 0.2);
                  border-top-color: #31c27c;
                  border-radius: 50%;
                  animation: spin 1s linear infinite;
                }
                @keyframes spin {
                  to { transform: rotate(360deg); }
                }
                .instruction {
                  font-size: 15px;
                  font-weight: 600;
                  color: #f0f0f0;
                  margin-bottom: 8px;
                  min-height: 22px;
                }
                .hint {
                  font-size: 12px;
                  color: #728078;
                  line-height: 1.6;
                }
                .success-badge {
                  display: none;
                  background-color: rgba(49, 194, 124, 0.15);
                  border: 1px solid #31c27c;
                  color: #31c27c;
                  padding: 10px 16px;
                  border-radius: 12px;
                  font-weight: 600;
                  margin-top: 12px;
                }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="logo-title">QQ音乐 终端版</div>
                <div class="sub-title">扫码登录</div>
                <div class="qr-container">
                  <div class="qr-loading" id="qrLoading">
                    <div class="spinner"></div>
                    <span>正在获取二维码...</span>
                  </div>
                  <img class="qr-image" id="qrImage" src="/qr.png" alt="登录二维码" />
                </div>
                <div class="instruction" id="instruction">请使用手机 QQ 扫描二维码</div>
                <div class="hint">手机扫码并确认授权后将自动完成登录。</div>
                <div class="success-badge" id="successBadge">登录成功，正在同步会话...</div>
              </div>
              <script>
                const qrImage = document.getElementById('qrImage');
                const qrLoading = document.getElementById('qrLoading');
                const instruction = document.getElementById('instruction');
                const successBadge = document.getElementById('successBadge');
                let qrLoaded = false;

                async function pollStatus() {
                  try {
                    const res = await fetch('/status');
                    if (!res.ok) return;
                    const data = await res.json();

                    if (data.ready && !qrLoaded) {
                      qrImage.src = '/qr.png?t=' + Date.now();
                      qrImage.onload = () => {
                        qrLoading.style.display = 'none';
                        qrImage.style.display = 'block';
                        qrLoaded = true;
                      };
                    }

                    if (data.status) {
                      instruction.innerText = data.status;
                    }

                    if (data.success) {
                      successBadge.innerText = '登录成功 [' + (data.nick || 'QQ用户') + ']，终端已同步';
                      successBadge.style.display = 'block';
                      instruction.style.color = '#31c27c';
                      return;
                    }
                  } catch (e) {}

                  setTimeout(pollStatus, 1500);
                }

                pollStatus();
              </script>
            </body>
            </html>
            """;
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
