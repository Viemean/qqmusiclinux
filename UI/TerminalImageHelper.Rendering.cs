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

public static partial class TerminalImageHelper
{
    private const int MaxCoverDimension = 1000;

    private static async Task<string?> ApplyRoundedCornersAsync(string sourceFile, string targetPng)
    {
        if (!File.Exists(sourceFile) || new FileInfo(sourceFile).Length == 0) return null;

        var tmpPng = targetPng + $".tmp.{Guid.NewGuid():N}.png";
        try
        {
            byte[] fileBytes = await File.ReadAllBytesAsync(sourceFile);
            var image = StbImageSharp.ImageResult.FromMemory(fileBytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            if (image == null || image.Width <= 0 || image.Height <= 0 || image.Data == null)
            {
                return null;
            }

            int width = image.Width;
            int height = image.Height;
            byte[] pixelData = image.Data;

            // 若图像尺寸超过 1000 像素，使用双线性插值算法等比缩放至 1000 像素内，大幅节省大对象堆与 Kitty Base64 传输内存
            if (width > MaxCoverDimension || height > MaxCoverDimension)
            {
                float scale = Math.Min((float)MaxCoverDimension / width, (float)MaxCoverDimension / height);
                int scaledW = Math.Max(1, (int)MathF.Round(width * scale));
                int scaledH = Math.Max(1, (int)MathF.Round(height * scale));
                pixelData = ResizeBilinear(pixelData, width, height, scaledW, scaledH);
                width = scaledW;
                height = scaledH;
            }

            float radius = MathF.Max(6.0f, width * 0.017f);
            ApplyGeometricRoundedCorners(pixelData, width, height, radius);

            await using (var outStream = File.Create(tmpPng))
            {
                var writer = new StbImageWriteSharp.ImageWriter();
                writer.WritePng(pixelData, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, outStream);
            }

            if (IsValidPngFile(tmpPng))
            {
                try
                {
                    File.Move(tmpPng, targetPng, true);
                    return targetPng;
                }
                catch
                {
                    return tmpPng;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("TerminalImageHelper", $"ApplyRoundedCorners failed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tmpPng)) File.Delete(tmpPng); } catch {}
            GC.Collect(2, GCCollectionMode.Optimized, false, false);
        }

        return null;
    }

    /// <summary>
    /// 对 4 通道 RGBA 像素数组执行高质量双线性插值等比缩放
    /// </summary>
    private static byte[] ResizeBilinear(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW == dstW && srcH == dstH) return src;

        byte[] dst = new byte[dstW * dstH * 4];
        float xRatio = (float)(srcW - 1) / Math.Max(1, dstW - 1);
        float yRatio = (float)(srcH - 1) / Math.Max(1, dstH - 1);

        for (int y = 0; y < dstH; y++)
        {
            float srcY = y * yRatio;
            int y1 = (int)srcY;
            int y2 = Math.Min(y1 + 1, srcH - 1);
            float yLerp = srcY - y1;

            int dstRowOffset = y * dstW * 4;
            int srcRow1Offset = y1 * srcW * 4;
            int srcRow2Offset = y2 * srcW * 4;

            for (int x = 0; x < dstW; x++)
            {
                float srcX = x * xRatio;
                int x1 = (int)srcX;
                int x2 = Math.Min(x1 + 1, srcW - 1);
                float xLerp = srcX - x1;

                int p11 = srcRow1Offset + x1 * 4;
                int p12 = srcRow1Offset + x2 * 4;
                int p21 = srcRow2Offset + x1 * 4;
                int p22 = srcRow2Offset + x2 * 4;

                int dstOffset = dstRowOffset + x * 4;

                for (int c = 0; c < 4; c++)
                {
                    float top = src[p11 + c] + (src[p12 + c] - src[p11 + c]) * xLerp;
                    float bottom = src[p21 + c] + (src[p22 + c] - src[p21 + c]) * xLerp;
                    float val = top + (bottom - top) * yLerp;
                    dst[dstOffset + c] = (byte)Math.Clamp((int)MathF.Round(val), 0, 255);
                }
            }
        }

        return dst;
    }

    private static void ApplyGeometricRoundedCorners(byte[] data, int w, int h, float radius)
    {
        if (w <= 0 || h <= 0 || radius <= 0f) return;

        float maxRadius = MathF.Min(w, h) / 2.0f;
        radius = Math.Clamp(radius, 1.0f, maxRadius);
        int rLimit = (int)MathF.Ceiling(radius + 1.0f);

        // 1. Top-Left
        float cxTL = radius;
        float cyTL = radius;
        int maxRTL = Math.Min(rLimit, w);
        int maxBTL = Math.Min(rLimit, h);
        for (int y = 0; y < maxBTL; y++)
        {
            float dy = y - cyTL;
            if (dy >= 0) continue;
            for (int x = 0; x < maxRTL; x++)
            {
                float dx = x - cxTL;
                if (dx >= 0) continue;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                int idx = (y * w + x) * 4 + 3;
                if (dist > radius + 0.5f)
                {
                    data[idx] = 0;
                }
                else if (dist > radius - 0.5f)
                {
                    float factor = radius + 0.5f - dist;
                    data[idx] = (byte)(data[idx] * Math.Clamp(factor, 0f, 1f));
                }
            }
        }

        // 2. Top-Right
        float cxTR = (w - 1) - radius;
        float cyTR = radius;
        int minLTR = Math.Max(0, w - rLimit);
        for (int y = 0; y < maxBTL; y++)
        {
            float dy = y - cyTR;
            if (dy >= 0) continue;
            for (int x = minLTR; x < w; x++)
            {
                float dx = x - cxTR;
                if (dx <= 0) continue;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                int idx = (y * w + x) * 4 + 3;
                if (dist > radius + 0.5f)
                {
                    data[idx] = 0;
                }
                else if (dist > radius - 0.5f)
                {
                    float factor = radius + 0.5f - dist;
                    data[idx] = (byte)(data[idx] * Math.Clamp(factor, 0f, 1f));
                }
            }
        }

        // 3. Bottom-Left
        float cxBL = radius;
        float cyBL = (h - 1) - radius;
        int minTBL = Math.Max(0, h - rLimit);
        for (int y = minTBL; y < h; y++)
        {
            float dy = y - cyBL;
            if (dy <= 0) continue;
            for (int x = 0; x < maxRTL; x++)
            {
                float dx = x - cxBL;
                if (dx >= 0) continue;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                int idx = (y * w + x) * 4 + 3;
                if (dist > radius + 0.5f)
                {
                    data[idx] = 0;
                }
                else if (dist > radius - 0.5f)
                {
                    float factor = radius + 0.5f - dist;
                    data[idx] = (byte)(data[idx] * Math.Clamp(factor, 0f, 1f));
                }
            }
        }

        // 4. Bottom-Right
        float cxBR = (w - 1) - radius;
        float cyBR = (h - 1) - radius;
        for (int y = minTBL; y < h; y++)
        {
            float dy = y - cyBR;
            if (dy <= 0) continue;
            for (int x = minLTR; x < w; x++)
            {
                float dx = x - cxBR;
                if (dx <= 0) continue;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                int idx = (y * w + x) * 4 + 3;
                if (dist > radius + 0.5f)
                {
                    data[idx] = 0;
                }
                else if (dist > radius - 0.5f)
                {
                    float factor = radius + 0.5f - dist;
                    data[idx] = (byte)(data[idx] * Math.Clamp(factor, 0f, 1f));
                }
            }
        }
    }

    private readonly record struct ImageCacheKey(string FilePath, int Cols, int Rows, long LastWriteTicks);

    private static readonly Lock s_cacheLock = new();
    private const int MaxMemoryCacheEntries = 12;
    private static readonly Dictionary<ImageCacheKey, LinkedListNode<(ImageCacheKey Key, byte[] Payload)>> s_memoryCache = new(MaxMemoryCacheEntries);
    private static readonly LinkedList<(ImageCacheKey Key, byte[] Payload)> s_lruList = new();

    /// <summary>
    /// 在终端指定行、列输出 Kitty 原生图像（f=100 PNG 格式分块传输）
    /// </summary>
    /// <param name="filePath">本地图像文件绝对路径</param>
    /// <param name="col">屏幕 1-based 列坐标</param>
    /// <param name="row">屏幕 1-based 行坐标</param>
    /// <param name="cols">占据列宽</param>
    /// <param name="rows">占据行高</param>
    public static void RenderKittyImage(string filePath, int col, int row, int cols, int rows)
    {
        if (!IsImageSupported || string.IsNullOrEmpty(filePath) || cols <= 0 || rows <= 0) return;

        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length == 0) return;

            var key = new ImageCacheKey(filePath, cols, rows, fi.LastWriteTimeUtc.Ticks);
            byte[]? cachedPayload = null;

            lock (s_cacheLock)
            {
                if (s_memoryCache.TryGetValue(key, out var node))
                {
                    s_lruList.Remove(node);
                    s_lruList.AddFirst(node);
                    cachedPayload = node.Value.Payload;
                }
            }

            if (cachedPayload == null)
            {
                byte[] fileBytes = File.ReadAllBytes(filePath);
                string base64 = Convert.ToBase64String(fileBytes);

                using var ms = new MemoryStream();
                using var writer = new StreamWriter(ms, Encoding.ASCII);

                // Kitty escape sequence: f=100 (PNG), a=T (transmit and display), c=cols, r=rows
                int chunkSize = 4096;
                for (int offset = 0; offset < base64.Length; offset += chunkSize)
                {
                    int len = Math.Min(chunkSize, base64.Length - offset);
                    string chunk = base64.Substring(offset, len);
                    bool isLast = (offset + len >= base64.Length);

                    if (offset == 0)
                    {
                        int m = isLast ? 0 : 1;
                        writer.Write($"\x1b_Ga=T,q=2,f=100,c={cols},r={rows},m={m};{chunk}\x1b\\");
                    }
                    else
                    {
                        int m = isLast ? 0 : 1;
                        writer.Write($"\x1b_Gq=2,m={m};{chunk}\x1b\\");
                    }
                }
                writer.Flush();
                cachedPayload = ms.ToArray();

                lock (s_cacheLock)
                {
                    if (!s_memoryCache.ContainsKey(key))
                    {
                        if (s_memoryCache.Count >= MaxMemoryCacheEntries)
                        {
                            var last = s_lruList.Last;
                            if (last != null)
                            {
                                s_lruList.RemoveLast();
                                s_memoryCache.Remove(last.Value.Key);
                            }
                        }

                        var newNode = new LinkedListNode<(ImageCacheKey Key, byte[] Payload)>((key, cachedPayload));
                        s_lruList.AddFirst(newNode);
                        s_memoryCache[key] = newNode;
                    }
                }
            }

            ClearImages();

            // 移动光标至指定行列（1-indexed）
            var moveCursorBytes = Encoding.ASCII.GetBytes($"\x1b[{row};{col}H");
            WriteRawBytesToTerminal(moveCursorBytes, cachedPayload);
            AppLogger.Info("TerminalImageHelper", $"RenderKittyImage sent {cachedPayload.Length} bytes at ({col}, {row}) size {cols}x{rows} (cached)");
        }
        catch (Exception ex)
        {
            AppLogger.Error("TerminalImageHelper", $"RenderKittyImage failed: {ex.Message}");
        }
    }

    private static void WriteRawBytesToTerminal(byte[] header, byte[] payload)
    {
        try
        {
            using var tty = File.OpenWrite("/dev/tty");
            tty.Write(header, 0, header.Length);
            tty.Write(payload, 0, payload.Length);
            tty.Flush();
            return;
        }
        catch
        {
            // fallback
        }

        try
        {
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(header, 0, header.Length);
            stdout.Write(payload, 0, payload.Length);
            stdout.Flush();
        }
        catch
        {
        }
    }

    private static void WriteRawBytesToTerminal(byte[] bytes)
    {
        try
        {
            using var tty = File.OpenWrite("/dev/tty");
            tty.Write(bytes, 0, bytes.Length);
            tty.Flush();
            return;
        }
        catch
        {
            // fallback
        }

        try
        {
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
        catch
        {
        }
    }

    /// <summary>
    /// 清除终端中显示的 Kitty 图像
    /// </summary>
    public static void ClearImages()
    {
        if (!IsImageSupported) return;
        try
        {
            byte[] cmd = Encoding.ASCII.GetBytes("\x1b_Ga=d,d=a,q=2\x1b\\");
            WriteRawBytesToTerminal(cmd);
        }
        catch
        {
        }
    }
}
