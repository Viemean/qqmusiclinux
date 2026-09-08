using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.UI;

/// <summary>
/// 终端原生图像能力探测与 Kitty Graphics Protocol 图像渲染辅助类
/// </summary>
public static partial class TerminalImageHelper
{
    private static readonly HttpClient s_httpClient = new();
    private static readonly string s_cacheDir = CacheManager.CoversDir;
    private static bool? s_isImageSupported;

    static TerminalImageHelper()
    {
        try
        {
            if (!Directory.Exists(s_cacheDir))
            {
                Directory.CreateDirectory(s_cacheDir);
            }
        }
        catch
        {
            // Ignore directory creation failure
        }
    }

    /// <summary>
    /// 探测当前终端是否原生支持显示图片（如 Kitty 图像协议）
    /// </summary>
    public static bool IsImageSupported
    {
        get
        {
            if (s_isImageSupported.HasValue) return s_isImageSupported.Value;

            // 0. 允许通过环境变量显式禁用或强制开启图片支持 (用于调试或特定终端覆盖)
            var disableEnv = Environment.GetEnvironmentVariable("QQMUSIC_DISABLE_IMAGE") ?? Environment.GetEnvironmentVariable("QQMUSIC_NO_IMAGE");
            if (disableEnv == "1" || disableEnv?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
            {
                s_isImageSupported = false;
                return false;
            }

            var forceEnv = Environment.GetEnvironmentVariable("QQMUSIC_FORCE_IMAGE");
            if (forceEnv == "1" || forceEnv?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
            {
                s_isImageSupported = true;
                return true;
            }

            // 1. 探测 Kitty 环境变量
            var kittyWindowId = Environment.GetEnvironmentVariable("KITTY_WINDOW_ID");
            var kittyPid = Environment.GetEnvironmentVariable("KITTY_PID");
            var term = Environment.GetEnvironmentVariable("TERM") ?? "";
            var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? "";

            if (!string.IsNullOrEmpty(kittyWindowId) ||
                !string.IsNullOrEmpty(kittyPid) ||
                term.Equals("xterm-kitty", StringComparison.OrdinalIgnoreCase))
            {
                s_isImageSupported = true;
                return true;
            }

            // 2. 探测原生支持 Kitty Graphics Protocol 的终端（Ghostty, WezTerm 等）
            if (termProgram.Equals("ghostty", StringComparison.OrdinalIgnoreCase) ||
                termProgram.Equals("WezTerm", StringComparison.OrdinalIgnoreCase))
            {
                s_isImageSupported = true;
                return true;
            }

            s_isImageSupported = false;
            return false;
        }
    }

    /// <summary>
}
