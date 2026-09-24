using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceAgent.Realtime;

/// <summary>
/// Writes every provider event, both directions, to a JSONL file with audio payloads elided.
/// For discovering what an under-documented API actually sends, and for proving what a call did.
/// Enabled with Realtime:TracePath; off by default because transcripts are personal data.
/// </summary>
public sealed class TracingChannel(IMessageChannel inner, string path) : IMessageChannel
{
    private readonly object _lock = new();
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;

    public bool IsOpen => inner.IsOpen;

    public Task SendAsync(string json, CancellationToken ct)
    {
        Write("out", json);
        return inner.SendAsync(json, ct);
    }

    public async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var message in inner.ReadAsync(ct))
        {
            Write("in", message);
            yield return message;
        }
    }

    public Task CloseAsync(string reason, CancellationToken ct) => inner.CloseAsync(reason, ct);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private void Write(string direction, string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { node = JsonValue.Create(json.Length > 200 ? json[..200] : json); }
        Elide(node);
        var line = new JsonObject
        {
            ["ms"] = Math.Round((DateTimeOffset.UtcNow - _start).TotalMilliseconds),
            ["dir"] = direction,
            ["msg"] = node,
        }.ToJsonString();
        lock (_lock) File.AppendAllText(path, line + Environment.NewLine);
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
