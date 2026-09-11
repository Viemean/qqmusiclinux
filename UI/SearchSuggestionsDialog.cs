using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Models;

namespace QQMusic.Tui.UI;

public sealed class SearchSuggestionsDialog : Dialog
{
    private ListView _listView = null!;
    private readonly List<string?> _values = [];
    private readonly List<bool> _historyRows = [];
    private readonly List<string> _display = [];

    public string? SelectedQuery { get; private set; }

    public SearchSuggestionsDialog(
        IReadOnlyList<string> history,
        IReadOnlyList<string> hotkeys,
        string currentText)
    {
        Title = "搜索建议";
        Width = 58;
        Height = 19;
        X = Pos.Center();
        Y = Pos.Center();
        SetScheme(MikuTheme.FrameBorderDim);

        AddSection("搜索历史", history, isHistory: true);
        AddSection("热门搜索", hotkeys, isHistory: false);
        if (!_values.Any(static value => value != null) && !string.IsNullOrWhiteSpace(currentText))
        {
            _values.Add(currentText.Trim());
            _historyRows.Add(false);
            _display.Add($"  搜索「{currentText.Trim()}」");
        }
        if (_display.Count == 0)
        {
            _values.Add(null);
            _historyRows.Add(false);
            _display.Add("  暂无搜索历史或热门词");
        }

        var hint = new Label
        {
            Text = "Enter 选择  Esc 关闭  C 清空历史",
            X = 2,
            Y = 0,
            Width = Dim.Fill(2)
        };
        hint.SetScheme(MikuTheme.FrameBorderDim);

        _listView = new ListView
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            CanFocus = true
        };
        RefreshSource();
        _listView.Accepted += (_, _) => AcceptSelection();
        _listView.KeyDown += (_, key) =>
        {
            if (key == Key.Enter || key.AsRune.Value == '\r' || key.AsRune.Value == '\n')
            {
                key.Handled = true;
                AcceptSelection();
            }
            else if (char.ToUpperInvariant((char)key.AsRune.Value) == 'C')
            {
                key.Handled = true;
                ClearHistoryRows();
            }
            else if (key == Key.Esc)
            {
                key.Handled = true;
                Application.RequestStop(this);
            }
        };

        KeyDown += (_, key) =>
        {
            if (key == Key.Esc)
            {
                key.Handled = true;
                Application.RequestStop(this);
            }
        };

        Add(hint, _listView);
        MikuTheme.ApplyTo(this, MikuTheme.FrameBorderDim);
        _listView.SetFocus();
    }

    private void AddSection(string name, IReadOnlyList<string> source, bool isHistory)
    {
        var unique = new List<string>();
        foreach (string raw in source)
        {
            string value = raw.Trim();
            if (string.IsNullOrEmpty(value) || _values.Any(existing => existing?.Equals(value, StringComparison.OrdinalIgnoreCase) == true)) continue;
            unique.Add(value);
        }
        if (unique.Count == 0) return;

        _values.Add(null);
        _historyRows.Add(false);
        _display.Add($"── {name} ──");
        foreach (string value in unique)
        {
            _values.Add(value);
            _historyRows.Add(isHistory);
            _display.Add($"  {value}");
        }
    }

    private void ClearHistoryRows()
    {
        SearchHistory.Clear();
        for (int i = _values.Count - 1; i >= 0; i--)
        {
            if (_historyRows[i])
            {
                _values.RemoveAt(i);
                _historyRows.RemoveAt(i);
                _display.RemoveAt(i);
            }
        }
        for (int i = _values.Count - 1; i >= 0; i--)
        {
            if (_values[i] == null && _display[i].Contains("搜索历史", StringComparison.Ordinal))
            {
                _values.RemoveAt(i);
                _historyRows.RemoveAt(i);
                _display.RemoveAt(i);
            }
        }
        RefreshSource();
    }

    private void RefreshSource()
    {
        _listView.SetSource(new ObservableCollection<string>(_display));
        int first = _values.FindIndex(static value => value != null);
        _listView.SelectedItem = first >= 0 ? first : null;
    }

    private void AcceptSelection()
    {
        int index = _listView.SelectedItem ?? -1;
        if (index < 0 || index >= _values.Count || _values[index] == null) return;
        SelectedQuery = _values[index];
        Application.RequestStop(this);
    }
}
