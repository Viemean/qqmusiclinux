using System.Buffers.Binary;
using System.Diagnostics;

namespace QQMusic.Tui.Services;

/// <summary>
/// 录音输入源类型
/// </summary>
public enum AudioRecordSource
{
    /// <summary>
    /// 系统内置音频内录 (电脑正在播放的声音，高保真免杂音)
    /// </summary>
    SystemInternal,

    /// <summary>
    /// 麦克风外录 (物理麦克风/外界扬声器放音)
    /// </summary>
    Microphone
}

/// <summary>
/// 纯内存实时流式录音会话 (彻底告别磁盘文件缓冲延迟，通过管道实时流入内存)
/// </summary>
public sealed class AudioRecordingSession : IDisposable
{
    private readonly Process? _process;
    private readonly MemoryStream _pcmStream = new(64 * 1024);
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _readerTask;
    private bool _isDisposed = false;

    public bool IsRunning => _process != null && !_process.HasExited;

    public AudioRecordingSession(AudioRecordSource source)
    {
        var inputDevice = source == AudioRecordSource.SystemInternal ? "@DEFAULT_SINK@.monitor" : "default";

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("pulse");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputDevice);
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("pipe:1"); // 纯内存标准输出管道，0 毫秒磁盘缓冲延迟！

        try
        {
            _process = Process.Start(psi);
            if (_process != null)
            {
                var token = _cts.Token;
                var stdout = _process.StandardOutput.BaseStream;
                var stderr = _process.StandardError;

                // 异步抽空 stderr 避免管道死锁
                _ = Task.Run(async () =>
                {
                    try { while (!token.IsCancellationRequested && await stderr.ReadLineAsync(token) != null) { } } catch { }
                }, token);

                // 核心异步流式读取任务
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
    /// 毫秒级无锁/快照获取当前内存中已累积的 16000Hz 单声道 16-bit PCM 采样数组
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
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(rawBytes.AsSpan(i * 2, 2));
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
        _pcmStream.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// 跨 Linux 音频环境（PulseAudio / PipeWire）的高性能内存流式录音服务
/// </summary>
public static class AudioRecordingService
{
    /// <summary>
    /// 启动内存实时流式录音会话 (输出通过标准管道实时流入内存，无任何磁盘缓冲与延迟)
    /// </summary>
    public static AudioRecordingSession StartRecordingSession(AudioRecordSource source)
    {
        return new AudioRecordingSession(source);
    }
}
