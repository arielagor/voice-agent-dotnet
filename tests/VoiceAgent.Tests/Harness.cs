using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using VoiceAgent.Audio;
using VoiceAgent.Metrics;
using VoiceAgent.Realtime;
using VoiceAgent.Telephony;

namespace VoiceAgent.Tests;

/// <summary>A realtime model the test drives by hand: it records what the bridge sends and emits what the test pushes.</summary>
public sealed class FakeRealtime : IRealtimeConnector
{
    public ConcurrentQueue<FakeModel> Connections { get; } = new();

    public Task<IMessageChannel> ConnectAsync(CancellationToken ct)
    {
        var model = new FakeModel();
        Connections.Enqueue(model);
        return Task.FromResult<IMessageChannel>(model);
    }

    public async Task<FakeModel> WaitForConnectionAsync(int index = 0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (Connections.Count > index) return Connections.ElementAt(index);
            await Task.Delay(10);
        }
        throw new TimeoutException("the bridge never connected to the model");
    }
}

public sealed class FakeModel : IMessageChannel
{
    private readonly Channel<string> _toBridge = Channel.CreateUnbounded<string>();
    public ConcurrentQueue<JsonElement> Received { get; } = new();
    public bool IsOpen { get; private set; } = true;

    public Task SendAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        Received.Enqueue(doc.RootElement.Clone());
        return Task.CompletedTask;
    }

    public void Push(object message) => _toBridge.Writer.TryWrite(JsonSerializer.Serialize(message));

    public void PushAudio(short[] pcm24k)
    {
        var bytes = new byte[pcm24k.Length * 2];
        Buffer.BlockCopy(pcm24k, 0, bytes, 0, bytes.Length);
        Push(new { type = "response.output_audio.delta", delta = Convert.ToBase64String(bytes) });
    }

    public Task<JsonElement> WaitForAsync(string type, Func<JsonElement, bool>? match = null) =>
        Wait.ForAsync(Received, e => e.GetProperty("type").GetString() == type && (match?.Invoke(e) ?? true), $"model never received {type}");

    public int CountOf(string type) => Received.Count(e => e.GetProperty("type").GetString() == type);

    public async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (await _toBridge.Reader.WaitToReadAsync(ct))
            while (_toBridge.Reader.TryRead(out var message))
                yield return message;
    }

    public Task CloseAsync(string reason, CancellationToken ct)
    {
        IsOpen = false;
        _toBridge.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        _toBridge.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Plays Twilio's side of a Media Streams call against the in-memory test server.</summary>
public sealed class TwilioCall : IAsyncDisposable
{
    private readonly WebSocketMessageChannel _socket;
    private readonly Task _reader;
    public ConcurrentQueue<JsonElement> Received { get; } = new();
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string StreamSid { get; } = "MZ" + Guid.NewGuid().ToString("N");
    public string CallSid { get; } = "CA" + Guid.NewGuid().ToString("N");

    private TwilioCall(WebSocketMessageChannel socket)
    {
        _socket = socket;
        _reader = Task.Run(async () =>
        {
            await foreach (var message in _socket.ReadAsync(CancellationToken.None))
            {
                using var doc = JsonDocument.Parse(message);
                Received.Enqueue(doc.RootElement.Clone());
            }
            Closed.TrySetResult();
        });
    }

    public static async Task<TwilioCall> ConnectAsync(TestServer server)
    {
        var ws = await server.CreateWebSocketClient().ConnectAsync(new Uri(server.BaseAddress, "media"), CancellationToken.None);
        return new TwilioCall(new WebSocketMessageChannel(ws));
    }

    public Task StartAsync(CallTokens tokens, string from = "+13105550142", string direction = "inbound",
        string purpose = "", string? tokenOverride = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["t"] = tokenOverride ?? tokens.Mint(CallSid, direction, purpose),
            ["dir"] = direction,
            ["from"] = from,
        };
        if (purpose.Length > 0) parameters["purpose"] = purpose;
        return Send(new
        {
            @event = "start",
            streamSid = StreamSid,
            start = new { streamSid = StreamSid, callSid = CallSid, customParameters = parameters },
        });
    }

    public async Task SendAudioAsync(IEnumerable<short[]> frames)
    {
        foreach (var frame in frames)
        {
            var mu = new byte[frame.Length];
            MuLaw.Encode(frame, mu);
            await Send(new { @event = "media", streamSid = StreamSid, media = new { payload = Convert.ToBase64String(mu) } });
        }
    }

    public Task SendMarkAsync(string name) => Send(new { @event = "mark", streamSid = StreamSid, mark = new { name } });

    public Task SendStopAsync() => Send(new { @event = "stop", streamSid = StreamSid });

    public Task<JsonElement> WaitForAsync(string evt, Func<JsonElement, bool>? match = null) =>
        Wait.ForAsync(Received, e => e.GetProperty("event").GetString() == evt && (match?.Invoke(e) ?? true), $"Twilio never received {evt}");

    public async Task WaitClosedAsync(int ms = 5000)
    {
        if (await Task.WhenAny(Closed.Task, Task.Delay(ms)) != Closed.Task)
            throw new TimeoutException("the bridge never closed the call");
    }

    private Task Send(object message) => _socket.SendAsync(JsonSerializer.Serialize(message), CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _socket.CloseAsync("test done", CancellationToken.None);
        await Task.WhenAny(_reader, Task.Delay(1000));
        await _socket.DisposeAsync();
    }
}

public static class Wait
{
    public static async Task<JsonElement> ForAsync(ConcurrentQueue<JsonElement> queue, Func<JsonElement, bool> match, string failure, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var item in queue)
                if (match(item)) return item;
            await Task.Delay(10);
        }
        throw new TimeoutException(failure);
    }

    public static async Task UntilAsync(Func<bool> condition, string failure, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException(failure);
    }
}

public sealed class BridgeFactory : WebApplicationFactory<Program>
{
    public FakeRealtime Model { get; } = new();
    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Security:CallTokenSecret"] = "test-secret",
        ["Security:ApiKey"] = "test-api-key",
        ["Twilio:AccountSid"] = "AC_test",
        ["Twilio:AuthToken"] = "test-auth-token",
        ["Twilio:FromNumber"] = "+13237466888",
        ["Twilio:PublicBaseUrl"] = "https://voice.test",
        ["Twilio:ValidateSignatures"] = "true",
        ["Calls:BookingFlushTimeoutMs"] = "400",
        ["Calls:EndPlaybackTimeoutMs"] = "3000",
        ["Data:Directory"] = RepoPath("data"),
    };
    public Action<IServiceCollection>? ExtraServices { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IRealtimeConnector>(Model);
            ExtraServices?.Invoke(services);
        });
    }

    public CallTokens Tokens => Services.GetRequiredService<CallTokens>();
    public MetricsRegistry Metrics => Services.GetRequiredService<MetricsRegistry>();

    public static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VoiceAgent.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("repo root"), relative);
    }
}

internal static class Pcm
{
    public static short[] Tone24k(double hz, int samples, double peak = 8000) =>
        Enumerable.Range(0, samples).Select(n => (short)(peak * Math.Sin(2 * Math.PI * hz * n / 24000.0))).ToArray();

    public static JsonNode? Output(JsonElement functionCallOutputItem) =>
        JsonNode.Parse(functionCallOutputItem.GetProperty("item").GetProperty("output").GetString()!);
}
