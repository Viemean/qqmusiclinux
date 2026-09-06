using System.Text;
using System.Text.RegularExpressions;
using QQMusic.Tui.Models;

namespace QQMusic.Tui.Utils;

public static partial class LyricParser
{
    /// <summary>
    /// Base64 安全解码为 UTF-8 文本
    /// </summary>
    public static string DecodeBase64(string? b64)
    {
        if (string.IsNullOrWhiteSpace(b64)) return "";
        try
        {
            var bytes = Convert.FromBase64String(b64.Trim());
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 解析标准及多时间戳 LRC 文本
    /// </summary>
    public static List<(TimeSpan Timestamp, string Text)> ParseLrc(string? lrcText)
    {
        if (string.IsNullOrWhiteSpace(lrcText)) return [];

        var lines = lrcText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<(TimeSpan Timestamp, string Text, int Order)>(lines.Length);
        int lineOrder = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var matches = TimestampRegex().Matches(trimmed);
            if (matches.Count == 0) continue;

            // 剥离时间戳提取正文
            var content = TimestampRegex().Replace(trimmed, "").Trim().Replace("&apos;", "’");
            if (string.IsNullOrEmpty(content) || content == "//") continue;

            foreach (Match m in matches)
            {
                var min = int.Parse(m.Groups[1].Value);
                var sec = int.Parse(m.Groups[2].Value);
                var msStr = m.Groups[3].Value.PadRight(3, '0');
                if (msStr.Length > 3) msStr = msStr[..3];
                var ms = int.Parse(msStr);

                var ts = new TimeSpan(0, 0, min, sec, ms);
                list.Add((ts, content, lineOrder++));
            }
        }

        // 稳定排序：时间戳相同时保留 LRC 文件中的原始相对先后排版顺序
        list.Sort((a, b) =>
        {
            int cmp = a.Timestamp.CompareTo(b.Timestamp);
            return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
        });

        var result = new List<(TimeSpan Timestamp, string Text)>(list.Count);
        foreach (var item in list)
        {
            result.Add((item.Timestamp, item.Text));
        }
        return result;
    }

    /// <summary>
    /// 解析本地 LRC 文本为 LyricLine 列表（智能识别同时间戳双语原歌词与中文翻译）
    /// </summary>
    public static List<LyricLine> ParseSingleLrc(string? lrcText)
    {
        var items = ParseLrc(lrcText);
        if (items.Count == 0) return [];

        var result = new List<LyricLine>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var curr = items[i];

            // 查找所有与当前行时间戳相同（<= 50ms）的候选行
            int j = i + 1;
            while (j < items.Count && Math.Abs((items[j].Timestamp - curr.Timestamp).TotalMilliseconds) <= 50)
            {
                j++;
            }

            // 若同一时间戳有多行且不含制作人员/标题元信息，进行双语对齐归并
            if (j > i + 1)
            {
                bool anyMeta = false;
                for (int k = i; k < j; k++)
                {
                    if (IsMetaInfoLine(items[k].Timestamp, items[k].Text))
                    {
                        anyMeta = true;
                        break;
                    }
                }

                if (!anyMeta)
                {
                    // 若只有 2 行，第二行直接作为译文；若有多行，优先找含汉字中文的行作为译文
                    int transIdx = i + 1;
                    if (j - i > 2)
                    {
                        for (int k = i + 1; k < j; k++)
                        {
                            if (HasChinese(items[k].Text))
                            {
                                transIdx = k;
                                break;
                            }
                        }
                    }

                    var transText = items[transIdx].Text;
                    result.Add(new LyricLine(curr.Timestamp, curr.Text, transText));
                    i = j - 1; // 跳过同时间戳的所有附加行
                    continue;
                }
            }

            result.Add(new LyricLine(curr.Timestamp, curr.Text));
        }

        return result;
    }

    private static bool HasChinese(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
        {
            if (c >= 0x4e00 && c <= 0x9fa5) return true;
        }
        return false;
    }

    /// <summary>
    /// 依据 QQ 音乐官方 Electron 架构的高精度双语时间轴智能对齐算法
    /// </summary>
    public static List<LyricLine> MergeLyrics(string rawLyric, string rawTrans)
    {
        var origItems = ParseLrc(rawLyric);
        if (origItems.Count == 0)
        {
            origItems = [(TimeSpan.Zero, "暂无歌词")];
        }

        var transItems = ParseLrc(rawTrans);
        var cleanTrans = transItems.FindAll(t =>
            !string.IsNullOrWhiteSpace(t.Text) &&
            t.Text != "//" &&
            !t.Text.Contains("享有") &&
            !t.Text.Contains("大模型") &&
            !t.Text.Contains("翻译贡献")
        );

        var usedSet = new HashSet<(TimeSpan, string)>(cleanTrans.Count);
        var result = new List<LyricLine>(origItems.Count);

        for (int i = 0; i < origItems.Count; i++)
        {
            var orig = origItems[i];
            string transText = "";

            bool isMeta = IsMetaInfoLine(orig.Timestamp, orig.Text);

            if (!isMeta && transItems.Count > 0)
            {
                // 1. 精确匹配 (< 80ms)
                var exact = transItems.Find(t => Math.Abs((t.Timestamp - orig.Timestamp).TotalMilliseconds) < 80);
                if (exact.Text != null && !usedSet.Contains(exact) &&
                    exact.Text != "//" && !exact.Text.Contains("享有") && !exact.Text.Contains("大模型") && !exact.Text.Contains("翻译贡献"))
                {
                    transText = exact.Text;
                    usedSet.Add(exact);
                }
                else if (cleanTrans.Count > 0)
                {
                    // 2. 300ms 紧凑容差候选匹配 (取时间距离最小的候选，避免跨句抢配)
                    var candidates = cleanTrans.FindAll(t => !usedSet.Contains(t) && Math.Abs((t.Timestamp - orig.Timestamp).TotalMilliseconds) <= 300);
                    if (candidates.Count > 0)
                    {
                        var best = candidates[0];
                        var minDiff = Math.Abs((best.Timestamp - orig.Timestamp).TotalMilliseconds);
                        for (int c = 1; c < candidates.Count; c++)
                        {
                            var diff = Math.Abs((candidates[c].Timestamp - orig.Timestamp).TotalMilliseconds);
                            if (diff < minDiff)
                            {
                                minDiff = diff;
                                best = candidates[c];
                            }
                        }
                        transText = best.Text;
                        usedSet.Add(best);
                    }
                    // 3. 总行数完全一致时的同索引保底回退
                    else if (cleanTrans.Count == origItems.Count && i < cleanTrans.Count && !usedSet.Contains(cleanTrans[i]))
                    {
                        transText = cleanTrans[i].Text;
                        usedSet.Add(cleanTrans[i]);
                    }
                }
            }

            result.Add(new LyricLine(orig.Timestamp, orig.Text, transText));
        }

        return result;
    }

    /// <summary>
    /// 判断是否为无需挂载歌词翻译的标题行或幕后制作人员元信息行
    /// </summary>
    private static bool IsMetaInfoLine(TimeSpan ts, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();

        // 0 秒且包含连字符的标题行（例如 "Breathe - Machico (マチコ)/LIN"）
        if (ts.TotalSeconds <= 0.05 && t.Contains(" - "))
        {
            return true;
        }

        // 制作人员常见标识
        if (t.StartsWith("词：") || t.StartsWith("词:") || t.StartsWith("作词：") || t.StartsWith("作词:") ||
            t.StartsWith("曲：") || t.StartsWith("曲:") || t.StartsWith("作曲：") || t.StartsWith("作曲:") ||
            t.StartsWith("编曲：") || t.StartsWith("编曲:") || t.StartsWith("制作：") || t.StartsWith("制作:") ||
            t.StartsWith("制作人：") || t.StartsWith("制作人:") || t.StartsWith("监制：") || t.StartsWith("监制:") ||
            t.StartsWith("混音：") || t.StartsWith("混音:") || t.StartsWith("录音：") || t.StartsWith("录音:") ||
            t.StartsWith("母带：") || t.StartsWith("母带:") || t.StartsWith("出品：") || t.StartsWith("出品:") ||
            t.StartsWith("企划：") || t.StartsWith("企划:") || t.StartsWith("OP：") || t.StartsWith("OP:") ||
            t.StartsWith("SP：") || t.StartsWith("SP:"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断当前歌词列表中是否包含有效翻译
    /// </summary>
    public static bool HasTranslation(IEnumerable<LyricLine>? lyrics)
    {
        if (lyrics == null) return false;
        foreach (var l in lyrics)
        {
            if (!string.IsNullOrWhiteSpace(l.Trans)) return true;
        }
        return false;
    }

    [GeneratedRegex(@"\[(\d{1,2}):(\d{1,2})(?:[\.:](\d{1,3}))?\]")]
    private static partial Regex TimestampRegex();
}
