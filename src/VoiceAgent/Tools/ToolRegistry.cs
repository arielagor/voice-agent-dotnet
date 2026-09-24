using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceAgent.Tools;

/// <summary>Per-call state a tool may read or write. Lives exactly as long as the call.</summary>
public sealed class ToolContext(string callId, string? callerNumber)
{
    public string CallId { get; } = callId;
    public string? CallerNumber { get; } = callerNumber;
    public HashSet<string> VerifiedAccounts { get; } = new(StringComparer.Ordinal);
    public int FailedVerifications { get; set; }
    public string? CallerName { get; set; }
    public List<string> Outcomes { get; } = [];

    /// <summary>The call so far ("agent: ..." / "caller: ..."), snapshotted when a tool is dispatched.</summary>
    public IReadOnlyList<string> Transcript { get; set; } = [];
}

public interface IVoiceTool
{
    string Name { get; }
    string Description { get; }
    JsonObject Parameters { get; }
    Task<JsonNode> InvokeAsync(JsonElement args, ToolContext context, CancellationToken ct);
}

public sealed record ToolResult(string Name, JsonNode Output, TimeSpan Elapsed, bool Failed);

/// <summary>
/// The agent's tool surface: declared to the model once per session, dispatched by name.
/// Every failure mode comes back to the model as an {"error": ...} object it can talk its
/// way around ("I couldn't find that slot, would 10:30 work?") instead of an exception that
/// kills the call.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IVoiceTool> _tools;

    public ToolRegistry(IEnumerable<IVoiceTool> tools) =>
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public IReadOnlyCollection<string> Names => _tools.Keys;

    public JsonArray Declarations() =>
        new(_tools.Values.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["parameters"] = t.Parameters.DeepClone(),
        }).ToArray());

    public async Task<ToolResult> DispatchAsync(string name, string? argumentsJson, ToolContext context, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        if (!_tools.TryGetValue(name, out var tool))
            return new ToolResult(name, Error($"unknown tool '{name}'"), watch.Elapsed, true);

        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            args = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new ToolResult(name, Error("arguments were not valid JSON"), watch.Elapsed, true);
        }

        var missing = MissingRequired(tool.Parameters, args);
        if (missing.Count > 0)
            return new ToolResult(name, Error($"missing required: {string.Join(", ", missing)}"), watch.Elapsed, true);

        try
        {
            var output = await tool.InvokeAsync(args, context, ct);
            bool failed = output is JsonObject o && o.ContainsKey("error");
            return new ToolResult(name, output, watch.Elapsed, failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolResult(name, Error($"{name} failed: {ex.Message}"), watch.Elapsed, true);
        }
    }

    public static JsonObject Error(string message) => new() { ["error"] = message };

    private static List<string> MissingRequired(JsonObject schema, JsonElement args)
    {
        var missing = new List<string>();
        if (schema["required"] is not JsonArray required) return missing;
        foreach (var item in required)
        {
            string key = item!.GetValue<string>();
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
                missing.Add(key);
        }
        return missing;
    }
}

internal static class JsonArgs
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim()
            : null;

    public static decimal? Dec(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString()?.Trim('$', ' '), out d)) return d;
        return null;
    }
}
