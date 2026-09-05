using System.Net;
using System.Net.Sockets;
using System.Text;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

/// <summary>
/// 终端无图形协议支持时的轻量扫码登录本地 HTTP 服务 (零反射，100% Native AOT 兼容)
/// </summary>
public sealed class LoginHttpServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private byte[]? _qrBytes;
    private readonly object _lock = new();
    private bool _isDisposed;

    public int Port { get; private set; }
    public string Url => Port > 0 ? $"http://127.0.0.1:{Port}/" : "";
    public bool IsRunning => _listener != null && !_isDisposed && (_cts?.IsCancellationRequested == false);

    /// <summary>
    /// 启动本地轻量 HTTP 服务
    /// </summary>
    /// <param name="initialQrBytes">初始二维码 PNG 二进制字节流</param>
    public bool Start(byte[]? initialQrBytes)
    {
        lock (_lock)
        {
            if (_isDisposed) return false;
            if (IsRunning)
            {
                UpdateQrCode(initialQrBytes);
                return true;
            }

            _qrBytes = initialQrBytes;
            _cts = new CancellationTokenSource();

            // 优先尝试标准常用端口 9898，若被占用则回退至系统动态分配可用端口 (端口 0)
            int preferredPort = 9898;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, preferredPort);
                _listener.Start();
                Port = preferredPort;
                AppLogger.Info("LoginHttpServer", $"Started HTTP server on preferred port {Port}");
            }
            catch (Exception ex)
            {
                AppLogger.Info("LoginHttpServer", $"Preferred port {preferredPort} unavailable ({ex.Message}), falling back to ephemeral port");
                try
                {
                    _listener = new TcpListener(IPAddress.Loopback, 0);
                    _listener.Start();
                    Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                    AppLogger.Info("LoginHttpServer", $"Started HTTP server on dynamic port {Port}");
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
    /// 动态更新二维码图像字节流（二维码失效刷新时调用）
    /// </summary>
    public void UpdateQrCode(byte[]? qrBytes)
    {
        lock (_lock)
        {
            _qrBytes = qrBytes;
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
              <title>QQ音乐终端版 - 扫码登录</title>
              <style>
                * { box-sizing: border-box; margin: 0; padding: 0; }
                body {
                  background-color: #121212;
                  color: #e0e0e0;
                  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "PingFang SC", "Hiragino Sans GB", "Microsoft YaHei", sans-serif;
                  min-height: 100vh;
                  display: flex;
                  align-items: center;
                  justify-content: center;
                  padding: 20px;
                }
                .card {
                  background-color: #1e1e1e;
                  border: 1px solid #2d3732;
                  border-radius: 16px;
                  padding: 32px 28px;
                  max-width: 360px;
                  width: 100%;
                  text-align: center;
                  box-shadow: 0 12px 36px rgba(0, 0, 0, 0.5);
                }
                .logo-title {
                  color: #31c27c;
                  font-size: 20px;
                  font-weight: 700;
                  letter-spacing: 1px;
                  margin-bottom: 6px;
                }
                .sub-title {
                  color: #8c9b93;
                  font-size: 13px;
                  margin-bottom: 24px;
                }
                .qr-wrapper {
                  background-color: #ffffff;
                  border-radius: 12px;
                  padding: 14px;
                  display: inline-block;
                  box-shadow: 0 4px 16px rgba(0, 0, 0, 0.3);
                  margin-bottom: 22px;
                }
                .qr-image {
                  width: 210px;
                  height: 210px;
                  display: block;
                  image-rendering: pixelated;
                }
                .instruction {
                  font-size: 14px;
                  font-weight: 500;
                  color: #f0f0f0;
                  margin-bottom: 8px;
                }
                .hint {
                  font-size: 12px;
                  color: #728078;
                  line-height: 1.5;
                }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="logo-title">QQ音乐 终端版</div>
                <div class="sub-title">网页协同扫码登录</div>
                <div class="qr-wrapper">
                  <img class="qr-image" src="/qr.png" alt="登录二维码" />
                </div>
                <div class="instruction">请使用手机 QQ 扫描二维码登录</div>
                <div class="hint">当前终端未检测到图像协议，已为您自动启动网页扫码协同。<br>登录成功或关闭终端窗口后，此服务将自动退出。</div>
              </div>
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
