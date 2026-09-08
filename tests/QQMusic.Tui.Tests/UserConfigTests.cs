using System.IO;
using QQMusic.Tui.Models;
using Xunit;

namespace QQMusic.Tui.Tests;

public class UserConfigTests
{
    [Fact]
    public void UserConfig_DefaultState_NotificationEnabled()
    {
        var config = new UserConfig();
        Assert.True(config.EnableSongSwitchNotification);
    }
}
