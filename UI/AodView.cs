using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Models;

namespace QQMusic.Tui.UI;

/// <summary>
/// AOD (Always On Display) 极简后台息屏视图
/// 类似手机 AOD 熄屏显示，纯黑静谧背景，仅在屏幕正中央居中显示：
/// 歌曲名字
/// 歌手 - 专辑
/// 冻结一切界面高频重绘，进入低功耗待机状态
/// </summary>
public sealed class AodView : View
{
    private readonly Label _titleLabel;
    private readonly Label _subtitleLabel;
    private Song? _currentSong;

    public AodView()
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;

        var blackScheme = new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Color.Black),
            Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Color.Black),
            HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Color.Black),
            HotFocus = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Color.Black)
        };
        SetScheme(blackScheme);

        // 歌曲名字居中（翡翠浅绿高亮）
        _titleLabel = new Label
        {
            X = Pos.Center(),
            Y = Pos.Center() - 1,
            TextAlignment = Alignment.Center
        };
        _titleLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.Black),
            Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.Black)
        });

        // 歌手 - 专辑居中（暗青弱化灰）
        _subtitleLabel = new Label
        {
            X = Pos.Center(),
            Y = Pos.Center() + 1,
            TextAlignment = Alignment.Center
        };
        _subtitleLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.Black),
            Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.Black)
        });

        Add(_titleLabel, _subtitleLabel);
        UpdateSong(null);
    }

    public void UpdateSong(Song? song)
    {
        _currentSong = song;
        if (_currentSong == null)
        {
            _titleLabel.Text = "暂无播放音乐";
            _subtitleLabel.Text = "按 Esc 退出后台模式";
        }
        else
        {
            _titleLabel.Text = _currentSong.Title ?? "未知歌曲";
            var artist = string.IsNullOrWhiteSpace(_currentSong.Artist) ? "未知歌手" : _currentSong.Artist;
            var album = string.IsNullOrWhiteSpace(_currentSong.Album) ? "未知专辑" : _currentSong.Album;
            _subtitleLabel.Text = $"{artist} - {album}";
        }

        _titleLabel.SetNeedsDraw();
        _subtitleLabel.SetNeedsDraw();
        SetNeedsDraw();
    }
}
