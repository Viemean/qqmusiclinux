using System;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;

namespace QQMusic.Tui.UI;

public sealed class WebdavServerEditDialog : Dialog
{
    private readonly TextField _nameInput;
    private readonly TextField _urlInput;
    private readonly TextField _userInput;
    private readonly TextField _passInput;
    private readonly Label _trustCertLabel;
    private bool _trustSelfSigned;
    private readonly TextField _rootPathInput;
    private readonly Label _statusLabel;

    private readonly WebDavServer _server;
    private readonly Action<WebDavServer> _onSaved;

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

    public WebdavServerEditDialog(WebDavServer? existingServer, Action<WebDavServer> onSaved)
    {
        _server = existingServer ?? new WebDavServer();
        _onSaved = onSaved;

        Title = existingServer == null ? "添加 WebDAV 站点" : "编辑 WebDAV 站点";
        Width = 66;
        Height = 20;
        X = Pos.Center();
        Y = Pos.Center();
        SetScheme(TransparentDialogScheme);

        // 1. 站点名称
        Add(new Label { Text = "站点名称:", X = 2, Y = 0 });
        _nameInput = new TextField
        {
            X = 14,
            Y = 0,
            Width = Dim.Fill(2),
            Text = _server.Name
        };
        _nameInput.SetScheme(TransparentDialogScheme);
        Add(_nameInput);

        // 2. 服务器 URL
        Add(new Label { Text = "服务 URL:", X = 2, Y = 2 });
        _urlInput = new TextField
        {
            X = 14,
            Y = 2,
            Width = Dim.Fill(2),
            Text = _server.Url
        };
        _urlInput.SetScheme(TransparentDialogScheme);
        Add(_urlInput);

        // 3. 用户名
        Add(new Label { Text = "用 户 名:", X = 2, Y = 4 });
        _userInput = new TextField
        {
            X = 14,
            Y = 4,
            Width = Dim.Fill(2),
            Text = _server.Username
        };
        _userInput.SetScheme(TransparentDialogScheme);
        Add(_userInput);

        // 4. 密码
        Add(new Label { Text = "密    码:", X = 2, Y = 6 });
        _passInput = new TextField
        {
            X = 14,
            Y = 6,
            Width = Dim.Fill(2),
            Text = _server.Password,
            Secret = true
        };
        _passInput.SetScheme(TransparentDialogScheme);
        Add(_passInput);

        // 5. 根路径
        Add(new Label { Text = "初始路径:", X = 2, Y = 8 });
        _rootPathInput = new TextField
        {
            X = 14,
            Y = 8,
            Width = Dim.Fill(2),
            Text = string.IsNullOrEmpty(_server.RootPath) ? "/" : _server.RootPath
        };
        _rootPathInput.SetScheme(TransparentDialogScheme);
        Add(_rootPathInput);

        // 6. 信任自签名证书（单层方括号切换控件）
        _trustSelfSigned = _server.TrustSelfSigned;
        Add(new Label { Text = "安全选项:", X = 2, Y = 10 });
        _trustCertLabel = new Label
        {
            X = 14,
            Y = 10,
            CanFocus = true,
            TabStop = TabBehavior.TabGroup
        };
        UpdateTrustCertLabel();
        _trustCertLabel.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                m.Handled = true;
                _trustCertLabel.SetFocus();
                _trustSelfSigned = !_trustSelfSigned;
                UpdateTrustCertLabel();
            }
        };
        _trustCertLabel.KeyDown += (s, k) =>
        {
            if (k == Key.Space || k == Key.Enter)
            {
                k.Handled = true;
                _trustSelfSigned = !_trustSelfSigned;
                UpdateTrustCertLabel();
            }
        };
        _trustCertLabel.HasFocusChanged += (s, e) => UpdateTrustCertLabel();
        Add(_trustCertLabel);

        // 7. 状态提示（初始为空，无需冗余提示信息）
        _statusLabel = new Label
        {
            Text = "",
            X = 2,
            Y = 12,
            Width = Dim.Fill(2)
        };
        _statusLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
        });
        Add(_statusLabel);

        // 底部按钮组
        var testBtn = new Button { Text = "测试连接 (T)", X = 2, Y = 14 };
        var saveBtn = new Button { Text = "保存 (Enter)", X = Pos.Right(testBtn) + 2, Y = 14 };
        var cancelBtn = new Button { Text = "取消 (Esc)", X = Pos.Right(saveBtn) + 2, Y = 14 };

        Add(testBtn, saveBtn, cancelBtn);

        testBtn.Accepting += async (s, e) =>
        {
            ApplyInputsToServer();
            _statusLabel.Text = "正在尝试连接 WebDAV 服务器...";
            _statusLabel.SetNeedsDraw();
            var res = await WebDavService.TestConnectionAsync(_server);
            _statusLabel.Text = res.Success ? $"✓ {res.Message}" : $"✗ {res.Message}";
            _statusLabel.SetNeedsDraw();
        };

        saveBtn.Accepting += (s, e) => ConfirmSave();
        cancelBtn.Accepting += (s, e) => CloseSelf();

        KeyDown += (s, k) =>
        {
            if (k == Key.Esc)
            {
                k.Handled = true;
                CloseSelf();
                return;
            }
            if (k == Key.Enter && !_nameInput.HasFocus && !_urlInput.HasFocus && !_userInput.HasFocus && !_passInput.HasFocus && !_rootPathInput.HasFocus)
            {
                k.Handled = true;
                ConfirmSave();
                return;
            }
        };
    }

    private void UpdateTrustCertLabel()
    {
        var mark = _trustSelfSigned ? "[✓]" : "[ ]";
        _trustCertLabel.Text = $"{mark} 信任自签名证书 (HTTPS)";

        if (_trustCertLabel.HasFocus)
        {
            _trustCertLabel.SetScheme(new Scheme
            {
                Normal = new Terminal.Gui.Drawing.Attribute(Color.White, MikuTheme.QqGreenDark),
                Focus  = new Terminal.Gui.Drawing.Attribute(Color.White, MikuTheme.QqGreenDark)
            });
        }
        else if (_trustSelfSigned)
        {
            _trustCertLabel.SetScheme(new Scheme
            {
                Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None),
                Focus  = new Terminal.Gui.Drawing.Attribute(Color.White, MikuTheme.QqGreenDark)
            });
        }
        else
        {
            _trustCertLabel.SetScheme(new Scheme
            {
                Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Color.None),
                Focus  = new Terminal.Gui.Drawing.Attribute(Color.White, MikuTheme.QqGreenDark)
            });
        }
    }

    private void ApplyInputsToServer()
    {
        _server.Name = string.IsNullOrWhiteSpace(_nameInput.Text) ? "WebDAV" : _nameInput.Text.Trim();
        _server.Url = _urlInput.Text?.Trim() ?? "";
        _server.Username = _userInput.Text?.Trim() ?? "";
        _server.Password = _passInput.Text?.Trim() ?? "";
        _server.RootPath = string.IsNullOrWhiteSpace(_rootPathInput.Text) ? "/" : _rootPathInput.Text.Trim();
        _server.TrustSelfSigned = _trustSelfSigned;
    }

    private void ConfirmSave()
    {
        ApplyInputsToServer();
        if (string.IsNullOrEmpty(_server.Url))
        {
            _statusLabel.Text = "错误: 请输入有效的服务器 URL (如 http://nas:5005/)";
            _urlInput.SetFocus();
            return;
        }

        if (!_server.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !_server.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _server.Url = "http://" + _server.Url;
        }

        _onSaved.Invoke(_server);
        CloseSelf();
    }

    private void CloseSelf()
    {
        Application.RequestStop(this);
    }

    protected override bool OnAccepting(CommandEventArgs? args)
    {
        // 阻止基类默认将内部按钮 Accepting 冒泡作为退出对话框
        return false;
    }
}
