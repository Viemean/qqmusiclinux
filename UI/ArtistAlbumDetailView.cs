using System;
using System.IO;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Models;

namespace QQMusic.Tui.UI;

/// <summary>
/// 右侧歌手/专辑详情视图：上半部分展示圆角封面/写真图片，下半部分展示详情与生平简介
/// </summary>
public sealed class ArtistAlbumDetailView : View
{
    private readonly View _imageContainer;
    private readonly Label _titleLabel;
    private readonly Label _subLabel;
    private readonly FrameView _descFrame;
    private readonly TextView _descTextView;
    private readonly Label _hintLabel;

    private string? _currentImagePath;
    private object? _resizeTimerToken;

    public bool IsInnerFocused => _descTextView.HasFocus || _descFrame.HasFocus || HasFocus;

    public event Action? Clicked;
    public event Action<bool>? TabNavigationRequested;

    public void SetFocusToDesc()
    {
        _descTextView.SetFocus();
    }

    public void SetActiveBorder(bool active)
    {
        _descFrame.SetScheme(active ? MikuTheme.FrameBorderActive : MikuTheme.FrameBorderDim);
        _descFrame.SetNeedsDraw();
    }

    public ArtistAlbumDetailView()
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;

        // 1. 上半部分：图片展示容器（约占 45% 高度）
        _imageContainer = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(45),
            CanFocus = false
        };

        _titleLabel = new Label
        {
            Text = "",
            X = Pos.Center(),
            Y = Pos.Center() - 1,
            Visible = !TerminalImageHelper.IsImageSupported
        };
        _titleLabel.SetScheme(MikuTheme.Base);

        _subLabel = new Label
        {
            Text = "",
            X = Pos.Center(),
            Y = Pos.Center() + 1,
            Visible = !TerminalImageHelper.IsImageSupported
        };
        _subLabel.SetScheme(MikuTheme.Base);

        _imageContainer.Add(_titleLabel, _subLabel);
        Add(_imageContainer);

        _hintLabel = new Label
        {
            Text = "A: 歌曲/专辑  O: 热门/最新  D: 收藏",
            X = Pos.Center(),
            Y = Pos.Bottom(_imageContainer),
            Height = 1,
            CanFocus = false
        };
        _hintLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None),
            Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None),
            HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None)
        });
        Add(_hintLabel);

        // 2. 下半部分：富文本简介框架
        _descFrame = new FrameView
        {
            Title = "简介",
            X = 0,
            Y = Pos.Bottom(_hintLabel),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true
        };
        _descFrame.SetScheme(MikuTheme.FrameBorderActive);

        _descTextView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true
        };
        _descTextView.SetScheme(new Scheme
        {
            Normal    = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Terminal.Gui.Drawing.Color.None),
            Focus     = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Terminal.Gui.Drawing.Color.None),
            HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuPinkAccent, Terminal.Gui.Drawing.Color.None),
            HotFocus  = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, MikuTheme.MikuPinkAccent),
            ReadOnly  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Terminal.Gui.Drawing.Color.None)
        });

        _descTextView.KeyDown += (s, k) =>
        {
            if (k == Key.Tab || k.ToString().Contains("Tab"))
            {
                k.Handled = true;
                TabNavigationRequested?.Invoke(!k.IsShift);
            }
        };

        _descTextView.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                if (!_descTextView.HasFocus)
                {
                    _descTextView.SetFocus();
                }
                Clicked?.Invoke();
            }
        };

        _descFrame.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                if (!_descTextView.HasFocus)
                {
                    _descTextView.SetFocus();
                }
                Clicked?.Invoke();
            }
        };

        MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                if (!_descTextView.HasFocus)
                {
                    _descTextView.SetFocus();
                }
                Clicked?.Invoke();
            }
        };

        _descFrame.Add(_descTextView);
        Add(_descFrame);

        _imageContainer.ViewportChanged += (s, e) =>
        {
            if (Visible)
            {
                TriggerImageRenderDelayed();
            }
        };
    }

    public void SetHintText(string text)
    {
        _hintLabel.Text = text;
        SetNeedsLayout();
    }

    public void SetArtist(ArtistDetail detail, string? imagePath)
    {
        _currentImagePath = imagePath;
        _titleLabel.Text = detail.Name;
        _subLabel.Text = $"歌手 · {detail.Songs.Count} 首热门单曲";
        _hintLabel.Text = "A: 歌曲/专辑  S: 热门/最新  D: 收藏";

        var brief = string.IsNullOrWhiteSpace(detail.Brief) ? "暂无歌手简介资料。" : detail.Brief.Trim();
        _descFrame.Title = "歌手简介";
        _descTextView.Text = $"歌手：{detail.Name}\n曲目：{detail.Songs.Count} 首热门作品\n\n{brief}";

        TriggerImageRenderDelayed();
    }

    public void SetAlbum(AlbumDetail detail, string? imagePath)
    {
        _currentImagePath = imagePath;
        _titleLabel.Text = detail.Name;
        _subLabel.Text = $"{detail.Artist} · {detail.Songs.Count} 首歌曲";
        _hintLabel.Text = "D: 收藏专辑";

        var desc = string.IsNullOrWhiteSpace(detail.Description) ? "暂无专辑详细背景资料。" : detail.Description.Trim();
        _descFrame.Title = "专辑介绍";
        _descTextView.Text = $"专辑：{detail.Name}\n歌手：{detail.Artist}\n发行：{detail.PublishDate}\n公司：{detail.Company}\n曲目：{detail.Songs.Count} 首歌曲\n\n{desc}";

        TriggerImageRenderDelayed();
    }

    public void OnActivated()
    {
        Visible = true;
        TriggerImageRenderDelayed();
    }

    public void OnDeactivated()
    {
        Visible = false;
        if (_resizeTimerToken != null)
        {
            Application.RemoveTimeout(_resizeTimerToken);
            _resizeTimerToken = null;
        }
        TerminalImageHelper.ClearImages();
    }

    public void OnWindowResized()
    {
        if (!Visible) return;
        TerminalImageHelper.ClearImages();
        TriggerImageRenderDelayed();
    }

    private void TriggerImageRenderDelayed()
    {
        if (!Visible) return;

        bool hasValidImage = !string.IsNullOrEmpty(_currentImagePath) && File.Exists(_currentImagePath);
        if (!TerminalImageHelper.IsImageSupported || !hasValidImage)
        {
            _titleLabel.Visible = true;
            _subLabel.Visible = true;
            SetNeedsDraw();
            return;
        }

        _titleLabel.Visible = false;
        _subLabel.Visible = false;

        if (_resizeTimerToken != null)
        {
            Application.RemoveTimeout(_resizeTimerToken);
            _resizeTimerToken = null;
        }

        // 第一阶段：60ms 快速首绘制
        _resizeTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(60), () =>
        {
            _resizeTimerToken = null;
            if (Visible)
            {
                RenderImageIfVisible();

                // 第二阶段：250ms 二次补位重绘，确保所有边框背景绘制完后 Kitty 原生图像稳定置顶
                Application.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
                {
                    if (Visible)
                    {
                        RenderImageIfVisible();
                    }
                    return false;
                });
            }
            return false;
        });
    }

    private void RenderImageIfVisible()
    {
        if (!Visible || !TerminalImageHelper.IsImageSupported || string.IsNullOrEmpty(_currentImagePath) || !File.Exists(_currentImagePath))
        {
            _titleLabel.Visible = true;
            _subLabel.Visible = true;
            SetNeedsDraw();
            return;
        }

        try
        {
            var origin = _imageContainer.FrameToScreen();
            int col = Math.Max(1, origin.X);
            int row = Math.Max(1, origin.Y);
            int containerCols = Math.Max(10, _imageContainer.Viewport.Width);
            int containerRows = Math.Max(4, _imageContainer.Viewport.Height);

            // 图像自适应居中，微圆角呈现
            int maxRowsByHeight = Math.Max(3, (int)(containerRows * 0.85));
            int maxRowsByWidth = Math.Max(3, (int)((containerCols * 0.85) / 2));
            int targetRows = Math.Max(3, Math.Min(maxRowsByHeight, maxRowsByWidth));
            int targetCols = targetRows * 2;

            int colOffset = Math.Max(1, (containerCols - targetCols) / 2);
            int rowOffset = Math.Max(1, (containerRows - targetRows) / 2);

            int renderCol = col + colOffset;
            int renderRow = Math.Max(1, row + rowOffset);

            TerminalImageHelper.RenderKittyImage(_currentImagePath, renderCol, renderRow, targetCols, targetRows);
        }
        catch
        {
            // 容错处理
        }
    }
}
