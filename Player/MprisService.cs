using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Player;

/// <summary>
/// Linux MPRIS2 D-Bus 媒体播放器控制规范服务
/// 基于系统原生 libgio-2.0.so.0 / libglib-2.0.so.0 P/Invoke 实现
/// 允许 KDE Plasma、GNOME、锁屏小部件及多媒体物理键盘控制 TUI 音乐播放
/// </summary>
public sealed unsafe partial class MprisService : IDisposable
{
    private const string LibGio = "libgio-2.0.so.0";
    private const string LibGlib = "libglib-2.0.so.0";

    private const int G_BUS_TYPE_SESSION = 2;
    private const uint DBUS_NAME_FLAG_REPLACE_EXISTING = 4;

    private const string ServiceName = "org.mpris.MediaPlayer2.qqmusic_tui";
    private const string MprisObjectPath = "/org/mpris/MediaPlayer2";

    private static MprisService? s_instance;

    private readonly Lock _lock = new();
    private bool _disposed;
    private volatile bool _isReady;

    private nint _mainLoop;
    private Thread? _loopThread;
    private nint _connection;
    private nint _nodeInfo;
    private uint _rootRegId;
    private uint _playerRegId;
    private GDBusInterfaceVTable* _vtable;

    // 当前 MPRIS 状态缓存
    private string _playbackStatus = "Stopped";
    private double _volume = 0.8;
    private PlaybackMode _playbackMode = PlaybackMode.ListLoop;
    private Song? _currentSong;
    private string? _currentCoverPath;
    private double _currentPositionSeconds;

    // 向上派发至 UI / 播放引擎的回调委托
    public Func<Task>? PlayPauseHandler { get; set; }
    public Func<Task>? PlayHandler { get; set; }
    public Func<Task>? PauseHandler { get; set; }
    public Func<Task>? StopHandler { get; set; }
    public Func<Task>? NextHandler { get; set; }
    public Func<Task>? PreviousHandler { get; set; }
    public Func<double, Task>? SeekHandler { get; set; }
    public Func<double, Task>? SetPositionHandler { get; set; }
    public Action<double>? VolumeSetHandler { get; set; }
    public Action<string>? LoopStatusSetHandler { get; set; }
    public Action<bool>? ShuffleSetHandler { get; set; }
    public Action? QuitHandler { get; set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GDBusInterfaceVTable
    {
        public nint MethodCall;
        public nint GetProperty;
        public nint SetProperty;
        public nint Pad0, Pad1, Pad2, Pad3, Pad4, Pad5, Pad6, Pad7;
    }

    private const string IntrospectionXml = """
        <!DOCTYPE node PUBLIC "-//freedesktop//DTD D-BUS Object Introspection 1.0//EN"
        "http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd">
        <node>
          <interface name="org.mpris.MediaPlayer2">
            <method name="Raise"/>
            <method name="Quit"/>
            <property name="CanQuit" type="b" access="read"/>
            <property name="CanRaise" type="b" access="read"/>
            <property name="HasTrackList" type="b" access="read"/>
            <property name="Identity" type="s" access="read"/>
            <property name="DesktopEntry" type="s" access="read"/>
            <property name="SupportedUriSchemes" type="as" access="read"/>
            <property name="SupportedMimeTypes" type="as" access="read"/>
          </interface>
          <interface name="org.mpris.MediaPlayer2.Player">
            <method name="Next"/>
            <method name="Previous"/>
            <method name="Pause"/>
            <method name="PlayPause"/>
            <method name="Stop"/>
            <method name="Play"/>
            <method name="Seek">
              <arg name="Offset" direction="in" type="x"/>
            </method>
            <method name="SetPosition">
              <arg name="TrackId" direction="in" type="o"/>
              <arg name="Position" direction="in" type="x"/>
            </method>
            <method name="OpenUri">
              <arg name="Uri" direction="in" type="s"/>
            </method>
            <signal name="Seeked">
              <arg name="Position" type="x"/>
            </signal>
            <property name="PlaybackStatus" type="s" access="read"/>
            <property name="LoopStatus" type="s" access="readwrite"/>
            <property name="Rate" type="d" access="readwrite"/>
            <property name="Shuffle" type="b" access="readwrite"/>
            <property name="Metadata" type="a{sv}" access="read"/>
            <property name="Volume" type="d" access="readwrite"/>
            <property name="Position" type="x" access="read"/>
            <property name="MinimumRate" type="d" access="read"/>
            <property name="MaximumRate" type="d" access="read"/>
            <property name="CanGoNext" type="b" access="read"/>
            <property name="CanGoPrevious" type="b" access="read"/>
            <property name="CanPlay" type="b" access="read"/>
            <property name="CanPause" type="b" access="read"/>
            <property name="CanSeek" type="b" access="read"/>
            <property name="CanControl" type="b" access="read"/>
          </interface>
        </node>
        """;

    public Task StartAsync()
    {
        s_instance = this;

        try
        {
            // 1. 启动专用 GLib 事件循环后台线程（驱动 GIO D-Bus 消息派发）
            _mainLoop = g_main_loop_new(0, 0);
            _loopThread = new Thread(() =>
            {
                if (_mainLoop != 0)
                {
                    g_main_loop_run(_mainLoop);
                }
            })
            {
                IsBackground = true,
                Name = "Mpris-GMainLoop"
            };
            _loopThread.Start();

            // 2. 连接会话总线 (Session Bus)
            _connection = g_bus_get_sync(G_BUS_TYPE_SESSION, 0, out nint connErr);
            if (_connection == 0)
            {
                AppLogger.Error("MprisService", "Failed to connect to D-Bus session bus via GIO");
                return Task.CompletedTask;
            }

            // 3. 解析 MPRIS2 Introspection XML
            _nodeInfo = g_dbus_node_info_new_for_xml(IntrospectionXml, out nint xmlErr);
            if (_nodeInfo == 0)
            {
                AppLogger.Error("MprisService", "Failed to parse MPRIS2 Introspection XML");
                return Task.CompletedTask;
            }

            nint rootIface = g_dbus_node_info_lookup_interface(_nodeInfo, "org.mpris.MediaPlayer2");
            nint playerIface = g_dbus_node_info_lookup_interface(_nodeInfo, "org.mpris.MediaPlayer2.Player");

            // 4. 构建 VTable（Native AOT UnmanagedCallersOnly 函数指针）
            _vtable = (GDBusInterfaceVTable*)NativeMemory.AllocZeroed((nuint)sizeof(GDBusInterfaceVTable));
            _vtable->MethodCall = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, void>)&OnMethodCall;
            _vtable->GetProperty = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint>)&OnGetProperty;
            _vtable->SetProperty = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, int>)&OnSetProperty;

            // 5. 在 /org/mpris/MediaPlayer2 上注册两个接口
            _rootRegId = g_dbus_connection_register_object(
                _connection,
                MprisObjectPath,
                rootIface,
                (nint)_vtable,
                0, 0, out _
            );

            _playerRegId = g_dbus_connection_register_object(
                _connection,
                MprisObjectPath,
                playerIface,
                (nint)_vtable,
                0, 0, out _
            );

            // 6. 申请 MPRIS2 熟知总线名称
            RequestBusName(ServiceName);

            _isReady = true;
            AppLogger.Info("MprisService", $"MPRIS2 service started successfully via GIO on {ServiceName}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("MprisService", "Failed to start native MPRIS2 service", ex);
        }

        return Task.CompletedTask;
    }

    private void RequestBusName(string name)
    {
        try
        {
            var tuple = g_variant_new_tuple(
                [
                    g_variant_new_string(name),
                    g_variant_new_uint32(DBUS_NAME_FLAG_REPLACE_EXISTING)
                ],
                2
            );

            nint res = g_dbus_connection_call_sync(
                _connection,
                "org.freedesktop.DBus",
                "/org/freedesktop/DBus",
                "org.freedesktop.DBus",
                "RequestName",
                tuple,
                0, 0, -1, 0, out nint err
            );

            if (res != 0)
            {
                g_variant_unref(res);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("MprisService", $"Failed to request bus name {name}", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMethodCall(
        nint connection,
        nint sender,
        nint objectPath,
        nint interfaceName,
        nint methodName,
        nint parameters,
        nint invocation,
        nint userData)
    {
        var iface = Marshal.PtrToStringUTF8(interfaceName) ?? "";
        var member = Marshal.PtrToStringUTF8(methodName) ?? "";

        AppLogger.Info("MprisService", $"Method called: {iface}.{member}");

        var instance = s_instance;
        if (instance != null)
        {
            if (iface == "org.mpris.MediaPlayer2")
            {
                switch (member)
                {
                    case "Quit":
                        instance.QuitHandler?.Invoke();
                        break;
                    case "Raise":
                        break;
                }
            }
            else if (iface == "org.mpris.MediaPlayer2.Player")
            {
                switch (member)
                {
                    case "PlayPause":
                        _ = instance.PlayPauseHandler?.Invoke();
                        break;
                    case "Play":
                        _ = instance.PlayHandler?.Invoke();
                        break;
                    case "Pause":
                        _ = instance.PauseHandler?.Invoke();
                        break;
                    case "Stop":
                        _ = instance.StopHandler?.Invoke();
                        break;
                    case "Next":
                        _ = instance.NextHandler?.Invoke();
                        break;
                    case "Previous":
                        _ = instance.PreviousHandler?.Invoke();
                        break;
                    case "Seek":
                        if (parameters != 0)
                        {
                            nint child = g_variant_get_child_value(parameters, 0);
                            if (child != 0)
                            {
                                long offsetUs = g_variant_get_int64(child);
                                g_variant_unref(child);
                                _ = instance.SeekHandler?.Invoke(offsetUs / 1_000_000.0);
                            }
                        }
                        break;
                    case "SetPosition":
                        if (parameters != 0)
                        {
                            nint child1 = g_variant_get_child_value(parameters, 1);
                            if (child1 != 0)
                            {
                                long posUs = g_variant_get_int64(child1);
                                g_variant_unref(child1);
                                _ = instance.SetPositionHandler?.Invoke(posUs / 1_000_000.0);
                            }
                        }
                        break;
                    case "OpenUri":
                        break;
                }
            }
        }

        // 返回 void
        g_dbus_method_invocation_return_value(invocation, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnGetProperty(
        nint connection,
        nint sender,
        nint objectPath,
        nint interfaceName,
        nint propertyName,
        nint error,
        nint userData)
    {
        var iface = Marshal.PtrToStringUTF8(interfaceName) ?? "";
        var prop = Marshal.PtrToStringUTF8(propertyName) ?? "";

        var instance = s_instance;
        if (instance == null) return 0;

        if (iface == "org.mpris.MediaPlayer2")
        {
            switch (prop)
            {
                case "CanQuit":
                    return g_variant_new_boolean(1);
                case "CanRaise":
                    return g_variant_new_boolean(0);
                case "HasTrackList":
                    return g_variant_new_boolean(0);
                case "Identity":
                    return g_variant_new_string("QQ Music (TUI)");
                case "DesktopEntry":
                    return g_variant_new_string("qqmusic");
                case "SupportedUriSchemes":
                    return CreateStringArray();
                case "SupportedMimeTypes":
                    return CreateStringArray("audio/mpeg", "audio/flac");
            }
        }
        else if (iface == "org.mpris.MediaPlayer2.Player")
        {
            string status;
            double vol;
            PlaybackMode mode;
            Song? song;
            string? coverPath;
            double posSec;

            lock (instance._lock)
            {
                status = instance._playbackStatus;
                vol = instance._volume;
                mode = instance._playbackMode;
                song = instance._currentSong;
                coverPath = instance._currentCoverPath;
                posSec = instance._currentPositionSeconds;
            }

            var (loopStatus, shuffle) = mode.ToMpris();

            switch (prop)
            {
                case "PlaybackStatus":
                    return g_variant_new_string(status);
                case "LoopStatus":
                    return g_variant_new_string(loopStatus);
                case "Rate":
                    return g_variant_new_double(1.0);
                case "Shuffle":
                    return g_variant_new_boolean(shuffle ? 1 : 0);
                case "Metadata":
                    return BuildMetadataVariant(song, coverPath);
                case "Volume":
                    return g_variant_new_double(vol);
                case "Position":
                    return g_variant_new_int64((long)(posSec * 1_000_000.0));
                case "MinimumRate":
                case "MaximumRate":
                    return g_variant_new_double(1.0);
                case "CanGoNext":
                case "CanGoPrevious":
                case "CanPlay":
                case "CanPause":
                case "CanSeek":
                case "CanControl":
                    return g_variant_new_boolean(1);
            }
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSetProperty(
        nint connection,
        nint sender,
        nint objectPath,
        nint interfaceName,
        nint propertyName,
        nint value,
        nint error,
        nint userData)
    {
        var iface = Marshal.PtrToStringUTF8(interfaceName) ?? "";
        var prop = Marshal.PtrToStringUTF8(propertyName) ?? "";

        var instance = s_instance;
        if (instance != null && iface == "org.mpris.MediaPlayer2.Player")
        {
            if (prop == "Volume")
            {
                double vol = g_variant_get_double(value);
                instance.VolumeSetHandler?.Invoke(Math.Clamp(vol, 0.0, 1.0));
                return 1;
            }
            if (prop == "LoopStatus")
            {
                nint strPtr = g_variant_get_string(value, out _);
                var loopStr = Marshal.PtrToStringUTF8(strPtr);
                if (loopStr != null)
                {
                    instance.LoopStatusSetHandler?.Invoke(loopStr);
                }
                return 1;
            }
            if (prop == "Shuffle")
            {
                bool shuffle = g_variant_get_boolean(value) != 0;
                instance.ShuffleSetHandler?.Invoke(shuffle);
                return 1;
            }
        }

        return 1;
    }

    private static nint CreateDictEntry(string key, nint valVariant)
    {
        nint k = g_variant_new_string(key);
        nint v = g_variant_new_variant(valVariant);
        return g_variant_new_dict_entry(k, v);
    }

    private static nint CreateStringArray(params string[] strings)
    {
        nint type = g_variant_type_new("as");
        nint builder = g_variant_builder_new(type);
        g_variant_type_free(type);

        foreach (var s in strings)
        {
            nint sv = g_variant_new_string(s);
            g_variant_builder_add_value(builder, sv);
        }

        nint arr = g_variant_builder_end(builder);
        g_variant_builder_unref(builder);
        return arr;
    }

    private static nint BuildMetadataVariant(Song? song, string? coverPath = null)
    {
        nint type = g_variant_type_new("a{sv}");
        nint builder = g_variant_builder_new(type);
        g_variant_type_free(type);

        var midClean = (song?.Mid ?? "").Replace("-", "_");
        var trackPath = string.IsNullOrEmpty(midClean)
            ? "/org/mpris/MediaPlayer2/TrackList/NoTrack"
            : $"/org/mpris/MediaPlayer2/track/{midClean}";

        // mpris:trackid (o)
        g_variant_builder_add_value(builder, CreateDictEntry("mpris:trackid", g_variant_new_object_path(trackPath)));

        // mpris:length (x)
        if (song != null && song.Duration > 0)
        {
            g_variant_builder_add_value(builder, CreateDictEntry("mpris:length", g_variant_new_int64((long)song.Duration * 1_000_000L)));
        }

        // xesam:title (s)
        g_variant_builder_add_value(builder, CreateDictEntry("xesam:title", g_variant_new_string(song?.Title ?? "")));

        // xesam:album (s)
        g_variant_builder_add_value(builder, CreateDictEntry("xesam:album", g_variant_new_string(song?.Album ?? "")));

        // xesam:artist (as)
        g_variant_builder_add_value(builder, CreateDictEntry("xesam:artist", CreateStringArray(song?.Artist ?? "")));

        // mpris:artUrl (s)
        string? artUrl = null;
        if (!string.IsNullOrEmpty(coverPath))
        {
            artUrl = coverPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                     coverPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     coverPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? coverPath
                : $"file://{Path.GetFullPath(coverPath)}";
        }
        else if (!string.IsNullOrEmpty(song?.AlbumMid))
        {
            artUrl = $"https://y.qq.com/music/photo_new/T002R1200x1200M000{song.AlbumMid}.jpg?max_age=2592000";
        }

        if (!string.IsNullOrEmpty(artUrl))
        {
            g_variant_builder_add_value(builder, CreateDictEntry("mpris:artUrl", g_variant_new_string(artUrl)));
        }

        nint dict = g_variant_builder_end(builder);
        g_variant_builder_unref(builder);
        return dict;
    }

    public void UpdatePlaybackStatus(bool isPlaying)
    {
        var status = isPlaying ? "Playing" : "Paused";
        lock (_lock)
        {
            if (_playbackStatus == status) return;
            _playbackStatus = status;
        }

        EmitPropertiesChanged(builder =>
        {
            g_variant_builder_add_value(builder, CreateDictEntry("PlaybackStatus", g_variant_new_string(status)));
        });
    }

    public void UpdateSong(Song? song, string? coverPath = null)
    {
        lock (_lock)
        {
            _currentSong = song;
            _currentCoverPath = coverPath;
            _currentPositionSeconds = 0;
        }

        EmitPropertiesChanged(builder =>
        {
            g_variant_builder_add_value(builder, CreateDictEntry("Metadata", BuildMetadataVariant(song, coverPath)));
        });
    }

    public void UpdateCover(string? coverPath)
    {
        lock (_lock)
        {
            _currentCoverPath = coverPath;
        }

        EmitPropertiesChanged(builder =>
        {
            g_variant_builder_add_value(builder, CreateDictEntry("Metadata", BuildMetadataVariant(_currentSong, _currentCoverPath)));
        });
    }

    public void UpdateVolume(int volumePercent)
    {
        double vol = Math.Clamp(volumePercent / 100.0, 0.0, 1.0);
        lock (_lock)
        {
            if (Math.Abs(_volume - vol) < 0.001) return;
            _volume = vol;
        }

        EmitPropertiesChanged(builder =>
        {
            g_variant_builder_add_value(builder, CreateDictEntry("Volume", g_variant_new_double(vol)));
        });
    }

    public void UpdatePlaybackMode(PlaybackMode mode)
    {
        lock (_lock)
        {
            if (_playbackMode == mode) return;
            _playbackMode = mode;
        }

        var (loopStatus, shuffle) = mode.ToMpris();
        EmitPropertiesChanged(builder =>
        {
            g_variant_builder_add_value(builder, CreateDictEntry("LoopStatus", g_variant_new_string(loopStatus)));
            g_variant_builder_add_value(builder, CreateDictEntry("Shuffle", g_variant_new_boolean(shuffle ? 1 : 0)));
        });
    }

    public void UpdatePosition(double seconds)
    {
        lock (_lock)
        {
            _currentPositionSeconds = seconds;
        }
    }

    public void EmitSeeked(double seconds)
    {
        lock (_lock)
        {
            _currentPositionSeconds = seconds;
        }

        if (_disposed || _connection == 0 || !_isReady) return;
        try
        {
            var tuple = g_variant_new_tuple(
                [
                    g_variant_new_int64((long)(seconds * 1_000_000.0))
                ],
                1
            );

            g_dbus_connection_emit_signal(
                _connection,
                null,
                MprisObjectPath,
                "org.mpris.MediaPlayer2.Player",
                "Seeked",
                tuple,
                out _
            );
        }
        catch (Exception ex)
        {
            AppLogger.Error("MprisService", "Failed to emit Seeked signal", ex);
        }
    }

    private void EmitPropertiesChanged(Action<nint> addChangedEntries)
    {
        if (_disposed || _connection == 0 || !_isReady) return;

        try
        {
            nint dictType = g_variant_type_new("a{sv}");
            nint dictBuilder = g_variant_builder_new(dictType);
            g_variant_type_free(dictType);

            addChangedEntries(dictBuilder);

            nint changedDict = g_variant_builder_end(dictBuilder);
            g_variant_builder_unref(dictBuilder);

            nint emptyInvalidated = CreateStringArray();

            var tuple = g_variant_new_tuple(
                [
                    g_variant_new_string("org.mpris.MediaPlayer2.Player"),
                    changedDict,
                    emptyInvalidated
                ],
                3
            );

            g_dbus_connection_emit_signal(
                _connection,
                null,
                MprisObjectPath,
                "org.freedesktop.DBus.Properties",
                "PropertiesChanged",
                tuple,
                out _
            );
        }
        catch (Exception ex)
        {
            AppLogger.Error("MprisService", "Failed to emit PropertiesChanged signal", ex);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            s_instance = null;

            if (_rootRegId != 0 && _connection != 0)
            {
                g_dbus_connection_unregister_object(_connection, _rootRegId);
                _rootRegId = 0;
            }

            if (_playerRegId != 0 && _connection != 0)
            {
                g_dbus_connection_unregister_object(_connection, _playerRegId);
                _playerRegId = 0;
            }

            if (_vtable != null)
            {
                NativeMemory.Free(_vtable);
                _vtable = null;
            }

            if (_mainLoop != 0)
            {
                g_main_loop_quit(_mainLoop);
                g_main_loop_unref(_mainLoop);
                _mainLoop = 0;
            }
        }
    }

    // --- Native GIO / GLib LibraryImports ---

    [LibraryImport(LibGlib)]
    private static partial nint g_main_loop_new(nint context, int isRunning);

    [LibraryImport(LibGlib)]
    private static partial void g_main_loop_run(nint loop);

    [LibraryImport(LibGlib)]
    private static partial void g_main_loop_quit(nint loop);

    [LibraryImport(LibGlib)]
    private static partial void g_main_loop_unref(nint loop);

    [LibraryImport(LibGlib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_variant_type_new(string typeString);

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

    [LibraryImport(LibGlib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_variant_new_string(string str);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_new_boolean(int value);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_new_double(double value);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_new_int64(long value);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_new_uint32(uint value);

    [LibraryImport(LibGlib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_variant_new_object_path(string path);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_new_variant(nint value);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_new_dict_entry(nint key, nint value);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_new_tuple(nint[] children, nuint nChildren);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_get_string(nint value, out nuint length);

    [LibraryImport(LibGlib)]
    private static partial int g_variant_get_boolean(nint value);

    [LibraryImport(LibGlib)]
    private static partial double g_variant_get_double(nint value);

    [LibraryImport(LibGlib)]
    private static partial long g_variant_get_int64(nint value);

    [LibraryImport(LibGlib)]
    private static partial nint g_variant_get_child_value(nint value, nuint index);

    [LibraryImport(LibGlib)]
    private static partial void g_variant_unref(nint value);

    // GIO Imports

    [LibraryImport(LibGio)]
    private static partial nint g_bus_get_sync(int busType, nint cancellable, out nint error);

    [LibraryImport(LibGio, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_dbus_node_info_new_for_xml(string xml, out nint error);

    [LibraryImport(LibGio, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_dbus_node_info_lookup_interface(nint info, string name);

    [LibraryImport(LibGio, StringMarshalling = StringMarshalling.Utf8)]
    private static partial uint g_dbus_connection_register_object(
        nint connection,
        string objectPath,
        nint interfaceInfo,
        nint vtable,
        nint userData,
        nint userDataFreeFunc,
        out nint error
    );

    [LibraryImport(LibGio)]
    private static partial int g_dbus_connection_unregister_object(nint connection, uint registrationId);

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

    [LibraryImport(LibGio, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int g_dbus_connection_emit_signal(
        nint connection,
        string? destinationBusName,
        string objectPath,
        string interfaceName,
        string signalName,
        nint parameters,
        out nint error
    );

    [LibraryImport(LibGio)]
    private static partial void g_dbus_method_invocation_return_value(nint invocation, nint parameters);
}
