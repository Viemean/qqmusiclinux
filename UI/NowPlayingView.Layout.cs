using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Models;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Color = Terminal.Gui.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;

namespace QQMusic.Tui.UI;

public sealed partial class NowPlayingView
{

    private void RenderCoverIfVisible()
    {
        if (!Visible || !TerminalImageHelper.IsImageSupported || string.IsNullOrEmpty(_coverFilePath) || !File.Exists(_coverFilePath))
        {
            return;
        }

        try
        {
            var origin = _coverContainer.FrameToScreen();
            int col = Math.Max(1, origin.X);
            int row = Math.Max(1, origin.Y);
            int containerCols = Math.Max(10, _coverContainer.Viewport.Width);
            int containerRows = Math.Max(6, _coverContainer.Viewport.Height);

            // 预留底部 5 行用于展示歌曲信息（歌手-歌曲名、留空行、专辑名），保持留白呼吸感
            int availableRowsForCover = Math.Max(4, containerRows - 5);
            int maxRowsByHeight = Math.Max(4, (int)(availableRowsForCover * 0.92));
            int maxRowsByWidth = Math.Max(4, (int)((containerCols * 0.85) / 2));
            int targetRows = Math.Max(4, Math.Min(maxRowsByHeight, maxRowsByWidth));
            int targetCols = targetRows * 2;

            // 水平居中
            int colOffset = Math.Max(1, (containerCols - targetCols) / 2);
            // 垂直居中于可用区域
            int rowOffset = Math.Max(1, (availableRowsForCover - targetRows) / 2);

            int renderCol = col + colOffset;
            int renderRow = Math.Max(1, row + rowOffset);

            TerminalImageHelper.RenderKittyImage(_coverFilePath, renderCol, renderRow, targetCols, targetRows);

            // 严格对齐：底部信息容器 X 坐标与封面起始列完全相同（colOffset），保持绝对左对齐
            UpdateSongInfoLayout(colOffset, targetCols, rowOffset + targetRows + 1);
        }
        catch
        {
            // 容错处理
        }
    }

    private void UpdateSongInfoLayout(int colOffset, int targetCols, int topRow)
    {
        if (_songInfoContainer == null) return;

        // 与封面始终保持绝对左对齐
        _songInfoContainer.X = colOffset;
        _songInfoContainer.Y = topRow;
        _songInfoContainer.Width = targetCols;
        _songInfoContainer.Height = 3;

        // 第 0 行：歌手 - 歌曲名
        _artistLink.X = 0;
        _artistLink.Y = 0;

        _hyphenLabel.X = Pos.Right(_artistLink);
        _hyphenLabel.Y = 0;

        _songTitleLabel.X = Pos.Right(_hyphenLabel);
        _songTitleLabel.Y = 0;
        _songTitleLabel.Width = Dim.Fill();

        // 第 1 行：留空间隔一行

        // 第 2 行：专辑名字（间隔一行距离）
        _albumLink.X = 0;
        _albumLink.Y = 2;
        _albumLink.Width = Dim.Fill();

        _songInfoContainer.SetNeedsDraw();
    }

    public void SetImmersiveState(bool enabled)
    {
        _isImmersiveMode = enabled;
        Height = enabled ? Dim.Fill(0) : Dim.Fill(5);
        UpdateImmersiveButtonHighlight();

        if (enabled)
        {
            TriggerImmersiveActivity();
            TriggerInteractiveActivity();
            StartImmersiveTimer();
            // 沉浸模式下自动取消选中歌手/专辑，需要取消沉浸模式才可以选择
            _artistLink.SetInteractiveEnabled(false);
            _albumLink.SetInteractiveEnabled(false);
            SetFocus();
            FocusChangedNotification?.Invoke();
        }
        else
        {
            StopImmersiveTimer();
            _transBtn.Visible = true;
            _immersiveBtn.Visible = true;
            if (_currentSong != null && (_currentSong.IsLocal || _currentSong.IsWebDav))
            {
                _matchLyricBtn.Visible = true;
            }
            _isInteractiveHighlightSuppressed = false;
            // 退出沉浸模式后恢复可选择
            _artistLink.SetInteractiveEnabled(true);
            _albumLink.SetInteractiveEnabled(true);
            _artistLink.SetHighlightSuppressed(false);
            _albumLink.SetHighlightSuppressed(false);
        }
        SetNeedsDraw();
    }

    private void UpdateImmersiveButtonHighlight()
    {
        if (_immersiveBtn == null) return;
        var color = _isImmersiveMode ? MikuTheme.QqGreenLight : MikuTheme.MikuTextMuted;
        var attr = new Attribute(color, Color.None);
        _immersiveBtn.SetScheme(new Scheme
        {
            Normal = attr,
            Focus = attr,
            HotNormal = attr,
            HotFocus = attr,
            Highlight = attr,
            Disabled = attr
        });
        _immersiveBtn.SetNeedsDraw();
    }

    public void TriggerImmersiveActivity()
    {
        _lastImmersiveActivityTick = Environment.TickCount64;
        bool isLocalOrWebDav = _currentSong != null && (_currentSong.IsLocal || _currentSong.IsWebDav);
        if (!_transBtn.Visible || !_immersiveBtn.Visible || (isLocalOrWebDav && !_matchLyricBtn.Visible))
        {
            _transBtn.Visible = true;
            _immersiveBtn.Visible = true;
            if (isLocalOrWebDav)
            {
                _matchLyricBtn.Visible = true;
            }
            SetNeedsDraw();
        }
    }

    public void TriggerInteractiveActivity()
    {
        _lastInteractiveActivityTick = Environment.TickCount64;
        if (_isInteractiveHighlightSuppressed)
        {
            _isInteractiveHighlightSuppressed = false;
            _artistLink.SetHighlightSuppressed(false);
            _albumLink.SetHighlightSuppressed(false);
        }
    }

    private void StartImmersiveTimer()
    {
        StopImmersiveTimer();
        _lastImmersiveActivityTick = Environment.TickCount64;
        _lastInteractiveActivityTick = Environment.TickCount64;
        _immersiveTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            if (_isImmersiveMode && Visible)
            {
                var now = Environment.TickCount64;

                // 1. 浮动功能按钮（译/沉浸/匹配）3 秒无操作自动隐藏
                if (now - _lastImmersiveActivityTick > 3000)
                {
                    if (_transBtn.Visible || _immersiveBtn.Visible || _matchLyricBtn.Visible)
                    {
                        _transBtn.Visible = false;
                        _immersiveBtn.Visible = false;
                        _matchLyricBtn.Visible = false;
                        SetNeedsDraw();
                    }
                }

                // 2. 交互项高亮 3 秒无操作自动隐藏
                if (!_isInteractiveHighlightSuppressed &&
                    _lastInteractiveActivityTick > 0 &&
                    now - _lastInteractiveActivityTick > 3000)
                {
                    _isInteractiveHighlightSuppressed = true;
                    _artistLink.SetHighlightSuppressed(true);
                    _albumLink.SetHighlightSuppressed(true);
                }
            }
            return _isImmersiveMode && Visible;
        });
    }

    private void StopImmersiveTimer()
    {
        if (_immersiveTimerToken != null)
        {
            Application.RemoveTimeout(_immersiveTimerToken);
            _immersiveTimerToken = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopImmersiveTimer();
        }
        base.Dispose(disposing);
    }
}


/// <summary>
/// 无装饰符号（无括号）、未获焦呈纯净灰度、获焦呈现翡翠绿的轻量交互标签控件
/// 仅在双击（LeftButtonDoubleClicked）或回车/空格时执行激活，单击仅做获焦，防止误触
/// </summary>
public sealed class InteractiveLinkView : Label
{
    private string _text;
    private bool _isHighlightSuppressed;

    public int ContentWidth { get; private set; }

    public event Action? LinkSelected;
    public event Action? NavigateNextRequested;
    public event Action? NavigatePrevRequested;

    public InteractiveLinkView(string initialText)
    {
        _text = initialText;
        CanFocus = true;
        TabStop = TabBehavior.TabGroup;
        Height = 1;
        UpdateMetrics();

        MouseEvent += (s, m) =>
        {
            if (!CanFocus) return; // 沉浸模式下禁用一切交互与选中

            // 单击仅获焦，双击才执行激活跳转（防止误触）
            if (m.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked))
            {
                SetFocus();
                LinkSelected?.Invoke();
                m.Handled = true;
                return;
            }

            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                SetFocus();
                m.Handled = true;
                return;
            }
        };

        KeyDown += (s, k) =>
        {
            if (!CanFocus) return;

            if (k == Key.Enter || k.AsRune.Value == '\r' || k.AsRune.Value == '\n')
            {
                LinkSelected?.Invoke();
                k.Handled = true;
                return;
            }

            if (k == Key.CursorRight || k == Key.CursorDown)
            {
                NavigateNextRequested?.Invoke();
                k.Handled = true;
                return;
            }

            if (k == Key.CursorLeft || k == Key.CursorUp)
            {
                NavigatePrevRequested?.Invoke();
                k.Handled = true;
                return;
            }
        };

        HasFocusChanged += (s, e) =>
        {
            UpdateVisualScheme();
        };
    }

    public void SetText(string text)
    {
        _text = text;
        UpdateMetrics();
    }

    public void SetInteractiveEnabled(bool enabled)
    {
        CanFocus = enabled;
        TabStop = enabled ? TabBehavior.TabGroup : TabBehavior.NoStop;
        UpdateVisualScheme();
    }

    public void SetHighlightSuppressed(bool suppressed)
    {
        if (_isHighlightSuppressed != suppressed)
        {
            _isHighlightSuppressed = suppressed;
            UpdateVisualScheme();
        }
    }

    private void UpdateMetrics()
    {
        Text = _text;
        ContentWidth = MainWindow.GetDisplayWidth(_text);
        Width = ContentWidth;
        UpdateVisualScheme();
    }

    private void UpdateVisualScheme()
    {
        bool showActive = CanFocus && HasFocus && !_isHighlightSuppressed;
        if (showActive)
        {
            SetScheme(new Scheme
            {
                Normal = new Attribute(MikuTheme.QqGreenPrimary, Color.None),
                Focus = new Attribute(MikuTheme.QqGreenPrimary, Color.None),
                HotNormal = new Attribute(MikuTheme.QqGreenPrimary, Color.None)
            });
        }
        else
        {
            SetScheme(new Scheme
            {
                Normal = new Attribute(MikuTheme.QqTextLyricDim, Color.None),
                Focus = new Attribute(MikuTheme.QqTextLyricDim, Color.None),
                HotNormal = new Attribute(MikuTheme.QqTextLyricDim, Color.None)
            });
        }
        SetNeedsDraw();
    }
}
