using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceAgent.Calls;
using VoiceAgent.Memory;
using VoiceAgent.Metrics;
using VoiceAgent.Telephony;
using VoiceAgent.Tools;

namespace VoiceAgent.Tests;

public class BookingIntegrityTests
{
    [Theory]
    [InlineData("Perfect, you're all set for ten tomorrow.")]
    [InlineData("I've booked you in for Friday at nine.")]
    [InlineData("Okay, I'm booking that now.")]
    [InlineData("Let me lock that in for you.")]
    [InlineData("That's on the calendar.")]
    [InlineData("Would you like me to book that? Great, I've scheduled it.")]
    public void Commitments_are_detected(string turn) => Assert.True(BookingIntegrity.DetectsCommitment(turn));

    [Theory]
    [InlineData("Would you like me to book that for you?")]
    [InlineData("I can book you for ten or eleven.")]
    [InlineData("I'd be happy to schedule a test drive.")]
    [InlineData("Shall I book the 9:30?")]
    [InlineData("Our service department is open until five.")]
    public void Offers_and_questions_never_count_as_commitments(string turn) =>
        Assert.False(BookingIntegrity.DetectsCommitment(turn));

    [Fact]
    public void Only_a_positively_readable_result_counts_as_booked()
    {
        Assert.True(BookingIntegrity.IsBookingSuccess(JsonNode.Parse("""{"success":true,"eventId":"apt_1"}""")));
        Assert.True(BookingIntegrity.IsBookingSuccess(JsonNode.Parse("""{"eventId":"apt_1"}""")));
        Assert.False(BookingIntegrity.IsBookingSuccess(JsonNode.Parse("""{"error":"slot taken"}""")));
        Assert.False(BookingIntegrity.IsBookingSuccess(JsonNode.Parse("""{"success":false,"eventId":"apt_1"}""")));
        Assert.False(BookingIntegrity.IsBookingSuccess(JsonNode.Parse("""{"status":"ok"}""")));
        Assert.False(BookingIntegrity.IsBookingSuccess(null));
    }

    [Fact]
    public void Outcome_names_the_unkept_promise() =>
        Assert.Equal(BookingOutcome.PromisedNotCommitted, BookingIntegrity.Outcome(promised: true, committed: false, flushRequested: true));

    [Theory]
    [InlineData("okay thanks, bye", true)]
    [InlineData("that's all I need", true)]
    [InlineData("have a great day", true)]
    [InlineData("I'd like to buy a car", false)]
    [InlineData("can you describe the bypass valve", false)]
    [InlineData("Yes, that's all correct.", false)] // live call 2026-09-24: hung up a turn early
    [InlineData("that's all right, go ahead", false)]
    [InlineData("That's all, thanks", true)]
    [InlineData("that's all for now", true)]
    [InlineData("Great, that's all I need. Thanks, bye.", true)]
    public void Goodbye_detection(string heard, bool expected) => Assert.Equal(expected, CallerPhrases.IsGoodbye(heard));

    [Theory]
    [InlineData("Yes, that's all", false)]     // live GPT-Live call: a partial of "Yes, that's all correct"
    [InlineData("Great, that's all I", false)]
    [InlineData("that's all, thanks", true)]
    [InlineData("okay, bye", true)]
    public void Streaming_partials_need_an_unambiguous_closing(string partial, bool expected) =>
        Assert.Equal(expected, CallerPhrases.IsGoodbye(partial, final: false));

    [Fact]
    public void The_same_text_is_a_goodbye_once_the_turn_is_over() =>
        Assert.True(CallerPhrases.IsGoodbye("Okay, that's all", final: true));
}

public class KnowledgeIndexTests
{
    private static readonly KnowledgeIndex Index = new(DemoBusiness.Load(BridgeFactory.RepoPath("data/demo-dealer.json")).Knowledge);

    [Theory]
    [InlineData("how much is an oil change", "oil-change")]
    [InlineData("what do I need to bring to get financing", "financing-documents")]
    [InlineData("I'm going to be late on my payment", "late-fees")]
    [InlineData("can I trade in my truck", "trade-in")]
    [InlineData("are you open on saturday", "hours")]
    public void Top_hit_is_the_right_article(string query, string expectedId) =>
        Assert.Equal(expectedId, Index.Search(query)[0].Article.Id);

    [Fact]
    public void Off_topic_query_returns_nothing_instead_of_a_confident_wrong_answer() =>
        Assert.Empty(Index.Search("what is the capital of peru"));
}

public class DealerToolTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // Wednesday 2026-09-23, 09:00 Pacific
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 16, 0, 0, TimeSpan.Zero);
    private readonly DemoBusiness _business = DemoBusiness.Load(BridgeFactory.RepoPath("data/demo-dealer.json"));
    private readonly ToolContext _ctx = new("call1", "+13105550142");
    private AppointmentBook Book => _book ??= new AppointmentBook(_business, new FixedClock(Now));
    private AppointmentBook? _book;

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task Availability_respects_department_days_hours_and_a_one_hour_lead()
    {
        var tool = new CheckAvailabilityTool(Book);
        var today = await tool.InvokeAsync(Args(new { department = "service", date = "2026-09-23" }), _ctx, default);
        Assert.Equal("2026-09-23T10:30", today["open_slots"]![0]!.GetValue<string>()); // 09:00 now + 60 min lead, next slot after

        var sunday = await tool.InvokeAsync(Args(new { department = "service", date = "2026-09-27" }), _ctx, default);
        Assert.Empty(sunday["open_slots"]!.AsArray());
    }

    [Fact]
    public async Task A_slot_cannot_be_booked_twice()
    {
        var tool = new BookAppointmentTool(Book);
        var args = Args(new { department = "sales", start = "2026-09-24T11:00", name = "Jordan", phone = "+13105550142" });
        Assert.True((await tool.InvokeAsync(args, _ctx, default))["success"]!.GetValue<bool>());
        Assert.Contains("not open", (await tool.InvokeAsync(args, _ctx, default))["error"]!.GetValue<string>());
        Assert.Equal("Jordan", _ctx.CallerName);
    }

    [Fact]
    public async Task Verification_locks_after_three_misses_even_if_the_fourth_guess_is_right()
    {
        var tool = new VerifyAccountTool(_business);
        for (int i = 0; i < VerifyAccountTool.MaxAttempts; i++)
            Assert.False((await tool.InvokeAsync(Args(new { last4 = "0000", zip = "90010" }), _ctx, default))["verified"]!.GetValue<bool>());

        var correct = await tool.InvokeAsync(Args(new { last4 = "4821", zip = "90010" }), _ctx, default);
        Assert.Contains("locked", correct["error"]!.GetValue<string>());
        Assert.Empty(_ctx.VerifiedAccounts);
    }

    [Fact]
    public async Task No_account_detail_comes_back_from_a_failed_verification()
    {
        var result = await new VerifyAccountTool(_business).InvokeAsync(Args(new { last4 = "4821", zip = "00000" }), _ctx, default);
        Assert.DoesNotContain("amount_due", result.ToJsonString());
        Assert.DoesNotContain("SM-10482", result.ToJsonString());
    }

    [Fact]
    public async Task Promise_to_pay_requires_verification_on_this_call_and_sane_terms()
    {
        var promise = new RecordPromiseToPayTool(_business, Book);
        var args = Args(new { account_id = "SM-20931", amount = 389.00, date = "2026-09-30" });
        Assert.Contains("not verified", (await promise.InvokeAsync(args, _ctx, default))["error"]!.GetValue<string>());

        await new VerifyAccountTool(_business).InvokeAsync(Args(new { last4 = "7730", zip = "91436" }), _ctx, default);
        Assert.True((await promise.InvokeAsync(args, _ctx, default))["success"]!.GetValue<bool>());

        var tooMuch = Args(new { account_id = "SM-20931", amount = 5000, date = "2026-09-30" });
        Assert.Contains("amount", (await promise.InvokeAsync(tooMuch, _ctx, default))["error"]!.GetValue<string>());

        var tooLate = Args(new { account_id = "SM-20931", amount = 100, date = "2026-11-30" });
        Assert.Contains("days out", (await promise.InvokeAsync(tooLate, _ctx, default))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Registry_turns_bad_calls_into_errors_the_model_can_recover_from()
    {
        var registry = new ToolRegistry([new BookAppointmentTool(Book)]);
        Assert.Contains("unknown tool", (await registry.DispatchAsync("transfer_funds", "{}", _ctx, default)).Output["error"]!.GetValue<string>());
        Assert.Contains("not valid JSON", (await registry.DispatchAsync("book_appointment", "{oops", _ctx, default)).Output["error"]!.GetValue<string>());
        var missing = await registry.DispatchAsync("book_appointment", """{"department":"sales","start":"2026-09-24T11:00"}""", _ctx, default);
        Assert.True(missing.Failed);
        Assert.Contains("name", missing.Output["error"]!.GetValue<string>());
    }
}

public class SupportingTypesTests
{
    [Theory]
    [InlineData("(775) 252-8333", "+17752528333")]
    [InlineData("775.252.8333", "+17752528333")]
    [InlineData("+17752528333", "+17752528333")]
    [InlineData("+442071838750", "+442071838750")]
    public void Caller_numbers_normalize_to_one_key(string input, string expected) =>
        Assert.Equal(expected, CallerMemoryStore.Normalize(input));

    [Fact]
    public void Caller_memory_persists_across_restarts()
    {
        string path = Path.Combine(Path.GetTempPath(), $"callers-{Guid.NewGuid():N}.json");
        try
        {
            new CallerMemoryStore(path).Record("+13105550142", "Jordan", ["booked service 2026-09-24 10:00"], DateTimeOffset.UtcNow);
            var reloaded = new CallerMemoryStore(path).Find("(310) 555-0142");
            Assert.Equal("Jordan", reloaded!.Name);
            Assert.Contains("booked service", CallerMemoryStore.Preamble(reloaded));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Call_tokens_bind_call_direction_and_purpose()
    {
        var tokens = new CallTokens("secret");
        string t = tokens.Mint("CA1", "outbound", "payment_reminder");
        Assert.True(tokens.Verify(t, "CA1", "outbound", "payment_reminder"));
        Assert.False(tokens.Verify(t, "CA2", "outbound", "payment_reminder"));
        Assert.False(tokens.Verify(t, "CA1", "inbound", "payment_reminder"));
        Assert.False(tokens.Verify(t, "CA1", "outbound", "service_reminder"));
        Assert.False(new CallTokens("other").Verify(t, "CA1", "outbound", "payment_reminder"));
    }

    [Fact]
    public void Percentiles_interpolate()
    {
        double[] sorted = [100, 200, 300, 400, 500];
        Assert.Equal(300, MetricsRegistry.Percentile(sorted, 0.5));
        Assert.Equal(480, MetricsRegistry.Percentile(sorted, 0.95), precision: 6);
    }

    [Fact]
    public void Disarming_the_idle_follow_up_keeps_the_tuned_end_of_turn_window()
    {
        var options = new VoiceAgent.Realtime.RealtimeOptions { SilenceDurationMs = 500, VadThreshold = 0.7 };
        var td = JsonNode.Parse(VoiceAgent.Realtime.RealtimeMessages.IdleFollowup(options, null))!["session"]!["turn_detection"]!;
        Assert.Equal(500, td["silence_duration_ms"]!.GetValue<int>());
        Assert.Equal(0.7, td["threshold"]!.GetValue<double>());
        Assert.Null(td["idle_timeout_ms"]);
        Assert.True(td.AsObject().ContainsKey("idle_timeout_ms")); // explicitly null, which is what disarms it
    }

    [Fact]
    public void Untuned_sessions_leave_the_provider_defaults_alone()
    {
        var td = JsonNode.Parse(VoiceAgent.Realtime.RealtimeMessages.IdleFollowup(new VoiceAgent.Realtime.RealtimeOptions(), 8000))!["session"]!["turn_detection"]!.AsObject();
        Assert.False(td.ContainsKey("silence_duration_ms"));
        Assert.False(td.ContainsKey("threshold"));
    }

    [Fact]
    public void Twiml_escapes_parameter_values()
    {
        string xml = Twiml.ConnectStream("wss://x/media", new Dictionary<string, string> { ["from"] = "\"<script>&" });
        Assert.Contains("&quot;&lt;script&gt;&amp;", xml);
    }
}
