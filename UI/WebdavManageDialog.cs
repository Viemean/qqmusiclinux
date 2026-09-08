using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;

namespace QQMusic.Tui.UI;

public sealed class WebdavManageDialog : Dialog
{
    private readonly ListView _serverListView;
    private readonly Label _statusLabel;
    private readonly Button _selectBtn;
    private readonly Button _addBtn;
    private readonly Button _editBtn;
    private readonly Button _deleteBtn;

    private readonly Action _onServersChanged;
    private readonly Action<WebDavServer> _onSelectAndOpen;
    private readonly List<WebDavServer> _servers = [];
    private readonly ConcurrentDictionary<string, (bool Success, string Message)> _connectionStatusMap = new();
    private bool _isCheckingConnections = false;

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

    public WebdavManageDialog(Action onServersChanged, Action<WebDavServer> onSelectAndOpen)
    {
        _onServersChanged = onServersChanged;
        _onSelectAndOpen = onSelectAndOpen;

        Title = "WebDAV 管理";
        Width = 74;
        Height = 17;
        X = Pos.Center();
        Y = Pos.Center();
        SetScheme(TransparentDialogScheme);

        var tipLabel = new Label
        {
            Text = "已配置的 WebDAV 站点清单 (回车选择进入，支持键盘热键与鼠标点击)：",
            X = 2,
            Y = 0
        };
        tipLabel.SetScheme(TransparentDialogScheme);
        Add(tipLabel);

        _serverListView = new ListView
        {
            X = 2,
            Y = 2,
            Width = Dim.Fill(2),
            Height = 8,
            CanFocus = true
        };
        _serverListView.SetScheme(TransparentDialogScheme);
        _serverListView.KeyBindings.Remove(Key.Space);

        Add(_serverListView);

        _statusLabel = new Label
        {
            Text = "正在自动检测 WebDAV 站点连通性...",
            X = 2,
            Y = 11,
            Width = Dim.Fill(2)
        };
        _statusLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
        });
        Add(_statusLabel);

        // 底部实体交互按钮组（移除了冗余的关闭按钮，按 Esc 即可退出）
        _selectBtn = new Button { Text = "激活 (Enter)", X = 2, Y = 13 };
        _addBtn    = new Button { Text = "添加 (A)", X = Pos.Right(_selectBtn) + 2, Y = 13 };
        _editBtn   = new Button { Text = "编辑 (E)", X = Pos.Right(_addBtn) + 2, Y = 13 };
        _deleteBtn = new Button { Text = "删除 (D)", X = Pos.Right(_editBtn) + 2, Y = 13 };

        _selectBtn.SetScheme(TransparentDialogScheme);
        _addBtn.SetScheme(TransparentDialogScheme);
        _editBtn.SetScheme(TransparentDialogScheme);
        _deleteBtn.SetScheme(TransparentDialogScheme);

        Add(_selectBtn, _addBtn, _editBtn, _deleteBtn);

        // 按钮直接绑定原生 Accepting（完美支持鼠标左键点击与键盘回车/空格触发）
        _selectBtn.Accepting += (s, e) => SelectCurrentServer();
        _addBtn.Accepting += (s, e) => ShowAddDialog();
        _editBtn.Accepting += (s, e) => ShowEditDialog();
        _deleteBtn.Accepting += (s, e) => DeleteCurrentServer();

        _serverListView.Accepted += (s, e) =>
        {
            SelectCurrentServer();
        };

        // 统一按键事件监听，无论焦点在列表还是在任何按钮上，热键均可穿透响应
        _serverListView.KeyDown += (s, k) => HandleCommonKeys(k);
        _selectBtn.KeyDown += (s, k) => HandleCommonKeys(k);
        _addBtn.KeyDown += (s, k) => HandleCommonKeys(k);
        _editBtn.KeyDown += (s, k) => HandleCommonKeys(k);
        _deleteBtn.KeyDown += (s, k) => HandleCommonKeys(k);
        KeyDown += (s, k) => HandleCommonKeys(k);

        ReloadServers();

        // 窗口展现后自动异步执行所有站点连通性测试
        Application.Invoke(() =>
        {
            _serverListView.SetFocus();
            _ = AutoCheckConnectionsAsync();
        });
    }

    protected override bool OnKeyDown(Key key)
    {
        if (HandleCommonKeys(key))
        {
            return true;
        }
        return base.OnKeyDown(key);
    }

    private bool HandleCommonKeys(Key k)
    {
        if (k == Key.Esc)
        {
            k.Handled = true;
            CloseSelf();
            return true;
        }

        if (k == Key.Enter)
        {
            if (_addBtn.HasFocus)
            {
                k.Handled = true;
                ShowAddDialog();
                return true;
            }
            if (_editBtn.HasFocus)
            {
                k.Handled = true;
                ShowEditDialog();
                return true;
            }
            if (_deleteBtn.HasFocus)
            {
                k.Handled = true;
                DeleteCurrentServer();
                return true;
            }

            k.Handled = true;
            SelectCurrentServer();
            return true;
        }

        char c = char.ToUpperInvariant((char)k.AsRune.Value);
        bool isA = k == Key.A || c == 'A';
        bool isE = k == Key.E || c == 'E';
        bool isD = k == Key.D || k == Key.DeleteChar || c == 'D';

        if (isA)
        {
            k.Handled = true;
            ShowAddDialog();
            return true;
        }
        if (isE)
        {
            k.Handled = true;
            ShowEditDialog();
            return true;
        }
        if (isD)
        {
            k.Handled = true;
            DeleteCurrentServer();
            return true;
        }

        return false;
    }

    private async Task AutoCheckConnectionsAsync()
    {
        if (_servers.Count == 0 || _isCheckingConnections)
        {
            if (_servers.Count == 0)
            {
                Application.Invoke(() =>
                {
                    _statusLabel.Text = "暂无配置的 WebDAV 站点，请点击下方「添加 (A)」新增站点";
                    _statusLabel.SetNeedsDraw();
                });
            }
            return;
        }

        _isCheckingConnections = true;
        Application.Invoke(() =>
        {
            _statusLabel.Text = "正在自动检测所有 WebDAV 站点连通性...";
            _statusLabel.SetNeedsDraw();
        });

        var serversSnapshot = _servers.ToList();
        var tasks = serversSnapshot.Select(async s =>
        {
            var res = await WebDavService.TestConnectionAsync(s);
            return (s.Id, res);
        }).ToList();

        var results = await Task.WhenAll(tasks);
        int okCount = 0;
        foreach (var (id, res) in results)
        {
            _connectionStatusMap[id] = res;
            if (res.Success) okCount++;
        }

        _isCheckingConnections = false;
        Application.Invoke(() =>
        {
            UpdateDisplayItems();
            _statusLabel.Text = $"检测完成：{okCount}/{serversSnapshot.Count} 个站点在线正常";
            _statusLabel.SetNeedsDraw();
        });
    }

    private void ReloadServers()
    {
        _servers.Clear();
        _servers.AddRange(WebDavService.GetServers());
        UpdateDisplayItems();
    }

    private void UpdateDisplayItems()
    {
        var active = WebDavService.GetActiveServer();
        var displayItems = new List<string>();
        int selIndex = 0;

        for (int i = 0; i < _servers.Count; i++)
        {
            var s = _servers[i];
            bool isActive = active != null && s.Id == active.Id;
            if (isActive) selIndex = i;

            var mark = isActive ? "[✓ 激活]" : "[      ]";

            string connTag;
            if (_connectionStatusMap.TryGetValue(s.Id, out var res))
            {
                connTag = res.Success ? "[在线]" : "[离线]";
            }
            else
            {
                connTag = "[检测中]";
            }

            displayItems.Add($"{mark} {connTag}  {s.Name}  ({s.Url})");
        }

        _serverListView.SetSource(new ObservableCollection<string>(displayItems));
        if (_servers.Count > 0)
        {
            _serverListView.SelectedItem = Math.Clamp(selIndex, 0, _servers.Count - 1);
        }
        else
        {
            _serverListView.SelectedItem = null;
        }
    }

    private WebDavServer? GetSelectedServer()
    {
        var idx = _serverListView.SelectedItem ?? -1;
        if (idx >= 0 && idx < _servers.Count)
        {
            return _servers[idx];
        }
        return null;
    }

    private void SelectCurrentServer()
    {
        var s = GetSelectedServer();
        if (s != null)
        {
            WebDavService.SetActiveServer(s.Id);
            _onSelectAndOpen.Invoke(s);
            CloseSelf();
        }
    }

    private void ShowAddDialog()
    {
        Application.Invoke(() =>
        {
            var dlg = new WebdavServerEditDialog(null, newServer =>
            {
                WebDavService.SaveServer(newServer);
                ReloadServers();
                _onServersChanged.Invoke();
            });
            Application.Run(dlg);

            ReloadServers();
            _serverListView.SetFocus();
            _ = AutoCheckConnectionsAsync();
        });
    }

    private void ShowEditDialog()
    {
        var s = GetSelectedServer();
        if (s == null) return;

        Application.Invoke(() =>
        {
            var dlg = new WebdavServerEditDialog(s, updatedServer =>
            {
                WebDavService.SaveServer(updatedServer);
                ReloadServers();
                _onServersChanged.Invoke();
            });
            Application.Run(dlg);

            ReloadServers();
            _serverListView.SetFocus();
            _ = AutoCheckConnectionsAsync();
        });
    }

    private void DeleteCurrentServer()
    {
        var s = GetSelectedServer();
        if (s == null) return;

        Application.Invoke(() =>
        {
            var confirmDlg = new Dialog
            {
                Title = "删除确认",
                Width = 56,
                Height = 8,
                X = Pos.Center(),
                Y = Pos.Center()
            };
            confirmDlg.SetScheme(TransparentDialogScheme);

            var msg = new Label
            {
                Text = $"确定要删除 WebDAV 站点「{s.Name}」吗？\n关联的本地曲库缓存也将被清除。",
                X = 2,
                Y = 1,
                Width = Dim.Fill(2)
            };
            msg.SetScheme(TransparentDialogScheme);
            confirmDlg.Add(msg);

            var okBtn = new Button { Text = "确认删除 (Enter)", X = 4, Y = 4 };
            var cancelBtn = new Button { Text = "取消 (Esc)", X = Pos.Right(okBtn) + 3, Y = 4 };
            okBtn.SetScheme(TransparentDialogScheme);
            cancelBtn.SetScheme(TransparentDialogScheme);

            okBtn.Accepting += (sender, args) =>
            {
                Application.RequestStop(confirmDlg);
                WebDavService.RemoveServer(s.Id);
                _connectionStatusMap.TryRemove(s.Id, out _);
                ReloadServers();
                _onServersChanged.Invoke();
                _serverListView.SetFocus();
            };

            cancelBtn.Accepting += (sender, args) =>
            {
                Application.RequestStop(confirmDlg);
            };

            confirmDlg.KeyDown += (sender, k) =>
            {
                if (k == Key.Esc)
                {
                    k.Handled = true;
                    Application.RequestStop(confirmDlg);
                }
            };

            confirmDlg.Add(okBtn, cancelBtn);
            Application.Run(confirmDlg);
        });
    }

    private void CloseSelf()
    {
        Application.RequestStop(this);
    }
}
