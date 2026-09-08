using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

/// <summary>
/// 录音输入源类型
/// </summary>
public enum AudioRecordSource
{
    /// <summary>
    /// 系统音频内录
    /// </summary>
    SystemInternal,

    /// <summary>
    /// 麦克风录音
    /// </summary>
    Microphone
}

[StructLayout(LayoutKind.Sequential)]
public struct pa_sample_spec
{
    public int format;   // PA_SAMPLE_S16LE = 3
    public uint rate;    // 16000
    public byte channels;// 1
}

[StructLayout(LayoutKind.Sequential)]
public struct pa_buffer_attr
{
    public uint maxlength;
    public uint tlength;
    public uint prebuf;
    public uint minreq;
    public uint fragsize;
}

public enum pa_stream_direction_t
{
    PA_STREAM_NODIRECTION = 0,
    PA_STREAM_PLAYBACK = 1,
    PA_STREAM_RECORD = 2,
    PA_STREAM_UPLOAD = 3
}

internal static partial class PulseAudioSimpleNative
{
    private const string LibPulseSimple = "libpulse-simple.so.0";
    private const string LibPulse = "libpulse.so.0";

    [LibraryImport(LibPulseSimple, EntryPoint = "pa_simple_new", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr pa_simple_new(
        string? server,
        string name,
        pa_stream_direction_t dir,
        string? dev,
        string stream_name,
        in pa_sample_spec ss,
        IntPtr map,
        in pa_buffer_attr attr,
        out int error);

    [LibraryImport(LibPulseSimple, EntryPoint = "pa_simple_read")]
    public static unsafe partial int pa_simple_read(IntPtr s, void* data, nuint bytes, out int error);

    [LibraryImport(LibPulseSimple, EntryPoint = "pa_simple_free")]
    public static partial void pa_simple_free(IntPtr s);

    [LibraryImport(LibPulse, EntryPoint = "pa_strerror")]
    public static partial IntPtr pa_strerror(int error);

    public static string GetErrorMessage(int error)
    {
        try
        {
            var ptr = pa_strerror(error);
            return ptr != IntPtr.Zero ? Marshal.PtrToStringUTF8(ptr) ?? $"Code {error}" : $"Code {error}";
        }
        catch
        {
            return $"Code {error}";
        }
    }
}

/// <summary>
/// 实时流式录音会话，基于 libpulse-simple 原生直连 PulseAudio / PipeWire
/// </summary>
public sealed class AudioRecordingSession : IDisposable
{
    private readonly AudioRecordSource _source;
    private IntPtr _pulseHandle;
    private readonly MemoryStream _pcmStream = new(64 * 1024);
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread? _recordThread;
    private bool _isDisposed = false;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _pulseHandle != IntPtr.Zero && !_isDisposed;
            }
        }
    }

    public AudioRecordingSession(AudioRecordSource source)
    {
        _source = source;
        var inputDevice = source == AudioRecordSource.SystemInternal
            ? AudioDeviceHelper.GetDefaultSinkMonitorDevice()
            : AudioDeviceHelper.GetDefaultMicrophoneDevice();

        var ss = new pa_sample_spec
        {
            format = 3, // PA_SAMPLE_S16LE
            rate = 16000,
            channels = 1
        };

        // 显式指定分片大小（fragsize = 2048 字节，约 64ms），避免 PulseAudio 服务端默认使用数秒的巨大缓冲导致读阻塞
        var attr = new pa_buffer_attr
        {
            maxlength = uint.MaxValue,
            tlength = uint.MaxValue,
            prebuf = uint.MaxValue,
            minreq = uint.MaxValue,
            fragsize = 2048
        };

        try
        {
            int error;
            _pulseHandle = PulseAudioSimpleNative.pa_simple_new(
                null,
                "qqmusic-tui",
                pa_stream_direction_t.PA_STREAM_RECORD,
                inputDevice,
                "Music Recognition",
                in ss,
                IntPtr.Zero,
                in attr,
                out error
            );

            // 若指定特定设备失败且为麦克风模式，尝试以系统默认设备重试
            if (_pulseHandle == IntPtr.Zero && source == AudioRecordSource.Microphone && !string.IsNullOrEmpty(inputDevice))
            {
                _pulseHandle = PulseAudioSimpleNative.pa_simple_new(
                    null,
                    "qqmusic-tui",
                    pa_stream_direction_t.PA_STREAM_RECORD,
                    null,
                    "Music Recognition",
                    in ss,
                    IntPtr.Zero,
                    in attr,
                    out error
                );
            }

            if (_pulseHandle == IntPtr.Zero)
            {
                var errStr = PulseAudioSimpleNative.GetErrorMessage(error);
                AppLogger.Warn("AudioRecording", $"Failed to initialize pa_simple_new. Device: {inputDevice}, Error: {error} ({errStr})");
                return;
            }

            AppLogger.Force("AudioRecording", $"Started session via libpulse-simple. Source: {source}, Device: {inputDevice}");

            _recordThread = new Thread(RecordLoop)
            {
                IsBackground = true,
                Name = "PulseRecordThread"
            };
            _recordThread.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AudioRecording", $"Exception in AudioRecordingSession init: {ex.Message}");
            _pulseHandle = IntPtr.Zero;
        }
    }

    private unsafe void RecordLoop()
    {
        byte[] buffer = new byte[2048]; // ~64ms of 16000Hz 16-bit mono
        fixed (byte* pBuf = buffer)
        {
            while (!_cts.IsCancellationRequested)
            {
                IntPtr handle;
                lock (_lock)
                {
                    handle = _pulseHandle;
                }
                if (handle == IntPtr.Zero) break;

                int res = PulseAudioSimpleNative.pa_simple_read(handle, pBuf, (nuint)buffer.Length, out int error);
                if (res < 0)
                {
                    if (!_cts.IsCancellationRequested)
                    {
                        var errStr = PulseAudioSimpleNative.GetErrorMessage(error);
                        AppLogger.Warn("AudioRecording", $"pa_simple_read error: {error} ({errStr})");
                    }
                    break;
                }

                if (_cts.IsCancellationRequested) break;

                lock (_lock)
                {
                    _pcmStream.Write(buffer, 0, buffer.Length);
                }
            }
        }

        // 仅在录音线程彻底退出读取循环后，才由本线程安全释放 pulseHandle
        // 彻底杜绝主线程并发调用 pa_simple_free 导致 pa_mutex_free 发生 EBUSY 断言崩溃
        lock (_lock)
        {
            if (_pulseHandle != IntPtr.Zero)
            {
                try
                {
                    PulseAudioSimpleNative.pa_simple_free(_pulseHandle);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("AudioRecording", $"pa_simple_free exception: {ex.Message}");
                }
                _pulseHandle = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// 获取当前缓冲区中已累积的 16000Hz 单声道 16-bit PCM 采样数组
    /// 包含基于 RMS 能量的自适应增益控制 (AGC)，防止按键瞬态杂音导致增益失效
    /// </summary>
    public short[] GetSnapshotSamples()
    {
        lock (_lock)
        {
            int totalBytes = (int)_pcmStream.Length;
            int sampleCount = totalBytes / 2;
            if (sampleCount == 0) return Array.Empty<short>();

            var rawBytes = _pcmStream.GetBuffer();
            var samples = new short[sampleCount];
            long sumSquares = 0;

            for (int i = 0; i < sampleCount; i++)
            {
                short val = BinaryPrimitives.ReadInt16LittleEndian(rawBytes.AsSpan(i * 2, 2));
                samples[i] = val;
                sumSquares += (long)val * val;
            }

            double rms = Math.Sqrt((double)sumSquares / sampleCount);

            // 针对麦克风或音量较低的音频输入，采用 RMS 能量自适应增益，补偿空气与设备衰减
            // 目标有效值设为 ~3200 (对应音乐泛音峰值约 18000~24000)，最大增益限制为 8.0 倍
            if ((_source == AudioRecordSource.Microphone || rms < 2000) && rms > 80 && rms < 3000)
            {
                float gain = Math.Min(8.0f, (float)(3200.0 / rms));
                if (gain > 1.2f)
                {
                    for (int i = 0; i < sampleCount; i++)
                    {
                        samples[i] = (short)Math.Clamp((int)(samples[i] * gain), short.MinValue, short.MaxValue);
                    }
                }
            }

            return samples;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try { _cts.Cancel(); } catch { }
        try { _recordThread?.Join(500); } catch { }

        // 若录音线程未启动或已停止，在此做兜底释放
        lock (_lock)
        {
            if (_pulseHandle != IntPtr.Zero && (_recordThread == null || !_recordThread.IsAlive))
            {
                try
                {
                    PulseAudioSimpleNative.pa_simple_free(_pulseHandle);
                }
                catch {}
                _pulseHandle = IntPtr.Zero;
            }
        }

        AppLogger.Force("AudioRecording", $"Stopped session. Captured {_pcmStream.Length} PCM bytes.");
        _pcmStream.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// 音频录制服务（PulseAudio / PipeWire 原生直连）
/// </summary>
public static class AudioRecordingService
{
    /// <summary>
    /// 启动流式录音会话
    /// </summary>
    public static AudioRecordingSession StartRecordingSession(AudioRecordSource source)
    {
        return new AudioRecordingSession(source);
    }
}
