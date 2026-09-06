using System.Text;
using Xunit;
using QQMusic.Tui.Api;

namespace QQMusic.Tui.Tests;

public class SignAndCryptoTests
{
    [Fact]
    public void ComputeZzcSign_ValidInput_StartsWithZzcAndIsLowercase()
    {
        var input = "{\"comm\":{\"uin\":\"10000\"}}";
        var sign = QqMusicApi.ComputeZzcSign(input);

        Assert.NotNull(sign);
        Assert.StartsWith("zzc", sign);
        Assert.Equal(sign.ToLowerInvariant(), sign);
        Assert.True(sign.Length > 10);
    }

    [Fact]
    public void ComputeZzcSign_SameInput_ProducesIdenticalSign()
    {
        var payload = "{\"req\":{\"module\":\"music.pf_song_detail_svr\",\"method\":\"get_song_detail_yqq\"}}";
        var sign1 = QqMusicApi.ComputeZzcSign(payload);
        var sign2 = QqMusicApi.ComputeZzcSign(payload);

        Assert.Equal(sign1, sign2);
    }

    [Fact]
    public void Ag1EncryptionAndDecryption_Roundtrip_MatchesOriginalPayload()
    {
        var originalPayload = "{\"test\":\"qqmusictui-test-payload-12345\"}";
        var encryptedBase64 = QqMusicApi.EncryptAg1Request(originalPayload);

        Assert.NotNull(encryptedBase64);
        var cipherBytes = Convert.FromBase64String(encryptedBase64);

        // AES-128-GCM nonce(12) + ciphertext(len) + tag(16)
        Assert.Equal(12 + Encoding.UTF8.GetByteCount(originalPayload) + 16, cipherBytes.Length);
    }
}
