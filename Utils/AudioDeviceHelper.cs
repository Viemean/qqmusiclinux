using System.Diagnostics;

namespace QQMusic.Tui.Utils;

/// <summary>
/// 系统音频设备探测与状态查询工具
/// 支持 Linux PipeWire / PulseAudio / ALSA 输出通道与内录/麦克风源探测
/// </summary>
public static class AudioDeviceHelper
{
    private static long s_lastCheckTick = 0;
    private static bool s_cachedHasOutput = true;
    private static bool s_cachedHasInternal = true;
    private static bool s_cachedHasMicrophone = true;
    private static readonly Lock s_lock = new();

    /// <summary>
    /// 检测系统是否存在可用的物理/虚拟音频输出通道 (Sink)
    /// </summary>
    public static bool HasAudioOutputDevice()
    {
        RefreshDeviceCacheIfNeeded();
        return s_cachedHasOutput;
    }

    /// <summary>
    /// 检测系统是否存在可用的系统内录源 (Monitor of Sink)
    /// </summary>
    public static bool HasInternalRecordDevice()
    {
        RefreshDeviceCacheIfNeeded();
        return s_cachedHasInternal;
    }

    /// <summary>
    /// 检测系统是否存在可用的物理麦克风输入源
    /// </summary>
    public static bool HasMicrophoneDevice()
    {
        RefreshDeviceCacheIfNeeded();
        return s_cachedHasMicrophone;
    }

    /// <summary>
    /// 获取当前默认输出设备的物理 Monitor 名称，防止使用 @DEFAULT_SINK@.monitor 时触发 PipeWire 回退至麦克风
    /// </summary>
    public static string GetDefaultSinkMonitorDevice()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pactl",
                Arguments = "get-default-sink",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd()?.Trim();
                if (process.WaitForExit(500) && process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    return $"{output}.monitor";
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AudioDeviceHelper", $"GetDefaultSinkMonitorDevice error: {ex.Message}");
        }

        return "@DEFAULT_SINK@.monitor";
    }

    /// <summary>
    /// 获取当前默认麦克风的物理 Source 名称，防止使用 "default" 时触发 PipeWire 隐式重定向
    /// </summary>
    public static string GetDefaultMicrophoneDevice()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pactl",
                Arguments = "get-default-source",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd()?.Trim();
                if (process.WaitForExit(500) && process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    if (!output.EndsWith(".monitor", StringComparison.OrdinalIgnoreCase))
                    {
                        return output;
                    }
                }
            }

            // 若默认源被指向了 monitor，则主动遍历 sources 寻找首个物理麦克风输入节点
            var listPsi = new ProcessStartInfo
            {
                FileName = "pactl",
                Arguments = "list short sources",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var listProcess = Process.Start(listPsi);
            if (listProcess != null)
            {
                var listOutput = listProcess.StandardOutput.ReadToEnd();
                if (listProcess.WaitForExit(500) && listProcess.ExitCode == 0)
                {
                    var lines = listOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('\t', StringSplitOptions.TrimEntries);
                        var name = parts.Length > 1 ? parts[1] : line;
                        if (!name.EndsWith(".monitor", StringComparison.OrdinalIgnoreCase) &&
                            !name.Contains("auto_null") &&
                            !name.Contains("dummy"))
                        {
                            return name;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AudioDeviceHelper", $"GetDefaultMicrophoneDevice error: {ex.Message}");
        }

        return "default";
    }

    /// <summary>
    /// 强制刷新设备缓存
    /// </summary>
    public static void InvalidateCache()
    {
        lock (s_lock)
        {
            s_lastCheckTick = 0;
        }
    }

    private static void RefreshDeviceCacheIfNeeded()
    {
        var now = Environment.TickCount64;
        if (now - s_lastCheckTick < 2500)
        {
            return;
        }

        lock (s_lock)
        {
            if (now - s_lastCheckTick < 2500) return;

            s_lastCheckTick = now;
            s_cachedHasOutput = ProbeAudioOutputSinks();
            (s_cachedHasInternal, s_cachedHasMicrophone) = ProbeAudioSources();
        }
    }

    private static bool ProbeAudioOutputSinks()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pactl",
                Arguments = "list short sinks",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                if (process.WaitForExit(800) && process.ExitCode == 0)
                {
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    // 过滤空行，判断是否存在至少一个非纯虚拟 dummy 输出通道
                    if (lines.Length > 0)
                    {
                        bool hasRealSink = false;
                        foreach (var line in lines)
                        {
                            if (!line.Contains("auto_null") && !line.Contains("dummy"))
                            {
                                hasRealSink = true;
                                break;
                            }
                        }
                        return hasRealSink || lines.Length > 0;
                    }
                    return false;
                }
            }
        }
        catch
        {
            // pactl 不可用时回退检测 /proc/asound/cards
        }

        try
        {
            const string procCards = "/proc/asound/cards";
            if (File.Exists(procCards))
            {
                var content = File.ReadAllText(procCards).Trim();
                if (!string.IsNullOrWhiteSpace(content) && !content.Contains("no soundcards"))
                {
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static (bool hasInternal, bool hasMic) ProbeAudioSources()
    {
        bool hasInternal = false;
        bool hasMic = false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pactl",
                Arguments = "list short sources",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                if (process.WaitForExit(800) && process.ExitCode == 0)
                {
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('\t', StringSplitOptions.TrimEntries);
                        var sourceName = parts.Length > 1 ? parts[1] : line;

                        if (sourceName.EndsWith(".monitor", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!sourceName.Contains("auto_null") && !sourceName.Contains("dummy"))
                            {
                                hasInternal = true;
                            }
                        }
                        else
                        {
                            if (!sourceName.Contains("auto_null") && !sourceName.Contains("dummy"))
                            {
                                hasMic = true;
                            }
                        }
                    }
                    return (hasInternal, hasMic);
                }
            }
        }
        catch
        {
            // pactl 探测失败时默认保持宽松可用，避免误报阻断
            hasInternal = true;
            hasMic = true;
        }

        return (hasInternal, hasMic);
    }
}
