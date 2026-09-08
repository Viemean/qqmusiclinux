using System.Text.Json.Serialization;

namespace QQMusic.Tui.Models;

public sealed class WebDavSongCache
{
    public string ServerId { get; set; } = "";
    public string Href { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public int Duration { get; set; }
    public string Quality { get; set; } = "标准 128k";
    public string? LocalCachedPath { get; set; }
    public long FileSize { get; set; }
    public DateTime? LastModified { get; set; }
    public string? EmbeddedLyrics { get; set; }
}

public sealed class WebDavItem
{
    public string Name { get; set; } = "";
    public string Href { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long ContentLength { get; set; }
    public DateTime? LastModified { get; set; }
}

public sealed class WebDavServer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "我的 WebDAV";
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool TrustSelfSigned { get; set; } = true;
    public string RootPath { get; set; } = "/";
    public List<string> ImportedPaths { get; set; } = [];
    public List<WebDavSongCache> CachedSongs { get; set; } = [];
}

public sealed class WebDavConfig
{
    public List<WebDavServer> Servers { get; set; } = [];
    public string? ActiveServerId { get; set; }
}

[JsonSerializable(typeof(WebDavConfig))]
[JsonSerializable(typeof(List<WebDavServer>))]
[JsonSerializable(typeof(WebDavServer))]
[JsonSerializable(typeof(List<WebDavSongCache>))]
[JsonSerializable(typeof(WebDavSongCache))]
[JsonSerializable(typeof(List<WebDavItem>))]
[JsonSerializable(typeof(WebDavItem))]
[JsonSerializable(typeof(List<string>))]
internal partial class WebDavJsonContext : JsonSerializerContext
{
}
