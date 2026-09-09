using System;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;
using Xunit;

namespace QQMusic.Tui.Tests;

public class NotificationAndCoverTests
{
    [Fact]
    public void HasSessionBusAddress_Respects_NoNotifyEnv()
    {
        var oldEnv = Environment.GetEnvironmentVariable("QQMUSIC_NO_NOTIFY");
        try
        {
            Environment.SetEnvironmentVariable("QQMUSIC_NO_NOTIFY", "1");
            Assert.False(DesktopNotificationService.HasSessionBusAddress());
        }
        finally
        {
            Environment.SetEnvironmentVariable("QQMUSIC_NO_NOTIFY", oldEnv);
        }
    }

    [Fact]
    public async Task EnsureCoverAsync_NullOrEmptyFilePath_ReturnsNull()
    {
        var emptySong = new Song("test", "Test Title", "Test Artist", "Test Album", 120) { LocalFilePath = "" };
        var cover = await LocalMusicService.EnsureCoverAsync(emptySong);
        Assert.Null(cover);

        var nonExistentSong = new Song("test2", "Test Title", "Test Artist", "Test Album", 120) { LocalFilePath = "/tmp/non_existent_music_file_xyz.mp3" };
        var cover2 = await LocalMusicService.EnsureCoverAsync(nonExistentSong);
        Assert.Null(cover2);
    }
}
