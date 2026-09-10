using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using QQMusic.Tui.Models;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Player;

/// <summary>
/// macOS Now Playing and remote-command integration through MediaPlayer.framework.
/// Uses the Objective-C runtime directly so the terminal app does not require AppKit hosting.
/// </summary>
public sealed unsafe partial class MacMediaSessionService : IDisposable
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string MediaPlayerFramework = "/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer";
    private const string AppKitFramework = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const long CommandSuccess = 0;

    private static MacMediaSessionService? s_instance;

    private nint _mediaPlayerHandle;
    private nint _commandCenter;
    private nint _commandTarget;
    private nint _nowPlayingCenter;
    private nint _currentArtwork;
    private bool _disposed;
    private bool _isReady;
    private Song? _currentSong;
    private string? _coverPath;
    private double _positionSeconds;
    private bool _isPlaying;
    private long _lastPositionUpdateTicks;

    public bool IsReady => _isReady;

    public Func<Task>? PlayPauseHandler { get; set; }
    public Func<Task>? PlayHandler { get; set; }
    public Func<Task>? PauseHandler { get; set; }
    public Func<Task>? StopHandler { get; set; }
    public Func<Task>? NextHandler { get; set; }
    public Func<Task>? PreviousHandler { get; set; }
    public Func<double, Task>? SetPositionHandler { get; set; }

    public Task StartAsync()
    {
        if (!OperatingSystem.IsMacOS()) return Task.CompletedTask;

        try
        {
            _mediaPlayerHandle = NativeLibrary.Load(MediaPlayerFramework);
            NativeLibrary.Load(AppKitFramework);
            var pool = CreateObject("NSAutoreleasePool");
            try
            {
                _nowPlayingCenter = Send(Class("MPNowPlayingInfoCenter"), Sel("defaultCenter"));
                _commandCenter = Send(Class("MPRemoteCommandCenter"), Sel("sharedCommandCenter"));
                _commandTarget = CreateCommandTarget();
                if (_nowPlayingCenter == 0 || _commandCenter == 0 || _commandTarget == 0)
                {
                    throw new InvalidOperationException("MediaPlayer framework did not expose its command or Now Playing center");
                }

                AddCommand("playCommand", "qqmusicPlay:");
                AddCommand("pauseCommand", "qqmusicPause:");
                AddCommand("stopCommand", "qqmusicStop:");
                AddCommand("togglePlayPauseCommand", "qqmusicToggle:");
                AddCommand("nextTrackCommand", "qqmusicNext:");
                AddCommand("previousTrackCommand", "qqmusicPrevious:");
                AddCommand("changePlaybackPositionCommand", "qqmusicSetPosition:");
                s_instance = this;
                _isReady = true;
                AppLogger.Info("MacMediaSession", "macOS Now Playing media session started");
            }
            finally
            {
                Release(pool);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("MacMediaSession", "Failed to start macOS media session", ex);
        }

        return Task.CompletedTask;
    }

    public void UpdatePlaybackStatus(bool isPlaying)
    {
        if (!_isReady || _nowPlayingCenter == 0) return;
        _isPlaying = isPlaying;
        SendLong(_nowPlayingCenter, Sel("setPlaybackState:"), isPlaying ? 1 : 2);
        PublishNowPlayingInfo();
    }

    public void UpdateSong(Song? song, string? coverPath = null)
    {
        _currentSong = song;
        _coverPath = coverPath;
        _positionSeconds = 0;
        ReplaceArtwork(coverPath);
        PublishNowPlayingInfo();
        if (song == null && _nowPlayingCenter != 0)
        {
            SendLong(_nowPlayingCenter, Sel("setPlaybackState:"), 3);
        }
    }

    public void UpdateCover(string? coverPath)
    {
        if (string.Equals(_coverPath, coverPath, StringComparison.Ordinal)) return;
        _coverPath = coverPath;
        ReplaceArtwork(coverPath);
        PublishNowPlayingInfo();
    }

    public void UpdatePosition(double seconds, bool force = false)
    {
        _positionSeconds = Math.Max(0, seconds);
        var now = Environment.TickCount64;
        if (!force && now - _lastPositionUpdateTicks < 5_000) return;
        _lastPositionUpdateTicks = now;
        PublishNowPlayingInfo();
    }

    private void PublishNowPlayingInfo()
    {
        if (!_isReady || _nowPlayingCenter == 0) return;
        var pool = CreateObject("NSAutoreleasePool");
        try
        {
            if (_currentSong == null)
            {
                SendPtr(_nowPlayingCenter, Sel("setNowPlayingInfo:"), 0);
                return;
            }

            var dictionary = Send(Class("NSMutableDictionary"), Sel("dictionary"));
            AddString(dictionary, "MPMediaItemPropertyTitle", _currentSong.Title);
            AddString(dictionary, "MPMediaItemPropertyArtist", _currentSong.Artist);
            AddString(dictionary, "MPMediaItemPropertyAlbumTitle", _currentSong.Album);
            AddNumber(dictionary, "MPMediaItemPropertyPlaybackDuration", _currentSong.Duration);
            AddNumber(dictionary, "MPNowPlayingInfoPropertyElapsedPlaybackTime", _positionSeconds);
            AddNumber(dictionary, "MPNowPlayingInfoPropertyPlaybackRate", _isPlaying ? 1 : 0);
            if (_currentArtwork != 0)
            {
                AddObject(dictionary, "MPMediaItemPropertyArtwork", _currentArtwork);
            }
            SendPtr(_nowPlayingCenter, Sel("setNowPlayingInfo:"), dictionary);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MacMediaSession", $"Failed to update Now Playing metadata: {ex.Message}");
        }
        finally
        {
            Release(pool);
        }
    }

    private void ReplaceArtwork(string? coverPath)
    {
        if (_currentArtwork != 0)
        {
            Release(_currentArtwork);
            _currentArtwork = 0;
        }
        if (!_isReady || string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath)) return;

        var pool = CreateObject("NSAutoreleasePool");
        try
        {
            var path = NativeString(Path.GetFullPath(coverPath));
            var image = SendPtr(Send(Class("NSImage"), Sel("alloc")), Sel("initWithContentsOfFile:"), path);
            if (image == 0) return;
            try
            {
                _currentArtwork = SendPtr(Send(Class("MPMediaItemArtwork"), Sel("alloc")), Sel("initWithImage:"), image);
            }
            finally
            {
                Release(image);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MacMediaSession", $"Failed to load album artwork: {ex.Message}");
        }
        finally
        {
            Release(pool);
        }
    }

    private nint CreateCommandTarget()
    {
        const string targetClassName = "QQMusicTuiRemoteCommandTarget";
        var targetClass = Class(targetClassName);
        if (targetClass == 0)
        {
            targetClass = objc_allocateClassPair(Class("NSObject"), targetClassName, 0);
            if (targetClass == 0) return 0;
            AddMethod(targetClass, "qqmusicPlay:", &OnPlay);
            AddMethod(targetClass, "qqmusicPause:", &OnPause);
            AddMethod(targetClass, "qqmusicStop:", &OnStop);
            AddMethod(targetClass, "qqmusicToggle:", &OnToggle);
            AddMethod(targetClass, "qqmusicNext:", &OnNext);
            AddMethod(targetClass, "qqmusicPrevious:", &OnPrevious);
            AddMethod(targetClass, "qqmusicSetPosition:", &OnSetPosition);
            objc_registerClassPair(targetClass);
        }
        return Send(Send(targetClass, Sel("alloc")), Sel("init"));
    }

    private static void AddMethod(nint targetClass, string selector, delegate* unmanaged[Cdecl]<nint, nint, nint, nint> implementation)
    {
        if (!class_addMethod(targetClass, Sel(selector), (nint)implementation, "q@:@"))
        {
            throw new InvalidOperationException($"Could not register Objective-C selector {selector}");
        }
    }

    private void AddCommand(string commandSelector, string actionSelector)
    {
        var command = Send(_commandCenter, Sel(commandSelector));
        if (command == 0) return;
        SendBool(command, Sel("setEnabled:"), true);
        SendTwoPtr(command, Sel("addTarget:action:"), _commandTarget, Sel(actionSelector));
    }

    private void RemoveCommand(string commandSelector, string actionSelector)
    {
        var command = Send(_commandCenter, Sel(commandSelector));
        if (command != 0)
        {
            SendTwoPtr(command, Sel("removeTarget:action:"), _commandTarget, Sel(actionSelector));
        }
    }

    private void AddString(nint dictionary, string keySymbol, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        AddObject(dictionary, keySymbol, NativeString(value));
    }

    private void AddNumber(nint dictionary, string keySymbol, double value)
    {
        var number = SendDouble(Class("NSNumber"), Sel("numberWithDouble:"), value);
        AddObject(dictionary, keySymbol, number);
    }

    private void AddObject(nint dictionary, string keySymbol, nint value)
    {
        var key = FrameworkConstant(keySymbol);
        if (key != 0 && value != 0) SendTwoPtr(dictionary, Sel("setObject:forKey:"), value, key);
    }

    private nint FrameworkConstant(string symbol)
    {
        return NativeLibrary.TryGetExport(_mediaPlayerHandle, symbol, out var address)
            ? Marshal.ReadIntPtr(address)
            : 0;
    }

    private static nint NativeString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return SendPtr(Class("NSString"), Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private static nint CreateObject(string className) => Send(Send(Class(className), Sel("alloc")), Sel("init"));
    private static nint Class(string name) => objc_getClass(name);
    private static nint Sel(string name) => sel_registerName(name);
    private static void Release(nint value)
    {
        if (value != 0) Send(value, Sel("release"));
    }

    private static nint Invoke(Func<Task>? handler)
    {
        try
        {
            if (handler != null) _ = handler();
        }
        catch (Exception ex)
        {
            AppLogger.Error("MacMediaSession", "Remote command handler failed", ex);
        }
        return (nint)CommandSuccess;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnPlay(nint self, nint command, nint mediaEvent) => Invoke(s_instance?.PlayHandler);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnPause(nint self, nint command, nint mediaEvent) => Invoke(s_instance?.PauseHandler);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnStop(nint self, nint command, nint mediaEvent) => Invoke(s_instance?.StopHandler);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnToggle(nint self, nint command, nint mediaEvent) => Invoke(s_instance?.PlayPauseHandler);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnNext(nint self, nint command, nint mediaEvent) => Invoke(s_instance?.NextHandler);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnPrevious(nint self, nint command, nint mediaEvent) => Invoke(s_instance?.PreviousHandler);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnSetPosition(nint self, nint command, nint mediaEvent)
    {
        var instance = s_instance;
        if (instance?.SetPositionHandler == null || mediaEvent == 0) return (nint)CommandSuccess;
        try
        {
            var position = SendDoubleReturn(mediaEvent, Sel("positionTime"));
            _ = instance.SetPositionHandler(position);
        }
        catch (Exception ex)
        {
            AppLogger.Error("MacMediaSession", "Position command handler failed", ex);
        }
        return (nint)CommandSuccess;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!OperatingSystem.IsMacOS()) return;

        var pool = CreateObject("NSAutoreleasePool");
        try
        {
            _isReady = false;
            s_instance = null;
            if (_commandCenter != 0 && _commandTarget != 0)
            {
                RemoveCommand("playCommand", "qqmusicPlay:");
                RemoveCommand("pauseCommand", "qqmusicPause:");
                RemoveCommand("stopCommand", "qqmusicStop:");
                RemoveCommand("togglePlayPauseCommand", "qqmusicToggle:");
                RemoveCommand("nextTrackCommand", "qqmusicNext:");
                RemoveCommand("previousTrackCommand", "qqmusicPrevious:");
                RemoveCommand("changePlaybackPositionCommand", "qqmusicSetPosition:");
            }
            if (_nowPlayingCenter != 0)
            {
                SendPtr(_nowPlayingCenter, Sel("setNowPlayingInfo:"), 0);
                SendLong(_nowPlayingCenter, Sel("setPlaybackState:"), 3);
            }
            if (_currentArtwork != 0)
            {
                Release(_currentArtwork);
                _currentArtwork = 0;
            }
            Release(_commandTarget);
            _commandTarget = 0;
            _commandCenter = 0;
            _nowPlayingCenter = 0;
        }
        finally
        {
            Release(pool);
        }
    }

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

    [LibraryImport(LibObjC)]
    private static partial void objc_registerClassPair(nint cls);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool class_addMethod(nint cls, nint selector, nint implementation, string types);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendPtr(nint receiver, nint selector, nint value);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendTwoPtr(nint receiver, nint selector, nint first, nint second);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendDouble(nint receiver, nint selector, double value);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial double SendDoubleReturn(nint receiver, nint selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendLong(nint receiver, nint selector, long value);
}
