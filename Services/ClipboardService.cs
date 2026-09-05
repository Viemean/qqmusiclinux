using System.Diagnostics;
using System.Text;

namespace QQMusic.Tui.Services;

/// <summary>
/// 跨桌面环境（Wayland / X11）与虚拟终端的系统剪贴板服务
/// </summary>
public static class ClipboardService
{
    /// <summary>
    /// 将指定文本复制到系统剪贴板（支持 Wayland wl-copy、X11 xclip/xsel 及终端 OSC 52 转义序列）
    /// </summary>
    public static bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        bool success = false;

        // 1. Wayland 环境优先尝试 wl-copy
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            if (TryRunWithStdin("wl-copy", text))
            {
                success = true;
            }
        }

        // 2. X11 环境（或 Wayland 未能成功）尝试 xclip / xsel
        if (!success && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            if (TryRunWithStdin("xclip", text, "-selection", "clipboard") ||
                TryRunWithStdin("xsel", text, "--clipboard", "--input"))
            {
                success = true;
            }
        }

        // 3. 增强：发送 ANSI OSC 52 剪贴板转义码至标准输出（兼容现代终端如 Kitty, Alacritty, Konsole 等）
        try
        {
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            Console.Out.Write($"\x1b]52;c;{base64}\x07");
            Console.Out.Flush();
            success = true;
        }
        catch
        {
            // 忽略终端转义写出异常
        }

        return success;
    }

    private static bool TryRunWithStdin(string fileName, string input, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            using (var sw = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                sw.Write(input);
                sw.Flush();
            }

            proc.WaitForExit(1000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
