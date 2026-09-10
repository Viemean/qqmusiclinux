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
    private LoginService.QrLoginType _loginType = LoginService.QrLoginType.Qq;

    private static Scheme TransparentDialogScheme { get; } = new Scheme
    {
        Normal    = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Color.None),
        Focus     = new Terminal.Gui.Drawing.Attribute(Color.White, MikuTheme.QqGreenDark),
        HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuPinkAccent, Color.None),
        HotFocus  = new Terminal.Gui.Drawing.Attribute(Color.White, MikuTheme.MikuPinkAccent),
        Disabled  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None),
        Highlight = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None),
        Active    = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
        ReadOnly  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None),
        Editable  = new Terminal.Gui.Drawing.Attribute(Color.White, Color.None)
    };

    /// <summary>
    /// 终端二维码专用高对比度实心深色背景方案（防止透明终端导致扫码对比度不足）
    /// </summary>
    private static Scheme QrCodeScheme { get; } = new Scheme
    {
        Normal    = new Terminal.Gui.Drawing.Attribute(Color.White, Color.Black),
        Focus     = new Terminal.Gui.Drawing.Attribute(Color.White, Color.Black),
        HotNormal = new Terminal.Gui.Drawing.Attribute(Color.White, Color.Black),
        HotFocus  = new Terminal.Gui.Drawing.Attribute(Color.White, Color.Black),
        Disabled  = new Terminal.Gui.Drawing.Attribute(Color.White, Color.Black),
        Highlight = new Terminal.Gui.Drawing.Attribute(Color.White, Color.Black),
        Active    = new Terminal.Gui.Drawing.Attribute(Color.White, Color.Black),
        ReadOnly  = new Terminal.Gui.Drawing.Attribute(Color.White, Color.Black),
        Editable  = new Terminal.Gui.Drawing.Attribute(Color.White, Color.Black)
    };

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
    private readonly Label _providerLabel;
    private readonly ListView _qrView;

    public LoginDialog(Action onLoginSuccess)
    {
        _onLoginSuccess = onLoginSuccess;

        Title = "QQ音乐账号登录";
        Width = 74;
        Height = 32;
        X = Pos.Center();
        Y = Pos.Center();
        SetScheme(TransparentDialogScheme);

        // 顶部切换按钮
        var webTabBtn = new Button
        {
            Text = "网页登录 (W)",
            X = 2,
            Y = 0
        };

        var qrTabBtn = new Button
        {
            Text = "终端扫码 (T)",
            X = Pos.Right(webTabBtn) + 2,
            Y = 0
        };

        var qqBtn = new Button
        {
            Text = "QQ (1)",
            X = 2,
            Y = 2
        };

        var weChatBtn = new Button
        {
            Text = "微信 (2)",
            X = Pos.Right(qqBtn) + 2,
            Y = 2
        };

        var qqMusicBtn = new Button
        {
            Text = "QQ音乐 (3)",
            X = Pos.Right(weChatBtn) + 2,
            Y = 2
        };

        _providerLabel = new Label
        {
            Text = "当前方式: QQ",
            X = Pos.Right(qqMusicBtn) + 2,
            Y = 2
        };

        var logoutBtn = new Button
        {
            Text = "退出账号",
            X = Pos.Right(qrTabBtn) + 2,
            Y = 0
        };

        var closeBtn = new Button
        {
            Text = "关闭 (Esc)",
            X = Pos.AnchorEnd(14),
            Y = 0
        };

        Add(webTabBtn, qrTabBtn, logoutBtn, closeBtn, qqBtn, weChatBtn, qqMusicBtn, _providerLabel);

        // ==================== 1. 网页登录容器 (默认激活) ====================
        _webContainer = new View
        {
            X = 0,
            Y = 4,
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
            Y = 4,
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
            X = Pos.Center(),
            Y = 1,
            Width = 45,
            Height = 23,
            CanFocus = false
        };
        _qrView.SetScheme(QrCodeScheme);
        _qrContainer.Add(_qrView);

        _qrTipLabel = new Label
        {
            Text = "提示: 手机扫码授权后将自动同步",
            X = Pos.Center(),
            Y = 24,
            Width = Dim.Fill(2),
            TextAlignment = Alignment.Center
        };
        _qrTipLabel.SetScheme(TransparentDialogScheme);
        _qrContainer.Add(_qrTipLabel);

        Add(_qrContainer);

        // ==================== 事件交互处理 ====================
        void SwitchToWebTab()
        {
            _webContainer.Visible = true;
            _qrContainer.Visible = false;
            webTabBtn.SetFocus();
            SetNeedsDraw();
        }

        void SwitchToQrTab()
        {
            _webContainer.Visible = false;
            _qrContainer.Visible = true;
            qrTabBtn.SetFocus();
            SetNeedsDraw();
        }

        webTabBtn.Accepting += (s, e) => SwitchToWebTab();
        qrTabBtn.Accepting += (s, e) => SwitchToQrTab();

        void SelectProvider(LoginService.QrLoginType type)
        {
            if (_loginType == type) return;
            _loginType = type;
            var name = LoginService.GetLoginTypeName(type);
            _providerLabel.Text = $"当前方式: {name}";
            _httpServer?.UpdateLoginType(name);
            _webStatusLabel.Text = $"状态: 正在切换到{name}登录...";
            _qrStatusLabel.Text = $"状态: 正在切换到{name}登录...";
            RequestRefresh();
            SetNeedsDraw();
        }

        qqBtn.Accepting += (s, e) => SelectProvider(LoginService.QrLoginType.Qq);
        weChatBtn.Accepting += (s, e) => SelectProvider(LoginService.QrLoginType.WeChat);
        qqMusicBtn.Accepting += (s, e) => SelectProvider(LoginService.QrLoginType.QqMusic);

        logoutBtn.Accepting += (s, e) =>
        {
            LoginService.Logout();
            _webStatusLabel.Text = "状态: 已退出登录";
            _qrStatusLabel.Text = "状态: 已退出登录";
            _httpServer?.UpdateStatus("已退出登录");
            _onLoginSuccess?.Invoke();
        };

        closeBtn.Accepting += (s, e) => CloseSelf();

        KeyDown += (s, k) =>
        {
            if (k == Key.Esc || k.AsRune.Value == 'q' || k.AsRune.Value == 'Q')
            {
                k.Handled = true;
                CloseSelf();
                return;
            }

            char c = char.ToUpperInvariant((char)k.AsRune.Value);
            if (c == 'W')
            {
                k.Handled = true;
                SwitchToWebTab();
                return;
            }
            if (c == 'T')
            {
                k.Handled = true;
                SwitchToQrTab();
                return;
            }
            if (c == '1')
            {
                k.Handled = true;
                SelectProvider(LoginService.QrLoginType.Qq);
                return;
            }
            if (c == '2')
            {
                k.Handled = true;
                SelectProvider(LoginService.QrLoginType.WeChat);
                return;
            }
            if (c == '3')
            {
                k.Handled = true;
                SelectProvider(LoginService.QrLoginType.QqMusic);
                return;
            }
            if (c == 'R')
            {
                k.Handled = true;
                RequestRefresh();
                return;
            }
            if (c == 'B' && _httpServer != null && _httpServer.IsRunning)
            {
                k.Handled = true;
                TryOpenBrowser(_httpServer.LocalUrl);
                return;
            }
        };

        // 启动网络扫码服务与轮询
        StartQrLoginFlow();
    }

    private volatile bool _refreshRequested;

    private void RequestRefresh()
    {
        _refreshRequested = true;
    }

    private void StartQrLoginFlow()
    {
        _httpServer ??= new LoginHttpServer();
        _httpServer.RefreshRequested += () =>
        {
            RequestRefresh();
            return Task.CompletedTask;
        };
        _httpServer.Start(null);

        Application.Invoke(() =>
        {
            _webLanUrlLabel.Text = $"局域网: {_httpServer.LanUrl}";
            _webLocalUrlLabel.Text = $"本机: {_httpServer.LocalUrl}";
        });

        Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                _refreshRequested = false;

                Application.Invoke(() =>
                {
                    _webStatusLabel.Text = "状态: 正在拉取二维码...";
                    _qrStatusLabel.Text = "状态: 正在拉取二维码...";
                });
                _httpServer?.UpdateStatus("正在获取二维码...");

                var selectedType = _loginType;
                _httpServer?.UpdateLoginType(LoginService.GetLoginTypeName(selectedType));
                var qr = await LoginService.FetchQrCodeAsync(selectedType, _cts.Token);
                if (qr == null)
                {
                    Application.Invoke(() =>
                    {
                        _webStatusLabel.Text = "状态: 二维码生成失败，按 R 重试";
                        _qrStatusLabel.Text = "状态: 二维码生成失败，按 R 重试";
                    });
                    _httpServer?.UpdateStatus("二维码生成失败，请点击刷新重试");

                    for (int i = 0; i < 15 && !_refreshRequested && !_cts.Token.IsCancellationRequested; i++)
                    {
                        try { await Task.Delay(200, _cts.Token); } catch { break; }
                    }
                    continue;
                }

                _httpServer?.UpdateQrCode(qr.ImageBytes, qr.MimeType);
                _httpServer?.UpdateStatus($"等待使用{LoginService.GetLoginTypeName(selectedType)}扫码...");

                Application.Invoke(() =>
                {
                    _webStatusLabel.Text = $"状态: 等待使用{LoginService.GetLoginTypeName(selectedType)}扫码...";
                    _qrStatusLabel.Text = $"状态: 等待使用{LoginService.GetLoginTypeName(selectedType)}扫码...";
                    _qrView.SetSource(new ObservableCollection<string>(qr.AsciiLines));
                    _qrTipLabel.Text = $"使用{LoginService.GetLoginTypeName(selectedType)}扫码，或浏览器打开: {_httpServer?.LanUrl} (按 R 刷新)";
                });

                if (selectedType == LoginService.QrLoginType.QqMusic)
                {
                    var status = await LoginService.WaitForQqMusicQrLoginAsync(qr, next =>
                    {
                        _httpServer?.UpdateStatus(next.Message);
                        Application.Invoke(() =>
                        {
                            _webStatusLabel.Text = $"状态: {next.Message}";
                            _qrStatusLabel.Text = $"状态: {next.Message}";
                        });
                    }, _cts.Token);

                    if (status.Event == LoginService.QrLoginEvent.Done)
                    {
                        _httpServer?.UpdateStatus($"登录成功 [{UserSession.Current.Nick}]", isSuccess: true, nick: UserSession.Current.Nick);
                        Application.Invoke(() =>
                        {
                            _onLoginSuccess?.Invoke();
                            CloseSelf();
                        });
                        return;
                    }
                    if (status.Event == LoginService.QrLoginEvent.Expired) continue;
                    while (!_cts.Token.IsCancellationRequested && !_refreshRequested && selectedType == _loginType)
                    {
                        try { await Task.Delay(500, _cts.Token); } catch { break; }
                    }
                    continue;
                }

                while (!_cts.Token.IsCancellationRequested && !_refreshRequested && selectedType == _loginType)
                {
                    try { await Task.Delay(1500, _cts.Token); } catch { break; }
                    if (_refreshRequested || selectedType != _loginType) break;

                    var status = await LoginService.PollQrStatusAsync(qr, _cts.Token);
                    if (status.Event == LoginService.QrLoginEvent.Done)
                    {
                        _httpServer?.UpdateStatus($"登录成功 [{UserSession.Current.Nick}]", isSuccess: true, nick: UserSession.Current.Nick);
                        Application.Invoke(() =>
                        {
                            _webStatusLabel.Text = $"状态: 登录成功 [{UserSession.Current.Nick}]";
                            _qrStatusLabel.Text = $"状态: 登录成功 [{UserSession.Current.Nick}]";
                            _onLoginSuccess?.Invoke();
                        });
                        try { await Task.Delay(1500, _cts.Token); } catch { }
                        _httpServer?.Stop();
                        Application.Invoke(CloseSelf);
                        return;
                    }

                    if (status.Event == LoginService.QrLoginEvent.Expired)
                    {
                        _httpServer?.UpdateStatus("二维码已失效，正在自动换新...");
                        Application.Invoke(() =>
                        {
                            _webStatusLabel.Text = "状态: 二维码已失效，正在自动换新...";
                            _qrStatusLabel.Text = "状态: 二维码已失效，正在自动换新...";
                        });
                        try { await Task.Delay(1000, _cts.Token); } catch { }
                        break;
                    }

                    _httpServer?.UpdateStatus(status.Message);
                    Application.Invoke(() =>
                    {
                        _webStatusLabel.Text = $"状态: {status.Message}";
                        _qrStatusLabel.Text = $"状态: {status.Message}";
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

    public void CloseSelf()
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

        Application.RequestStop(this);
    }

    protected override bool OnAccepting(CommandEventArgs? args)
    {
        // 阻断基类 Dialog 默认将内部任意 Button 的 Accepting 冒泡作为 RequestStop() 的行为。
        // LoginDialog 的退出由 closeBtn 与 Esc/Q 快捷键显式调用 CloseSelf() 负责。
        return false;
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
