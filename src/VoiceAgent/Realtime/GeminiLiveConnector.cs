using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace VoiceAgent.Realtime;

/// <summary>
/// Gemini Live speaks its own protocol (setup / realtimeInput / serverContent / toolCall), not
/// the OpenAI-shaped one xAI and OpenAI share. Rather than teach the call loop a second dialect,
/// this adapter translates in both directions, so CallSession runs unchanged on every provider.
///
/// Where Gemini has no equivalent, the translation is explicit rather than silent:
///   response.cancel      -> nothing; Gemini interrupts itself on caller speech ("interrupted")
///   idle follow-up       -> nothing; Gemini has no server-side silence prompt
///   speech_stopped/commit-> not emitted; Gemini does not expose them, so turn timelines are shorter
/// </summary>
public sealed class GeminiLiveConnector(RealtimeOptions options) : IRealtimeConnector
{
    public const string DefaultUrl =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    public async Task<IMessageChannel> ConnectAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("Realtime:ApiKey is not configured.");
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("x-goog-api-key", options.ApiKey);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        string url = options.Url.Contains("generativelanguage", StringComparison.Ordinal) ? options.Url : DefaultUrl;
        await socket.ConnectAsync(new Uri(url), ct);
        IMessageChannel wire = new WebSocketMessageChannel(socket);
        if (!string.IsNullOrWhiteSpace(options.TracePath)) wire = new TracingChannel(wire, options.TracePath);
        return new GeminiLiveChannel(wire, options);
    }
}

/// <summary>The translating channel. Public so tests can drive it over a fake inner socket.</summary>
public sealed class GeminiLiveChannel(IMessageChannel inner, RealtimeOptions options) : IMessageChannel
{
    private const string GreetingCue = "[The phone call has just connected. Greet the caller now.]";

    private readonly Channel<string> _translated = Channel.CreateUnbounded<string>();
    private readonly Dictionary<string, string> _toolNames = [];
    private bool _greeted;
    private bool _inModelTurn;
    private int _callerTurn;
    private string _callerText = "";
    private bool _callerTurnOpen;

    public bool IsOpen => inner.IsOpen;

    // ------------------------------------------------------------- bridge -> Gemini

    public Task SendAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        return e.GetProperty("type").GetString() switch
        {
            "session.update" when e.GetProperty("session").TryGetProperty("instructions", out _) =>
                inner.SendAsync(Setup(e.GetProperty("session")), ct),
            "session.update" => Task.CompletedTask, // idle follow-up toggle: no Gemini equivalent
            "input_audio_buffer.append" => inner.SendAsync(new JsonObject
            {
                ["realtimeInput"] = new JsonObject
                {
                    ["audio"] = new JsonObject { ["data"] = e.GetProperty("audio").GetString(), ["mimeType"] = "audio/pcm;rate=24000" },
                },
            }.ToJsonString(), ct),
            "response.create" when !_greeted => SendGreetingAsync(ct),
            "response.create" => Task.CompletedTask, // Gemini continues on its own after a tool result or a text turn
            "response.cancel" => Task.CompletedTask,
            "conversation.item.create" => SendItemAsync(e.GetProperty("item"), ct),
            _ => Task.CompletedTask,
        };
    }

    private Task SendGreetingAsync(CancellationToken ct)
    {
        _greeted = true;
        return inner.SendAsync(TextTurn(GreetingCue), ct);
    }

    private Task SendItemAsync(JsonElement item, CancellationToken ct)
    {
        switch (item.GetProperty("type").GetString())
        {
            case "function_call_output":
            {
                string id = item.GetProperty("call_id").GetString()!;
                JsonNode? output = JsonNode.Parse(item.GetProperty("output").GetString() ?? "null");
                return inner.SendAsync(new JsonObject
                {
                    ["toolResponse"] = new JsonObject
                    {
                        ["functionResponses"] = new JsonArray(new JsonObject
                        {
                            ["id"] = id,
                            ["name"] = _toolNames.GetValueOrDefault(id, ""),
                            ["response"] = output is JsonObject o ? o : new JsonObject { ["result"] = output },
                        }),
                    },
                }.ToJsonString(), ct);
            }
            case "message":
                // System turns (booking-integrity flush, wrap-up) become a completed text turn,
                // which is also what makes Gemini respond to them.
                _greeted = true;
                string text = item.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
                return inner.SendAsync(TextTurn(text), ct);
            default:
                return Task.CompletedTask;
        }
    }

    internal string Setup(JsonElement session)
    {
        var declarations = new JsonArray();
        foreach (var tool in session.GetProperty("tools").EnumerateArray())
            declarations.Add(new JsonObject
            {
                ["name"] = tool.GetProperty("name").GetString(),
                ["description"] = tool.GetProperty("description").GetString(),
                ["parameters"] = JsonNode.Parse(tool.GetProperty("parameters").GetRawText()),
            });

        var activity = new JsonObject();
        if (options.SilenceDurationMs is { } silence) activity["silenceDurationMs"] = silence;

        return new JsonObject
        {
            ["setup"] = new JsonObject
            {
                ["model"] = options.Model.StartsWith("models/", StringComparison.Ordinal) ? options.Model : "models/" + options.Model,
                ["generationConfig"] = new JsonObject
                {
                    ["responseModalities"] = new JsonArray("AUDIO"),
                    ["speechConfig"] = new JsonObject
                    {
                        ["voiceConfig"] = new JsonObject { ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = options.Voice } },
                    },
                },
                ["systemInstruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = session.GetProperty("instructions").GetString() }),
                },
                ["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations }),
                ["inputAudioTranscription"] = new JsonObject(),
                ["outputAudioTranscription"] = new JsonObject(),
                ["realtimeInputConfig"] = new JsonObject { ["automaticActivityDetection"] = activity },
            },
        }.ToJsonString();
    }

    private static string TextTurn(string text) => new JsonObject
    {
        ["clientContent"] = new JsonObject
        {
            ["turns"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = text }),
            }),
            ["turnComplete"] = true,
        },
    }.ToJsonString();

    // ------------------------------------------------------------- Gemini -> bridge

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
        var root = doc.RootElement;
        var output = new List<string>();

        if (root.TryGetProperty("setupComplete", out _))
            output.Add(Event("session.updated"));

        if (root.TryGetProperty("toolCall", out var toolCall))
        {
            foreach (var call in toolCall.GetProperty("functionCalls").EnumerateArray())
            {
                string id = call.TryGetProperty("id", out var idEl) ? idEl.GetString()! : Guid.NewGuid().ToString("N");
                string name = call.GetProperty("name").GetString()!;
                _toolNames[id] = name;
                output.Add(new JsonObject
                {
                    ["type"] = "response.function_call_arguments.done",
                    ["call_id"] = id,
                    ["name"] = name,
                    ["arguments"] = call.TryGetProperty("args", out var args) ? args.GetRawText() : "{}",
                }.ToJsonString());
            }
        }

        if (root.TryGetProperty("serverContent", out var content))
        {
            if (content.TryGetProperty("interrupted", out var interrupted) && interrupted.GetBoolean())
            {
                output.Add(Event("input_audio_buffer.speech_started"));
                if (_inModelTurn) output.Add(Event("response.cancelled"));
                _inModelTurn = false;
            }

            if (content.TryGetProperty("inputTranscription", out var heard) && heard.TryGetProperty("text", out var heardText))
            {
                if (!_callerTurnOpen)
                {
                    _callerTurnOpen = true;
                    _callerTurn++;
                    _callerText = "";
                }
                _callerText += heardText.GetString();
                output.Add(new JsonObject
                {
                    ["type"] = "conversation.item.input_audio_transcription.completed",
                    ["item_id"] = $"gemini-caller-{_callerTurn}",
                    ["transcript"] = _callerText.Trim(),
                }.ToJsonString());
            }

            if (content.TryGetProperty("modelTurn", out var turn) && turn.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (!part.TryGetProperty("inlineData", out var data)) continue;
                    BeginModelTurn(output);
                    output.Add(new JsonObject
                    {
                        ["type"] = "response.output_audio.delta",
                        ["delta"] = data.GetProperty("data").GetString(),
                    }.ToJsonString());
                }
            }

            if (content.TryGetProperty("outputTranscription", out var said) && said.TryGetProperty("text", out var saidText))
            {
                BeginModelTurn(output);
                output.Add(new JsonObject { ["type"] = "response.output_audio_transcript.delta", ["delta"] = saidText.GetString() }.ToJsonString());
            }

            if (content.TryGetProperty("turnComplete", out var complete) && complete.GetBoolean() && _inModelTurn)
            {
                _inModelTurn = false;
                output.Add(Event("response.done"));
            }
        }

        if (root.TryGetProperty("goAway", out _))
            output.Add(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["message"] = "gemini goAway" } }.ToJsonString());

        return output;
    }

    private void BeginModelTurn(List<string> output)
    {
        _callerTurnOpen = false;
        if (_inModelTurn) return;
        _inModelTurn = true;
        output.Add(Event("response.created"));
    }

    private static string Event(string type) => new JsonObject { ["type"] = type }.ToJsonString();

    public Task CloseAsync(string reason, CancellationToken ct) => inner.CloseAsync(reason, ct);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
