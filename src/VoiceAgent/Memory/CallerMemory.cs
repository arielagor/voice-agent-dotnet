using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace VoiceAgent.Memory;

public sealed record CallerProfile(string Number, string? Name, DateTimeOffset LastCallUtc, int CallCount, List<string> RecentOutcomes);

/// <summary>
/// Memory across calls, keyed by caller number. Within a call the realtime session holds the
/// conversation itself; this is what survives the hang-up: who they are and what happened
/// last time, so a returning caller is greeted by name and not asked to start over.
///
/// Outcomes come from tool results (a booking, a promise to pay), not from the model's
/// narration, so the memory never repeats a promise the system did not keep.
/// </summary>
public sealed class CallerMemoryStore(string? persistPath = null)
{
    private const int MaxOutcomes = 5;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ConcurrentDictionary<string, CallerProfile> _profiles = Load(persistPath);
    private readonly object _writeLock = new();

    public CallerProfile? Find(string? number) =>
        number is not null && _profiles.TryGetValue(Normalize(number), out var p) ? p : null;

    public void Record(string? number, string? name, IEnumerable<string> outcomes, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(number)) return;
        string key = Normalize(number);
        _profiles.AddOrUpdate(
            key,
            _ => new CallerProfile(key, name, nowUtc, 1, outcomes.TakeLast(MaxOutcomes).ToList()),
            (_, prior) => prior with
            {
                Name = name ?? prior.Name,
                LastCallUtc = nowUtc,
                CallCount = prior.CallCount + 1,
                RecentOutcomes = prior.RecentOutcomes.Concat(outcomes).TakeLast(MaxOutcomes).ToList(),
            });
        Persist();
    }

    /// <summary>The preamble injected into the session instructions for a returning caller.</summary>
    public static string? Preamble(CallerProfile? profile)
    {
        if (profile is null) return null;
        var lines = new List<string>
        {
            $"RETURNING CALLER: this number has called {profile.CallCount} time(s) before, last on " +
            $"{profile.LastCallUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.",
        };
        if (profile.Name is not null)
            lines.Add($"Their name on file is {profile.Name}. Greet them by name, then confirm it is them before sharing anything private.");
        if (profile.RecentOutcomes.Count > 0)
            lines.Add("What happened on recent calls (from system records): " + string.Join("; ", profile.RecentOutcomes) + ".");
        return string.Join(" ", lines);
    }

    /// <summary>E.164-ish key: "(775) 252-8333", "775.252.8333" and "+17752528333" are one caller.</summary>
    internal static string Normalize(string number)
    {
        string digits = new(number.Where(char.IsDigit).ToArray());
        return digits.Length == 10 ? "+1" + digits : "+" + digits;
    }

    private static ConcurrentDictionary<string, CallerProfile> Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new();
        var items = JsonSerializer.Deserialize<List<CallerProfile>>(File.ReadAllText(path), JsonOptions) ?? [];
        return new(items.ToDictionary(p => p.Number));
    }

    private void Persist()
    {
        if (persistPath is null) return;
        lock (_writeLock)
        {
            var tmp = persistPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_profiles.Values.OrderBy(p => p.Number).ToList(), JsonOptions));
            File.Move(tmp, persistPath, overwrite: true);
        }
    }
}
