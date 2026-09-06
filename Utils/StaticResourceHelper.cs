using System.Reflection;
using System.Text;

namespace QQMusic.Tui.Utils;

/// <summary>
/// 静态 Web 资源加载器：支持磁盘外部热重载与二进制内置程序集保底
/// </summary>
public static class StaticResourceHelper
{
    private static readonly Assembly s_assembly = typeof(StaticResourceHelper).Assembly;

    /// <summary>
    /// 读取静态 Web 文件内容（优先物理磁盘，缺失时自动回退至二进制内嵌资源）
    /// </summary>
    public static string LoadStaticText(string fileName)
    {
        var filePath = ResolveDiskFilePath(fileName);
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            try
            {
                return File.ReadAllText(filePath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("StaticResourceHelper", $"Failed to read {filePath}: {ex.Message}");
            }
        }

        return LoadEmbeddedText(fileName);
    }

    /// <summary>
    /// 从当前程序集资源流中读取内嵌文本
    /// </summary>
    public static string LoadEmbeddedText(string fileName)
    {
        var resourceName = $"QQMusic.Tui.www.{fileName}";
        try
        {
            using var stream = s_assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("StaticResourceHelper", $"Failed to load embedded resource: {resourceName}", ex);
        }

        return "";
    }

    /// <summary>
    /// 解析外部物理磁盘文件路径
    /// </summary>
    public static string? ResolveDiskFilePath(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        var p1 = Path.Combine(baseDir, "www", fileName);
        if (File.Exists(p1)) return p1;

        var curDir = Directory.GetCurrentDirectory();
        var p2 = Path.Combine(curDir, "www", fileName);
        if (File.Exists(p2)) return p2;

        var p3 = Path.Combine("/usr/share/qqmusic-tui/www", fileName);
        if (File.Exists(p3)) return p3;

        return null;
    }
}
