using System;
using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Services;

namespace QQMusic.Tui.UI;

public sealed class FolderManageDialog : Dialog
{
    private readonly ListView _folderListView;
    private readonly Action _onFoldersChanged;
    private readonly Action _onAddRequested;
    private readonly Action _onRescanRequested;
    private readonly List<string> _folders = [];

    private static Scheme TransparentDialogScheme { get; } = new Scheme
    {
        Normal    = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Terminal.Gui.Drawing.Color.None),
        Focus     = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, MikuTheme.QqGreenDark),
        HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuPinkAccent, Terminal.Gui.Drawing.Color.None),
        HotFocus  = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, MikuTheme.MikuPinkAccent),
        Disabled  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Terminal.Gui.Drawing.Color.None),
        Highlight = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Terminal.Gui.Drawing.Color.None),
        Active    = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
        ReadOnly  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Terminal.Gui.Drawing.Color.None),
        Editable  = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, Terminal.Gui.Drawing.Color.None)
    };

    public FolderManageDialog(Action onFoldersChanged, Action onAddRequested, Action onRescanRequested)
    {
        _onFoldersChanged = onFoldersChanged;
        _onAddRequested = onAddRequested;
        _onRescanRequested = onRescanRequested;

        Title = "本地音乐目录管理";
        Width = 66;
        Height = 14;
        SetScheme(TransparentDialogScheme);

        var tipLabel = new Label
        {
            Text = "已添加的扫描目录 (按 D 移除选中项，按 A 新增)：",
            X = 2,
            Y = 0
        };
        tipLabel.SetScheme(TransparentDialogScheme);
        Add(tipLabel);

        _folderListView = new ListView
        {
            X = 2,
            Y = 2,
            Width = Dim.Fill(2),
            Height = 6
        };
        _folderListView.SetScheme(TransparentDialogScheme);
        _folderListView.KeyBindings.Remove(Key.Space);

        ReloadFolders();
        Add(_folderListView);

        var hintLabel = new Label
        {
            Text = "[A] 添加目录   [D] 删除目录   [R] 重新扫描   [Esc] 关闭",
            X = 2,
            Y = 9
        };
        hintLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Terminal.Gui.Drawing.Color.None)
        });
        Add(hintLabel);

        KeyDown += (s, k) =>
        {
            if (k == Key.Esc)
            {
                k.Handled = true;
                RequestStop();
                return;
            }

            char c = char.ToUpperInvariant((char)k.AsRune.Value);
            if (c == 'A')
            {
                k.Handled = true;
                RequestStop();
                _onAddRequested.Invoke();
            }
            else if (c == 'D')
            {
                k.Handled = true;
                DeleteSelected();
            }
            else if (c == 'R')
            {
                k.Handled = true;
                RequestStop();
                _onRescanRequested.Invoke();
            }
        };
    }

    private void ReloadFolders()
    {
        _folders.Clear();
        _folders.AddRange(LocalMusicService.GetFolders());

        var displayList = new List<string>();
        if (_folders.Count == 0)
        {
            displayList.Add("(当前暂无已添加的目录，请按 A 键添加)");
        }
        else
        {
            for (int i = 0; i < _folders.Count; i++)
            {
                displayList.Add($"{i + 1}. {_folders[i]}");
            }
        }

        _folderListView.SetSource(new ObservableCollection<string>(displayList));
    }

    private void DeleteSelected()
    {
        if (_folders.Count == 0) return;
        var selectedIdx = _folderListView.SelectedItem ?? 0;
        if (selectedIdx >= 0 && selectedIdx < _folders.Count)
        {
            var folder = _folders[selectedIdx];
            LocalMusicService.RemoveFolder(folder);
            ReloadFolders();
            _onFoldersChanged.Invoke();
        }
    }
}
