using Xunit;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Tests;

public class LyricParserTests
{
    [Fact]
    public void ParseLrc_StandardTimestamps_ParsesCorrectly()
    {
        var lrc = "[00:12.34]第一行歌词\n[01:05.678]第二行歌词";
        var result = LyricParser.ParseLrc(lrc);

        Assert.Equal(2, result.Count);
        Assert.Equal(new TimeSpan(0, 0, 0, 12, 340), result[0].Timestamp);
        Assert.Equal("第一行歌词", result[0].Text);

        Assert.Equal(new TimeSpan(0, 0, 1, 5, 678), result[1].Timestamp);
        Assert.Equal("第二行歌词", result[1].Text);
    }

    [Fact]
    public void ParseLrc_WithoutMilliseconds_ParsesCorrectly()
    {
        var lrc = "[01:30]没有毫秒的歌词行";
        var result = LyricParser.ParseLrc(lrc);

        Assert.Single(result);
        Assert.Equal(new TimeSpan(0, 0, 1, 30, 0), result[0].Timestamp);
        Assert.Equal("没有毫秒的歌词行", result[0].Text);
    }

    [Fact]
    public void ParseLrc_MultipleTimestamps_SplitsCorrectly()
    {
        var lrc = "[00:10.00][00:20.00]副歌重复段落";
        var result = LyricParser.ParseLrc(lrc);

        Assert.Equal(2, result.Count);
        Assert.Equal(new TimeSpan(0, 0, 0, 10, 0), result[0].Timestamp);
        Assert.Equal("副歌重复段落", result[0].Text);

        Assert.Equal(new TimeSpan(0, 0, 0, 20, 0), result[1].Timestamp);
        Assert.Equal("副歌重复段落", result[1].Text);
    }

    [Fact]
    public void ParseLrc_HtmlEntities_ReplacesApos()
    {
        var lrc = "[00:05.00]Don&apos;t go away";
        var result = LyricParser.ParseLrc(lrc);

        Assert.Single(result);
        Assert.Equal("Don’t go away", result[0].Text);
    }

    [Fact]
    public void ParseLrc_EmptyOrComment_Skips()
    {
        var lrc = "\n[ti:Test Title]\n//\n   \n[00:01.00]Valid";
        var result = LyricParser.ParseLrc(lrc);

        Assert.Single(result);
        Assert.Equal("Valid", result[0].Text);
    }

    [Fact]
    public void DecodeBase64_ValidBase64_DecodesSuccessfully()
    {
        var plain = "QQ音乐终端播放器";
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plain));
        var decoded = LyricParser.DecodeBase64(b64);

        Assert.Equal(plain, decoded);
    }

    [Fact]
    public void DecodeBase64_InvalidBase64_ReturnsEmptyString()
    {
        var decoded = LyricParser.DecodeBase64("This is not base64!!!");
        Assert.Equal("", decoded);
    }
}
