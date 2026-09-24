using System.Net.WebSockets;

namespace VoiceAgent.Realtime;

public sealed class RealtimeOptions
{
    /// <summary>"xai" (or any OpenAI-compatible realtime endpoint via Url), or "scripted" for local runs.</summary>
    public string Provider { get; set; } = "xai";
    public string Url { get; set; } = "wss://api.x.ai/v1/realtime";

    /// <summary>
    /// Pinned, not "latest": the alias was probed on the production line and still resolved to
    /// the previous model, so an alias is not a trustworthy upgrade signal.
    /// </summary>
    public string Model { get; set; } = "grok-voice-think-fast-2.0";
    public string ApiKey { get; set; } = "";
    public string Voice { get; set; } = "Leo";
    public string TranscriptionModel { get; set; } = "grok-2-audio";

    /// <summary>Playback speed. 1.16 read as natural on the live line; 1.33 sounded rushed.</summary>
    public double SpeechSpeed { get; set; } = 1.16;

    /// <summary>Re-engage a caller who goes silent this long after the agent finishes.</summary>
    public int IdleFollowupMs { get; set; } = 8000;

    /// <summary>
    /// Server VAD end-of-turn window. Null leaves the provider default, which is what the
    /// production line runs. Set to trade interruption risk against reply latency.
    /// </summary>
    public int? SilenceDurationMs { get; set; }

    /// <summary>Server VAD sensitivity, 0.1 to 0.9 on xAI (default 0.85). Null leaves the default.</summary>
    public double? VadThreshold { get; set; }
}

public interface IRealtimeConnector
{
    Task<IMessageChannel> ConnectAsync(CancellationToken ct);
}

public sealed class WebSocketRealtimeConnector(RealtimeOptions options) : IRealtimeConnector
{
    public async Task<IMessageChannel> ConnectAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("Realtime:ApiKey is not configured.");

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {options.ApiKey}");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        var uri = new Uri($"{options.Url}?model={Uri.EscapeDataString(options.Model)}");
        await socket.ConnectAsync(uri, ct);
        return new WebSocketMessageChannel(socket);
    }
}
