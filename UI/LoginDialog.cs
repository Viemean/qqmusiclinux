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

    // 1. 网页登录容器 (默认首选)
    private readonly View _webContainer;
    private readonly Label _webLanUrlLabel;
    private readonly Label _webLocalUrlLabel;
    private readonly Label _webStatusLabel;
    private readonly Button _openBrowserBtn;

    // 2. 终端字符扫码容器
    private readonly View _qrContainer;
    private readonly Label _qrStatusLabel;
    private readonly Label _qrTipLabel;
    private readonly ListView _qrView;

    // 3. Cookie 导入容器
    private readonly View _cookieContainer;
    private readonly TextField _cookieInput;
    private readonly Label _cookieStatusLabel;

    public LoginDialog(Action onLoginSuccess)
    {
        _onLoginSuccess = onLoginSuccess;

        Title = "用户登录";
        Width = 66;
        Height = 28;
        SetScheme(MikuTheme.Dialog);

        // 顶部切换按钮
        var webTabBtn = new Button
        {
            Text = "网页登录 (Web)",
            X = 2,
            Y = 0
        };

        var qrTabBtn = new Button
        {
            Text = "终端扫码",
            X = Pos.Right(webTabBtn) + 2,
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
            Text = "退出账号",
            X = Pos.Right(cookieTabBtn) + 2,
            Y = 0
        };

        var closeBtn = new Button
        {
            Text = "关闭 (Esc)",
            X = Pos.AnchorEnd(14),
            Y = 0
        };

        Add(webTabBtn, qrTabBtn, cookieTabBtn, logoutBtn, closeBtn);

        // ==================== 1. 网页登录容器 (默认激活) ====================
        _webContainer = new View
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Visible = true
        };

        var webTitleLabel = new Label
        {
            Text = "网页扫码登录",
            X = 2,
            Y = 1
        };
        _webContainer.Add(webTitleLabel);

        var webDescLabel = new Label
        {
            Text = "在浏览器中打开以下任一地址完成扫码授权：",
            X = 2,
            Y = 3
        };
        _webContainer.Add(webDescLabel);

        _webLanUrlLabel = new Label
        {
            Text = "局域网地址: 正在获取...",
            X = 2,
            Y = 5
        };
        _webContainer.Add(_webLanUrlLabel);

        _webLocalUrlLabel = new Label
        {
            Text = "本机地址: 正在获取...",
            X = 2,
            Y = 7
        };
        _webContainer.Add(_webLocalUrlLabel);

        _webStatusLabel = new Label
        {
            Text = "状态: 正在初始化二维码...",
            X = 2,
            Y = 10
        };
        _webContainer.Add(_webStatusLabel);

        _openBrowserBtn = new Button
        {
            Text = "打开本机浏览器 (B)",
            X = 2,
            Y = 13
        };
        _openBrowserBtn.Accepting += (s, e) =>
        {
            if (_httpServer != null && _httpServer.IsRunning)
            {
                TryOpenBrowser(_httpServer.LocalUrl);
            }
        };
        _webContainer.Add(_openBrowserBtn);

        var webHelpLabel = new Label
        {
            Text = "提示: 手机或外部设备在浏览器中打开上述地址完成扫码即可，终端会自动同步登录状态。\n如需直接在终端显示二维码，可切换至 [终端扫码]。",
            X = 2,
            Y = 16,
            Width = Dim.Fill(2)
        };
        _webContainer.Add(webHelpLabel);

        Add(_webContainer);

        // ==================== 2. 终端字符扫码容器 ====================
        _qrContainer = new View
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Visible = false
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

        Add(_qrContainer);

        // ==================== 3. Cookie 导入容器 ====================
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

        // ==================== 事件交互处理 ====================
        webTabBtn.Accepting += (s, e) =>
        {
            _webContainer.Visible = true;
            _qrContainer.Visible = false;
            _cookieContainer.Visible = false;
        };

        qrTabBtn.Accepting += (s, e) =>
        {
            _webContainer.Visible = false;
            _qrContainer.Visible = true;
            _cookieContainer.Visible = false;
        };

        cookieTabBtn.Accepting += (s, e) =>
        {
            _webContainer.Visible = false;
            _qrContainer.Visible = false;
            _cookieContainer.Visible = true;
            _cookieInput.SetFocus();
        };

        logoutBtn.Accepting += (s, e) =>
        {
            LoginService.Logout();
            _webStatusLabel.Text = "状态: 已退出登录";
            _qrStatusLabel.Text = "状态: 已退出登录";
            _httpServer?.UpdateStatus("已退出登录");
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
                TryOpenBrowser(_httpServer.LocalUrl);
            }
        };

        // 启动网络扫码服务与轮询
        StartQrLoginFlow();

        MikuTheme.ApplyTo(this, MikuTheme.Dialog);
    }

    private void StartQrLoginFlow()
    {
        _httpServer ??= new LoginHttpServer();
        _httpServer.Start(null);

        Application.Invoke(() =>
        {
            _webLanUrlLabel.Text = $"局域网: {_httpServer.LanUrl}";
            _webLocalUrlLabel.Text = $"本机: {_httpServer.LocalUrl}";
        });

        Task.Run(async () =>
        {
            var qr = await LoginService.FetchQrCodeAsync(_cts.Token);
            if (qr == null)
            {
                Application.Invoke(() =>
                {
                    _webStatusLabel.Text = "状态: 二维码生成失败，请检查网络";
                    _qrStatusLabel.Text = "状态: 二维码生成失败，请检查网络";
                });
                _httpServer?.UpdateStatus("二维码生成失败，请检查网络");
                return;
            }

            _httpServer.UpdateQrCode(qr.PngBytes);
            _httpServer.UpdateStatus("等待扫码...");

            Application.Invoke(() =>
            {
                _webStatusLabel.Text = "状态: 等待扫码...";
                _qrStatusLabel.Text = "状态: 等待扫码...";
                _qrView.SetSource(new ObservableCollection<string>(qr.AsciiLines));
                _qrTipLabel.Text = $"手机扫码或浏览器打开: {_httpServer.LanUrl}";
            });

            // 轮询登录状态
            while (!_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(2000, _cts.Token);
                var status = await LoginService.PollQrStatusAsync(qr.QrSig, qr.PtqrToken, _cts.Token);

                if (status.Code == 0) // 成功
                {
                    _httpServer?.UpdateStatus($"登录成功 [{UserSession.Current.Nick}]", isSuccess: true, nick: UserSession.Current.Nick);
                    Application.Invoke(() =>
                    {
                        _webStatusLabel.Text = $"状态: 登录成功 [{UserSession.Current.Nick}]";
                        _qrStatusLabel.Text = $"状态[0]: 登录成功 [{UserSession.Current.Nick}]";
                        _onLoginSuccess?.Invoke();
                    });
                    await Task.Delay(1500);
                    _httpServer?.Stop();
                    Application.Invoke(CloseSelf);
                    break;
                }
                else if (status.Code == 67) // 认证中
                {
                    _httpServer?.UpdateStatus("已扫码，请在手机上确认授权...");
                    Application.Invoke(() =>
                    {
                        _webStatusLabel.Text = "状态: 已扫码，请在手机上确认授权...";
                        _qrStatusLabel.Text = "状态[67]: 已扫码，请在手机上确认授权...";
                    });
                }
                else if (status.Code == 65) // 失效
                {
                    _httpServer?.UpdateStatus("二维码已失效，请重新打开登录窗口");
                    Application.Invoke(() =>
                    {
                        _webStatusLabel.Text = "状态: 二维码已失效，请重新打开登录窗口";
                        _qrStatusLabel.Text = "状态[65]: 二维码已失效，请重新打开登录窗口";
                    });
                    _httpServer?.Stop();
                    break;
                }
                else
                {
                    _httpServer?.UpdateStatus(status.Message ?? "等待扫码...");
                    Application.Invoke(() =>
                    {
                        _webStatusLabel.Text = $"状态[{status.Code}]: {status.Message}";
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
