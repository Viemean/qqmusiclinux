using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace QQMusic.Tui.UI;

/// <summary>
/// 极简微型半透明自隐藏滚动条：
/// 1. 宽 1 格，平时静止 3 秒后自动隐藏，保持界面清爽；
/// 2. 列表/歌词滚动或鼠标滑动时光标亮起展示；
/// 3. 支持鼠标点击滑块上下方分页跳转，以及鼠标直接点击/拖动滑块直达对应位置。
/// </summary>
public sealed class ThinScrollBarView : View
{
    private readonly Label _trackLabel;
    private int _totalItems = 0;
    private int _visibleItems = 0;
    private int _firstVisibleItem = 0;
    private long _lastActivityTick = 0;
    private bool _isActive = false;
    private object? _timeoutToken;

    public event Action<int>? ScrollPositionChanged;

    public ThinScrollBarView()
    {
        Width = 1;
        Height = Dim.Fill();
        CanFocus = false;
        TabStop = TabBehavior.NoStop;

        _trackLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = 1,
            Height = Dim.Fill(),
            CanFocus = false,
            TabStop = TabBehavior.NoStop,
            Text = ""
        };
        _trackLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None),
            Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None),
            HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None)
        });
        Add(_trackLabel);

        // 定时检查是否超过 3 秒无活动，平滑隐藏
        _timeoutToken = Application.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            if (_isActive && Environment.TickCount64 - _lastActivityTick > 3000)
            {
                _isActive = false;
                _trackLabel.Text = "";
                SetNeedsDraw();
            }
            return true;
        });

        MouseEvent += (s, m) =>
        {
            TriggerActivity();

            if (_totalItems <= _visibleItems || _visibleItems <= 0)
            {
                return;
            }

            if (!m.Position.HasValue) return;

            int clickY = m.Position.Value.Y;
            int trackHeight = Viewport.Height > 0 ? Viewport.Height : (Frame.Height > 0 ? Frame.Height : 20);
            int maxFirstItem = Math.Max(1, _totalItems - _visibleItems);
            int thumbHeight = Math.Max(1, (int)Math.Round((double)_visibleItems / _totalItems * trackHeight));
            int maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
            int thumbTop = maxThumbTop > 0 ? Math.Clamp((int)Math.Round((double)_firstVisibleItem / maxFirstItem * maxThumbTop), 0, maxThumbTop) : 0;

            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                m.Handled = true;
                if (clickY < thumbTop)
                {
                    // 点击滑块上方：向上翻页
                    int target = Math.Max(0, _firstVisibleItem - _visibleItems);
                    ScrollPositionChanged?.Invoke(target);
                }
                else if (clickY >= thumbTop + thumbHeight)
                {
                    // 点击滑块下方：向下翻页
                    int target = Math.Min(maxFirstItem, _firstVisibleItem + _visibleItems);
                    ScrollPositionChanged?.Invoke(target);
                }
                else
                {
                    // 直接拖拽或点中滑块
                    if (maxThumbTop > 0)
                    {
                        double ratio = (double)clickY / trackHeight;
                        int target = Math.Clamp((int)Math.Round(ratio * maxFirstItem), 0, maxFirstItem);
                        ScrollPositionChanged?.Invoke(target);
                    }
                }
            }
            else if (m.Flags.HasFlag(MouseFlags.WheeledUp))
            {
                m.Handled = true;
                ScrollPositionChanged?.Invoke(Math.Max(0, _firstVisibleItem - 3));
            }
            else if (m.Flags.HasFlag(MouseFlags.WheeledDown))
            {
                m.Handled = true;
                ScrollPositionChanged?.Invoke(Math.Min(maxFirstItem, _firstVisibleItem + 3));
            }
        };
    }

    public bool AutoShowOnMetricsChange { get; set; } = true;

    public void UpdateMetrics(int totalItems, int visibleItems, int firstVisibleItem)
    {
        bool changed = _totalItems != totalItems || _visibleItems != visibleItems || _firstVisibleItem != firstVisibleItem;
        _totalItems = totalItems;
        _visibleItems = visibleItems;
        _firstVisibleItem = firstVisibleItem;

        if (changed)
        {
            if (AutoShowOnMetricsChange)
            {
                TriggerActivity();
            }
            else if (_isActive)
            {
                RenderScrollBar();
            }
        }
    }

    public void TriggerActivity()
    {
        _lastActivityTick = Environment.TickCount64;
        if (!_isActive)
        {
            _isActive = true;
        }
        RenderScrollBar();
    }

    private void RenderScrollBar()
    {
        if (!_isActive || _totalItems <= _visibleItems || _visibleItems <= 0)
        {
            _trackLabel.Text = "";
            SetNeedsDraw();
            return;
        }

        int trackHeight = Viewport.Height > 0 ? Viewport.Height : (Frame.Height > 0 ? Frame.Height : 20);
        int maxFirstItem = Math.Max(1, _totalItems - _visibleItems);
        int thumbHeight = Math.Max(1, (int)Math.Round((double)_visibleItems / _totalItems * trackHeight));
        int maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        int thumbTop = maxThumbTop > 0 ? Math.Clamp((int)Math.Round((double)_firstVisibleItem / maxFirstItem * maxThumbTop), 0, maxThumbTop) : 0;

        var sb = new StringBuilder();
        for (int y = 0; y < trackHeight; y++)
        {
            if (y >= thumbTop && y < thumbTop + thumbHeight)
            {
                sb.Append('█');
            }
            else
            {
                sb.Append('│');
            }
            if (y < trackHeight - 1)
            {
                sb.Append('\n');
            }
        }

        _trackLabel.Text = sb.ToString();
        SetNeedsDraw();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _timeoutToken != null)
        {
            Application.RemoveTimeout(_timeoutToken);
            _timeoutToken = null;
        }
        base.Dispose(disposing);
    }
}
