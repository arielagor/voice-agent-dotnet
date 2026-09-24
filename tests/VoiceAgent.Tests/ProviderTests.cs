using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceAgent.Realtime;

namespace VoiceAgent.Tests;

public class ProviderDialectTests
{
    private static readonly JsonArray Tools = new(new JsonObject
    {
        ["type"] = "function",
        ["name"] = "search_knowledge",
        ["description"] = "d",
        ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray() },
    });

    [Fact]
    public void OpenAi_session_uses_the_ga_shape_with_no_flat_legacy_fields()
    {
        var options = new RealtimeOptions { Provider = "openai", Voice = "marin", TranscriptionModel = "gpt-live-transcribe" };
        var session = JsonNode.Parse(RealtimeMessages.SessionUpdate(options, "be nice", Tools.DeepClone().AsArray(), 8000))!["session"]!.AsObject();

        Assert.Equal("realtime", session["type"]!.GetValue<string>());
        Assert.False(session.ContainsKey("voice"));
        Assert.False(session.ContainsKey("turn_detection"));
        Assert.False(session.ContainsKey("input_audio_transcription"));
        Assert.Equal("marin", session["audio"]!["output"]!["voice"]!.GetValue<string>());
        Assert.Equal("gpt-live-transcribe", session["audio"]!["input"]!["transcription"]!["model"]!.GetValue<string>());
        Assert.Equal(8000, session["audio"]!["input"]!["turn_detection"]!["idle_timeout_ms"]!.GetValue<int>());
    }

    [Fact]
    public void Xai_session_keeps_the_shape_the_production_line_runs()
    {
        var session = JsonNode.Parse(RealtimeMessages.SessionUpdate(new RealtimeOptions(), "be nice", Tools.DeepClone().AsArray(), 8000))!["session"]!.AsObject();
        Assert.Equal("Leo", session["voice"]!.GetValue<string>());
        Assert.Equal("grok-2-audio", session["input_audio_transcription"]!["model"]!.GetValue<string>());
        Assert.Equal(1.16, session["audio"]!["output"]!["speed"]!.GetValue<double>());
    }
}

public class GptLiveAdapterTests
{
    private static string Pcm(double peak, int samples = 2400)
    {
        var s = Enumerable.Range(0, samples).Select(n => (short)(peak * Math.Sin(2 * Math.PI * 300 * n / 24000.0))).ToArray();
        var bytes = new byte[s.Length * 2];
        Buffer.BlockCopy(s, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    private static string Delta(double peak) => JsonSerializer.Serialize(new { type = "session.output_audio.delta", delta = Pcm(peak) });

    [Fact]
    public void Continuous_silence_does_not_open_a_reply()
    {
        var channel = new GptLiveChannel(new FakeModel(), new RealtimeOptions { Provider = "gpt-live" });
        var events = Enumerable.Range(0, 20).SelectMany(_ => channel.Translate(Delta(3))).ToList();
        Assert.DoesNotContain(events, e => e.Contains("response.created"));
        Assert.All(events, e => Assert.Contains("response.output_audio.delta", e)); // still forwarded to the caller
    }

    [Fact]
    public async Task Audible_audio_opens_a_reply_and_sustained_silence_closes_it()
    {
        var channel = new GptLiveChannel(new FakeModel(), new RealtimeOptions { Provider = "gpt-live" });
        Assert.Contains(channel.Translate(Delta(9000)), e => e.Contains("response.created"));
        Assert.DoesNotContain(channel.Translate(Delta(3)), e => e.Contains("response.done")); // a breath, not the end
        await Task.Delay(GptLiveChannel.SilenceEndsReplyMs + 100);
        Assert.Contains(channel.Translate(Delta(3)), e => e.Contains("response.done"));
    }

    [Fact]
    public void Delegated_tool_calls_are_unwrapped_from_response_events()
    {
        var channel = new GptLiveChannel(new FakeModel(), new RealtimeOptions { Provider = "gpt-live" });
        var e = channel.Translate("""{"type":"response.event","delegation_id":"d1","event":{"type":"response.output_item.done","item":{"type":"function_call","call_id":"call_1","name":"book_appointment","arguments":"{\"start\":\"2026-09-25T09:00\"}"}}}""").Single();
        var call = JsonDocument.Parse(e).RootElement;
        Assert.Equal("call_1", call.GetProperty("call_id").GetString());
        Assert.Equal("book_appointment", call.GetProperty("name").GetString());
    }
}

public class GeminiAdapterTests
{
    private static (GeminiLiveChannel Channel, FakeModel Inner) Create(int? silence = null)
    {
        var inner = new FakeModel();
        var options = new RealtimeOptions { Provider = "gemini", Model = "gemini-3.8-live", Voice = "Puck", SilenceDurationMs = silence };
        return (new GeminiLiveChannel(inner, options), inner);
    }

    [Fact]
    public async Task Session_update_becomes_a_setup_with_tools_voice_and_transcription()
    {
        var (channel, inner) = Create(silence: 600);
        var tools = new JsonArray(new JsonObject
        {
            ["type"] = "function",
            ["name"] = "book_appointment",
            ["description"] = "Book it",
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["start"] = new JsonObject { ["type"] = "string" } } },
        });
        await channel.SendAsync(RealtimeMessages.SessionUpdate(new RealtimeOptions(), "You are the receptionist.", tools, 8000), default);

        var setup = inner.Received.Single().GetProperty("setup");
        Assert.Equal("models/gemini-3.8-live", setup.GetProperty("model").GetString());
        Assert.Equal("Puck", setup.GetProperty("generationConfig").GetProperty("speechConfig").GetProperty("voiceConfig")
            .GetProperty("prebuiltVoiceConfig").GetProperty("voiceName").GetString());
        Assert.Equal("book_appointment", setup.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
        Assert.Equal(600, setup.GetProperty("realtimeInputConfig").GetProperty("automaticActivityDetection").GetProperty("silenceDurationMs").GetInt32());
        Assert.True(setup.TryGetProperty("inputAudioTranscription", out _));
    }

    [Fact]
    public async Task First_response_create_greets_later_ones_are_left_to_gemini()
    {
        var (channel, inner) = Create();
        await channel.SendAsync(RealtimeMessages.ResponseCreate(), default);
        await channel.SendAsync(RealtimeMessages.ResponseCreate(), default);
        await channel.SendAsync(RealtimeMessages.ResponseCancel(), default);
        Assert.Single(inner.Received);
        Assert.Contains("Greet the caller", inner.Received.Single().GetProperty("clientContent").GetRawText());
    }

    [Fact]
    public async Task Tool_round_trip_keeps_the_call_id_and_restores_the_name()
    {
        var (channel, inner) = Create();
        var events = channel.Translate("""{"toolCall":{"functionCalls":[{"id":"fc-9","name":"verify_account","args":{"last4":"4821","zip":"90010"}}]}}""").ToList();
        var call = JsonDocument.Parse(events.Single()).RootElement;
        Assert.Equal("response.function_call_arguments.done", call.GetProperty("type").GetString());
        Assert.Equal("fc-9", call.GetProperty("call_id").GetString());
        Assert.Contains("4821", call.GetProperty("arguments").GetString());

        await channel.SendAsync(RealtimeMessages.FunctionCallOutput("fc-9", JsonNode.Parse("""{"verified":true}""")), default);
        var response = inner.Received.Single().GetProperty("toolResponse").GetProperty("functionResponses")[0];
        Assert.Equal("fc-9", response.GetProperty("id").GetString());
        Assert.Equal("verify_account", response.GetProperty("name").GetString());
        Assert.True(response.GetProperty("response").GetProperty("verified").GetBoolean());
    }

    [Fact]
    public void Model_audio_and_turn_boundaries_map_to_response_events()
    {
        var (channel, _) = Create();
        var types = new[]
        {
            """{"setupComplete":{}}""",
            """{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"AAAA"}}]}}}""",
            """{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"AAAA"}}]}}}""",
            """{"serverContent":{"outputTranscription":{"text":"Thanks for calling."}}}""",
            """{"serverContent":{"turnComplete":true}}""",
        }.SelectMany(channel.Translate).Select(e => JsonDocument.Parse(e).RootElement.GetProperty("type").GetString()).ToList();

        Assert.Equal(
            ["session.updated", "response.created", "response.output_audio.delta", "response.output_audio.delta",
             "response.output_audio_transcript.delta", "response.done"],
            types);
    }

    [Fact]
    public void Interruption_is_a_barge_in_and_caller_transcripts_accumulate_per_turn()
    {
        var (channel, _) = Create();
        channel.Translate("""{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"data":"AAAA"}}]}}}""").ToList();
        var interrupted = channel.Translate("""{"serverContent":{"interrupted":true}}""").ToList();
        Assert.Contains(interrupted, e => e.Contains("input_audio_buffer.speech_started"));
        Assert.Contains(interrupted, e => e.Contains("response.cancelled"));

        channel.Translate("""{"serverContent":{"inputTranscription":{"text":"Thanks, "}}}""").ToList();
        var last = JsonDocument.Parse(channel.Translate("""{"serverContent":{"inputTranscription":{"text":"bye."}}}""").Single()).RootElement;
        Assert.Equal("Thanks, bye.", last.GetProperty("transcript").GetString());
        Assert.Equal("gemini-caller-1", last.GetProperty("item_id").GetString());
    }
}
