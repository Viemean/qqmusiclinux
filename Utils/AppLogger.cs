using System.Text;

namespace QQMusic.Tui.Utils;

/// <summary>
/// 应用程序日志服务。生产环境下默认不进行磁盘 I/O 刷盘，仅在调试模式或严重异常时写盘。
/// </summary>
public static class AppLogger
{
    private static readonly string s_logFilePath = "/tmp/qqmusic_debug.log";
    private static readonly object s_lock = new();
    private static bool s_debugEnabled = false;

    public static void Init(bool debugEnabled = false)
    {
        s_debugEnabled = debugEnabled;
        if (!s_debugEnabled) return;

        try
        {
            lock (s_lock)
            {
                File.AppendAllText(s_logFilePath, $"=== QQ Music TUI Session Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===\n", Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    public static void Info(string module, string message)
    {
        if (s_debugEnabled) Log("INFO", module, message);
    }

    public static void Warn(string module, string message)
    {
        if (s_debugEnabled) Log("WARN", module, message);
    }

    public static void Debug(string module, string message)
    {
        if (s_debugEnabled) Log("DEBUG", module, message);
    }

    public static void Error(string module, string message, Exception? ex = null)
    {
        var fullMsg = ex != null ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" : message;
        Log("ERROR", module, fullMsg);
    }

    private static void Log(string level, string module, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{module}] {message}\n";
        try
        {
            lock (s_lock)
            {
                File.AppendAllText(s_logFilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
