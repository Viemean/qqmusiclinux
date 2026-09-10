using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using QQMusic.Tui.Api;
using QQMusic.Tui.Models;
using QQMusic.Tui.Services.AcrCloud;
using QQMusic.Tui.Services.Shazam;
using Xunit;
using Xunit.Abstractions;

namespace QQMusic.Tui.Tests;

/// <summary>
/// 官方 OpenAPI 联调与维护排查测试用例集
/// 专用于维护期排查协议变动、网关状态、登录鉴权、推荐算法及听歌识曲服务可用性
/// 默认离线模式自动跳过，排查时可通过环境变量 QQMUSIC_ONLINE_TEST=1 激活：
/// env QQMUSIC_ONLINE_TEST=1 dotnet test tests/QQMusic.Tui.Tests/QQMusic.Tui.Tests.csproj --filter "Category=OfficialApi"
/// </summary>
[Trait("Category", "OfficialApi")]
public class OfficialApiTests
{
    private readonly ITestOutputHelper _output;

    private const string JayChouSingerMid = "0025NhlN2yWrP4"; // 周杰伦
    private const string QingtianSongMid = "0039MnYb0qxYhV";   // 晴天
    private const string YehuiMeiAlbumMid = "000MkMni19ClKG";  // 叶惠美

    private static bool s_sessionInitialized = false;
    private static readonly object s_lock = new();

    public OfficialApiTests(ITestOutputHelper output)
    {
        _output = output;
        EnsureSessionAndConfigInitialized();
    }

    private static bool IsOnlineTestEnabled()
    {
        var env = Environment.GetEnvironmentVariable("QQMUSIC_ONLINE_TEST");
        return env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 初始化测试运行鉴权凭证：
    /// 1. 标准规范：优先从环境变量 QQMUSIC_TEST_COOKIE / QQMUSIC_TEST_UIN 读取专用测试账号凭证
    /// 2. 显式本地授权：只有在显式设置环境变量 QQMUSIC_USE_LOCAL_SESSION=1 时，才允许加载本地 TUI 真实会话 (~/.config/qqmusic-tui/session.json)
    /// 3. 默认隔离模式：未配置上述环境变量时，默认以无状态游客模式执行，杜绝隐式读取宿主机隐私或污染个人数据
    /// </summary>
    private void EnsureSessionAndConfigInitialized()
    {
        lock (s_lock)
        {
            if (s_sessionInitialized) return;
            s_sessionInitialized = true;

            var envCookie = Environment.GetEnvironmentVariable("QQMUSIC_TEST_COOKIE")
                            ?? Environment.GetEnvironmentVariable("QQMUSIC_COOKIE");
            var envUin = Environment.GetEnvironmentVariable("QQMUSIC_TEST_UIN")
                         ?? Environment.GetEnvironmentVariable("QQMUSIC_UIN");
            var allowLocalSession = string.Equals(Environment.GetEnvironmentVariable("QQMUSIC_USE_LOCAL_SESSION"), "1", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(Environment.GetEnvironmentVariable("QQMUSIC_USE_LOCAL_SESSION"), "true", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(envCookie))
            {
                var cookieDict = ParseCookieStringToDictionary(envCookie);
                UserSession.Current.Cookies = cookieDict;
                if (!string.IsNullOrWhiteSpace(envUin))
                {
                    UserSession.Current.Uin = envUin;
                }
                else if (cookieDict.TryGetValue("uin", out var u) || cookieDict.TryGetValue("qqmusic_uin", out u))
                {
                    UserSession.Current.Uin = u.TrimStart('o');
                }
                _output.WriteLine($"[鉴权初始化] 已从环境变量注入专用测试 Cookie，Uin: {MaskIdentifier(UserSession.Current.Uin)}");
            }
            else if (allowLocalSession)
            {
                // 显式授权时才读取本地真实登录信息
                UserSession.Load();
                if (UserSession.Current.IsLoggedIn)
                {
                    _output.WriteLine($"[鉴权初始化] 已显式授权加载本地会话，Uin: {MaskIdentifier(UserSession.Current.Uin)}, 昵称: {UserSession.Current.Nick}, VIP: {UserSession.Current.IsVip}");
                }
                else
                {
                    _output.WriteLine("[鉴权初始化] 本地未检测到有效登录凭据，以游客模式运行");
                }
            }
            else
            {
                _output.WriteLine("[鉴权初始化] 默认测试隔离模式运行（未设置 QQMUSIC_TEST_COOKIE 且未开启 QQMUSIC_USE_LOCAL_SESSION=1），以安全游客状态执行接口测试");
            }

            // ACRCloud 密钥注入管理：优先环境变量，显式授权时才读取本地
            var acrKey = Environment.GetEnvironmentVariable("ACRCLOUD_ACCESS_KEY");
            var acrSecret = Environment.GetEnvironmentVariable("ACRCLOUD_ACCESS_SECRET");
            var acrHost = Environment.GetEnvironmentVariable("ACRCLOUD_HOST");
            if (!string.IsNullOrWhiteSpace(acrKey) && !string.IsNullOrWhiteSpace(acrSecret))
            {
                AcrCloudConfig.Current.AccessKey = acrKey;
                AcrCloudConfig.Current.AccessSecret = acrSecret;
                if (!string.IsNullOrWhiteSpace(acrHost))
                {
                    AcrCloudConfig.Current.Host = acrHost;
                }
            }
            else if (!allowLocalSession)
            {
                // 未显式授权使用本地配置时，清空敏感密钥保证测试隔离
                AcrCloudConfig.Current.AccessKey = "";
                AcrCloudConfig.Current.AccessSecret = "";
            }
        }
    }

    private static string MaskIdentifier(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length <= 4) return "***";
        return string.Concat(raw.AsSpan(0, 3), "****", raw.AsSpan(raw.Length - 2));
    }

    private static Dictionary<string, string> ParseCookieStringToDictionary(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var idx = part.IndexOf('=');
            if (idx > 0)
            {
                var key = part[..idx].Trim();
                var val = part[(idx + 1)..].Trim();
                dict[key] = val;
            }
        }
        return dict;
    }

    #region 1. 基础模块 - 搜索

    [Fact]
    public async Task SearchAsync_OfficialApi_ReturnsValidSongList()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine("[模块: 基础搜索] 正在测试 QqMusicApi.SearchAsync (关键词: '周杰伦 晴天')...");
        var songs = await QqMusicApi.SearchAsync("周杰伦 晴天", page: 1, pageSize: 10);

        Assert.NotNull(songs);
        Assert.NotEmpty(songs);

        var firstSong = songs[0];
        _output.WriteLine($"[模块: 基础搜索] 成功拉取 {songs.Count} 首结果，首曲: '{firstSong.Title}' - '{firstSong.Artist}' (Mid: {firstSong.Mid}, 时长: {firstSong.FormattedDuration})");

        Assert.False(string.IsNullOrWhiteSpace(firstSong.Mid));
        Assert.Contains("晴天", firstSong.Title);
        Assert.Contains("周杰伦", firstSong.Artist);
        Assert.True(firstSong.Duration > 0);
    }

    #endregion

    #region 2. 播放模块 - 音质与播放流

    [Fact]
    public async Task ProbeSongQualitiesAsync_OfficialApi_DetectsAvailableTiers()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 播放探测] 正在测试 QqMusicApi.ProbeSongQualitiesAsync (SongMid: {QingtianSongMid})...");
        var options = await QqMusicApi.ProbeSongQualitiesAsync(QingtianSongMid);

        Assert.NotNull(options);
        Assert.NotEmpty(options);
        foreach (var opt in options)
        {
            _output.WriteLine($"[模块: 播放探测] 检测到音质: {opt.Name} ({opt.Badge}), 档位: {opt.Tier}, 规格: {opt.Spec}, 可用: {opt.Available}");
        }

        Assert.Contains(options, opt => opt.Tier == AudioQualityTier.Standard || opt.Tier == AudioQualityTier.HQ || opt.Tier == AudioQualityTier.SQ);
    }

    [Fact]
    public async Task GetPlayUrlForTierAsync_OfficialApi_ResolvesPlayableStream()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 播放地址] 正在测试 QqMusicApi.GetPlayUrlForTierAsync (SongMid: {QingtianSongMid})...");
        var (url, quality, tier) = await QqMusicApi.GetPlayUrlForTierAsync(QingtianSongMid);

        _output.WriteLine($"[模块: 播放地址] 接口解析结果 -> 档位: {tier}, 描述: {quality}, 播放流 URL: {(string.IsNullOrEmpty(url) ? "(受版权限制或需VIP)" : url)}");
        Assert.NotNull(quality);
        if (!string.IsNullOrEmpty(url))
        {
            Assert.StartsWith("http", url, StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion

    #region 3. 歌词模块 - 同步歌词与时间轴

    [Fact]
    public async Task GetLyricsAsync_OfficialApi_ParsesTimestampsAndText()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 歌词同步] 正在测试 QqMusicApi.GetLyricsAsync (SongMid: {QingtianSongMid})...");
        var lyrics = await QqMusicApi.GetLyricsAsync(QingtianSongMid);

        Assert.NotNull(lyrics);
        Assert.NotEmpty(lyrics);

        var firstLine = lyrics.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Text));
        _output.WriteLine($"[模块: 歌词同步] 成功解析歌词行数: {lyrics.Count}, 首句时间轴: {firstLine?.Timestamp} => '{firstLine?.Text}'");

        Assert.Contains(lyrics, line => line.Text.Contains("故事的小黄花") || line.Text.Contains("晴天"));
    }

    #endregion

    #region 4. 用户交互模块 - 歌曲收藏与取消收藏闭环

    [Fact]
    public async Task FavoriteSongLifecycle_OfficialApi_AddsAndRemovesFavorite()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine("[模块: 歌曲收藏] 正在检查当前用户登录状态...");
        if (!UserSession.Current.IsLoggedIn)
        {
            _output.WriteLine("[模块: 歌曲收藏] 当前未处于登录态，跳过收藏/取消收藏网络测试");
            return;
        }

        // 先通过搜索获取标准歌曲对象
        var searchResults = await QqMusicApi.SearchAsync("周杰伦 晴天", 1, 1);
        Assert.NotEmpty(searchResults);
        var testSong = searchResults[0];

        _output.WriteLine($"[模块: 歌曲收藏] 1. 执行添加收藏 -> '{testSong.Title}' (Id: {testSong.Id}, Mid: {testSong.Mid})...");
        bool added = false;
        try
        {
            var addResult = await QqMusicApi.AddSongToFavoriteAsync(testSong);
            _output.WriteLine($"[模块: 歌曲收藏] 添加收藏结果: {addResult}");
            Assert.True(addResult, "添加歌曲到收藏夹应返回成功");
            added = true;
        }
        finally
        {
            if (added)
            {
                // 确保即使断言失败也必定执行清理还原，绝不污染用户收藏夹
                await Task.Delay(500);
                _output.WriteLine($"[模块: 歌曲收藏] 2. 执行取消收藏 (异常安全清理) -> '{testSong.Title}'...");
                var removeResult = await QqMusicApi.RemoveSongFromFavoriteAsync(testSong);
                _output.WriteLine($"[模块: 歌曲收藏] 取消收藏结果: {removeResult}");
                Assert.True(removeResult, "从收藏夹移除歌曲应返回成功");
            }
        }
    }

    #endregion

    #region 5. 个性化推荐模块 - 每日推荐与猜你喜欢

    [Fact]
    public async Task GetDailyRecommendSongsAsync_OfficialApi_ReturnsDailyPlaylist()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine("[模块: 个性化推荐] 正在测试 QqMusicApi.GetDailyRecommendSongsAsync (每日30首)...");
        if (!UserSession.Current.IsLoggedIn)
        {
            _output.WriteLine("[模块: 个性化推荐] 当前未登录，验证未登录状态安全防护");
            var guestList = await QqMusicApi.GetDailyRecommendSongsAsync();
            Assert.Empty(guestList);
            return;
        }

        var dailySongs = await QqMusicApi.GetDailyRecommendSongsAsync();
        _output.WriteLine($"[模块: 个性化推荐] 成功拉取今日每日推荐曲目数: {dailySongs.Count}");
        Assert.NotNull(dailySongs);
        Assert.NotEmpty(dailySongs);
        var sample = dailySongs[0];
        _output.WriteLine($"[模块: 个性化推荐] 每日推荐示例曲目: '{sample.Title}' - '{sample.Artist}'");
        Assert.False(string.IsNullOrWhiteSpace(sample.Mid));
    }

    [Fact]
    public async Task GetGuessRecommendSongsAsync_OfficialApi_ReturnsRadioTracks()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine("[模块: 个性化推荐] 正在测试 QqMusicApi.GetGuessRecommendSongsAsync (猜你喜欢电台)...");
        if (!UserSession.Current.IsLoggedIn)
        {
            _output.WriteLine("[模块: 个性化推荐] 当前未登录，验证未登录状态安全防护");
            var guestList = await QqMusicApi.GetGuessRecommendSongsAsync(count: 10);
            Assert.Empty(guestList);
            return;
        }

        var guessSongs = await QqMusicApi.GetGuessRecommendSongsAsync(count: 10);
        _output.WriteLine($"[模块: 个性化推荐] 成功拉取猜你喜欢曲目数: {guessSongs.Count}");
        Assert.NotNull(guessSongs);
        Assert.NotEmpty(guessSongs);
        var sample = guessSongs[0];
        _output.WriteLine($"[模块: 个性化推荐] 猜你喜欢示例曲目: '{sample.Title}' - '{sample.Artist}'");
        Assert.False(string.IsNullOrWhiteSpace(sample.Mid));
    }

    #endregion

    #region 6. 听歌识曲模块 - 原生 Shazam 与 ACRCloud 双引擎

    [Fact]
    public async Task NativeShazamService_OfficialApi_PerformsRecognitionNetworkExchange()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine("[模块: 听歌识曲-Shazam] 正在测试苹果 Shazam 官方指纹编码与网络识别通道...");

        // 构造标准的 16kHz 16-bit 单声道 PCM 测试样本 (3 秒正弦波)
        int sampleRate = 16000;
        int durationSeconds = 3;
        short[] testPcm = new short[sampleRate * durationSeconds];
        double freq = 440.0;
        for (int i = 0; i < testPcm.Length; i++)
        {
            testPcm[i] = (short)(Math.Sin(2 * Math.PI * freq * i / sampleRate) * 16000);
        }

        _output.WriteLine($"[模块: 听歌识曲-Shazam] 发送 {testPcm.Length} 个 PCM 采样点进行官方网关通信测试...");
        var (success, title, artist, album, error) = await NativeShazamService.RecognizePcmSamplesAsync(testPcm);

        _output.WriteLine($"[模块: 听歌识曲-Shazam] 网关响应结果: Success={success}, Title='{title}', Artist='{artist}', 消息='{error}'");
        // 对于纯正弦波，官方服务将正常响应但无法匹配曲目，返回 Success=false 且 Error="未识别到歌曲"，或识别出同频率音乐
        // 关键断言是网络与接口契约未抛出异常，error 包含有效的业务响应而非崩溃
        Assert.False(string.IsNullOrEmpty(error) && !success);
    }

    [Fact]
    public async Task AcrCloudService_OfficialApi_PerformsRecognitionNetworkExchange()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine("[模块: 听歌识曲-ACRCloud] 正在检查 ACRCloud 密钥与网关配置...");
        if (!AcrCloudConfig.Current.IsConfigured)
        {
            _output.WriteLine("[模块: 听歌识曲-ACRCloud] 未配置 ACRCloud 密钥，安全跳过接口联调");
            return;
        }

        _output.WriteLine($"[模块: 听歌识曲-ACRCloud] 当前网关 Host: {AcrCloudConfig.Current.Host}, Key: {AcrCloudConfig.Current.AccessKey[..6]}***");

        // 构造 3 秒 PCM 样本
        int sampleRate = 16000;
        int durationSeconds = 3;
        short[] testPcm = new short[sampleRate * durationSeconds];
        for (int i = 0; i < testPcm.Length; i++)
        {
            testPcm[i] = (short)(Math.Sin(2 * Math.PI * 523.25 * i / sampleRate) * 15000);
        }

        _output.WriteLine($"[模块: 听歌识曲-ACRCloud] 发送 HMAC 签名请求至官方网关...");
        var (success, title, artist, album, error) = await AcrCloudService.RecognizePcmSamplesAsync(testPcm);

        _output.WriteLine($"[模块: 听歌识曲-ACRCloud] 识别响应结果: Success={success}, Title='{title}', 消息='{error}'");
        Assert.NotNull(error);
    }

    #endregion

    #region 7. 元数据与归档模块 - 歌手与专辑详情

    [Fact]
    public async Task GetSingerDetailAsync_OfficialApi_FetchesArtistInfo()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 歌手元数据] 正在测试 QqMusicApi.GetSingerDetailAsync (SingerMid: {JayChouSingerMid})...");
        var detail = await QqMusicApi.GetSingerDetailAsync(JayChouSingerMid, singerName: "周杰伦");

        Assert.NotNull(detail);
        _output.WriteLine($"[模块: 歌手元数据] 歌手名称: {detail.Name}, 曲目数: {detail.Songs.Count}, 简介长度: {detail.Brief.Length} 字符");

        Assert.Equal("周杰伦", detail.Name);
        Assert.NotEmpty(detail.Songs);
        Assert.Contains(detail.Songs, s => s.Artist.Contains("周杰伦"));
    }

    [Fact]
    public async Task GetSingerSongListAsync_OfficialApi_ReturnsTopTracks()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 歌手曲目] 正在测试 QqMusicApi.GetSingerSongListAsync (SingerMid: {JayChouSingerMid})...");
        var (songs, total) = await QqMusicApi.GetSingerSongListAsync(JayChouSingerMid, begin: 0, pageSize: 20);

        Assert.NotNull(songs);
        Assert.NotEmpty(songs);
        Assert.True(total > 0);
        _output.WriteLine($"[模块: 歌手曲目] 周杰伦已发行歌曲总数: {total}, 本页拉取: {songs.Count}");

        Assert.All(songs, song => Assert.False(string.IsNullOrEmpty(song.Mid)));
    }

    [Fact]
    public async Task GetAlbumDetailInfoAsync_OfficialApi_FetchesAlbumMetadata()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 专辑元数据] 正在测试 QqMusicApi.GetAlbumDetailInfoAsync (AlbumMid: {YehuiMeiAlbumMid})...");
        var album = await QqMusicApi.GetAlbumDetailInfoAsync(YehuiMeiAlbumMid);

        Assert.NotNull(album);
        _output.WriteLine($"[模块: 专辑元数据] 专辑名: '{album.Name}', 发行日期: '{album.PublishDate}', 唱片公司: '{album.Company}'");

        Assert.Equal(YehuiMeiAlbumMid, album.Mid);
        Assert.Contains("叶惠美", album.Name);
        Assert.Contains("周杰伦", album.Artist);
        Assert.NotEmpty(album.Songs);
    }

    [Fact]
    public async Task GetAlbumSongsAsync_OfficialApi_FetchesAlbumTracks()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 专辑曲目] 正在测试 QqMusicApi.GetAlbumSongsAsync (AlbumMid: {YehuiMeiAlbumMid})...");
        var songs = await QqMusicApi.GetAlbumSongsAsync(YehuiMeiAlbumMid);

        Assert.NotNull(songs);
        Assert.NotEmpty(songs);
        _output.WriteLine($"[模块: 专辑曲目] 成功拉取专辑《叶惠美》曲目数: {songs.Count}, 包含: {string.Join(", ", songs.Take(3).Select(s => s.Title))} 等");

        Assert.Contains(songs, s => s.Title.Contains("以父之名") || s.Title.Contains("晴天") || s.Title.Contains("东风破"));
    }

    #endregion

    #region 8. 账号网关模块 - 二维码登录

    [Theory]
    [InlineData(LoginService.QrLoginType.Qq, "image/png")]
    [InlineData(LoginService.QrLoginType.WeChat, "image/jpeg")]
    [InlineData(LoginService.QrLoginType.QqMusic, "image/png")]
    public async Task FetchQrCodeAsync_OfficialGateways_ReturnValidQrData(LoginService.QrLoginType type, string mimeType)
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 登录网关] 正在获取 {LoginService.GetLoginTypeName(type)} 登录二维码...");
        var qr = await LoginService.FetchQrCodeAsync(type);

        Assert.NotNull(qr);
        Assert.NotEmpty(qr.ImageBytes);
        Assert.Equal(mimeType, qr.MimeType);
        Assert.Equal(type, qr.Type);
        Assert.False(string.IsNullOrEmpty(qr.Identifier));
        Assert.DoesNotContain(qr.AsciiLines, line => line.StartsWith("二维码渲染失败", StringComparison.Ordinal));

        _output.WriteLine($"[模块: 登录网关] {LoginService.GetLoginTypeName(type)} 二维码大小: {qr.ImageBytes.Length} bytes");
    }

    #endregion
}
