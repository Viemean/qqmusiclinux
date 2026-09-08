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

    public LoginDialog(Action onLoginSuccess)
    {
        _onLoginSuccess = onLoginSuccess;

        Title = "用户登录";
        Width = 68;
        Height = 28;
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

        Add(webTabBtn, qrTabBtn, logoutBtn, closeBtn);

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
            Height = 20,
            CanFocus = false
        };
        _qrView.SetScheme(TransparentDialogScheme);
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

                var qr = await LoginService.FetchQrCodeAsync(_cts.Token);
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

                _httpServer?.UpdateQrCode(qr.PngBytes);
                _httpServer?.UpdateStatus("等待手机扫码...");

                Application.Invoke(() =>
                {
                    _webStatusLabel.Text = "状态: 等待手机扫码...";
                    _qrStatusLabel.Text = "状态: 等待手机扫码...";
                    _qrView.SetSource(new ObservableCollection<string>(qr.AsciiLines));
                    _qrTipLabel.Text = $"手机扫码或浏览器打开: {_httpServer?.LanUrl} (按 R 刷新)";
                });

                // 轮询该二维码状态
                while (!_cts.Token.IsCancellationRequested && !_refreshRequested)
                {
                    try { await Task.Delay(2000, _cts.Token); } catch { break; }
                    if (_refreshRequested) break;

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
                        try { await Task.Delay(1500, _cts.Token); } catch { }
                        _httpServer?.Stop();
                        Application.Invoke(CloseSelf);
                        return;
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
                    else if (status.Code == 65) // 失效：自动重启外循环换取新二维码
                    {
                        _httpServer?.UpdateStatus("二维码已失效，正在自动换新...");
                        Application.Invoke(() =>
                        {
                            _webStatusLabel.Text = "状态[65]: 二维码已失效，正在自动换新...";
                            _qrStatusLabel.Text = "状态[65]: 二维码已失效，正在自动换新...";
                        });
                        try { await Task.Delay(1000, _cts.Token); } catch { }
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
