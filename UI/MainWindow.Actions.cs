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
    private void ToggleQuickSearch()
    {
        if (_isSearchActive) return;

        if (_quickSearchBar.Visible)
        {
            _quickSearchBar.Dismiss();
        }
        else
        {
            _quickSearchBar.ShowAndFocus();
        }
    }

    private void ToggleDesktopNotification()
    {
        UserConfig.Current.EnableSongSwitchNotification = !UserConfig.Current.EnableSongSwitchNotification;
        UserConfig.Current.Save();
        string stateStr = UserConfig.Current.EnableSongSwitchNotification ? "已开启" : "已关闭";
        if (!DesktopNotificationService.Instance.IsAvailable && UserConfig.Current.EnableSongSwitchNotification)
        {
            _controlBar.UpdateStatus("[桌面通知] 当前系统环境未检测到可用的 D-Bus 通知服务");
        }
        else
        {
            _controlBar.UpdateStatus($"[桌面通知] 切歌气泡已{stateStr} (按 B 切换)");
        }
        AppLogger.Info("MainWindow", $"Desktop song switch notification toggled: {stateStr}");
    }

    private async Task HandleExportSongAsync()
    {
        Song? targetSong = null;
        if (_songListView.Songs.Count > 0 && _songListView.SelectedItem is { } idx && idx >= 0 && idx < _songListView.Songs.Count)
        {
            targetSong = _songListView.Songs[idx];
        }
        else
        {
            targetSong = _activeSong ?? _controlBar.CurrentSong;
        }

        if (targetSong == null)
        {
            _controlBar.UpdateStatus("[导出] 请先在列表中选中歌曲或起播一首歌曲");
            return;
        }

        _controlBar.UpdateStatus($"[导出中] 正在导出: {targetSong.Title}...");
        var quality = _actualQualityTier;

        _ = Task.Run(async () =>
        {
            var res = await AudioExportService.ExportSongAsync(targetSong, quality).ConfigureAwait(false);
            Application.Invoke(() =>
            {
                if (res.Success)
                {
                    _controlBar.UpdateStatus($"[导出成功] 已保存至: {Path.GetFileName(res.FilePath)} (按 X 再次导出)");
                }
                else
                {
                    _controlBar.UpdateStatus($"[导出失败] {res.Message}");
                }
            });
        });
    }

}
