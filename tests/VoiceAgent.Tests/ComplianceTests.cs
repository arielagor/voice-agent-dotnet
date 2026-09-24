using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VoiceAgent.Compliance;
using VoiceAgent.Telephony;

namespace VoiceAgent.Tests;

public class ReadBackGateTests
{
    private const string Phone = "+13105550142";

    [Theory]
    [InlineData("agent: I have Jordan Reyes at three one oh, five five five, zero one four two. Is that right?")]
    [InlineData("agent: Jordan Reyes, 310-555-0142, tomorrow at nine. Correct?")]
    [InlineData("agent: That's (310) 555 0142?")]
    public void A_read_back_followed_by_a_yes_allows_booking(string readBack) =>
        Assert.True(ReadBackGate.Check([readBack, "caller: Yes, that's all correct."], Phone).Allowed);

    [Fact]
    public void No_read_back_blocks_booking() =>
        Assert.False(ReadBackGate.Check(["caller: my number is 310 555 0142", "caller: book it"], Phone).Allowed);

    [Fact]
    public void A_read_back_without_an_answer_blocks_booking() =>
        Assert.Contains("not confirmed",
            ReadBackGate.Check(["agent: 310-555-0142, is that right?"], Phone).Reason);

    [Fact]
    public void A_correction_is_not_a_confirmation() =>
        Assert.False(ReadBackGate.Check(["agent: 310-555-0142, right?", "caller: No, it's 0143."], Phone).Allowed);

    [Fact]
    public void A_yes_that_came_before_the_read_back_does_not_count() =>
        Assert.False(ReadBackGate.Check(["caller: yes", "agent: 310-555-0142, correct?"], Phone).Allowed);

    [Fact]
    public void A_different_number_read_back_does_not_count() =>
        Assert.False(ReadBackGate.Check(["agent: 310-555-9999, correct?", "caller: yes"], Phone).Allowed);
}

public class OutboundCallPolicyTests
{
    private static readonly TimeZoneInfo La = VoiceAgent.Tools.DemoBusiness.ResolveZone("America/Los_Angeles");

    // Wednesday 2026-09-23, 14:00 Pacific
    private static FixedClock Afternoon() => new(new DateTimeOffset(2026, 9, 23, 21, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Seven_attempts_in_seven_days_then_no_more()
    {
        var clock = Afternoon();
        var policy = new OutboundCallPolicy(new OutboundPolicyOptions(), clock);
        for (int i = 0; i < 7; i++)
        {
            Assert.True(policy.Check("SM-1", La).Allowed);
            policy.RecordAttempt("SM-1");
            clock.Now = clock.Now.AddMinutes(30); // 2:00 to 5:30 p.m., inside calling hours
        }
        var eighth = policy.Check("SM-1", La);
        Assert.False(eighth.Allowed);
        Assert.Contains("cap is 7", eighth.Reason);
        Assert.True(policy.Check("SM-2", La).Allowed); // per account
    }

    [Fact]
    public void Attempts_age_out_of_the_window()
    {
        var clock = Afternoon();
        var policy = new OutboundCallPolicy(new OutboundPolicyOptions(), clock);
        for (int i = 0; i < 7; i++) policy.RecordAttempt("SM-1");
        clock.Now = clock.Now.AddDays(7).AddMinutes(1);
        Assert.True(policy.Check("SM-1", La).Allowed);
    }

    [Fact]
    public void A_conversation_starts_a_quiet_period()
    {
        var clock = Afternoon();
        var policy = new OutboundCallPolicy(new OutboundPolicyOptions(), clock);
        policy.RecordConversation("SM-1");
        clock.Now = clock.Now.AddDays(6);
        Assert.Contains("quiet period", policy.Check("SM-1", La).Reason);
        clock.Now = clock.Now.AddDays(1).AddMinutes(1);
        Assert.True(policy.Check("SM-1", La).Allowed);
    }

    [Theory]
    [InlineData(14, 30, true)]   // 7:30 a.m. Pacific in UTC is 14:30 -> too early
    [InlineData(15, 0, false)]   // 8:00 a.m. -> allowed
    [InlineData(3, 59, false)]   // 8:59 p.m. -> allowed
    [InlineData(4, 0, true)]     // 9:00 p.m. -> too late
    public void Calls_only_inside_local_calling_hours(int utcHour, int utcMinute, bool blocked)
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 23, utcHour, utcMinute, 0, TimeSpan.Zero));
        var decision = new OutboundCallPolicy(new OutboundPolicyOptions(), clock).Check("SM-1", La);
        Assert.Equal(!blocked, decision.Allowed);
    }
}

/// <summary>Whole-call behaviour of the verbatim disclosures and the read-back gate.</summary>
public class DisclosureCallTests : IAsyncLifetime
{
    private readonly BridgeFactory _factory = new();

    public DisclosureCallTests() => _factory.Settings["Calls:RecordingNotice"] = "true";

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task The_recording_notice_plays_verbatim_before_the_model_says_anything()
    {
        await using var twilio = await TwilioCall.ConnectAsync(_factory.Server);
        await twilio.StartAsync(_factory.Tokens);
        var model = await _factory.Model.WaitForConnectionAsync();
        await model.WaitForAsync("session.update");
        model.Push(new { type = "session.updated" });

        var mark = await twilio.WaitForAsync("mark", e => e.GetProperty("mark").GetProperty("name").GetString() == "disclosure-recording-notice");
        int noticeBytes = twilio.Received.TakeWhile(e => e.GetProperty("event").GetString() == "media")
            .Sum(e => Convert.FromBase64String(e.GetProperty("media").GetProperty("payload").GetString()!).Length);
        var expected = File.ReadAllBytes(BridgeFactory.RepoPath("data/disclosures/recording-notice.ulaw"));
        Assert.Equal(expected.Length, noticeBytes); // the whole clip, byte for byte in length

        // The caller talks during the notice: the model does not hear it and is not asked to greet.
        await twilio.SendAudioAsync(Signal.Tone(300, -14, 10));
        await Task.Delay(200);
        Assert.Equal(0, model.CountOf("input_audio_buffer.append"));
        Assert.Equal(0, model.CountOf("response.create"));

        await twilio.SendMarkAsync("disclosure-recording-notice"); // Twilio: finished playing
        await model.WaitForAsync("response.create");
    }

    [Fact]
    public async Task On_a_payment_reminder_the_servicing_disclosure_plays_after_verification_and_before_the_model_continues()
    {
        _factory.Settings["Calls:RecordingNotice"] = "false";
        await using var twilio = await TwilioCall.ConnectAsync(_factory.Server);
        await twilio.StartAsync(_factory.Tokens, direction: "outbound", purpose: "payment_reminder", account: "SM-20931");
        var model = await _factory.Model.WaitForConnectionAsync();
        Assert.Contains("OUTBOUND", (await model.WaitForAsync("session.update")).GetProperty("session").GetProperty("instructions").GetString());
        model.Push(new { type = "session.updated" });
        await model.WaitForAsync("response.create");

        model.Push(new { type = "response.function_call_arguments.done", call_id = "fc_v", name = "verify_account", arguments = """{"last4":"7730","zip":"91436"}""" });

        await twilio.WaitForAsync("mark", e => e.GetProperty("mark").GetProperty("name").GetString() == "disclosure-servicing-notice");
        await model.WaitForAsync("conversation.item.create", e => e.GetProperty("item").GetProperty("type").GetString() == "function_call_output");
        await Task.Delay(200);
        Assert.Equal(1, model.CountOf("response.create")); // held: only the greeting so far

        await twilio.SendMarkAsync("disclosure-servicing-notice");
        await Wait.UntilAsync(() => model.CountOf("response.create") == 2, "model not released after the disclosure");

        Assert.True(_factory.Services.GetRequiredService<OutboundCallPolicy>().InQuietPeriod("SM-20931"));
    }

    [Fact]
    public async Task A_booking_without_a_read_back_is_refused_by_the_tool_itself()
    {
        _factory.Settings["Calls:RecordingNotice"] = "false";
        await using var twilio = await TwilioCall.ConnectAsync(_factory.Server);
        await twilio.StartAsync(_factory.Tokens);
        var model = await _factory.Model.WaitForConnectionAsync();
        await model.WaitForAsync("session.update");
        model.Push(new { type = "session.updated" });

        string start = CallFlowTests.NextServiceDay().ToString("yyyy-MM-dd") + "T11:00";
        model.Push(new
        {
            type = "response.function_call_arguments.done", call_id = "fc_b", name = "book_appointment",
            arguments = JsonSerializer.Serialize(new { department = "service", start, name = "Jordan Reyes", phone = "+13105550142" }),
        });

        var output = await model.WaitForAsync("conversation.item.create", e => e.GetProperty("item").GetProperty("type").GetString() == "function_call_output");
        Assert.Contains("read the callback number back", output.GetProperty("item").GetProperty("output").GetString());
    }
}

public class OutboundEndpointTests : IAsyncLifetime
{
    private readonly BridgeFactory _factory = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 23, 21, 0, 0, TimeSpan.Zero)); // 2 p.m. Pacific

    public OutboundEndpointTests() =>
        _factory.ExtraServices = services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddHttpClient<TwilioRestClient>().ConfigurePrimaryHttpMessageHandler(() => new CreatedCallHandler());
        };

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", "test-api-key");
        return c;
    }

    [Fact]
    public async Task A_payment_reminder_needs_a_known_account()
    {
        var response = await Client().PostAsJsonAsync("/calls/outbound", new { to = "+13105550142", purpose = "payment_reminder" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_eighth_call_in_a_week_is_refused_before_anything_is_dialled()
    {
        var client = Client();
        for (int i = 0; i < 7; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/calls/outbound",
                new { to = "+13105550142", purpose = "payment_reminder", accountId = "SM-20931" })).StatusCode);

        var eighth = await client.PostAsJsonAsync("/calls/outbound", new { to = "+13105550142", purpose = "payment_reminder", accountId = "SM-20931" });
        Assert.Equal((HttpStatusCode)429, eighth.StatusCode);
        Assert.Contains("cap is 7", await eighth.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task No_reminder_calls_after_nine_at_night()
    {
        _clock.Now = new DateTimeOffset(2026, 9, 24, 4, 30, 0, TimeSpan.Zero); // 9:30 p.m. Pacific
        var response = await Client().PostAsJsonAsync("/calls/outbound", new { to = "+13105550142", purpose = "payment_reminder", accountId = "SM-20931" });
        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.Contains("calling hours", await response.Content.ReadAsStringAsync());
    }

    private sealed class CreatedCallHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"sid":"CA_fake"}""", Encoding.UTF8, "application/json"),
            });
    }
}
