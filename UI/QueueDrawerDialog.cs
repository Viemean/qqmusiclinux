using System.Collections.ObjectModel;
using System.Globalization;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace QQMusic.Tui.UI;

/// <summary>
/// 待播播放队列抽屉对话框，用于查看、即时插播与整理后续曲目
/// </summary>
public sealed class QueueDrawerDialog : Dialog
{
    private readonly ListView _queueListView;
    private readonly Label _currentPlayingLabel;
    private List<Song> _cachedSongs = [];

    private long _lastClickTick = 0;
    private int _lastClickRow = -1;

    public Song? SelectedSongToPlay { get; private set; }

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

    public QueueDrawerDialog()
    {
        Title = "播放列表";
        Width = 76;
        Height = 18;
        SetScheme(TransparentDialogScheme);

        _currentPlayingLabel = new Label
        {
            X = 2,
            Y = 0,
            Width = Dim.Fill(2),
            Height = 1
        };
        _currentPlayingLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
        });
        Add(_currentPlayingLabel);

        _queueListView = new ListView
        {
            X = 2,
            Y = 2,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2)
        };
        _queueListView.SetScheme(TransparentDialogScheme);
        _queueListView.KeyBindings.Remove(Key.Space);
        Add(_queueListView);

        var hintLabel = new Label
        {
            Text = "[Enter/双击] 播放   [D/Del] 移除选中   [C] 清空列表   [Esc] 关闭",
            X = 2,
            Y = Pos.AnchorEnd(1)
        };
        hintLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None)
        });
        Add(hintLabel);

        ReloadQueueData();

        // 原生激活事件（按回车或终端双击）触发播放
        _queueListView.Accepted += (s, e) =>
        {
            int idx = _queueListView.SelectedItem ?? -1;
            PlaySongAtIndex(idx);
        };

        // 鼠标单击与双击交互：支持原生双击标志与 400ms 快速连击判定（适配 Linux 终端驱动）
        _queueListView.MouseEvent += (s, m) =>
        {
            if (m.Position.HasValue && (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked)))
            {
                var clickedRow = _queueListView.Viewport.Y + m.Position.Value.Y;
                bool isDoubleClick = m.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked);

                if (!isDoubleClick && m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
                {
                    var now = Environment.TickCount64;
                    if (now - _lastClickTick < 400 && _lastClickRow == clickedRow)
                    {
                        isDoubleClick = true;
                        _lastClickTick = 0;
                    }
                    else
                    {
                        _lastClickTick = now;
                        _lastClickRow = clickedRow;
                    }
                }

                if (isDoubleClick)
                {
                    PlaySongAtIndex(clickedRow);
                }
            }
        };

        // 统一按键分发逻辑，直接在 ListView 与 Dialog 双重拦截，防止 ListView 字符搜索吞噬按键
        void HandleKeyAction(Key k)
        {
            if (!MainWindow.IsTerminalWindowFocused)
            {
                k.Handled = true;
                return;
            }

            if (k == Key.Esc)
            {
                k.Handled = true;
                Application.RequestStop(this);
                return;
            }

            if (k == Key.Enter || k.AsRune.Value == '\r' || k.AsRune.Value == '\n')
            {
                k.Handled = true;
                int idx = _queueListView.SelectedItem ?? -1;
                PlaySongAtIndex(idx);
                return;
            }

            bool isD = k == Key.DeleteChar || k == Key.D || k == Key.D.WithShift ||
                       k.AsRune.Value == 'd' || k.AsRune.Value == 'D';
            if (isD)
            {
                k.Handled = true;
                RemoveCurrentSelectedItem();
                return;
            }

            bool isC = k == Key.C || k == Key.C.WithShift ||
                       k.AsRune.Value == 'c' || k.AsRune.Value == 'C';
            if (isC)
            {
                k.Handled = true;
                PlaybackQueueService.Instance.ClearUpcoming();
                ReloadQueueData();
                return;
            }
        }

        _queueListView.KeyDown += (s, k) => HandleKeyAction(k);
        KeyDown += (s, k) =>
        {
            if (k.Handled) return;
            HandleKeyAction(k);
        };
    }

    private void PlaySongAtIndex(int idx)
    {
        if (idx >= 0 && idx < _cachedSongs.Count)
        {
            SelectedSongToPlay = _cachedSongs[idx];
            PlaybackQueueService.Instance.SetCurrentIndex(idx);
            Application.RequestStop(this);
        }
    }

    private void ReloadQueueData()
    {
        var queue = PlaybackQueueService.Instance;
        _cachedSongs = queue.ActiveSongs.ToList();
        int curIdx = queue.CurrentIndex;
        var curSong = queue.CurrentSong;

        Title = $"播放列表 (共 {_cachedSongs.Count} 首)";

        if (curSong != null)
        {
            _currentPlayingLabel.Text = $"▶ 正在播放: {curSong.Title} - {curSong.Artist}";
        }
        else
        {
            _currentPlayingLabel.Text = "  队列暂无正在播放的歌曲";
        }

        if (_cachedSongs.Count == 0)
        {
            _queueListView.SetSource(new ObservableCollection<string>(["  (播放队列为空，在主列表按回车起播或按 N 插队)"]));
            return;
        }

        var displayItems = new List<string>(_cachedSongs.Count);
        for (int i = 0; i < _cachedSongs.Count; i++)
        {
            var s = _cachedSongs[i];
            string marker = (i == curIdx) ? "▶ " : "  ";
            string titlePart = SongListView.TruncateAndPadWide(s.Title, 34);
            string artistPart = SongListView.TruncateAndPadWide(s.Artist, 24);
            displayItems.Add($"{marker}{i + 1,2}. {titlePart} - {artistPart}");
        }

        _queueListView.SetSource(new ObservableCollection<string>(displayItems));
        if (curIdx >= 0 && curIdx < displayItems.Count)
        {
            _queueListView.SelectedItem = curIdx;
        }
    }

    public void RemoveCurrentSelectedItem()
    {
        int idx = _queueListView.SelectedItem ?? -1;
        if (idx >= 0 && idx < _cachedSongs.Count)
        {
            PlaybackQueueService.Instance.RemoveAt(idx);
            ReloadQueueData();
            if (idx < _cachedSongs.Count)
            {
                _queueListView.SelectedItem = idx;
            }
            else if (_cachedSongs.Count > 0)
            {
                _queueListView.SelectedItem = _cachedSongs.Count - 1;
            }
        }
    }

    public void ClearUpcomingSongs()
    {
        PlaybackQueueService.Instance.ClearUpcoming();
        ReloadQueueData();
    }

    public void PlayCurrentSelectedItem()
    {
        int idx = _queueListView.SelectedItem ?? -1;
        PlaySongAtIndex(idx);
    }
}
