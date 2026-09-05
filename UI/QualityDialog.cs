using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;

namespace QQMusic.Tui.UI;

public sealed class QualityDialog : Dialog
{
    private readonly ListView _qualityListView;
    private readonly Action<AudioQualityTier, QualityOption?> _onQualitySelected;
    private readonly List<QualityOption> _options = [];
    private AudioQualityTier _currentTier;

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

    public QualityDialog(Song? activeSong, AudioQualityTier currentTier, Action<AudioQualityTier, QualityOption?> onQualitySelected, string customTitle = "音质切换")
    {
        _currentTier = currentTier;
        _onQualitySelected = onQualitySelected;

        Title = customTitle;
        Width = 56;
        Height = 11;
        SetScheme(TransparentDialogScheme);

        var tipLabel = new Label
        {
            Text = activeSong != null
                ? $"曲目: {activeSong.Title} - {activeSong.Artist}"
                : "全局默认音质设置 (无正在播放曲目)",
            X = 2,
            Y = 0
        };
        tipLabel.SetScheme(TransparentDialogScheme);
        Add(tipLabel);

        _qualityListView = new ListView
        {
            X = 2,
            Y = 2,
            Width = Dim.Fill(2),
            Height = 4
        };
        _qualityListView.SetScheme(TransparentDialogScheme);

        // 初始化默认档位
        BuildDefaultOptions();
        RefreshDisplayList();

        _qualityListView.KeyBindings.Remove(Key.Space);
        _qualityListView.Accepted += (s, e) => ApplySelection();
        Add(_qualityListView);

        var confirmBtn = new Button
        {
            Text = "确定",
            X = Pos.AnchorEnd(20),
            Y = 7,
            ShadowStyle = ShadowStyles.None
        };
        confirmBtn.KeyBindings.Remove(Key.Space);
        confirmBtn.Accepting += (s, e) => ApplySelection();
        Add(confirmBtn);

        var cancelBtn = new Button
        {
            Text = "取消",
            X = Pos.AnchorEnd(10),
            Y = 7,
            ShadowStyle = ShadowStyles.None
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

        // 异步探测真实音源状态
        if (activeSong != null)
        {
            Task.Run(async () =>
            {
                var probed = await QqMusicApi.ProbeSongQualitiesAsync(activeSong.Mid, activeSong.EffectiveMediaMid);
                Application.Invoke(() =>
                {
                    _options.Clear();
                    _options.AddRange(probed);
                    RefreshDisplayList();
                });
            });
        }

        MikuTheme.ApplyTo(this, TransparentDialogScheme);
    }

    private void BuildDefaultOptions()
    {
        _options.Clear();
        _options.Add(new QualityOption(AudioQualityTier.HiRes, "Hi-Res", "Hi-Res", "24bit / 96kHz", "", true));
        _options.Add(new QualityOption(AudioQualityTier.SQ, "SQ", "SQ", "16bit / 44.1kHz", "", true));
        _options.Add(new QualityOption(AudioQualityTier.HQ, "HQ", "HQ", "320kbps", "", true));
        _options.Add(new QualityOption(AudioQualityTier.Standard, "标准", "标准", "128kbps", "", true));
    }

    private void RefreshDisplayList()
    {
        var displayList = new List<string>();
        int selectedIndex = 0;

        for (int i = 0; i < _options.Count; i++)
        {
            var opt = _options[i];
            var isCurrent = opt.Tier == _currentTier;
            if (isCurrent) selectedIndex = i;
            displayList.Add(opt.DisplayText(isCurrent));
        }

        _qualityListView.SetSource(new ObservableCollection<string>(displayList));
        if (selectedIndex >= 0 && selectedIndex < displayList.Count)
        {
            _qualityListView.SelectedItem = selectedIndex;
        }
    }

    private void ApplySelection()
    {
        var idx = _qualityListView.SelectedItem ?? -1;
        if (idx >= 0 && idx < _options.Count)
        {
            var opt = _options[idx];
            if (!opt.Available)
            {
                return;
            }

            _currentTier = opt.Tier;
            _onQualitySelected?.Invoke(opt.Tier, opt);
            Application.RequestStop();
        }
    }
}
