using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Views;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.UI;

public sealed partial class MainWindow
{
    // ==================== WebDAV 远程私有云音乐支持 ====================
    private bool _isWebDavFlatMode = true;
    private bool _isImportingWebDav = false;
    private bool _isScanningWebDavMetadata = false;
    private string _currentWebDavHref = "";
    private readonly Stack<string> _webDavPathHistory = new();
    private List<WebDavItem> _currentWebDavItems = [];

    private async Task LoadWebDavMusicAsync()
    {
        _currentViewMode = ViewMode.WebDav;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        var server = WebDavService.GetActiveServer();
        if (server == null)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("未配置 WebDAV 服务器 (请按 F 键管理/添加 WebDAV 站点)", "WebDAV: 未连接");
                _controlBar.UpdateStatus("[WebDAV] 未配置私有云站点，请按 F 键添加站点");
            });
            return;
        }

        if (_isWebDavFlatMode)
        {
            LoadWebDavFlatLibrary(server);
        }
        else
        {
            if (string.IsNullOrEmpty(_currentWebDavHref))
            {
                _currentWebDavHref = string.IsNullOrEmpty(server.RootPath) ? "/" : server.RootPath;
                _webDavPathHistory.Clear();
            }
            await LoadWebDavDirectoryAsync(server, _currentWebDavHref);
        }
    }

    private void LoadWebDavFlatLibrary(WebDavServer server)
    {
        var cached = server.CachedSongs ?? [];
        if (cached.Count == 0)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("WebDAV 曲库为空 (按 D 切换至目录树后按 A 导入目录，按 F 管理站点)", $"WebDAV: {server.Name} (0 首)");
                _controlBar.UpdateStatus($"[WebDAV · 平铺曲库] 当前站点 [{server.Name}] 暂无导入歌曲，按 D 切换到目录树");
            });
            return;
        }

        var rawSongs = cached.Select(c => WebDavService.ToSongModel(server, c));
        var songs = WebDavService.DeduplicateSongs(rawSongs);

        Application.Invoke(() =>
        {
            var dupNotice = songs.Count < cached.Count ? $"已智能去重 {cached.Count - songs.Count} 首重复镜像，" : "";
            var title = $"WebDAV 曲库: {server.Name} ({dupNotice}共 {songs.Count} 首，按 S 嗅探歌曲信息，按 A 导入目录，按 D 切目录树，按 F 管理)";
            _songListView.SetSongs(songs, title);
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
            }
            _songListView.SetFocusToList();
            _controlBar.UpdateStatus($"[WebDAV · 平铺曲库] 已载入 {songs.Count} 首歌曲 (站点: {server.Name})");
        });
    }

    private async Task LoadWebDavDirectoryAsync(WebDavServer server, string href)
    {
        Application.Invoke(() =>
        {
            _songListView.SetMessage("正在通过 WebDAV PROPFIND 读取远程目录清单...", $"WebDAV: {server.Name} (加载中)");
            _controlBar.UpdateStatus($"[WebDAV] 正在获取目录: {href}");
        });

        var items = await WebDavService.ListDirectoryAsync(server, href);
        _currentWebDavItems = items;

        var displayLines = new List<string>();
        bool hasParentBack = _webDavPathHistory.Count > 0 || (!string.IsNullOrEmpty(href) && href != "/" && href != server.RootPath);
        if (hasParentBack)
        {
            displayLines.Add("[📁 .. (返回上级目录)]");
        }

        foreach (var it in items)
        {
            if (it.IsDirectory)
            {
                displayLines.Add($"📁 {it.Name}/");
            }
            else
            {
                var sizeMb = it.ContentLength > 0 ? $" ({it.ContentLength / 1024 / 1024.0:F1}MB)" : "";
                displayLines.Add($"🎵 {it.Name}{sizeMb}");
            }
        }

        Application.Invoke(() =>
        {
            if (displayLines.Count == 0)
            {
                _songListView.SetMessage("当前 WebDAV 目录为空或未发现音频文件 (按 A 导入，按 F 管理站点)", $"WebDAV: {href}");
                _controlBar.UpdateStatus($"[WebDAV] 目录为空: {href}");
                return;
            }

            var title = $"WebDAV 目录: {href} (按 Enter 打开/播放，按 A 扫描本目录/选中项，按 D 切平铺曲库，按 F 管理)";
            _songListView.SetCustomItems(
                displayLines,
                title,
                onAccepted: async idx =>
                {
                    await HandleWebDavItemAcceptedAsync(server, idx, hasParentBack);
                }
            );
            _songListView.SetFocusToList();
            _controlBar.UpdateStatus($"[WebDAV] 目录就绪: {href} (共 {items.Count} 项)");
        });
    }

    private async Task HandleWebDavItemAcceptedAsync(WebDavServer server, int clickedIndex, bool hasParentBack)
    {
        if (hasParentBack && clickedIndex == 0)
        {
            await NavigateUpWebDavFolderAsync();
            return;
        }

        int itemIdx = hasParentBack ? clickedIndex - 1 : clickedIndex;
        if (itemIdx < 0 || itemIdx >= _currentWebDavItems.Count) return;

        var targetItem = _currentWebDavItems[itemIdx];
        if (targetItem.IsDirectory)
        {
            _webDavPathHistory.Push(_currentWebDavHref);
            _currentWebDavHref = targetItem.Href;
            await LoadWebDavDirectoryAsync(server, _currentWebDavHref);
        }
        else
        {
            // 点击音频文件：将当前目录下所有音频文件作为当前待播歌单
            var audioItems = _currentWebDavItems.Where(it => !it.IsDirectory).ToList();
            var songs = audioItems.Select(it => WebDavService.ToSongModel(server, it)).ToList();
            int curPlayIdx = audioItems.IndexOf(targetItem);
            if (curPlayIdx < 0) curPlayIdx = 0;

            if (songs.Count > 0)
            {
                PlaybackQueueService.Instance.Mode = _currentPlaybackMode;
                PlaybackQueueService.Instance.SetQueue(songs, curPlayIdx);
                await PlaySongAsync(songs[curPlayIdx]);
            }
        }
    }

    private async Task ImportCurrentWebDavFolderAsync()
    {
        var server = WebDavService.GetActiveServer();
        if (server == null) return;

        if (_isImportingWebDav)
        {
            _controlBar.UpdateStatus("[WebDAV] 正在深度检索并导入目录中，请稍候...");
            return;
        }

        _isImportingWebDav = true;
        try
        {
            string targetHref = _currentWebDavHref;
            string targetDisplayName = _currentWebDavHref;

            if (!_isWebDavFlatMode)
            {
                bool hasParentBack = _webDavPathHistory.Count > 0 || (!string.IsNullOrEmpty(_currentWebDavHref) && _currentWebDavHref != "/" && _currentWebDavHref != server.RootPath);
                int selectedIdx = _songListView.SelectedItem ?? -1;
                int itemIdx = hasParentBack ? selectedIdx - 1 : selectedIdx;

                if (itemIdx >= 0 && itemIdx < _currentWebDavItems.Count)
                {
                    var selectedItem = _currentWebDavItems[itemIdx];
                    if (selectedItem.IsDirectory)
                    {
                        targetHref = selectedItem.Href;
                        targetDisplayName = $"{selectedItem.Name}/";
                    }
                }

                if (string.IsNullOrEmpty(targetHref)) targetHref = "/";
                if (string.IsNullOrEmpty(targetDisplayName)) targetDisplayName = targetHref;
            }
            else
            {
                targetHref = server.RootPath ?? "/";
                targetDisplayName = "全部曲库";
            }

            _controlBar.UpdateStatus($"[WebDAV] 正在增量扫描目录: {targetDisplayName} ...");
            long lastRefreshTick = Environment.TickCount64;

            var count = await WebDavService.ScanFolderRecursiveAsync(server, targetHref, prog =>
            {
                Application.Invoke(() =>
                {
                    _controlBar.UpdateStatus($"[WebDAV 增量扫描] {prog}");
                    if (_isWebDavFlatMode && Environment.TickCount64 - lastRefreshTick > 1000)
                    {
                        lastRefreshTick = Environment.TickCount64;
                        LoadWebDavFlatLibrary(server);
                    }
                });
            });

            _isWebDavFlatMode = true;
            LoadWebDavFlatLibrary(server);
            _controlBar.UpdateStatus($"[WebDAV] 扫描完成！曲库现有 {server.CachedSongs?.Count ?? count} 首音频，已切换至平铺曲库 (按 D 可切回目录树)");
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainWindow.Library", $"WebDAV import failed: {ex.Message}");
            _controlBar.UpdateStatus($"[WebDAV] 导入失败: {ex.Message}");
        }
        finally
        {
            _isImportingWebDav = false;
        }
    }

    private CancellationTokenSource? _webDavScanCts;

    private async Task ScanWebDavMetadataAsync()
    {
        var server = WebDavService.GetActiveServer();
        if (server == null) return;

        if (_isScanningWebDavMetadata)
        {
            _controlBar.UpdateStatus("[WebDAV] 正在嗅探/同步歌曲元数据中，请勿重复操作...");
            return;
        }

        _isScanningWebDavMetadata = true;
        _webDavScanCts?.Dispose();
        _webDavScanCts = new CancellationTokenSource();
        var ct = _webDavScanCts.Token;

        try
        {
            _controlBar.UpdateStatus("[WebDAV 元数据嗅探] 正在启动 8 线程 HTTP Range 头部嗅探...");
            long lastRefreshTick = Environment.TickCount64;

            int enriched = await WebDavService.BatchEnrichMetadataHeadersAsync(server, (songInfo, cur, total) =>
            {
                Application.Invoke(() =>
                {
                    _controlBar.UpdateStatus($"[WebDAV 元数据嗅探] {cur}/{total} : {songInfo}");
                    if (_isWebDavFlatMode && Environment.TickCount64 - lastRefreshTick > 1000)
                    {
                        lastRefreshTick = Environment.TickCount64;
                        LoadWebDavFlatLibrary(server);
                    }
                });
            }, ct);

            _controlBar.UpdateStatus($"[WebDAV] 元数据嗅探完成！共成功识别 {enriched} 首歌曲信息");
            if (_isWebDavFlatMode)
            {
                LoadWebDavFlatLibrary(server);
            }
        }
        catch (OperationCanceledException)
        {
            _controlBar.UpdateStatus("[WebDAV] 元数据嗅探任务已停止");
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainWindow.Library", $"WebDAV metadata scan failed: {ex.Message}");
            _controlBar.UpdateStatus($"[WebDAV] 元数据嗅探失败: {ex.Message}");
        }
        finally
        {
            _isScanningWebDavMetadata = false;
            _webDavScanCts?.Dispose();
            _webDavScanCts = null;
        }
    }

    private async Task ToggleWebDavViewModeAsync()
    {
        _isWebDavFlatMode = !_isWebDavFlatMode;
        await LoadWebDavMusicAsync();
    }

    private async Task RefreshWebDavAsync()
    {
        var server = WebDavService.GetActiveServer();
        if (server == null) return;

        if (_isWebDavFlatMode)
        {
            LoadWebDavFlatLibrary(server);
        }
        else
        {
            await LoadWebDavDirectoryAsync(server, _currentWebDavHref);
        }
    }

    private async Task NavigateUpWebDavFolderAsync()
    {
        var server = WebDavService.GetActiveServer();
        if (server == null) return;

        if (_webDavPathHistory.Count > 0)
        {
            _currentWebDavHref = _webDavPathHistory.Pop();
        }
        else
        {
            var trimmed = _currentWebDavHref.TrimEnd('/');
            var lastSlash = trimmed.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                _currentWebDavHref = trimmed[..lastSlash];
                if (string.IsNullOrEmpty(_currentWebDavHref)) _currentWebDavHref = "/";
            }
            else
            {
                _currentWebDavHref = string.IsNullOrEmpty(server.RootPath) ? "/" : server.RootPath;
            }
        }
        await LoadWebDavDirectoryAsync(server, _currentWebDavHref);
    }

    private void ShowWebdavManageDialog()
    {
        using var dlg = new WebdavManageDialog(
            onServersChanged: () =>
            {
                if (_currentViewMode == ViewMode.WebDav)
                {
                    _ = LoadWebDavMusicAsync();
                }
            },
            onSelectAndOpen: server =>
            {
                _currentWebDavHref = string.IsNullOrEmpty(server.RootPath) ? "/" : server.RootPath;
                _webDavPathHistory.Clear();
                _ = LoadWebDavMusicAsync();
            }
        );
        Application.Run(dlg);
        _songListView.SetFocusToList();
    }
}
