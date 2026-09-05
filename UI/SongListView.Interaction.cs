using System.Collections.ObjectModel;
using Rectangle = System.Drawing.Rectangle;
using Terminal.Gui.Drawing;
using QQMusic.Tui.Models;

namespace QQMusic.Tui.UI;

public sealed partial class SongListView
{
    public void SetFocusToList()
    {
        _listView.SetFocus();
    }

    public void SetSelectedIndex(int index)
    {
        int total = _customItems.Count > 0 ? _customItems.Count : _songs.Count;
        if (index >= 0 && index < total)
        {
            _listView.SelectedItem = index;
            _scrollBar.UpdateMetrics(total, _listView.Viewport.Height, _listView.Viewport.Y);
        }
    }

    public void ApplyInnerScheme(Scheme scheme)
    {
        _listView.SetScheme(scheme);
        var iconScheme = new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None),
            Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
            HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
        };
        _scrollTopBtn.SetScheme(iconScheme);
        _locatePlayingBtn.SetScheme(iconScheme);
    }

    public void ScrollToTop()
    {
        if (_isRadioMode) return;

        var count = _songs.Count > 0 ? _songs.Count : _customItems.Count;
        if (count > 0)
        {
            _listView.SelectedItem = 0;
            _listView.Viewport = new Rectangle(0, 0, _listView.Viewport.Width, _listView.Viewport.Height);
            _listView.SetFocus();
            _listView.SetNeedsDraw();
            StatusNotification?.Invoke("[返回顶部] 已回到列表起始位置");
        }
    }

    public bool LocatePlayingSong()
    {
        if (_isRadioMode) return false;

        if (string.IsNullOrEmpty(_playingSongMid) || _songs.Count == 0)
        {
            StatusNotification?.Invoke("[定位提示] 当前未播放歌曲或列表为空");
            return false;
        }

        int targetIdx = -1;
        for (int i = 0; i < _songs.Count; i++)
        {
            if (_songs[i].Mid == _playingSongMid)
            {
                targetIdx = i;
                break;
            }
        }

        if (targetIdx >= 0)
        {
            _listView.SelectedItem = targetIdx;
            int viewH = _listView.Viewport.Height > 0 ? _listView.Viewport.Height : 20;
            int targetTop = Math.Max(0, targetIdx - (viewH / 2));
            _listView.Viewport = new Rectangle(_listView.Viewport.X, targetTop, _listView.Viewport.Width, _listView.Viewport.Height);
            _listView.SetFocus();
            _listView.SetNeedsDraw();
            var song = _songs[targetIdx];
            StatusNotification?.Invoke($"[已定位] 第 {targetIdx + 1:D2} 首: 《{song.Title}》 - {song.Artist}");
            return true;
        }

        StatusNotification?.Invoke("[定位提示] 当前播放曲目未在当前列表中");
        return false;
    }

    public void SetPlayingSong(string? songMid)
    {
        if (_playingSongMid != songMid)
        {
            _playingSongMid = songMid;
            _listView.SetNeedsDraw();
        }
    }

    public void PageUpList()
    {
        if (_songs.Count == 0) return;
        int pageStep = Math.Max(1, _listView.Viewport.Height > 0 ? _listView.Viewport.Height - 1 : 10);
        int cur = _listView.SelectedItem ?? 0;
        int target = Math.Max(0, cur - pageStep);
        _listView.SelectedItem = target;
        _scrollBar.TriggerActivity();
        UpdateSubColumnTitle(target);
    }

    public void PageDownList()
    {
        if (_songs.Count == 0) return;
        int pageStep = Math.Max(1, _listView.Viewport.Height > 0 ? _listView.Viewport.Height - 1 : 10);
        int cur = _listView.SelectedItem ?? 0;
        int target = Math.Min(_songs.Count - 1, cur + pageStep);
        _listView.SelectedItem = target;
        _scrollBar.TriggerActivity();
        UpdateSubColumnTitle(target);
        CheckTriggerLoadMore();
    }

    public void UpdateFocusedRowDisplay()
    {
        if (_isRadioMode || _songs.Count == 0) return;
        if (_listView.Source is not ObservableCollection<string> obs) return;

        int currentRow = _listView.SelectedItem ?? 0;
        if (currentRow < 0 || currentRow >= _songs.Count) return;

        if (_lastHighlightRow >= 0 && _lastHighlightRow < _songs.Count && _lastHighlightRow != currentRow && _lastHighlightRow < obs.Count)
        {
            obs[_lastHighlightRow] = FormatSongRow(_lastHighlightRow, isSelected: false);
        }

        if (currentRow < obs.Count)
        {
            obs[currentRow] = FormatSongRow(currentRow, isSelected: true);
            _lastHighlightRow = currentRow;
        }

        _listView.SetNeedsDraw();
    }

    private void UpdateSubColumnTitle(int? index = null)
    {
        if (_isRadioMode) return;

        var current = index ?? _listView.SelectedItem ?? 0;
        if (current >= 0 && current < _songs.Count)
        {
            var selSong = _songs[current];
            var albumText = string.IsNullOrWhiteSpace(selSong.Album) ? "单曲" : selSong.Album;
            string subHint = _focusedSubColumn switch
            {
                SongSubColumn.Title => "[歌名 (回车播放)]",
                SongSubColumn.Artist => $"[已选歌手: {selSong.Artist} (回车进入)]",
                SongSubColumn.Album => $"[已选专辑: {albumText} (回车进入)]",
                _ => "[歌名 (回车播放)]"
            };
            SetMarqueeTitle($"《{selSong.Title}》 · {subHint}");
        }
    }
}
