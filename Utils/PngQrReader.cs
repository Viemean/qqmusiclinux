using System.IO.Compression;

namespace QQMusic.Tui.Utils;

public static class PngQrReader
{
    /// <summary>
    /// 将 111x111 1-bit PNG 图像解码为终端半块字符多行文本
    /// </summary>
    public static List<string> DecodePngToBlockText(byte[] pngBytes)
    {
        try
        {
            // 校验 PNG 魔数
            if (pngBytes.Length < 33 ||
                pngBytes[0] != 137 || pngBytes[1] != 80 || pngBytes[2] != 78 || pngBytes[3] != 71)
            {
                return ["无法识别的 PNG 图像数据"];
            }

            // 查找 IDAT Chunk
            int idatOffset = -1;
            int idatLength = 0;

            for (int i = 8; i < pngBytes.Length - 8; i++)
            {
                if (pngBytes[i] == (byte)'I' &&
                    pngBytes[i + 1] == (byte)'D' &&
                    pngBytes[i + 2] == (byte)'A' &&
                    pngBytes[i + 3] == (byte)'T')
                {
                    idatOffset = i + 4;
                    idatLength = (pngBytes[i - 4] << 24) |
                                 (pngBytes[i - 3] << 16) |
                                 (pngBytes[i - 2] << 8) |
                                  pngBytes[i - 1];
                    break;
                }
            }

            if (idatOffset < 0 || idatLength <= 0 || idatOffset + idatLength > pngBytes.Length)
            {
                return ["未找到有效的图像数据段 (IDAT)"];
            }

            // 解压 zlib 数据
            byte[] decompressed;
            using (var ms = new MemoryStream(pngBytes, idatOffset, idatLength))
            using (var zlib = new ZLibStream(ms, CompressionMode.Decompress))
            using (var outMs = new MemoryStream())
            {
                zlib.CopyTo(outMs);
                decompressed = outMs.ToArray();
            }

            const int width = 111;
            const int height = 111;
            int stride = (width + 7) / 8 + 1; // 1 字节 filter + 14 字节位数据 = 15

            if (decompressed.Length < height * stride)
            {
                return ["图像解压尺寸异常"];
            }

            // 提取像素位矩阵 (0=黑, 1=白)
            var bits = new int[height, width];
            for (int y = 0; y < height; y++)
            {
                int rowStart = y * stride;
                // 跳过 filter 字节 (通常为 0)
                int bitIdx = 0;
                for (int b = 1; b < stride && bitIdx < width; b++)
                {
                    byte val = decompressed[rowStart + b];
                    for (int shift = 7; shift >= 0 && bitIdx < width; shift--)
                    {
                        bits[y, bitIdx++] = (val >> shift) & 1;
                    }
                }
            }

            // 降采样到 37x37 二维码模块网格 (步进为 3)
            const int step = 3;
            const int modules = 37;
            var grid = new int[modules, modules];
            for (int my = 0; my < modules; my++)
            {
                for (int mx = 0; mx < modules; mx++)
                {
                    grid[my, mx] = bits[my * step, mx * step];
                }
            }

            // 利用上下半块字符 (▀, ▄, █, 空格) 压缩渲染为 (modules + 1) / 2 行
            // 注意：QR 码中 0 通常为黑块，1 为白背景。在终端中（默认黑底）：
            // 黑块需要绘制前景色，白块留空（或视终端主题而定）。
            // 为适应主流暗色终端，0 (黑模块) 绘制块字符，1 (白底) 绘制空格。
            var lines = new List<string>();
            int totalRows = (modules + 1) / 2;

            for (int r = 0; r < totalRows; r++)
            {
                int topY = r * 2;
                int botY = topY + 1;
                var sb = new System.Text.StringBuilder(modules);

                for (int x = 0; x < modules; x++)
                {
                    int top = grid[topY, x];
                    int bot = (botY < modules) ? grid[botY, x] : 1;

                    if (top == 0 && bot == 0) sb.Append('█');
                    else if (top == 0 && bot == 1) sb.Append('▀');
                    else if (top == 1 && bot == 0) sb.Append('▄');
                    else sb.Append(' ');
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
