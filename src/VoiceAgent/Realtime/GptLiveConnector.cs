using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace VoiceAgent.Realtime;

/// <summary>
/// OpenAI's GPT-Live (v1/live/sessions) is a different architecture from the realtime API: a
/// full-duplex voice front end that delegates reasoning and tool calls to a backend Responses
/// model. This adapter maps it onto the event stream CallSession already speaks.
///
/// Mapped from the published docs (2026-09): session.start with a delegation block;
/// session.input_audio.append; session.output_audio.delta; session.input/output_transcript.delta;
/// function calls nested in response.event -> response.output_item.done; results returned with
/// response.item.create + response.create.
///
/// Not documented, handled defensively and verified against a raw trace on a live call:
/// session readiness, reply boundaries, interruption, greeting, and system-message injection.
/// Anything not mappable is dropped explicitly (see <see cref="SendAsync"/>), never guessed at.
/// </summary>
public sealed class GptLiveConnector(RealtimeOptions options) : IRealtimeConnector
{
    public const string DefaultUrl = "wss://api.openai.com/v1/live/sessions";

    public async Task<IMessageChannel> ConnectAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("Realtime:ApiKey is not configured.");
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {options.ApiKey}");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await socket.ConnectAsync(new Uri(options.Url.Contains("/live/", StringComparison.Ordinal) ? options.Url : DefaultUrl), ct);
        IMessageChannel wire = new WebSocketMessageChannel(socket);
        if (!string.IsNullOrWhiteSpace(options.TracePath)) wire = new TracingChannel(wire, options.TracePath);
        return new GptLiveChannel(wire, options);
    }
}

public sealed class GptLiveChannel(IMessageChannel inner, RealtimeOptions options) : IMessageChannel
{
    public const int SilenceEndsReplyMs = 700;

    private readonly Channel<string> _translated = Channel.CreateUnbounded<string>();
    private bool _ready;
    private bool _inReply;
    private DateTimeOffset _lastAudible;
    private bool _callerTurnOpen;
    private int _callerTurn;
    private string _callerText = "";

    public bool IsOpen => inner.IsOpen;

    // ------------------------------------------------------------- bridge -> GPT-Live

    public Task SendAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        switch (e.GetProperty("type").GetString())
        {
            case "session.update" when e.GetProperty("session").TryGetProperty("instructions", out _):
                return inner.SendAsync(SessionStart(e.GetProperty("session")), ct);
            case "input_audio_buffer.append":
                return inner.SendAsync(new JsonObject
                {
                    ["type"] = "session.input_audio.append",
                    ["audio"] = e.GetProperty("audio").GetString(),
                }.ToJsonString(), ct);
            case "conversation.item.create" when e.GetProperty("item").GetProperty("type").GetString() == "function_call_output":
                return inner.SendAsync(new JsonObject
                {
                    ["type"] = "response.item.create",
                    ["item"] = JsonNode.Parse(e.GetProperty("item").GetRawText()),
                }.ToJsonString(), ct);
            case "response.create" when _ready:
                // Only meaningful after a tool result: it resumes the backend. The voice layer
                // runs its own turns, so a bare response.create elsewhere is harmless to resend.
                return inner.SendAsync("""{"type":"response.create"}""", ct);
            default:
                // Deliberately dropped: idle follow-up toggles, response.cancel (the voice layer
                // is full duplex and handles interruption itself), and system messages, for which
                // the Live API documents no mid-session injection. The last means the booking
                // integrity flush cannot run on this provider; the call record still reports an
                // unkept promise.
                return Task.CompletedTask;
        }
    }

    internal string SessionStart(JsonElement session) => new JsonObject
    {
        ["type"] = "session.start",
        ["session"] = new JsonObject
        {
            ["model"] = options.Model,
            ["instructions"] = session.GetProperty("instructions").GetString() +
                "\n\nThe call is already connected: greet the caller as soon as the session starts.",
            ["audio"] = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
                ["output"] = new JsonObject { ["voice"] = options.Voice },
            },
            ["delegation"] = new JsonObject
            {
                ["type"] = "responses",
                ["responses"] = new JsonObject
                {
                    ["model"] = options.DelegateModel,
                    ["tools"] = JsonNode.Parse(session.GetProperty("tools").GetRawText()),
                    ["tool_choice"] = "auto",
                },
            },
        },
    }.ToJsonString();

    // ------------------------------------------------------------- GPT-Live -> bridge

    public async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var raw in inner.ReadAsync(ct))
                    foreach (var translated in Translate(raw))
                        _translated.Writer.TryWrite(translated);
            }
            finally
            {
                _translated.Writer.TryComplete();
            }
        }, ct);

        await foreach (var message in _translated.Reader.ReadAllAsync(ct))
            yield return message;
        await pump;
    }

    internal IEnumerable<string> Translate(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var e = doc.RootElement;
        string type = e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        var output = new List<string>();

        if (!_ready && type.StartsWith("session.", StringComparison.Ordinal) &&
            (type.Contains("start", StringComparison.Ordinal) || type.Contains("creat", StringComparison.Ordinal) || type.Contains("updat", StringComparison.Ordinal)))
        {
            _ready = true;
            output.Add(Event("session.updated"));
            return output;
        }

        switch (type)
        {
            case "session.output_audio.delta":
                // The voice layer streams audio continuously, silence included, and emits no
                // reply-boundary events (confirmed in the raw trace). Replies are segmented by
                // energy: the first audible chunk opens one, SilenceEndsReplyMs of silence closes it.
                string? audio = Text(e, "delta") ?? Text(e, "audio");
                if (audio is not null && IsAudible(audio))
                {
                    BeginReply(output);
                    _lastAudible = DateTimeOffset.UtcNow;
                }
                else if (_inReply && DateTimeOffset.UtcNow - _lastAudible > TimeSpan.FromMilliseconds(SilenceEndsReplyMs))
                {
                    EndReply(output);
                }
                output.Add(new JsonObject { ["type"] = "response.output_audio.delta", ["delta"] = audio }.ToJsonString());
                break;
            case "session.output_transcript.delta":
                output.Add(new JsonObject { ["type"] = "response.output_audio_transcript.delta", ["delta"] = Text(e, "delta") }.ToJsonString());
                break;
            case "session.input_transcript.delta":
                if (!_callerTurnOpen)
                {
                    _callerTurnOpen = true;
                    _callerTurn++;
                    _callerText = "";
                }
                _callerText += Text(e, "delta");
                output.Add(new JsonObject
                {
                    ["type"] = "conversation.item.input_audio_transcription.completed",
                    ["item_id"] = $"live-caller-{_callerTurn}",
                    ["transcript"] = _callerText.Trim(),
                }.ToJsonString());
                break;
            case "response.event" when e.TryGetProperty("event", out var inner) && Text(inner, "type") == "response.output_item.done":
                var item = inner.TryGetProperty("item", out var nested) ? nested : inner;
                if (Text(item, "call_id") is { } callId && Text(item, "name") is { } name)
                    output.Add(new JsonObject
                    {
                        ["type"] = "response.function_call_arguments.done",
                        ["call_id"] = callId,
                        ["name"] = name,
                        ["arguments"] = Text(item, "arguments") ?? "{}",
                    }.ToJsonString());
                break;
            case "error":
                output.Add(raw);
                break;
            default:
                if (type.EndsWith("output_audio.done", StringComparison.Ordinal) || type.EndsWith("output_transcript.done", StringComparison.Ordinal))
                    EndReply(output);
                else if (type.Contains("speech_started", StringComparison.Ordinal) || type.Contains("interrupt", StringComparison.Ordinal))
                {
                    output.Add(Event("input_audio_buffer.speech_started"));
                    if (_inReply) output.Add(Event("response.cancelled"));
                    _inReply = false;
                }
                break;
        }
        return output;
    }

    private void BeginReply(List<string> output)
    {
        _callerTurnOpen = false;
        if (_inReply) return;
        _inReply = true;
        output.Add(Event("response.created"));
    }

    private void EndReply(List<string> output)
    {
        if (!_inReply) return;
        _inReply = false;
        output.Add(Event("response.done"));
    }

    internal static bool IsAudible(string base64Pcm16)
    {
        var bytes = Convert.FromBase64String(base64Pcm16);
        if (bytes.Length < 2) return false;
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return Audio.EnergyVad.Dbfs(samples) > -45.0;
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Event(string type) => new JsonObject { ["type"] = type }.ToJsonString();

    public Task CloseAsync(string reason, CancellationToken ct) => inner.CloseAsync(reason, ct);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
