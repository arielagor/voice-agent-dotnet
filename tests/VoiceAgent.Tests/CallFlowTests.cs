using System.Text.Json;
using VoiceAgent.Audio;
using VoiceAgent.Metrics;

namespace VoiceAgent.Tests;

/// <summary>
/// Whole calls through the real ASP.NET Core pipeline: Twilio's side of a Media Streams socket
/// on one end, a scripted realtime model on the other, the production CallSession in between.
/// </summary>
public class CallFlowTests : IAsyncLifetime
{
    private readonly BridgeFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<(TwilioCall Twilio, FakeModel Model)> ConnectedCallAsync(string from = "+13105550142", int connection = 0)
    {
        var twilio = await TwilioCall.ConnectAsync(_factory.Server);
        await twilio.StartAsync(_factory.Tokens, from);
        var model = await _factory.Model.WaitForConnectionAsync(connection);
        await model.WaitForAsync("session.update");
        model.Push(new { type = "session.updated" });
        await model.WaitForAsync("response.create");
        return (twilio, model);
    }

    [Fact]
    public async Task A_stream_without_a_valid_call_token_is_refused_before_the_model_is_reached()
    {
        await using var twilio = await TwilioCall.ConnectAsync(_factory.Server);
        await twilio.StartAsync(_factory.Tokens, tokenOverride: "forged");

        await twilio.WaitClosedAsync();
        Assert.Empty(_factory.Model.Connections);
        Assert.Equal(1, _factory.Metrics.Count("streams_rejected"));
    }

    [Fact]
    public async Task Session_is_configured_with_voice_instructions_and_the_tool_surface()
    {
        await using var twilio = await TwilioCall.ConnectAsync(_factory.Server);
        await twilio.StartAsync(_factory.Tokens);
        var model = await _factory.Model.WaitForConnectionAsync();

        var update = await model.WaitForAsync("session.update");
        var session = update.GetProperty("session");
        Assert.Equal("Leo", session.GetProperty("voice").GetString());
        string instructions = session.GetProperty("instructions").GetString()!;
        Assert.Contains("Sunset Motors", instructions);
        Assert.Contains("INBOUND", instructions);
        var toolNames = session.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(["search_knowledge", "check_availability", "book_appointment", "verify_account", "record_promise_to_pay"], toolNames);
        Assert.Equal(24000, session.GetProperty("audio").GetProperty("input").GetProperty("format").GetProperty("rate").GetInt32());
    }

    [Fact]
    public async Task Greeting_audio_is_transcoded_to_8k_mulaw_and_its_latency_recorded()
    {
        var (twilio, model) = await ConnectedCallAsync();
        await using var _ = twilio;

        model.Push(new { type = "response.created" });
        model.PushAudio(Pcm.Tone24k(440, 2400)); // 100 ms at 24 kHz

        var media = await twilio.WaitForAsync("media");
        Assert.Equal(twilio.StreamSid, media.GetProperty("streamSid").GetString());
        var payload = Convert.FromBase64String(media.GetProperty("media").GetProperty("payload").GetString()!);
        Assert.Equal(800, payload.Length); // 100 ms at 8 kHz, one byte per sample
        Assert.Single(_factory.Metrics.Samples(LatencyKind.GreetingFirstAudio));
    }

    [Fact]
    public async Task Caller_audio_reaches_the_model_as_24k_pcm_including_frames_sent_before_it_was_ready()
    {
        await using var twilio = await TwilioCall.ConnectAsync(_factory.Server);
        await twilio.StartAsync(_factory.Tokens);
        var model = await _factory.Model.WaitForConnectionAsync();
        await model.WaitForAsync("session.update");

        await twilio.SendAudioAsync(Signal.Tone(300, -20, 3)); // arrives before session.updated
        await Task.Delay(100);
        Assert.Equal(0, model.CountOf("input_audio_buffer.append"));

        model.Push(new { type = "session.updated" });
        await Wait.UntilAsync(() => model.CountOf("input_audio_buffer.append") == 3, "buffered frames were not flushed");
        var append = await model.WaitForAsync("input_audio_buffer.append");
        Assert.Equal(960, Convert.FromBase64String(append.GetProperty("audio").GetString()!).Length); // 480 samples x 2 bytes
    }

    [Fact]
    public async Task Server_detected_barge_in_clears_twilio_and_cancels_the_response()
    {
        var (twilio, model) = await ConnectedCallAsync();
        await using var _ = twilio;
        model.Push(new { type = "response.created" });
        model.PushAudio(Pcm.Tone24k(440, 4800));
        await twilio.WaitForAsync("media");

        model.Push(new { type = "input_audio_buffer.speech_started" });

        await twilio.WaitForAsync("clear");
        await model.WaitForAsync("response.cancel");
        Assert.Equal(1, _factory.Metrics.Count("barge_in_server"));
    }

    [Fact]
    public async Task Local_vad_barge_in_clears_twilio_without_waiting_for_the_model()
    {
        var (twilio, model) = await ConnectedCallAsync();
        await using var _ = twilio;
        await twilio.SendAudioAsync(Signal.Noise(-62, 15, seed: 11)); // line noise: calibrates the floor

        model.Push(new { type = "response.created" });
        model.PushAudio(Pcm.Tone24k(440, 4800));
        await twilio.WaitForAsync("media");

        await twilio.SendAudioAsync(Signal.Tone(250, -16, 5)); // the caller talks over the agent

        await twilio.WaitForAsync("clear");
        await model.WaitForAsync("response.cancel");
        Assert.Equal(1, _factory.Metrics.Count("barge_in_local"));
        Assert.Equal(0, _factory.Metrics.Count("barge_in_server"));
        Assert.Single(_factory.Metrics.Samples(LatencyKind.BargeInClear));
    }

    [Fact]
    public async Task Knowledge_tool_call_is_executed_and_its_result_returned_to_the_model()
    {
        var (twilio, model) = await ConnectedCallAsync();
        await using var _ = twilio;

        model.Push(new
        {
            type = "response.function_call_arguments.done",
            call_id = "fc_1",
            name = "search_knowledge",
            arguments = """{"query":"how much is a synthetic oil change"}""",
        });

        var output = await model.WaitForAsync("conversation.item.create",
            e => e.GetProperty("item").GetProperty("type").GetString() == "function_call_output");
        Assert.Equal("fc_1", output.GetProperty("item").GetProperty("call_id").GetString());
        var result = Pcm.Output(output)!;
        Assert.Equal("oil-change", result["results"]![0]!["id"]!.GetValue<string>());
        Assert.Contains("$89.95", result.ToJsonString());
        await Wait.UntilAsync(() => model.CountOf("response.create") == 2, "no response.create after the tool result");
    }

    [Fact]
    public async Task Goodbye_ends_the_call_only_after_twilio_confirms_the_closing_line_played()
    {
        var (twilio, model) = await ConnectedCallAsync();

        model.Push(new { type = "conversation.item.input_audio_transcription.completed", transcript = "Great, that's all I need. Bye!" });
        await model.WaitForAsync("session.update", e =>
            e.GetProperty("session").GetProperty("turn_detection").GetProperty("idle_timeout_ms").ValueKind == JsonValueKind.Null);

        model.Push(new { type = "response.created" });
        model.PushAudio(Pcm.Tone24k(440, 2400));
        model.Push(new { type = "response.done" });

        await twilio.WaitForAsync("mark", e => e.GetProperty("mark").GetProperty("name").GetString() == "end-call");
        Assert.False(twilio.Closed.Task.IsCompleted); // not cut off while the goodbye is still playing

        await twilio.SendMarkAsync("end-call");
        await twilio.WaitClosedAsync();
        await Wait.UntilAsync(() => _factory.Metrics.Count("calls_ended") == 1, "call not recorded as ended");
    }

    [Fact]
    public async Task Promised_but_unbooked_appointment_is_flushed_before_teardown()
    {
        var (twilio, model) = await ConnectedCallAsync();

        model.Push(new { type = "response.created" });
        model.Push(new { type = "response.output_audio_transcript.delta", delta = "Perfect, you're all set for ten tomorrow." });
        model.Push(new { type = "response.done" });
        model.Push(new { type = "conversation.item.input_audio_transcription.completed", transcript = "Thanks, bye." });

        await model.WaitForAsync("conversation.item.create", e =>
            e.GetProperty("item").GetProperty("type").GetString() == "message" &&
            e.GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString()!.Contains("BOOKING INTEGRITY CHECK"));

        // The model obeys the flush and books the slot it promised.
        string start = NextServiceDay().ToString("yyyy-MM-dd") + "T10:00";
        model.Push(new
        {
            type = "response.function_call_arguments.done",
            call_id = "fc_book",
            name = "book_appointment",
            arguments = JsonSerializer.Serialize(new { department = "service", start, name = "Jordan Reyes", phone = "+13105550142" }),
        });
        await model.WaitForAsync("conversation.item.create", e => e.GetProperty("item").GetProperty("type").GetString() == "function_call_output");

        await twilio.SendStopAsync(); // caller hangs up
        await twilio.WaitClosedAsync();
        await Wait.UntilAsync(() => _factory.Metrics.Count("booking_CommittedViaFlush") == 1, "flushed booking not recorded");
    }

    [Fact]
    public async Task A_promise_the_flush_could_not_keep_is_recorded_rather_than_lost()
    {
        var (twilio, model) = await ConnectedCallAsync();

        model.Push(new { type = "response.created" });
        model.Push(new { type = "response.output_audio_transcript.delta", delta = "I've booked you in for Friday." });
        model.Push(new { type = "response.done" });
        await Wait.UntilAsync(() => model.CountOf("response.create") == 1, "setup");

        await twilio.SendStopAsync(); // hangs up before any tool call; the flush gets 400 ms in this config
        await model.WaitForAsync("conversation.item.create", e => e.GetProperty("item").GetProperty("type").GetString() == "message");
        await twilio.WaitClosedAsync();
        await Wait.UntilAsync(() => _factory.Metrics.Count("booking_PromisedNotCommitted") == 1, "unkept promise not recorded");
    }

    [Fact]
    public async Task A_returning_caller_is_greeted_with_what_the_system_recorded_last_time()
    {
        const string caller = "(775) 555-0199";
        var (first, model) = await ConnectedCallAsync(caller);
        model.Push(new
        {
            type = "response.function_call_arguments.done",
            call_id = "fc_v",
            name = "verify_account",
            arguments = """{"last4":"4821","zip":"90010"}""",
        });
        await model.WaitForAsync("conversation.item.create", e => e.GetProperty("item").GetProperty("type").GetString() == "function_call_output");
        await first.SendStopAsync();
        await first.WaitClosedAsync();
        await first.DisposeAsync();

        await using var second = await TwilioCall.ConnectAsync(_factory.Server);
        await second.StartAsync(_factory.Tokens, "+17755550199");
        var model2 = await _factory.Model.WaitForConnectionAsync(1);
        var update = await model2.WaitForAsync("session.update");
        string instructions = update.GetProperty("session").GetProperty("instructions").GetString()!;
        Assert.Contains("RETURNING CALLER", instructions);
        Assert.Contains("Jordan", instructions);
    }

    [Fact]
    public async Task A_turn_that_the_model_commits_but_never_answers_gets_a_forced_reply()
    {
        var (twilio, model) = await ConnectedCallAsync();
        await using var _ = twilio;

        model.Push(new { type = "input_audio_buffer.committed" });

        await Wait.UntilAsync(() => model.CountOf("response.create") == 2, "no forced response", ms: 3000);
        Assert.Equal(1, _factory.Metrics.Count("forced_responses"));
    }

    internal static DateTime NextServiceDay()
    {
        var day = DateTime.Today.AddDays(2);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(1);
        return day;
    }
}
