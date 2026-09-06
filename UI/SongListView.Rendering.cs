using System.Collections.ObjectModel;
using System.Text;
using Rectangle = System.Drawing.Rectangle;
using Terminal.Gui.Drawing;
using Terminal.Gui.Text;
using QQMusic.Tui.Models;

namespace QQMusic.Tui.UI;

public sealed partial class SongListView
{
    private bool OnMarqueeTick()
    {
        if (_isMarqueePaused || string.IsNullOrEmpty(_currentFullTitle)) return true;

        int curWidth = Viewport.Width > 0 ? Viewport.Width : Frame.Width;
        int maxW = Math.Max(12, curWidth - 4);
        int textW = GetDisplayWidth(_currentFullTitle);

        if (textW <= maxW)
        {
            if (_lastDispatchedTitle != _currentFullTitle)
            {
                _lastDispatchedTitle = _currentFullTitle;
                _marqueeOffset = 0;
                DisplayTitleChanged?.Invoke(_currentFullTitle);
            }
            return true;
        }

        string loopText = _currentFullTitle + "        ";
        int runeCount = loopText.GetRuneCount();
        if (runeCount > 0)
        {
            _marqueeOffset = (_marqueeOffset + 1) % runeCount;
        }
        var slice = GetMarqueeSlice(loopText, _marqueeOffset, maxW);
        if (_lastDispatchedTitle != slice)
        {
            _lastDispatchedTitle = slice;
            DisplayTitleChanged?.Invoke(slice);
        }

        return true;
    }

    private void SetMarqueeTitle(string fullText)
    {
        if (_currentFullTitle == fullText) return;

        _currentFullTitle = fullText;
        _marqueeOffset = 0;

        int curWidth = Viewport.Width > 0 ? Viewport.Width : Frame.Width;
        int maxW = Math.Max(12, curWidth - 4);
        int textW = GetDisplayWidth(fullText);

        if (textW <= maxW)
        {
            _lastDispatchedTitle = fullText;
            DisplayTitleChanged?.Invoke(fullText);
        }
        else
        {
            string loopText = fullText + "        ";
            var slice = GetMarqueeSlice(loopText, 0, maxW);
            _lastDispatchedTitle = slice;
            DisplayTitleChanged?.Invoke(slice);
        }
    }

    private void CheckTriggerLoadMore()
    {
        if (_isRadioMode) return;

        int totalCount = _songs.Count > 0 ? _songs.Count : _customItems.Count;
        if (totalCount == 0) return;

        int current = _listView.SelectedItem ?? 0;
        int viewBottom = _listView.Viewport.Y + _listView.Viewport.Height;

        if (current >= totalCount - 6 || viewBottom >= totalCount - 4)
        {
            LoadMoreRequested?.Invoke();
        }
    }

    private void RefreshRadioDisplay()
    {
        if (!_isRadioMode || _currentRadioSong == null) return;

        int totalWidth = _listView.Viewport.Width;
        if (totalWidth <= 0)
        {
            int curW = Viewport.Width > 0 ? Viewport.Width : Frame.Width;
            totalWidth = curW > 0 ? curW - 2 : 68;
        }

        int cardWidth = Math.Clamp(totalWidth - 4, 38, 64);
        int indent = Math.Max(0, (totalWidth - cardWidth) / 2);
        string pad = new string(' ', indent);
        string borderLine = pad + "+" + new string('-', cardWidth - 2) + "+";
        int innerW = cardWidth - 4;

        var song = _currentRadioSong;
        var albumStr = string.IsNullOrWhiteSpace(song.Album) ? "单曲" : song.Album;

        string FormatRow(string content)
        {
            int w = GetDisplayWidth(content);
            if (w < innerW)
            {
                return pad + "| " + content + new string(' ', innerW - w) + " |";
            }
            if (w > innerW)
            {
                return pad + "| " + TruncateAndPadWide(content, innerW) + " |";
            }
            return pad + "| " + content + " |";
        }

        string FormatCenteredRow(string content)
        {
            int w = GetDisplayWidth(content);
            if (w >= innerW)
            {
                return FormatRow(content);
            }
            int leftPad = (innerW - w) / 2;
            int rightPad = innerW - w - leftPad;
            return pad + "| " + new string(' ', leftPad) + content + new string(' ', rightPad) + " |";
        }

        var lines = new List<string>(18)
        {
            "",
            borderLine,
            FormatCenteredRow("[ 个性电台 · 猜你喜欢 ]"),
            borderLine,
            FormatRow(""),
            FormatRow($"  曲名: 《{song.Title}》"),
            FormatRow($"  歌手: {song.Artist}"),
            FormatRow($"  专辑: {albumStr}"),
            FormatRow($"  音质: {_currentRadioQuality}          收听计数: 第 {_currentRadioPlayedCount:D2} 首"),
            FormatRow(""),
            borderLine,
            FormatRow("  [ 快捷操作指南 ]"),
            FormatRow(""),
            FormatRow("  [Space]   播放 / 暂停        [ L ] 下一首 (切歌)"),
            FormatRow("  [ S ]     收藏当前播放       [ V ] 沉浸播放界面"),
            FormatRow("  [ M ]     静音 / 恢复音量    [ O ] 切换播放顺序"),
            FormatRow(""),
            borderLine
        };

        _radioDisplayLines.Clear();
        _radioDisplayLines.AddRange(lines);
        _listView.SetSource(new ObservableCollection<string>(_radioDisplayLines));
    }

    private void RefreshDisplayList()
    {
        if (_isUpdatingDisplay) return;

        if (_songs.Count == 0)
        {
            _listView.SetSource(new ObservableCollection<string>());
            _listView.SelectedItem = null;
            _scrollBar.UpdateMetrics(0, _listView.Viewport.Height, 0);
            return;
        }

        try
        {
            _isUpdatingDisplay = true;

            int totalWidth = _listView.Viewport.Width;
            if (totalWidth <= 0)
            {
                int curW = Viewport.Width > 0 ? Viewport.Width : Frame.Width;
                totalWidth = curW > 0 ? curW - 2 : 80;
            }

            int remain = Math.Max(30, totalWidth - 13);
            _titleColWidth = Math.Max(16, (int)Math.Round(remain * 0.46));
            _artistColWidth = Math.Max(10, (int)Math.Round(remain * 0.24));
            _albumColWidth = Math.Max(12, remain - _titleColWidth - _artistColWidth);

            var prevSelected = _listView.SelectedItem;
            int selectedIdx = (prevSelected.HasValue && prevSelected.Value >= 0 && prevSelected.Value < _songs.Count)
                ? prevSelected.Value
                : 0;

            var displayList = new List<string>(_songs.Count);
            for (int i = 0; i < _songs.Count; i++)
            {
                displayList.Add(FormatSongRow(i, isSelected: (i == selectedIdx)));
            }
            _lastHighlightRow = selectedIdx;

            var prevViewportY = _listView.Viewport.Y;
            _listView.SetSource(new ObservableCollection<string>(displayList));
            if (prevSelected.HasValue && prevSelected.Value >= 0 && prevSelected.Value < _songs.Count)
            {
                _listView.SelectedItem = prevSelected.Value;
            }
            else if (_songs.Count > 0)
            {
                _listView.SelectedItem = 0;
            }
            else
            {
                _listView.SelectedItem = null;
            }
            if (prevViewportY > 0)
            {
                _listView.Viewport = new Rectangle(_listView.Viewport.X, prevViewportY, _listView.Viewport.Width, _listView.Viewport.Height);
            }
            _scrollBar.UpdateMetrics(_songs.Count, _listView.Viewport.Height, _listView.Viewport.Y);
        }
        finally
        {
            _isUpdatingDisplay = false;
        }
    }

    private string FormatSongRow(int index, bool isSelected)
    {
        if (index < 0 || index >= _songs.Count) return "";
        var s = _songs[index];
        var albumStr = string.IsNullOrWhiteSpace(s.Album) ? "单曲" : s.Album;

        string titleText = s.Title;
        string artistText = s.Artist;
        string albumText = albumStr;

        if (isSelected)
        {
            switch (_focusedSubColumn)
            {
                case SongSubColumn.Title:
                    titleText = $"[ {s.Title} ]";
                    break;
                case SongSubColumn.Artist:
                    artistText = $"[▶ {s.Artist} ◀]";
                    break;
                case SongSubColumn.Album:
                    albumText = $"[▶ {albumStr} ◀]";
                    break;
            }
        }

        var titleCol = TruncateAndPadWide(titleText, _titleColWidth);
        var artistCol = TruncateAndPadWide(artistText, _artistColWidth);
        var albumCol = TruncateAndPadWide(albumText, _albumColWidth);
        return $"{(index + 1):D2}  {titleCol}  {artistCol}  {albumCol}";
    }

    public static int GetDisplayWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.GetColumns();
    }

    private static string GetMarqueeSlice(string loopText, int offset, int targetWidth)
    {
        if (string.IsNullOrEmpty(loopText)) return "";
        var runes = loopText.ToRunes();
        int totalLen = runes.Length;
        if (totalLen == 0) return "";
        offset %= totalLen;

        var sb = new StringBuilder();
        int curW = 0;
        for (int i = 0; i < totalLen * 2; i++)
        {
            var rune = runes[(offset + i) % totalLen];
            int runeW = rune.GetColumns();
            if (curW + runeW > targetWidth)
            {
                break;
            }
            sb.Append(rune);
            curW += runeW;
        }

        if (curW < targetWidth)
        {
            sb.Append(' ', targetWidth - curW);
        }
        return sb.ToString();
    }

    public static string TruncateAndPadWide(string text, int targetWidth)
    {
        if (targetWidth <= 0) return "";
        if (string.IsNullOrEmpty(text))
        {
            return new string(' ', targetWidth);
        }

        int totalW = GetDisplayWidth(text);
        if (totalW == targetWidth)
        {
            return text;
        }
        if (totalW < targetWidth)
        {
            return text + new string(' ', targetWidth - totalW);
        }

        int limit = Math.Max(1, targetWidth - 2);
        int currentW = 0;
        var sb = new StringBuilder(targetWidth);

        foreach (var rune in text.EnumerateRunes())
        {
            int runeW = rune.GetColumns();
            if (currentW + runeW > limit)
            {
                break;
            }
            sb.Append(rune);
            currentW += runeW;
        }

        sb.Append("..");
        currentW += 2;

        if (currentW < targetWidth)
        {
            sb.Append(' ', targetWidth - currentW);
        }

        return sb.ToString();
    }
}
