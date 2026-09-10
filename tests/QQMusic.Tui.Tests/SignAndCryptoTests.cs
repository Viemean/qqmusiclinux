using System;
using System.Security.Cryptography;
using System.Text;
using QQMusic.Tui.Api;
using Xunit;

namespace QQMusic.Tui.Tests;

public class SignAndCryptoTests
{
    private static readonly byte[] s_testAg1ResponseKey = [122, 63, 140, 29, 94, 155, 47, 10, 108, 77, 126, 139, 31, 58, 92, 157, 14, 43, 111, 74, 129];
    private static readonly byte[] s_testAg1RequestKey = [189, 48, 95, 16, 208, 255, 116, 182, 239, 84, 218, 184, 53, 181, 225, 207];

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
    public void EncryptAg1Request_DecryptWithKey_RoundtripMatchesOriginal()
    {
        var originalPayload = "{\"test\":\"qqmusictui-test-payload-12345\"}";
        var encryptedBase64 = QqMusicApi.EncryptAg1Request(originalPayload);

        Assert.NotNull(encryptedBase64);
        var cipherBytes = Convert.FromBase64String(encryptedBase64);

        // AES-128-GCM: nonce(12) + ciphertext(len) + tag(16)
        int plainLen = Encoding.UTF8.GetByteCount(originalPayload);
        Assert.Equal(12 + plainLen + 16, cipherBytes.Length);

        var nonce = cipherBytes[..12];
        var ciphertext = cipherBytes[12..(12 + plainLen)];
        var tag = cipherBytes[(12 + plainLen)..];

        var decryptedBytes = new byte[plainLen];
        using var aesGcm = new AesGcm(s_testAg1RequestKey, 16);
        aesGcm.Decrypt(nonce, ciphertext, tag, decryptedBytes);

        var decryptedText = Encoding.UTF8.GetString(decryptedBytes);
        Assert.Equal(originalPayload, decryptedText);
    }

    [Fact]
    public void DecryptAg1Response_XorEncryptedPayload_SuccessfullyRestoresJson()
    {
        var originalJson = "{\"code\":0,\"data\":{\"msg\":\"ok\",\"uin\":\"2647681420\"}}";
        var plainBytes = Encoding.UTF8.GetBytes(originalJson);

        // 模拟服务端根据 s_ag1ResponseKey 进行的异或加密流
        var encryptedStream = new byte[plainBytes.Length];
        for (int i = 0; i < plainBytes.Length; i++)
        {
            encryptedStream[i] = (byte)(plainBytes[i] ^ s_testAg1ResponseKey[i % s_testAg1ResponseKey.Length]);
        }

        var restoredJson = QqMusicApi.DecryptAg1Response(encryptedStream);
        Assert.Equal(originalJson, restoredJson);
    }
}
