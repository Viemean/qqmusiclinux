using System.Diagnostics;
using System.Runtime.InteropServices;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services.QqAudioRecognition;

/// <summary>
/// 官方高精度 QAFP 特征提取 Runner（基于极简微型 ARM64 Bionic 环境与 QEMU 管道）
/// </summary>
public static class QafpNativeRunner
{
    private static string? _cachedRunnerPath;
    private static string? _cachedSysrootPath;
    private static string? _cachedModelPath;
    private static string? _cachedQemuPath;
    private static bool _initialized;
    private static readonly object _lock = new();

    /// <summary>
    /// 当前系统是否具备官方原生 Runner 运行环境
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return !string.IsNullOrEmpty(_cachedRunnerPath) &&
                   !string.IsNullOrEmpty(_cachedSysrootPath) &&
                   !string.IsNullOrEmpty(_cachedModelPath);
        }
    }

    /// <summary>
    /// Runner 二进制绝对路径
    /// </summary>
    public static string? RunnerPath
    {
        get
        {
            EnsureInitialized();
            return _cachedRunnerPath;
        }
    }

    /// <summary>
    /// 微型 Bionic sysroot 路径
    /// </summary>
    public static string? SysrootPath
    {
        get
        {
            EnsureInitialized();
            return _cachedSysrootPath;
        }
    }

    /// <summary>
    /// 官方模型 qafp.bin 路径
    /// </summary>
    public static string? ModelPath
    {
        get
        {
            EnsureInitialized();
            return _cachedModelPath;
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_lock)
        {
            if (_initialized) return;

            // 1. 查找 qemu-aarch64-static (如果在 x86_64/其它非 aarch64 平台)
            if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            {
                _cachedQemuPath = FindExecutable("qemu-aarch64-static") ??
                                  FindExecutable("qemu-aarch64");
            }

            // 2. 候选路径
            var candidates = new List<string>();

            // 环境变量
            var envDir = Environment.GetEnvironmentVariable("QQMUSIC_QAFP_RUNNER_DIR");
            if (!string.IsNullOrEmpty(envDir)) candidates.Add(envDir);

            // 用户本地数据目录
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userHome))
            {
                candidates.Add(Path.Combine(userHome, ".local", "share", "qqmusic-tui", "qafp"));
            }

            // 程序同级目录
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                candidates.Add(Path.Combine(baseDir, "qafp"));
                candidates.Add(Path.Combine(baseDir, ".bin", "qafp"));
            }

            // 当前工作目录
            var cwd = Directory.GetCurrentDirectory();
            candidates.Add(Path.Combine(cwd, ".bin", "qafp"));
            candidates.Add("/tmp/qafp_runtime");

            foreach (var dir in candidates)
            {
                var runner = Path.Combine(dir, "qafp_runner");
                var sysroot = Path.Combine(dir, "sysroot");
                var model = Path.Combine(dir, "recognize_model");

                if (File.Exists(runner) && Directory.Exists(sysroot) && File.Exists(model))
                {
                    _cachedRunnerPath = runner;
                    _cachedSysrootPath = sysroot;
                    _cachedModelPath = model;
                    AppLogger.Force("QafpNativeRunner", $"发现官方 QAFP 运行时: {dir}");
                    break;
                }
            }

            _initialized = true;
        }
    }

    /// <summary>
    /// 从 8000Hz 16-bit 单声道 PCM 数据中提取 100% 比特级官方 QAFP 特征
    /// </summary>
    public static QafpFeature? Extract(short[] pcm8k)
    {
        if (pcm8k == null || pcm8k.Length < 8000) return null;
        if (!IsAvailable) return null;

        try
        {
            var pcmBytes = new byte[pcm8k.Length * 2];
            Buffer.BlockCopy(pcm8k, 0, pcmBytes, 0, pcmBytes.Length);

            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (!string.IsNullOrEmpty(_cachedQemuPath))
            {
                psi.FileName = _cachedQemuPath;
                psi.ArgumentList.Add("-L");
                psi.ArgumentList.Add(_cachedSysrootPath!);
                psi.ArgumentList.Add(_cachedRunnerPath!);
                psi.ArgumentList.Add(_cachedModelPath!);
                psi.ArgumentList.Add("-");
                psi.ArgumentList.Add("-");
            }
            else
            {
                psi.FileName = _cachedRunnerPath!;
                psi.ArgumentList.Add(_cachedModelPath!);
                psi.ArgumentList.Add("-");
                psi.ArgumentList.Add("-");
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start()) return null;

            // 异步写入 stdin 并读取 stdout
            using var stdoutStream = new MemoryStream();
            var readStdoutTask = Task.Run(() => process.StandardOutput.BaseStream.CopyTo(stdoutStream));

            // 将 PCM 写入子进程 stdin
            process.StandardInput.BaseStream.Write(pcmBytes, 0, pcmBytes.Length);
            process.StandardInput.BaseStream.Flush();
            process.StandardInput.Close();

            if (!process.WaitForExit(5000))
            {
                process.Kill(true);
                AppLogger.Force("QafpNativeRunner", "QAFP Runner 执行超时已终止");
                return null;
            }

            readStdoutTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                var stderr = process.StandardError.ReadToEnd();
                AppLogger.Force("QafpNativeRunner", $"Runner 退出码异常 ({process.ExitCode}): {stderr}");
                return null;
            }

            var featBytes = stdoutStream.ToArray();
            if (featBytes.Length < 20)
            {
                AppLogger.Force("QafpNativeRunner", $"Runner 输出特征长度过短 ({featBytes.Length} bytes)");
                return null;
            }

            float duration = (float)pcm8k.Length / 8000.0f;
            return new QafpFeature(featBytes, duration, 0, 0.0f);
        }
        catch (Exception ex)
        {
            AppLogger.Force("QafpNativeRunner", $"Runner 执行异常: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 启动一个按需常驻的长连接特征提取 Worker 会话（弹窗/批量识别场景下免除进程反复拉起开销）
    /// </summary>
    public static QafpWorkerSession? StartWorkerSession()
    {
        if (!IsAvailable) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                psi.FileName = RunnerPath!;
                psi.ArgumentList.Add("--server");
                psi.ArgumentList.Add(ModelPath!);
            }
            else
            {
                psi.FileName = _cachedQemuPath ?? "qemu-aarch64-static";
                psi.ArgumentList.Add("-L");
                psi.ArgumentList.Add(SysrootPath!);
                psi.ArgumentList.Add(RunnerPath!);
                psi.ArgumentList.Add("--server");
                psi.ArgumentList.Add(ModelPath!);
            }

            var proc = Process.Start(psi);
            if (proc == null) return null;

            var session = new QafpWorkerSession(proc);
            return session.IsReady ? session : null;
        }
        catch (Exception ex)
        {
            AppLogger.Force("QafpNativeRunner", $"启动 Worker 异常: {ex.Message}");
            return null;
        }
    }

    private static string? FindExecutable(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in paths)
        {
            var full = Path.Combine(p, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}

/// <summary>
/// 长连接常驻 QAFP 特征提取 Worker 会话（按需拉起，生命周期随用随销，归零释放内存）
/// </summary>
public sealed class QafpWorkerSession : IDisposable
{
    private readonly Process? _process;
    private readonly Stream? _stdin;
    private readonly Stream? _stdout;
    private readonly Lock _lock = new();
    private bool _isDisposed;

    public bool IsReady { get; }

    internal QafpWorkerSession(Process process)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput.BaseStream;

        // 等待 4 字节就绪魔数 "QAFP" (0x51414650)
        byte[] readyBuf = new byte[4];
        int readTotal = 0;
        var sw = Stopwatch.StartNew();
        while (readTotal < 4 && sw.ElapsedMilliseconds < 5000)
        {
            int r = _stdout.Read(readyBuf, readTotal, 4 - readTotal);
            if (r <= 0) break;
            readTotal += r;
        }

        if (readTotal == 4 && readyBuf[0] == 0x51 && readyBuf[1] == 0x41 && readyBuf[2] == 0x46 && readyBuf[3] == 0x50)
        {
            IsReady = true;
            AppLogger.Force("QafpWorkerSession", $"常驻 Worker 已就绪 (初始化耗时: {sw.ElapsedMilliseconds}ms)");
        }
        else
        {
            AppLogger.Force("QafpWorkerSession", "常驻 Worker 握手失败");
            Dispose();
        }
    }

    public QafpFeature? Extract(short[] pcm8k)
    {
        if (!IsReady || _isDisposed || pcm8k == null || pcm8k.Length < (int)(8000 * 1.5))
        {
            return null;
        }

        lock (_lock)
        {
            if (_isDisposed || _stdin == null || _stdout == null) return null;

            try
            {
                int pcmByteLen = pcm8k.Length * 2;
                byte[] hdr = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(hdr, pcmByteLen);

                _stdin.Write(hdr, 0, 4);
                byte[] rawPcm = new byte[pcmByteLen];
                Buffer.BlockCopy(pcm8k, 0, rawPcm, 0, pcmByteLen);
                _stdin.Write(rawPcm, 0, pcmByteLen);
                _stdin.Flush();

                // 读取 4 字节响应长度
                byte[] respHdr = new byte[4];
                if (ReadExact(_stdout, respHdr, 4) != 0) return null;

                int featLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(respHdr);
                if (featLen <= 0 || featLen > 65536) return null;

                byte[] featData = new byte[featLen];
                if (ReadExact(_stdout, featData, featLen) != 0) return null;

                float duration = (float)pcm8k.Length / 8000.0f;
                return new QafpFeature(featData, duration, 0, 0.0f);
            }
            catch (Exception ex)
            {
                AppLogger.Force("QafpWorkerSession", $"Worker 通信异常: {ex.Message}");
                return null;
            }
        }
    }

    private static int ReadExact(Stream s, byte[] buf, int count)
    {
        int done = 0;
        while (done < count)
        {
            int r = s.Read(buf, done, count - done);
            if (r <= 0) return -1;
            done += r;
        }
        return 0;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                if (_stdin != null && _process != null && !_process.HasExited)
                {
                    byte[] quitHdr = new byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(quitHdr, -1);
                    _stdin.Write(quitHdr, 0, 4);
                    _stdin.Flush();
                    _stdin.Close();

                    if (!_process.WaitForExit(1000))
                    {
                        _process.Kill(true);
                    }
                }
            }
            catch {}
            finally
            {
                try { _process?.Dispose(); } catch {}
                AppLogger.Force("QafpWorkerSession", "常驻 Worker 已退出销毁，完全回收 18MB 驻留内存");
            }
        }
    }
}
