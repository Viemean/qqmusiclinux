using StbImageSharp;

namespace QQMusic.Tui.Utils;

public static class PngQrReader
{
    /// <summary>
    /// 将二维码图像解码为适合终端显示的半块字符。
    /// 支持登录接口返回的 PNG 与 JPEG，并保留四周静区。
    /// </summary>
    public static List<string> DecodeToBlockText(byte[] imageBytes)
    {
        try
        {
            var image = ImageResult.FromMemory(imageBytes, ColorComponents.RedGreenBlue);
            if (image.Width <= 0 || image.Height <= 0 || image.Data.Length == 0)
            {
                return ["无法识别的二维码图像数据"];
            }

            int w = image.Width;
            int h = image.Height;

            // 1. 精确探测黑色有效内容区域边界 (去除原始图片自带的边缘留白)
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int rowOffset = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    int p = rowOffset + x * 3;
                    int lum = (image.Data[p] * 299 + image.Data[p + 1] * 587 + image.Data[p + 2] * 114) / 1000;
                    if (lum < 128)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (maxX < minX || maxY < minY)
            {
                return ["未检测到有效的二维码数据"];
            }

            // 2. 基于左上角定位图案 (7 个模块宽度的黑色顶边) 动态计算模块物理尺寸
            int cornerLen = 0;
            while (minX + cornerLen <= maxX)
            {
                int p = (minY * w + minX + cornerLen) * 3;
                int lum = (image.Data[p] * 299 + image.Data[p + 1] * 587 + image.Data[p + 2] * 114) / 1000;
                if (lum >= 128) break;
                cornerLen++;
            }

            if (cornerLen < 3) cornerLen = 7;
            double modSize = cornerLen / 7.0;
            int qrWidth = maxX - minX + 1;
            int rawModules = (int)Math.Round(qrWidth / modSize);

            // 规范到合法 QR 模块数: 21, 25, 29, 33, 37, 41, 45... (4*k + 21)
            int numModules = 21;
            int bestDiff = int.MaxValue;
            for (int m = 21; m <= 177; m += 4)
            {
                int diff = Math.Abs(m - rawModules);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    numModules = m;
                }
            }

            // 3. 四周统一增加 2 个模块的标准白色静区 (Quiet Zone)
            const int quietZone = 2;
            int outputSize = numModules + quietZone * 2;
            var grid = new bool[outputSize, outputSize];

            for (int row = 0; row < numModules; row++)
            {
                int centerY = minY + (int)((row + 0.5) * (maxY - minY + 1) / numModules);
                for (int col = 0; col < numModules; col++)
                {
                    int centerX = minX + (int)((col + 0.5) * (maxX - minX + 1) / numModules);

                    // 3x3 局部采样平均，过滤缩放伪影与孤立噪点
                    int lumSum = 0;
                    int count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int py = Math.Clamp(centerY + dy, 0, h - 1);
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int px = Math.Clamp(centerX + dx, 0, w - 1);
                            int p = (py * w + px) * 3;
                            lumSum += (image.Data[p] * 299 + image.Data[p + 1] * 587 + image.Data[p + 2] * 114) / 1000;
                            count++;
                        }
                    }

                    // true 表示黑色模块，false 表示白色背景/静区
                    grid[row + quietZone, col + quietZone] = (lumSum / count) < 128;
                }
            }

            // 4. 构建半块字符行 (双行垂直合并显示)
            var lines = new List<string>((outputSize + 1) / 2);
            for (int y = 0; y < outputSize; y += 2)
            {
                var sb = new System.Text.StringBuilder(outputSize);
                for (int x = 0; x < outputSize; x++)
                {
                    bool top = grid[y, x];
                    bool bottom = y + 1 < outputSize && grid[y + 1, x];
                    sb.Append((top, bottom) switch
                    {
                        (true, true) => '█',
                        (true, false) => '▀',
                        (false, true) => '▄',
                        _ => ' '
                    });
                }
                lines.Add(sb.ToString());
            }

            return lines;
        }
        catch (Exception ex)
        {
            return [$"二维码渲染失败: {ex.Message}"];
        }
    }
}
