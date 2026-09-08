using System.IO;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services;
using Xunit;

namespace QQMusic.Tui.Tests;

public class AudioExportServiceTests
{
    [Theory]
    [InlineData("Artist/Name", "Artist_Name")]
    [InlineData("Title: Test*?\"<>|", "Title_ Test______")]
    [InlineData("Clean Song Title", "Clean Song Title")]
    public void SanitizeFileName_RemovesInvalidCharacters(string input, string expected)
    {
        var result = AudioExportService.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void InferAudioExtension_FlacMagicBytes_ReturnsDotFlac()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // fLaC header
            File.WriteAllBytes(tempFile, [0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00, 0x22]);
            var ext = AudioExportService.InferAudioExtension(tempFile, AudioQualityTier.Standard);
            Assert.Equal(".flac", ext);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void InferAudioExtension_Id3MagicBytes_ReturnsDotMp3()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // ID3 header
            File.WriteAllBytes(tempFile, [0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00]);
            var ext = AudioExportService.InferAudioExtension(tempFile, AudioQualityTier.SQ);
            Assert.Equal(".mp3", ext);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void InferAudioExtension_MpegSyncword_ReturnsDotMp3()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // MPEG 1 Layer III syncword: 0xFF, 0xFB
            File.WriteAllBytes(tempFile, [0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00]);
            var ext = AudioExportService.InferAudioExtension(tempFile, AudioQualityTier.SQ);
            Assert.Equal(".mp3", ext);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }
}
