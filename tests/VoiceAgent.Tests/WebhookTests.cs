using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using VoiceAgent.Telephony;

namespace VoiceAgent.Tests;

public class WebhookTests : IAsyncLifetime
{
    private readonly BridgeFactory _factory = new();
    private readonly CapturingHandler _twilioApi = new();

    public WebhookTests() =>
        _factory.ExtraServices = services =>
            services.AddHttpClient<TwilioRestClient>().ConfigurePrimaryHttpMessageHandler(() => _twilioApi);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static readonly Dictionary<string, string> InboundForm = new()
    {
        ["CallSid"] = "CA0123456789abcdef0123456789abcdef",
        ["From"] = "+13105550142",
        ["To"] = "+13237466888",
        ["CallStatus"] = "ringing",
    };

    private async Task<HttpResponseMessage> PostSignedAsync(string pathAndQuery, Dictionary<string, string> form, string? signature = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, pathAndQuery) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Add("X-Twilio-Signature",
            signature ?? TwilioSignature.Compute("test-auth-token", "https://voice.test" + pathAndQuery, form));
        return await _factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task Unsigned_webhook_is_refused()
    {
        var response = await _factory.CreateClient().PostAsync("/voice/incoming", new FormUrlEncodedContent(InboundForm));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Forged_signature_is_refused()
    {
        var response = await PostSignedAsync("/voice/incoming", InboundForm, signature: "bm90LWEtc2lnbmF0dXJl");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Signed_inbound_call_gets_twiml_that_streams_to_the_media_socket_with_a_call_bound_token()
    {
        var response = await PostSignedAsync("/voice/incoming", InboundForm);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var stream = xml.Descendants("Stream").Single();
        Assert.Equal("wss://voice.test/media", stream.Attribute("url")!.Value);
        var parameters = stream.Elements("Parameter").ToDictionary(p => p.Attribute("name")!.Value, p => p.Attribute("value")!.Value);
        Assert.Equal("inbound", parameters["dir"]);
        Assert.Equal("+13105550142", parameters["from"]);
        Assert.True(_factory.Tokens.Verify(parameters["t"], InboundForm["CallSid"], "inbound"));
        Assert.False(_factory.Tokens.Verify(parameters["t"], "CA_some_other_call", "inbound"));
    }

    [Fact]
    public async Task Outbound_call_answered_by_voicemail_never_connects_the_agent()
    {
        var form = new Dictionary<string, string>(InboundForm) { ["AnsweredBy"] = "machine_end_beep", ["To"] = "+13105550142" };
        var response = await PostSignedAsync("/voice/outbound?purpose=payment_reminder", form);

        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<Stream", body);
        Assert.Contains("<Say>", body);
        Assert.DoesNotContain("payment", body, StringComparison.OrdinalIgnoreCase); // nothing about the debt on a voicemail
    }

    [Fact]
    public async Task Outbound_call_answered_by_a_person_binds_the_purpose_into_the_token()
    {
        var form = new Dictionary<string, string>(InboundForm) { ["AnsweredBy"] = "human", ["To"] = "+13105550142" };
        var response = await PostSignedAsync("/voice/outbound?purpose=payment_reminder", form);

        var stream = XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants("Stream").Single();
        var p = stream.Elements("Parameter").ToDictionary(e => e.Attribute("name")!.Value, e => e.Attribute("value")!.Value);
        Assert.Equal("payment_reminder", p["purpose"]);
        Assert.True(_factory.Tokens.Verify(p["t"], form["CallSid"], "outbound", "payment_reminder"));
        Assert.False(_factory.Tokens.Verify(p["t"], form["CallSid"], "outbound", "service_reminder"));
    }

    [Fact]
    public async Task Placing_an_outbound_call_requires_the_api_key()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/calls/outbound", new { to = "+13105550142", purpose = "payment_reminder" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(_twilioApi.LastRequest);
    }

    [Fact]
    public async Task Placing_an_outbound_call_sends_a_well_formed_request_to_twilio()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-api-key");
        // Service reminders carry no account; payment reminders are covered by OutboundEndpointTests.
        var response = await client.PostAsJsonAsync("/calls/outbound", new { to = "+13105550142", purpose = "service_reminder" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("CA_created_by_fake", await response.Content.ReadAsStringAsync());

        var sent = _twilioApi.LastRequest!;
        Assert.Equal("https://api.twilio.com/2010-04-01/Accounts/AC_test/Calls.json", sent.Uri.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("AC_test:test-auth-token")), sent.Authorization);
        Assert.Equal("+13105550142", sent.Form["To"]);
        Assert.Equal("+13237466888", sent.Form["From"]);
        Assert.Equal("https://voice.test/voice/outbound?purpose=service_reminder", sent.Form["Url"]);
        Assert.Equal("Enable", sent.Form["MachineDetection"]);
    }

    [Fact]
    public async Task Unknown_outbound_purpose_is_rejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-api-key");
        var response = await client.PostAsJsonAsync("/calls/outbound", new { to = "+13105550142", purpose = "sell_them_a_warranty" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Health_and_metrics_endpoints_respond()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Contains("latency_ms", await client.GetStringAsync("/metrics"));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public sealed record Captured(Uri Uri, string? Authorization, Dictionary<string, string> Form);
        public Captured? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var form = body.Split('&').Select(kv => kv.Split('=', 2))
                .ToDictionary(kv => Uri.UnescapeDataString(kv[0].Replace('+', ' ')), kv => Uri.UnescapeDataString(kv[1].Replace('+', ' ')));
            LastRequest = new Captured(request.RequestUri!, request.Headers.Authorization?.ToString(), form);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"sid":"CA_created_by_fake","status":"queued"}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
