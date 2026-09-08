using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Color = Terminal.Gui.Drawing.Color;

namespace QQMusic.Tui.UI;

/// <summary>
/// 主列表内部即时查找悬浮面板 (G 键触发)
/// 规格：宽 52 列、高 3 行、初音绿半透明圆角边框、行粒度去重比对、环形游标循环跳转
/// </summary>
public sealed class QuickSearchFloatingBar : FrameView
{
    private readonly TextField _searchField;
    private readonly Label _countLabel;

    private List<int> _matchedIndices = [];
    private int _currentMatchPointer = -1;

    public event Action<int>? RowSelected;
    public event Action? DismissRequested;
    public Func<string, List<int>>? SearchProvider { get; set; }

    private static Scheme FloatingSearchScheme { get; } = new Scheme
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

    public QuickSearchFloatingBar()
    {
        Title = "列表查找";
        Width = 52;
        Height = 3;
        BorderStyle = LineStyle.Rounded;
        SetScheme(MikuTheme.FrameBorderActive);
        ShadowStyle = ShadowStyles.None;
        Visible = false;

        var prefixLabel = new Label
        {
            Text = "查找:",
            X = 1,
            Y = 0
        };
        prefixLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
        });
        Add(prefixLabel);

        _searchField = new TextField
        {
            X = Pos.Right(prefixLabel) + 1,
            Y = 0,
            Width = Dim.Fill(13),
            CanFocus = true
        };
        _searchField.SetScheme(FloatingSearchScheme);
        Add(_searchField);

        _countLabel = new Label
        {
            Text = "[ 0 / 0 ]",
            X = Pos.AnchorEnd(11),
            Y = 0,
            Width = 10
        };
        _countLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextSub, Color.None)
        });
        Add(_countLabel);

        // 响应输入内容实时触发检索
        _searchField.TextChanged += (s, e) =>
        {
            ExecuteSearchAndNavigate(jumpToFirst: true);
        };

        // 按键监听：Enter / Down 顺向循环跳转，Shift+Enter / Up 逆向循环跳转，Esc 收起
        _searchField.KeyDown += (s, k) =>
        {
            if (k == Key.Esc)
            {
                k.Handled = true;
                Dismiss();
                return;
            }

            if (k == Key.Enter || k.AsRune.Value == '\r' || k.AsRune.Value == '\n')
            {
                k.Handled = true;
                NavigateNext();
                return;
            }

            if (k == Key.CursorDown)
            {
                k.Handled = true;
                NavigateNext();
                return;
            }

            if (k == Key.CursorUp || k == Key.Enter.WithShift)
            {
                k.Handled = true;
                NavigatePrev();
                return;
            }
        };

        KeyDown += (s, k) =>
        {
            if (k == Key.Esc)
            {
                k.Handled = true;
                Dismiss();
                return;
            }
        };
    }

    public void ShowAndFocus()
    {
        Visible = true;
        _searchField.Text = "";
        _matchedIndices.Clear();
        _currentMatchPointer = -1;
        _countLabel.Text = "[ 0 / 0 ]";
        _searchField.SetFocus();
        SetNeedsDraw();
    }

    public void Dismiss()
    {
        Visible = false;
        DismissRequested?.Invoke();
    }

    private void ExecuteSearchAndNavigate(bool jumpToFirst)
    {
        var kw = _searchField.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(kw))
        {
            _matchedIndices.Clear();
            _currentMatchPointer = -1;
            _countLabel.Text = "[ 0 / 0 ]";
            return;
        }

        _matchedIndices = SearchProvider?.Invoke(kw) ?? [];
        if (_matchedIndices.Count > 0)
        {
            if (jumpToFirst || _currentMatchPointer < 0 || _currentMatchPointer >= _matchedIndices.Count)
            {
                _currentMatchPointer = 0;
            }
            _countLabel.Text = $"[ {_currentMatchPointer + 1} / {_matchedIndices.Count} ]";
            RowSelected?.Invoke(_matchedIndices[_currentMatchPointer]);
        }
        else
        {
            _currentMatchPointer = -1;
            _countLabel.Text = "[ 0 / 0 ]";
        }
    }

    private void NavigateNext()
    {
        if (_matchedIndices.Count == 0) return;
        _currentMatchPointer = (_currentMatchPointer + 1) % _matchedIndices.Count;
        _countLabel.Text = $"[ {_currentMatchPointer + 1} / {_matchedIndices.Count} ]";
        RowSelected?.Invoke(_matchedIndices[_currentMatchPointer]);
    }

    private void NavigatePrev()
    {
        if (_matchedIndices.Count == 0) return;
        _currentMatchPointer = (_currentMatchPointer - 1 + _matchedIndices.Count) % _matchedIndices.Count;
        _countLabel.Text = $"[ {_currentMatchPointer + 1} / {_matchedIndices.Count} ]";
        RowSelected?.Invoke(_matchedIndices[_currentMatchPointer]);
    }
}
