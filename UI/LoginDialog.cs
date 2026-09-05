using System.Collections.ObjectModel;
using System.Diagnostics;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;

namespace QQMusic.Tui.UI;

public sealed class LoginDialog : Dialog
{
    private readonly Action _onLoginSuccess;
    private readonly CancellationTokenSource _cts = new();
    private LoginHttpServer? _httpServer;

    private readonly View _qrContainer;
    private readonly Label _qrStatusLabel;
    private readonly Label _qrTipLabel;
    private readonly Button _openBrowserBtn;
    private readonly ListView _qrView;

    private readonly View _cookieContainer;
    private readonly TextField _cookieInput;
    private readonly Label _cookieStatusLabel;

    public LoginDialog(Action onLoginSuccess)
    {
        _onLoginSuccess = onLoginSuccess;

        Title = "用户登录";
        Width = 62;
        Height = 28;
        SetScheme(MikuTheme.Dialog);

        // 顶部切换按钮
        var qrTabBtn = new Button
        {
            Text = "扫码登录",
            X = 2,
            Y = 0
        };

        var cookieTabBtn = new Button
        {
            Text = "Cookie 导入",
            X = Pos.Right(qrTabBtn) + 2,
            Y = 0
        };

        var logoutBtn = new Button
        {
            Text = "退出当前账号",
            X = Pos.Right(cookieTabBtn) + 2,
            Y = 0
        };

        var closeBtn = new Button
        {
            Text = "关闭 (Esc)",
            X = Pos.AnchorEnd(14),
            Y = 0
        };

        Add(qrTabBtn, cookieTabBtn, logoutBtn, closeBtn);

        // 1. 扫码登录容器
        _qrContainer = new View
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        _qrStatusLabel = new Label
        {
            Text = "状态: 正在初始化二维码...",
            X = 2,
            Y = 0
        };
        _qrContainer.Add(_qrStatusLabel);

        _qrView = new ListView
        {
            X = 2,
            Y = 1,
            Width = 40,
            Height = 20
        };
        _qrContainer.Add(_qrView);

        _qrTipLabel = new Label
        {
            Text = "提示: 手机扫码授权后将自动同步",
            X = 2,
            Y = 21,
            Width = Dim.Fill(2)
        };
        _qrContainer.Add(_qrTipLabel);

        _openBrowserBtn = new Button
        {
            Text = "浏览器打开 (B)",
            X = 2,
            Y = 22,
            Visible = false
        };
        _openBrowserBtn.Accepting += (s, e) =>
        {
            if (_httpServer != null && _httpServer.IsRunning)
            {
                TryOpenBrowser(_httpServer.Url);
            }
        };
        _qrContainer.Add(_openBrowserBtn);

        Add(_qrContainer);

        // 2. Cookie 导入容器
        _cookieContainer = new View
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Visible = false
        };

        var cookieDesc = new Label
        {
            Text = "请在下方粘贴网页版或客户端提取的 Cookie 字符串:\n(包含 uin 与 skey 或 qm_keyst)",
            X = 2,
            Y = 1
        };
        _cookieContainer.Add(cookieDesc);

        _cookieInput = new TextField
        {
            X = 2,
            Y = 5,
            Width = Dim.Fill(2),
            Text = ""
        };
        _cookieContainer.Add(_cookieInput);

        var saveCookieBtn = new Button
        {
            Text = "保存并应用",
            X = 2,
            Y = 8
        };
        _cookieContainer.Add(saveCookieBtn);

        _cookieStatusLabel = new Label
        {
            Text = "",
            X = 2,
            Y = 10
        };
        _cookieContainer.Add(_cookieStatusLabel);

        Add(_cookieContainer);

        // 事件处理
        qrTabBtn.Accepting += (s, e) =>
        {
            _qrContainer.Visible = true;
            _cookieContainer.Visible = false;
        };

        cookieTabBtn.Accepting += (s, e) =>
        {
            _qrContainer.Visible = false;
            _cookieContainer.Visible = true;
            _cookieInput.SetFocus();
        };

        logoutBtn.Accepting += (s, e) =>
        {
            LoginService.Logout();
            _qrStatusLabel.Text = "状态: 已退出登录";
            _onLoginSuccess?.Invoke();
        };

        closeBtn.Accepting += (s, e) => CloseSelf();

        saveCookieBtn.Accepting += (s, e) =>
        {
            var raw = _cookieInput.Text.ToString()?.Trim() ?? "";
            if (LoginService.ImportCookieString(raw))
            {
                _cookieStatusLabel.Text = $"导入成功: 已绑定账号 {UserSession.Current.Uin}";
                _onLoginSuccess?.Invoke();
                Task.Delay(1000).ContinueWith(_ => Application.Invoke(CloseSelf));
            }
            else
            {
                _cookieStatusLabel.Text = "错误: 未在 Cookie 中识别到有效的 uin 或凭证字段";
            }
        };

        KeyDown += (s, k) =>
        {
            if (k == Key.Esc)
            {
                CloseSelf();
            }
            else if ((k.AsRune.Value == 'b' || k.AsRune.Value == 'B') && _httpServer != null && _httpServer.IsRunning)
            {
                TryOpenBrowser(_httpServer.Url);
            }
        };

        // 启动二维码生成与轮询
        StartQrLoginFlow();

        MikuTheme.ApplyTo(this, MikuTheme.Dialog);
    }

    private void StartQrLoginFlow()
    {
        Task.Run(async () =>
        {
            var qr = await LoginService.FetchQrCodeAsync(_cts.Token);
            if (qr == null)
            {
                Application.Invoke(() =>
                {
                    _qrStatusLabel.Text = "状态: 二维码生成失败，请检查网络";
                });
                return;
            }

            // 终端不支持图片协议时，启动本地轻量 HTTP 服务协同网页扫码
            if (!TerminalImageHelper.IsImageSupported)
            {
                _httpServer ??= new LoginHttpServer();
                _httpServer.Start(qr.PngBytes);
            }

            Application.Invoke(() =>
            {
                _qrStatusLabel.Text = "状态: 等待手机扫码...";
                _qrView.SetSource(new ObservableCollection<string>(qr.AsciiLines));
                if (_httpServer != null && _httpServer.IsRunning)
                {
                    _qrTipLabel.Text = $"网页扫码: {_httpServer.Url} (按 B 打开)";
                    _openBrowserBtn.Visible = true;
                }
                else
                {
                    _qrTipLabel.Text = "可手机扫码，或打开: /tmp/qqmusic_login_qr.png";
                    _openBrowserBtn.Visible = false;
                }
            });

            // 轮询状态
            while (!_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(2000, _cts.Token);
                var status = await LoginService.PollQrStatusAsync(qr.QrSig, qr.PtqrToken, _cts.Token);

                if (status.Code == 0) // 成功
                {
                    _httpServer?.Stop();
                    Application.Invoke(() =>
                    {
                        _qrStatusLabel.Text = $"状态[0]: 登录成功 [{UserSession.Current.Nick}]";
                        _onLoginSuccess?.Invoke();
                    });
                    await Task.Delay(1500);
                    Application.Invoke(CloseSelf);
                    break;
                }
                else if (status.Code == 67) // 认证中
                {
                    Application.Invoke(() =>
                    {
                        _qrStatusLabel.Text = "状态[67]: 已扫码，请在手机上确认授权...";
                    });
                }
                else if (status.Code == 65) // 失效
                {
                    _httpServer?.Stop();
                    Application.Invoke(() =>
                    {
                        _qrStatusLabel.Text = "状态[65]: 二维码已失效，请重新打开登录窗口";
                        _openBrowserBtn.Visible = false;
                    });
                    break;
                }
                else
                {
                    Application.Invoke(() =>
                    {
                        _qrStatusLabel.Text = $"状态[{status.Code}]: {status.Message}";
                    });
                }
            }
        }, _cts.Token);
    }

    private static void TryOpenBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = $"\"{url}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Ignore headless or browser missing error
            }
        }
    }

    private void CloseSelf()
    {
        try
        {
            _httpServer?.Dispose();
            _httpServer = null;
        }
        catch
        {
        }

        try
        {
            _cts.Cancel();
        }
        catch
        {
        }

        Application.RequestStop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _httpServer?.Dispose();
                _httpServer = null;
            }
            catch {}

            try
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            catch {}
        }
        base.Dispose(disposing);
    }
}
