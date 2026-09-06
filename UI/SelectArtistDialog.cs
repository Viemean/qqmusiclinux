using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Models;

namespace QQMusic.Tui.UI;

/// <summary>
/// 多歌手选择弹窗
/// </summary>
public sealed class SelectArtistDialog : Dialog
{
    private readonly ListView _artistListView;
    private readonly List<ArtistInfo> _artists;
    private readonly Action<ArtistInfo>? _onSelected;

    public ArtistInfo? SelectedArtist { get; private set; }

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

    public SelectArtistDialog(IReadOnlyList<ArtistInfo> artists, Action<ArtistInfo>? onSelected = null, bool inLyricArea = false)
    {
        _artists = artists.ToList();
        _onSelected = onSelected;

        Title = "选择歌手";
        int dlgW = 46;
        int dlgH = Math.Clamp(_artists.Count + 7, 9, 14);
        Width = dlgW;
        Height = dlgH;
        Y = Pos.Center();

        if (inLyricArea)
        {
            // 定位在右侧歌词区域水平中心，避开左侧封面图层
            X = Pos.Percent(74) - (dlgW / 2);
        }
        else
        {
            X = Pos.Center();
        }

        SetScheme(TransparentDialogScheme);

        var promptLabel = new Label
        {
            Text = "该歌曲包含多位歌手，请选择进入：",
            X = 2,
            Y = 0
        };
        promptLabel.SetScheme(TransparentDialogScheme);
        Add(promptLabel);

        _artistListView = new ListView
        {
            X = 2,
            Y = 2,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
            CanFocus = true
        };
        _artistListView.SetScheme(TransparentDialogScheme);

        var displayItems = _artists.Select((a, idx) => $"{idx + 1:D2}.  {a.Name}").ToList();
        _artistListView.SetSource(new ObservableCollection<string>(displayItems));
        _artistListView.SelectedItem = 0;

        // 键盘回车或空格直接确认选择进入歌手主页，支持数字键直达
        _artistListView.KeyDown += (s, k) =>
        {
            if (k == Key.Enter || k == Key.Space || k.AsRune.Value == '\r' || k.AsRune.Value == '\n')
            {
                k.Handled = true;
                ConfirmSelection();
                return;
            }

            // 支持数字键 1~9 快捷选择并直接进入
            var ch = (char)k.AsRune.Value;
            if (ch >= '1' && ch <= '9')
            {
                int numIdx = ch - '1';
                if (numIdx < _artists.Count)
                {
                    k.Handled = true;
                    _artistListView.SelectedItem = numIdx;
                    ConfirmSelection();
                    return;
                }
            }
        };

        // 鼠标单击直接选中并关闭弹窗（即点即选）
        _artistListView.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) && m.Position.HasValue)
            {
                var clickedIdx = _artistListView.Viewport.Y + m.Position.Value.Y;
                if (clickedIdx >= 0 && clickedIdx < _artists.Count)
                {
                    _artistListView.SelectedItem = clickedIdx;
                    ConfirmSelection();
                    m.Handled = true;
                }
            }
        };

        _artistListView.Accepted += (s, e) => ConfirmSelection();

        KeyDown += (s, k) =>
        {
            if (k == Key.Esc || k.AsRune.Value == 'q' || k.AsRune.Value == 'Q')
            {
                k.Handled = true;
                Application.RequestStop(this);
            }
            else if (k == Key.Enter || k.AsRune.Value == '\r' || k.AsRune.Value == '\n')
            {
                k.Handled = true;
                ConfirmSelection();
            }
        };

        // 底部居中对称摆放按钮，保留充裕间距防止字符重叠
        var confirmBtn = new Button
        {
            Text = "进入主页",
            X = Pos.Center() - 14,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        confirmBtn.SetScheme(TransparentDialogScheme);
        confirmBtn.Accepting += (s, e) => ConfirmSelection();

        var cancelBtn = new Button
        {
            Text = "取消 (Esc)",
            X = Pos.Center() + 2,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        cancelBtn.SetScheme(TransparentDialogScheme);
        cancelBtn.Accepting += (s, e) => Application.RequestStop(this);

        Add(_artistListView, confirmBtn, cancelBtn);
        MikuTheme.ApplyTo(this, TransparentDialogScheme);

        // 确保默认焦点赋予歌手列表
        _artistListView.SetFocus();
    }

    private void ConfirmSelection()
    {
        var idx = _artistListView.SelectedItem ?? 0;
        if (idx >= 0 && idx < _artists.Count)
        {
            SelectedArtist = _artists[idx];
            Application.RequestStop(this);
            _onSelected?.Invoke(SelectedArtist);
        }
    }
}
