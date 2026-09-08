using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using QQMusic.Tui.Models;
using QQMusic.Tui.UI;
using QQMusic.Tui.Utils;

namespace QQMusic.Tui.Services;

public sealed partial class WebPlaybackServer
{
    private async Task HandleSseEventsAsync(TcpClient client, NetworkStream stream, CancellationToken ct)
    {
        string headers = "HTTP/1.1 200 OK\r\n" +
                         "Content-Type: text/event-stream\r\n" +
                         "Cache-Control: no-cache\r\n" +
                         "Connection: keep-alive\r\n" +
                         "Access-Control-Allow-Origin: *\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        using (var initWriteCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            initWriteCts.CancelAfter(TimeSpan.FromSeconds(3));
            await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), initWriteCts.Token).ConfigureAwait(false);
            await stream.FlushAsync(initWriteCts.Token).ConfigureAwait(false);
        }

        var sseClient = new SseClient(client, stream, ct);
        lock (_sseLock)
        {
            _sseClients.Add(sseClient);
        }

        var syncJson = BuildStateJson("sync");
        sseClient.Channel.Writer.TryWrite($"data: {syncJson}\r\n\r\n");

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(sseClient.Cts.Token);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!heartbeatCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(15000, heartbeatCts.Token).ConfigureAwait(false);
                    sseClient.Channel.Writer.TryWrite(": ping\r\n\r\n");
                }
            }
            catch {}
        }, heartbeatCts.Token);

        try
        {
            var reader = sseClient.Channel.Reader;
            while (await reader.WaitToReadAsync(sseClient.Cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var msg))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(msg);
                    using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(sseClient.Cts.Token);
                    writeCts.CancelAfter(TimeSpan.FromSeconds(3));
                    await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), writeCts.Token).ConfigureAwait(false);
                    await stream.FlushAsync(writeCts.Token).ConfigureAwait(false);
                }
            }
        }
        catch
        {
        }
        finally
        {
            try { heartbeatCts.Cancel(); } catch {}
            lock (_sseLock)
            {
                _sseClients.Remove(sseClient);
            }
            sseClient.Dispose();
        }
    }

    private void HandleApiAction(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("action", out var actionProp))
            {
                var action = actionProp.GetString();
                switch (action)
                {
                    case "next":
                        NextRequested?.Invoke();
                        break;
                    case "prev":
                        PreviousRequested?.Invoke();
                        break;
                    case "toggle":
                        TogglePlayRequested?.Invoke();
                        break;
                    case "favorite":
                    case "toggle_favorite":
                        ToggleFavoriteRequested?.Invoke();
                        break;
                    case "mode":
                    case "toggle_mode":
                        ToggleModeRequested?.Invoke();
                        break;
                    case "quality":
                    case "toggle_quality":
                        ToggleQualityRequested?.Invoke();
                        break;
                    case "ended":
                        PlaybackEnded?.Invoke();
                        break;
                    case "toggle_audio":
                        AudioOutputEnabled = !AudioOutputEnabled;
                        AudioOutputToggled?.Invoke(AudioOutputEnabled);
                        BroadcastState("audio_toggled");
                        break;
                    case "set_audio":
                        if (doc.RootElement.TryGetProperty("enabled", out var enProp))
                        {
                            AudioOutputEnabled = enProp.GetBoolean();
                            AudioOutputToggled?.Invoke(AudioOutputEnabled);
                            BroadcastState("audio_toggled");
                        }
                        break;
                    case "seek":
                        if (doc.RootElement.TryGetProperty("position", out var posProp))
                        {
                            var pos = posProp.GetDouble();
                            SeekRequested?.Invoke(pos);
                        }
                        break;
                    case "volume":
                        if (doc.RootElement.TryGetProperty("volume", out var volProp))
                        {
                            var vol = volProp.GetInt32();
                            VolumeRequested?.Invoke(vol);
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("WebPlaybackServer", $"Failed to parse action json: {ex.Message}");
        }
    }

    private void HandleApiProgress(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("position", out var posProp))
            {
                double pos = posProp.GetDouble();
                double dur = doc.RootElement.TryGetProperty("duration", out var durProp) ? durProp.GetDouble() : TotalDurationSeconds;
                CurrentPositionSeconds = pos;
                if (dur > 0) TotalDurationSeconds = dur;
                ProgressReported?.Invoke(pos, dur);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("WebPlaybackServer", $"Failed to parse progress json: {ex.Message}");
        }
    }

    public void BroadcastState(string eventType)
    {
        var json = BuildStateJson(eventType);
        BroadcastSse(json);
    }

    private string BuildStateJson(string eventType)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append($"\"type\":\"{eventType}\",");
        sb.Append($"\"isPlaying\":{(IsPlaying ? "true" : "false")},");
        sb.Append($"\"position\":{CurrentPositionSeconds:F2},");
        sb.Append($"\"duration\":{TotalDurationSeconds:F2},");
        sb.Append($"\"volume\":{Volume},");
        sb.Append($"\"audioEnabled\":{(AudioOutputEnabled ? "true" : "false")},");
        sb.Append($"\"isFavorite\":{(IsCurrentSongFavorite ? "true" : "false")},");

        string modeStr = CurrentPlaybackMode switch
        {
            PlaybackMode.SingleLoop => "single_loop",
            PlaybackMode.Shuffle => "shuffle",
            PlaybackMode.Sequential => "sequential",
            _ => "list_loop"
        };
        sb.Append($"\"mode\":\"{modeStr}\",");
        sb.Append($"\"qualityTier\":{(int)ActualQualityTier},");
        sb.Append($"\"qualityBadge\":\"{AudioQualityHelper.GetBadge(ActualQualityTier)}\",");

        string streamUrl = "";
        if (AudioOutputEnabled && !string.IsNullOrEmpty(CurrentPlayUrl))
        {
            streamUrl = (CurrentPlayUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         CurrentPlayUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                         ? CurrentPlayUrl : "/stream/audio";
        }
        sb.Append($"\"streamUrl\":\"{EscapeJson(streamUrl)}\",");

        sb.Append("\"song\":");
        if (CurrentSong == null)
        {
            sb.Append("null,");
        }
        else
        {
            sb.Append('{');
            sb.Append($"\"id\":{CurrentSong.Id},");
            sb.Append($"\"mid\":\"{EscapeJson(CurrentSong.Mid)}\",");
            sb.Append($"\"title\":\"{EscapeJson(CurrentSong.Title)}\",");
            sb.Append($"\"artist\":\"{EscapeJson(CurrentSong.Artist)}\",");
            sb.Append($"\"album\":\"{EscapeJson(CurrentSong.Album)}\",");
            sb.Append($"\"albumMid\":\"{EscapeJson(CurrentSong.AlbumMid)}\",");
            sb.Append($"\"quality\":\"{EscapeJson(CurrentSong.Quality)}\",");
            sb.Append($"\"isLocal\":{(CurrentSong.IsLocal ? "true" : "false")}");
            sb.Append("},");
        }

        sb.Append("\"lyrics\":[");
        if (CurrentLyrics != null && CurrentLyrics.Count > 0)
        {
            for (int i = 0; i < CurrentLyrics.Count; i++)
            {
                var l = CurrentLyrics[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                sb.Append($"\"timeMs\":{(long)l.Timestamp.TotalMilliseconds},");
                sb.Append($"\"text\":\"{EscapeJson(l.Text)}\",");
                sb.Append($"\"trans\":\"{EscapeJson(l.Trans)}\"");
                sb.Append('}');
            }
        }
        sb.Append(']');

        sb.Append('}');
        return sb.ToString();
    }

    private static string EscapeJson(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "")
                .Replace("\n", "\\n");
    }

    private void BroadcastSse(string json)
    {
        string message = $"data: {json}\r\n\r\n";
        List<SseClient> targets;
        lock (_sseLock)
        {
            if (_sseClients.Count == 0) return;
            targets = new List<SseClient>(_sseClients);
        }

        foreach (var client in targets)
        {
            client.Channel.Writer.TryWrite(message);
        }
    }
}
