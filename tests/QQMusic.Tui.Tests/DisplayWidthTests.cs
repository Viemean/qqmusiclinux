using Xunit;
using QQMusic.Tui.UI;

namespace QQMusic.Tui.Tests;

public class DisplayWidthTests
{
    [Fact]
    public void GetDisplayWidth_Ascii_ReturnsCharCount()
    {
        var text = "Hello World!";
        var width = SongListView.GetDisplayWidth(text);
        Assert.Equal(12, width);
    }

    [Fact]
    public void GetDisplayWidth_Chinese_ReturnsDoubleWidth()
    {
        var text = "七里香";
        var width = SongListView.GetDisplayWidth(text);
        Assert.Equal(6, width);
    }

    [Fact]
    public void GetDisplayWidth_Mixed_CalculatesAccurately()
    {
        var text = "周杰伦 - 晴天 (Live)";
        var width = SongListView.GetDisplayWidth(text);
        Assert.Equal(20, width);
    }

    [Fact]
    public void GetDisplayWidth_LatinAccented_CountsAsSingleColumn()
    {
        var text = "Café";
        var width = SongListView.GetDisplayWidth(text);
        Assert.Equal(4, width);
    }

    [Fact]
    public void TruncateAndPadWide_ShorterText_PadsToExactWidth()
    {
        var text = "晴天";
        var padded = SongListView.TruncateAndPadWide(text, 10);

        Assert.Equal(10, SongListView.GetDisplayWidth(padded));
        Assert.StartsWith("晴天", padded);
        Assert.EndsWith("      ", padded);
    }

    [Fact]
    public void TruncateAndPadWide_ExactWidth_ReturnsSame()
    {
        var text = "七里香";
        var result = SongListView.TruncateAndPadWide(text, 6);

        Assert.Equal(text, result);
        Assert.Equal(6, SongListView.GetDisplayWidth(result));
    }

    [Fact]
    public void TruncateAndPadWide_LongerText_TruncatesWithEllipsisAndPadsToExactWidth()
    {
        var text = "这是一首非常非常长的歌曲名字";
        var truncated = SongListView.TruncateAndPadWide(text, 12);

        Assert.Equal(12, SongListView.GetDisplayWidth(truncated));
        Assert.Contains("..", truncated);
    }
}
