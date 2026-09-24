using System.Collections.Concurrent;

namespace VoiceAgent.Metrics;

/// <summary>
/// The latencies a caller actually feels, measured at the bridge. All of them exclude the
/// carrier leg (Twilio to the handset), which the bridge cannot see; they include everything
/// the bridge and the model add.
/// </summary>
public enum LatencyKind
{
    /// <summary>Stream start to the first greeting audio sent to Twilio.</summary>
    GreetingFirstAudio,

    /// <summary>
    /// Last voiced caller frame to the agent's first reply audio. Includes the model's own
    /// end-of-turn silence window, which is usually the largest single term.
    /// </summary>
    TurnResponse,

    /// <summary>First voiced frame of a barge-in to the Twilio "clear" that silences the agent.</summary>
    BargeInClear,

    /// <summary>Tool dispatch round trip, per call to a tool.</summary>
    Tool,
}

public sealed class MetricsRegistry
{
    private readonly ConcurrentDictionary<LatencyKind, ConcurrentQueue<double>> _samples = new();
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private const int MaxSamplesPerKind = 5000;

    public void Observe(LatencyKind kind, double milliseconds)
    {
        var q = _samples.GetOrAdd(kind, _ => new ConcurrentQueue<double>());
        q.Enqueue(milliseconds);
        while (q.Count > MaxSamplesPerKind && q.TryDequeue(out _)) { }
    }

    public void Increment(string counter) => _counters.AddOrUpdate(counter, 1, (_, v) => v + 1);

    public long Count(string counter) => _counters.GetValueOrDefault(counter);

    public IReadOnlyList<double> Samples(LatencyKind kind) =>
        _samples.TryGetValue(kind, out var q) ? q.ToArray() : [];

    public object Snapshot() => new
    {
        counters = _counters.OrderBy(c => c.Key).ToDictionary(c => c.Key, c => c.Value),
        latency_ms = Enum.GetValues<LatencyKind>().ToDictionary(
            k => k.ToString(),
            k => Summarize(Samples(k))),
    };

    public static object Summarize(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0) return new { n = 0 };
        var sorted = samples.OrderBy(v => v).ToArray();
        return new
        {
            n = sorted.Length,
            p50 = Math.Round(Percentile(sorted, 0.50), 1),
            p95 = Math.Round(Percentile(sorted, 0.95), 1),
            max = Math.Round(sorted[^1], 1),
        };
    }

    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1) return sorted[0];
        double rank = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
