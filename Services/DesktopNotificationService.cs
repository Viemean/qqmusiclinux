using System.IO;
using System.Runtime.InteropServices;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

/// <summary>
/// Linux 原生桌面切歌通知服务
/// 基于系统原生 libgio-2.0.so.0 / libglib-2.0.so.0 P/Invoke 调用 D-Bus org.freedesktop.Notifications
/// 支持封面缩略图、音质徽标及防堆叠原地替换
/// </summary>
public sealed unsafe partial class DesktopNotificationService : IDisposable
{
    private const string LibGio = "libgio-2.0.so.0";
    private const string LibGlib = "libglib-2.0.so.0";
    private const int G_BUS_TYPE_SESSION = 2;

    private static readonly DesktopNotificationService s_instance = new();
    public static DesktopNotificationService Instance => s_instance;

    private readonly Lock _lock = new();
    private nint _connection;
    private uint _lastNotificationId;
    private bool _initialized;
    private bool _isAvailable;
    private bool _disposed;

    private DesktopNotificationService()
    {
    }

    public bool IsAvailable => _isAvailable;

    /// <summary>
    /// 检查当前环境是否具备 D-Bus 会话总线连接条件
    /// </summary>
    public static bool HasSessionBusAddress()
    {
        if (!OperatingSystem.IsLinux()) return false;

        var noNotifyEnv = Environment.GetEnvironmentVariable("QQMUSIC_NO_NOTIFY") ?? Environment.GetEnvironmentVariable("QQMUSIC_DISABLE_NOTIFY");
        if (noNotifyEnv == "1" || string.Equals(noNotifyEnv, "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var addr = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (!string.IsNullOrEmpty(addr)) return true;

        try
        {
            uint uid = geteuid();
            if (File.Exists($"/run/user/{uid}/bus")) return true;
        }
        catch
        {
            // 忽略读取 uid 异常
        }

        return false;
    }

    /// <summary>
    /// 初始化 D-Bus 会话总线连接
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;

        lock (_lock)
        {
            if (_initialized) return;
            _initialized = true;

            if (!OperatingSystem.IsLinux())
            {
                _isAvailable = false;
                return;
            }

            if (!HasSessionBusAddress())
            {
                _isAvailable = false;
                AppLogger.Info("DesktopNotification", "D-Bus session bus not detected in current environment. Notification service will not be started.");
                return;
            }

            try
            {
                _connection = g_bus_get_sync(G_BUS_TYPE_SESSION, 0, out nint error);
                if (_connection != 0)
                {
                    // 检测环境中是否真正存在 org.freedesktop.Notifications 服务守护进程
                    bool hasNotificationDaemon = CheckNotificationDaemonRunning(_connection);
                    if (!hasNotificationDaemon)
                    {
                        _isAvailable = false;
                        g_object_unref(_connection);
                        _connection = 0;
                        AppLogger.Info("DesktopNotification", "D-Bus session bus is present, but org.freedesktop.Notifications daemon is not running. Notification service will not be started.");
                        return;
                    }

                    _isAvailable = true;
                    AppLogger.Info("DesktopNotification", "Connected to D-Bus session bus and confirmed notification service is available.");
                }
                else
                {
                    _isAvailable = false;
                    AppLogger.Warn("DesktopNotification", "Could not connect to D-Bus session bus.");
                }
            }
            catch (Exception ex)
            {
                _isAvailable = false;
                AppLogger.Warn("DesktopNotification", $"Failed to initialize D-Bus notification service: {ex.Message}");
            }
        }
    }

    private static bool CheckNotificationDaemonRunning(nint connection)
    {
        if (connection == 0) return false;
        try
        {
            nint[] paramChildren = [g_variant_new_string("org.freedesktop.Notifications")];
            nint parameters = g_variant_new_tuple(paramChildren, 1);
            nint reply = g_dbus_connection_call_sync(
                connection,
                "org.freedesktop.DBus",
                "/org/freedesktop/DBus",
                "org.freedesktop.DBus",
                "NameHasOwner",
                parameters,
                0,
                0,
                500,
                0,
                out nint callErr
            );

            if (reply != 0)
            {
                nint child = g_variant_get_child_value(reply, 0);
                bool hasOwner = false;
                if (child != 0)
                {
                    hasOwner = g_variant_get_boolean(child);
                    g_variant_unref(child);
                }
                g_variant_unref(reply);
                return hasOwner;
            }
            else if (callErr != 0)
            {
                g_error_free(callErr);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DesktopNotification", $"CheckNotificationDaemonRunning error: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// 切歌时发射桌面气泡通知
    /// </summary>
    /// <param name="song">当前播放歌曲</param>
    /// <param name="quality">音频品质级别</param>
    /// <param name="coverPath">本地封面缓存绝对路径</param>
    public void NotifySongSwitch(Song song, AudioQualityTier quality, string? coverPath = null)
    {
        if (!UserConfig.Current.EnableSongSwitchNotification)
        {
            return;
        }

        if (!_initialized)
        {
            Initialize();
        }

        if (!_isAvailable || _connection == 0 || _disposed)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            SendNotificationInternal(song, quality, coverPath);
        });
    }

    private void SendNotificationInternal(Song song, AudioQualityTier quality, string? coverPath)
    {
        lock (_lock)
        {
            if (!_isAvailable || _connection == 0 || _disposed) return;

            try
            {
                string title = string.IsNullOrWhiteSpace(song.Title) ? "未知歌曲" : song.Title;
                string artist = string.IsNullOrWhiteSpace(song.Artist) ? "未知歌手" : song.Artist;
                string album = string.IsNullOrWhiteSpace(song.Album) ? "" : song.Album;
                string qualityBadge = AudioQualityHelper.GetBadge(quality);

                string body = string.IsNullOrEmpty(album)
                    ? $"{artist}  [{qualityBadge}]"
                    : $"{artist} - {album}\n[{qualityBadge}]";

                bool hasValidCover = !string.IsNullOrEmpty(coverPath) && File.Exists(coverPath);
                string appIcon = hasValidCover ? coverPath! : "multimedia-audio-player";

                // 1. app_name (s)
                nint appName = g_variant_new_string("qqmusic-tui");

                // 2. replaces_id (u) - 始终使用 0 作为新切歌事件弹出桌面气泡，避免桌面环境静默替换已归档历史
                nint replacesId = g_variant_new_uint32(0);

                // 3. app_icon (s)
                nint icon = g_variant_new_string(appIcon);

                // 4. summary (s)
                nint summary = g_variant_new_string(title);

                // 5. body (s)
                nint bodyVar = g_variant_new_string(body);

                // 6. actions (as)
                nint asType = g_variant_type_new("as");
                nint asBuilder = g_variant_builder_new(asType);
                g_variant_type_free(asType);
                nint actions = g_variant_builder_end(asBuilder);
                g_variant_builder_unref(asBuilder);

                // 7. hints (a{sv})
                nint hintsType = g_variant_type_new("a{sv}");
                nint hintsBuilder = g_variant_builder_new(hintsType);
                g_variant_type_free(hintsType);

                if (hasValidCover)
                {
                    // "image-path" -> <coverPath>
                    nint k1 = g_variant_new_string("image-path");
                    nint v1Val = g_variant_new_string(coverPath!);
                    nint v1 = g_variant_new_variant(v1Val);
                    nint entry1 = g_variant_new_dict_entry(k1, v1);
                    g_variant_builder_add_value(hintsBuilder, entry1);

                    // 兼容旧规范 "image_path"
                    nint k2 = g_variant_new_string("image_path");
                    nint v2Val = g_variant_new_string(coverPath!);
                    nint v2 = g_variant_new_variant(v2Val);
                    nint entry2 = g_variant_new_dict_entry(k2, v2);
                    g_variant_builder_add_value(hintsBuilder, entry2);
                }

                nint hints = g_variant_builder_end(hintsBuilder);
                g_variant_builder_unref(hintsBuilder);

                // 8. expire_timeout (i) - 3500ms
                nint expireTimeout = g_variant_new_int32(3500);

                nint[] children = [
                    appName,
                    replacesId,
                    icon,
                    summary,
                    bodyVar,
                    actions,
                    hints,
                    expireTimeout
                ];

                nint parameters = g_variant_new_tuple(children, (nuint)children.Length);

                nint reply = g_dbus_connection_call_sync(
                    _connection,
                    "org.freedesktop.Notifications",
                    "/org/freedesktop/Notifications",
                    "org.freedesktop.Notifications",
                    "Notify",
                    parameters,
                    0,
                    0,
                    2000,
                    0,
                    out nint callErr
                );

                // 注意：g_dbus_connection_call_sync 已消费 parameters 的 floating reference，严禁再次 g_variant_unref(parameters)

                if (reply != 0)
                {
                    nint idChild = g_variant_get_child_value(reply, 0);
                    if (idChild != 0)
                    {
                        _lastNotificationId = g_variant_get_uint32(idChild);
                        g_variant_unref(idChild);
                    }
                    g_variant_unref(reply);
                }
                else
                {
                    if (callErr != 0)
                    {
                        try
                        {
                            var err = Marshal.PtrToStructure<GError>(callErr);
                            string? msg = Marshal.PtrToStringUTF8(err.Message);
                            AppLogger.Debug("DesktopNotification", $"D-Bus Notify failed: code={err.Code}, msg={msg}");
                        }
                        catch {}
                        finally
                        {
                            g_error_free(callErr);
                        }
                    }
                    else
                    {
                        AppLogger.Debug("DesktopNotification", "D-Bus Notify returned null (possibly no notification daemon running)");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("DesktopNotification", $"Failed to send D-Bus notification: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _connection = 0;
        }
    }

    #region P/Invoke GLib & GIO

    [LibraryImport(LibGio)]
    private static partial nint g_bus_get_sync(int busType, nint cancellable, out nint error);

    [LibraryImport(LibGio, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_dbus_connection_call_sync(
        nint connection,
        string busName,
        string objectPath,
        string interfaceName,
        string methodName,
        nint parameters,
        nint replyType,
        int flags,
        int timeoutMsec,
        nint cancellable,
        out nint error
    );

    [LibraryImport(LibGlib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_variant_new_string(string str);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_new_uint32(uint value);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_new_int32(int value);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_new_tuple(nint[] children, nuint nChildren);

    [LibraryImport(LibGlib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_variant_type_new(string typeStr);

    [LibraryImport(LibGlib)]
    private static partial void g_variant_type_free(nint type);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_builder_new(nint type);

    [LibraryImport(LibGlib)]
    private static partial void g_variant_builder_add_value(nint builder, nint value);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_builder_end(nint builder);

    [LibraryImport(LibGlib)]
    private static partial void g_variant_builder_unref(nint builder);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_new_dict_entry(nint key, nint value);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_new_variant(nint value);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_get_child_value(nint value, nuint index);

    [LibraryImport(LibGlib)]
    private static partial uint g_variant_get_uint32(nint value);

    [LibraryImport(LibGlib)]
    private static partial void g_variant_unref(nint value);

    [LibraryImport(LibGlib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool g_variant_get_boolean(nint value);

    [LibraryImport("libgobject-2.0.so.0")]
    private static partial void g_object_unref(nint obj);

    [LibraryImport("libc")]
    private static partial uint geteuid();

    [LibraryImport(LibGlib)]
    private static partial void g_error_free(nint error);

    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint Domain;
        public int Code;
        public nint Message;
    }

    #endregion
}
