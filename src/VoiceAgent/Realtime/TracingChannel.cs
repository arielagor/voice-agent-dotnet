using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace VoiceAgent.Realtime;

/// <summary>
/// Writes every provider event, both directions, to a JSONL file with audio payloads elided.
/// For discovering what an under-documented API actually sends, and for proving what a call did.
/// Enabled with Realtime:TracePath; off by default because transcripts are personal data.
///
/// Writes go through a queue drained by one background task. The first version appended to the
/// file synchronously per event, ~38 ms each on Windows, at 50 audio frames a second: the call
/// loop fell 3.5 s behind and the tracer inflated the very latencies it was there to explain.
/// </summary>
public sealed class TracingChannel : IMessageChannel
{
    private readonly IMessageChannel _inner;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly Task _writer;
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;

    public TracingChannel(IMessageChannel inner, string path)
    {
        _inner = inner;
        _writer = Task.Run(async () =>
        {
            await using var file = new StreamWriter(path, append: true);
            await foreach (var line in _lines.Reader.ReadAllAsync())
            {
                await file.WriteLineAsync(line);
                if (_lines.Reader.Count == 0) await file.FlushAsync();
            }
        });
    }

    public bool IsOpen => _inner.IsOpen;

    public Task SendAsync(string json, CancellationToken ct)
    {
        Enqueue("out", json);
        return _inner.SendAsync(json, ct);
    }

    public async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var message in _inner.ReadAsync(ct))
        {
            Enqueue("in", message);
            yield return message;
        }
    }

    public Task CloseAsync(string reason, CancellationToken ct) => _inner.CloseAsync(reason, ct);

    public async ValueTask DisposeAsync()
    {
        _lines.Writer.TryComplete();
        await Task.WhenAny(_writer, Task.Delay(2000));
        await _inner.DisposeAsync();
    }

    private void Enqueue(string direction, string json)
    {
        double ms = Math.Round((DateTimeOffset.UtcNow - _start).TotalMilliseconds);
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { node = JsonValue.Create(json.Length > 200 ? json[..200] : json); }
        Elide(node);
        _lines.Writer.TryWrite(new JsonObject { ["ms"] = ms, ["dir"] = direction, ["msg"] = node }.ToJsonString());
    }

    /// <summary>Audio is replaced by its length so a trace stays readable and small.</summary>
    private static void Elide(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToList())
            {
                if (key is "audio" or "delta" or "data" or "payload" && obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 64)
                    obj[key] = $"<{s.Length} b64 chars>";
                else
                    Elide(obj[key]);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr) Elide(item);
        }
    }
}
