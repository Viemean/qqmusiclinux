using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Player;
using QQMusic.Tui.Services;
using QQMusic.Tui.Utils;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Color = Terminal.Gui.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;

namespace QQMusic.Tui.UI;

public sealed partial class MainWindow
{
    private string GetUserStatusText()
    {
        if (UserSession.Current.IsLoggedIn)
        {
            var name = string.IsNullOrEmpty(UserSession.Current.Nick) ? UserSession.Current.Uin : UserSession.Current.Nick;
            var vip = UserSession.Current.IsVip
                ? UserSession.Current.VipLevel > 0 ? $" 绿钻LV{UserSession.Current.VipLevel}" : " 绿钻"
                : "";
            var musicLevel = UserSession.Current.MusicLevel > 0 ? $" 乐力{UserSession.Current.MusicLevel}" : "";
            return $"[U] {name}{vip}{musicLevel}";
        }
        return "[U] 登录";
    }

    /// <summary>
    /// 计算文本的终端视觉宽度（考虑 CJK 宽字符占 2 列）
    /// </summary>
    private static int GetVisualWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int width = 0;
        foreach (var ch in text)
        {
            width += ch > 127 ? 2 : 1;
        }
        return width;
    }

    /// <summary>
    /// 动态刷新顶部右上角按钮（识曲、账号状态与 Web 协同按钮）的防重叠独立布局
    /// 自右向左依次排列：[W] Web -> [U] 账号 -> [R] 识曲
    /// </summary>
    internal void UpdateTopRightButtonsLayout()
    {
        if (_userStatusBtn == null || _recognizeBtn == null || _webBtn == null || _searchField == null) return;

        // 1. 最右侧：Web 协同按钮 [W] Web (右侧保留 1 列安全留白)
        var isWebRunning = (_standaloneWebServer?.IsRunning == true) || (_player is WebPlayer);
        var webText = isWebRunning ? "[W] Web:开" : "[W] Web";
        _webBtn.Text = webText;
        int webBtnWidth = GetVisualWidth(webText);
        int webAnchorOffset = webBtnWidth + 1;
        _webBtn.X = Pos.AnchorEnd(webAnchorOffset);

        // 2. 账号按钮 [U] 登录 / [U] 账号: ... (排在 Web 按钮左侧，间隔 2 列)
        var statusText = GetUserStatusText();
        _userStatusBtn.Text = statusText;
        int userBtnWidth = GetVisualWidth(statusText);
        int userAnchorOffset = webAnchorOffset + 2 + userBtnWidth;
        _userStatusBtn.X = Pos.AnchorEnd(userAnchorOffset);

        // 3. 识曲按钮 [R] 识曲 (排在账号按钮左侧，间隔 2 列)
        const string recText = "[R] 识曲";
        _recognizeBtn.Text = recText;
        int recBtnWidth = GetVisualWidth(recText);
        int recAnchorOffset = userAnchorOffset + 2 + recBtnWidth;
        _recognizeBtn.X = Pos.AnchorEnd(recAnchorOffset);

        // 4. 搜索框自动填满左侧剩余空间 (避开识曲、账号与 Web 按钮并留出 2 列间距)
        _searchField.Width = Dim.Fill(recAnchorOffset + 2);

        SetNeedsLayout();
    }

    public void UpdateLyricTitle(string text)
    {
        Application.Invoke(() =>
        {
            _lyricTitleLabel.Text = string.IsNullOrEmpty(text) ? "┤歌词├" : $"┤{text}├";
            _lyricTitleLabel.SetNeedsDraw();
            _lyricFrame.SetNeedsDraw();
        });
    }

}
