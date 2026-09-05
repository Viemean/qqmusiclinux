using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Models;

namespace QQMusic.Tui.UI;

/// <summary>
/// 添加到歌单对话框：展示用户可写的自建歌单列表（含我喜欢），供用户选择并添加当前歌曲
/// </summary>
public sealed class AddToPlaylistDialog : Dialog
{
    private readonly ListView _playlistListView;
    private readonly List<Playlist> _writablePlaylists = [];
    private readonly Action<Playlist> _onSelected;

    public AddToPlaylistDialog(Song song, List<Playlist> playlists, Action<Playlist> onSelected)
    {
        _onSelected = onSelected;

        Title = "添加到歌单 (Add to Playlist)";
        Width = 60;
        Height = 16;
        SetScheme(MikuTheme.Dialog);

        // 仅保留用户有写入权限的自建歌单（含我喜欢）
        _writablePlaylists.AddRange(playlists.Where(p => p.IsCreated));

        var tipLabel = new Label
        {
            Text = $"曲目: {song.Title} - {song.Artist}",
            X = 2,
            Y = 0
        };
        Add(tipLabel);

        var chooseLabel = new Label
        {
            Text = "请选择目标歌单 (回车确认):",
            X = 2,
            Y = 1
        };
        Add(chooseLabel);

        _playlistListView = new ListView
        {
            X = 2,
            Y = 3,
            Width = Dim.Fill(2),
            Height = 7
        };
        _playlistListView.KeyBindings.Remove(Key.Space);

        var displayItems = new List<string>();
        for (int i = 0; i < _writablePlaylists.Count; i++)
        {
            var p = _writablePlaylists[i];
            var typeTag = p.DirId == 201 ? "[我喜欢]" : "[自建]";
            displayItems.Add($"{(i + 1):D2}  {typeTag}  {p.Title}  (共 {p.SongNum} 首)");
        }

        if (displayItems.Count == 0)
        {
            displayItems.Add("暂无可写入的自建歌单");
        }

        _playlistListView.SetSource(new ObservableCollection<string>(displayItems));
        _playlistListView.Accepted += (s, e) => ConfirmSelection();
        Add(_playlistListView);

        var confirmBtn = new Button
        {
            Text = "添加",
            X = Pos.AnchorEnd(20),
            Y = 11
        };
        confirmBtn.KeyBindings.Remove(Key.Space);
        confirmBtn.Accepting += (s, e) => ConfirmSelection();
        Add(confirmBtn);

        var cancelBtn = new Button
        {
            Text = "取消",
            X = Pos.AnchorEnd(10),
            Y = 11
        };
        cancelBtn.KeyBindings.Remove(Key.Space);
        cancelBtn.Accepting += (s, e) => Application.RequestStop();
        Add(cancelBtn);

        KeyDown += (s, k) =>
        {
            if (k == Key.Esc || k == Key.Q)
            {
                Application.RequestStop();
            }
        };

        MikuTheme.ApplyTo(this, MikuTheme.Dialog);
    }

    private void ConfirmSelection()
    {
        var idx = _playlistListView.SelectedItem ?? -1;
        if (idx >= 0 && idx < _writablePlaylists.Count)
        {
            Application.RequestStop();
            _onSelected.Invoke(_writablePlaylists[idx]);
        }
    }
}
