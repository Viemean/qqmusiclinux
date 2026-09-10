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

            const int targetModules = 41;
            const int quietZone = 2;
            const int outputSize = targetModules + quietZone * 2;
            var grid = new bool[outputSize, outputSize];
            int side = Math.Min(image.Width, image.Height);
            int offsetX = (image.Width - side) / 2;
            int offsetY = (image.Height - side) / 2;

            for (int y = 0; y < targetModules; y++)
            {
                int sourceY = offsetY + Math.Min(side - 1, (2 * y + 1) * side / (2 * targetModules));
                for (int x = 0; x < targetModules; x++)
                {
                    int sourceX = offsetX + Math.Min(side - 1, (2 * x + 1) * side / (2 * targetModules));
                    int pixel = (sourceY * image.Width + sourceX) * 3;
                    int luminance = (image.Data[pixel] * 299 + image.Data[pixel + 1] * 587 + image.Data[pixel + 2] * 114) / 1000;
                    grid[y + quietZone, x + quietZone] = luminance < 128;
                }
            }

            var lines = new List<string>((outputSize + 1) / 2);
            for (int y = 0; y < outputSize; y += 2)
            {
                var line = new System.Text.StringBuilder(outputSize);
                for (int x = 0; x < outputSize; x++)
                {
                    bool top = grid[y, x];
                    bool bottom = y + 1 < outputSize && grid[y + 1, x];
                    line.Append((top, bottom) switch
                    {
                        (true, true) => '█',
                        (true, false) => '▀',
                        (false, true) => '▄',
                        _ => ' '
                    });
                }
                lines.Add(line.ToString());
            }
            return lines;
        }
        catch (Exception ex)
        {
            return [$"二维码渲染失败: {ex.Message}"];
        }
    }
}
