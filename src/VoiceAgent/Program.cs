using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using VoiceAgent.Calls;
using VoiceAgent.Compliance;
using VoiceAgent.Memory;
using VoiceAgent.Metrics;
using VoiceAgent.Realtime;
using VoiceAgent.Telephony;
using VoiceAgent.Tools;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.Configure<TwilioOptions>(config.GetSection("Twilio"));
builder.Services.Configure<RealtimeOptions>(config.GetSection("Realtime"));
builder.Services.Configure<CallOptions>(config.GetSection("Calls"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<TwilioOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RealtimeOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CallOptions>>().Value);

builder.Services.AddSingleton(TimeProvider.System);
// Everything that reads configuration does so lazily, at resolve time, so hosts and test
// harnesses that layer configuration after Program starts are honoured.
builder.Services.AddSingleton(_ => new CallTokens(config["Security:CallTokenSecret"] ?? ""));
builder.Services.AddSingleton<MetricsRegistry>();
// A JSON null in appsettings reaches IConfiguration as "", which is not "no path".
builder.Services.AddSingleton(_ => new CallerMemoryStore(string.IsNullOrWhiteSpace(config["Memory:Path"]) ? null : config["Memory:Path"]));

string DataFile(string name) => Path.Combine(config["Data:Directory"] ?? Path.Combine(AppContext.BaseDirectory, "data"), name);
builder.Services.AddSingleton(_ => DemoBusiness.Load(DataFile("demo-dealer.json")));
builder.Services.AddSingleton(sp => new AgentProfile(
    sp.GetRequiredService<DemoBusiness>(), File.ReadAllText(DataFile("agent-instructions.md"))));
builder.Services.AddSingleton(sp => new KnowledgeIndex(sp.GetRequiredService<DemoBusiness>().Knowledge));
builder.Services.AddSingleton<AppointmentBook>();
builder.Services.AddSingleton<IVoiceTool, SearchKnowledgeTool>();
builder.Services.AddSingleton<IVoiceTool, CheckAvailabilityTool>();
builder.Services.AddSingleton<IVoiceTool, BookAppointmentTool>();
builder.Services.AddSingleton<IVoiceTool, VerifyAccountTool>();
builder.Services.AddSingleton<IVoiceTool, RecordPromiseToPayTool>();
builder.Services.AddSingleton<ToolRegistry>();

builder.Services.Configure<OutboundPolicyOptions>(config.GetSection("Outbound"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<OutboundPolicyOptions>>().Value);
builder.Services.AddSingleton<OutboundCallPolicy>();
builder.Services.AddSingleton(_ => new DisclosureLibrary(DataFile("disclosures")));

builder.Services.AddSingleton(sp => WebSocketRealtimeConnector.For(sp.GetRequiredService<RealtimeOptions>()));
builder.Services.AddHttpClient<TwilioRestClient>();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var twilioOptions = app.Services.GetRequiredService<TwilioOptions>();
var callTokens = app.Services.GetRequiredService<CallTokens>();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/metrics", (MetricsRegistry metrics) => Results.Json(metrics.Snapshot()));

// Inbound: Twilio's signed webhook -> TwiML that connects the call to the media socket.
app.MapPost("/voice/incoming", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    if (!IsSignedByTwilio(request, form)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    string callSid = form["CallSid"].ToString();
    return Results.Content(StreamTwiml(callSid, "inbound", "", form["From"].ToString()), "text/xml");
});

// Outbound: the TwiML Twilio fetches once the callee answers. The purpose rides in the query
// string, which is part of the URL Twilio signs, so it cannot be altered in transit.
app.MapPost("/voice/outbound", async (HttpRequest request, DemoBusiness business) =>
{
    var form = await request.ReadFormAsync();
    if (!IsSignedByTwilio(request, form)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    // Never leave account details on a voicemail: a machine gets a neutral callback request.
    if (form["AnsweredBy"].ToString().StartsWith("machine", StringComparison.Ordinal))
        return Results.Content(Twiml.Say($"Hello, this is a courtesy call from {business.BusinessName}. Please call us back at your convenience. Thank you."), "text/xml");

    string purpose = request.Query["purpose"].ToString();
    string account = request.Query["account"].ToString();
    return Results.Content(StreamTwiml(form["CallSid"].ToString(), "outbound", purpose, form["To"].ToString(), account), "text/xml");
});

// Place an outbound call (payment or service reminder). Internal callers only. A payment
// reminder is checked against the account's call-frequency and calling-hours policy before
// anything is dialled, and every attempt is recorded against the account.
app.MapPost("/calls/outbound", async (HttpRequest request, OutboundCallRequest body, TwilioRestClient twilioRest,
    OutboundCallPolicy policy, DemoBusiness business, CancellationToken ct) =>
{
    string? key = config["Security:ApiKey"];
    if (string.IsNullOrEmpty(key) || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(request.Headers["X-Api-Key"].ToString()), Encoding.UTF8.GetBytes(key)))
        return Results.Unauthorized();
    if (body.Purpose is not ("payment_reminder" or "service_reminder")) return Results.BadRequest(new { error = "unknown purpose" });

    string account = body.AccountId ?? "";
    if (body.Purpose == "payment_reminder")
    {
        if (!business.Accounts.Any(a => a.Id == account)) return Results.BadRequest(new { error = "a payment reminder needs a known accountId" });
        // Demo: the consumer's zone is the business's. A deployment takes it from the account's address.
        var decision = policy.Check(account, business.Zone);
        if (!decision.Allowed) return Results.Json(new { error = "call not permitted", reason = decision.Reason }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    string baseUrl = twilioOptions.PublicBaseUrl.TrimEnd('/');
    string query = $"purpose={Uri.EscapeDataString(body.Purpose)}" + (account.Length > 0 ? $"&account={Uri.EscapeDataString(account)}" : "");
    string sid = await twilioRest.CreateCallAsync(body.To, $"{baseUrl}/voice/outbound?{query}", $"{baseUrl}/voice/status", ct);
    if (account.Length > 0) policy.RecordAttempt(account);
    return Results.Ok(new { callSid = sid });
});

app.MapPost("/voice/status", () => Results.NoContent());

app.Map("/media", async (HttpContext context, IServiceProvider services, ILogger<CallSession> log) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await using var channel = new WebSocketMessageChannel(socket);
    var session = ActivatorUtilities.CreateInstance<CallSession>(services, channel);
    await session.RunAsync(context.RequestAborted);
});

app.Run();

bool IsSignedByTwilio(HttpRequest request, IFormCollection form)
{
    if (!twilioOptions.ValidateSignatures) return true;
    if (string.IsNullOrEmpty(twilioOptions.AuthToken) || string.IsNullOrEmpty(twilioOptions.PublicBaseUrl)) return false;

    string url = twilioOptions.PublicBaseUrl.TrimEnd('/') + request.Path + request.QueryString;
    var parameters = form.Select(f => new KeyValuePair<string, string>(f.Key, f.Value.ToString()));
    return TwilioSignature.IsValid(twilioOptions.AuthToken, url, parameters, request.Headers["X-Twilio-Signature"]);
}

string StreamTwiml(string callSid, string direction, string purpose, string from, string account = "")
{
    string wsUrl = twilioOptions.PublicBaseUrl.TrimEnd('/').Replace("https://", "wss://").Replace("http://", "ws://") + "/media";
    var parameters = new Dictionary<string, string>
    {
        ["t"] = callTokens.Mint(callSid, direction, CallTokens.PurposeKey(purpose, account)),
        ["dir"] = direction,
        ["from"] = from,
    };
    if (purpose.Length > 0) parameters["purpose"] = purpose;
    if (account.Length > 0) parameters["account"] = account;
    return Twiml.ConnectStream(wsUrl, parameters);
}

public sealed record OutboundCallRequest(string To, string Purpose, string? AccountId = null);

public partial class Program;
