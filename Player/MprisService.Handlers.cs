using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Player;

public sealed unsafe partial class MprisService
{
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

}
