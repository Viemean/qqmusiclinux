using System.Buffers.Binary;
using System.Diagnostics;

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

/// <summary>
/// 实时流式录音会话，通过管道写入内存流
/// </summary>
public sealed class AudioRecordingSession : IDisposable
{
    private readonly AudioRecordSource _source;
    private readonly Process? _process;
    private readonly MemoryStream _pcmStream = new(64 * 1024);
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _readerTask;
    private bool _isDisposed = false;

    public bool IsRunning => _process != null && !_process.HasExited;

    public AudioRecordingSession(AudioRecordSource source)
    {
        _source = source;
        var inputDevice = source == AudioRecordSource.SystemInternal
            ? Utils.AudioDeviceHelper.GetDefaultSinkMonitorDevice()
            : Utils.AudioDeviceHelper.GetDefaultMicrophoneDevice();

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("pulse");
        psi.ArgumentList.Add("-thread_queue_size");
        psi.ArgumentList.Add("1024");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputDevice);
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("pipe:1");

        try
        {
            _process = Process.Start(psi);
            if (_process != null)
            {
                Utils.AppLogger.Force("AudioRecording", $"Started session. Source: {source}, DeviceNode: {inputDevice}, PID: {_process.Id}");
                var token = _cts.Token;
                var stdout = _process.StandardOutput.BaseStream;
                var stderr = _process.StandardError;

                _ = Task.Run(async () =>
                {
                    try { while (!token.IsCancellationRequested && await stderr.ReadLineAsync(token) != null) { } } catch { }
                }, token);

                _readerTask = Task.Run(async () =>
                {
                    byte[] buffer = new byte[4096];
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            int bytesRead = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                            if (bytesRead <= 0) break;

                            lock (_lock)
                            {
                                _pcmStream.Write(buffer, 0, bytesRead);
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                }, token);
            }
        }
        catch
        {
            _process = null;
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

        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(true);
                }
            }
            catch { }
            finally
            {
                _process.Dispose();
            }
        }

        try { _readerTask?.Wait(200); } catch { }
        Utils.AppLogger.Force("AudioRecording", $"Stopped session. Captured {_pcmStream.Length} PCM bytes.");
        _pcmStream.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// 音频录制服务（PulseAudio / PipeWire）
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
