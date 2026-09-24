using System.Collections.Concurrent;

namespace VoiceAgent.Compliance;

public sealed class OutboundPolicyOptions
{
    /// <summary>Attempts allowed per account in any rolling window (Reg F's presumption is 7 in 7 days).</summary>
    public int MaxAttemptsPerWindow { get; set; } = 7;
    public int WindowDays { get; set; } = 7;

    /// <summary>After a completed conversation, no further calls about the account for this long.</summary>
    public int QuietDaysAfterConversation { get; set; } = 7;

    /// <summary>Calling hours in the consumer's local time (the FDCPA's 8 a.m. to 9 p.m. convention).</summary>
    public int EarliestHour { get; set; } = 8;
    public int LatestHour { get; set; } = 21;
}

/// <summary>
/// Decides whether an outbound payment-reminder call may be placed at all, before any dial.
/// Encodes the frequency and time-of-day rules a lender's collection calls work under: a cap on
/// attempts per account in a rolling window, a quiet period after an actual conversation, and a
/// local-time calling window. The thresholds are configuration; a lender's compliance team owns
/// the numbers. Attempts are in-process here; a deployment keeps them in its system of record.
/// </summary>
public sealed class OutboundCallPolicy(OutboundPolicyOptions options, TimeProvider clock)
{
    public sealed record Decision(bool Allowed, string? Reason);

    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _attempts = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastConversation = new();

    public Decision Check(string accountId, TimeZoneInfo consumerZone)
    {
        var now = clock.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, consumerZone);
        if (local.Hour < options.EarliestHour || local.Hour >= options.LatestHour)
            return new(false, $"outside calling hours ({options.EarliestHour}:00 to {options.LatestHour}:00 consumer local time)");

        if (_lastConversation.TryGetValue(accountId, out var talked) && now - talked < TimeSpan.FromDays(options.QuietDaysAfterConversation))
            return new(false, $"spoke with the consumer {Math.Floor((now - talked).TotalDays)} day(s) ago; quiet period is {options.QuietDaysAfterConversation} days");

        int recent = Attempts(accountId).Count(t => now - t < TimeSpan.FromDays(options.WindowDays));
        if (recent >= options.MaxAttemptsPerWindow)
            return new(false, $"{recent} attempts in the last {options.WindowDays} days; the cap is {options.MaxAttemptsPerWindow}");

        return new(true, null);
    }

    public void RecordAttempt(string accountId)
    {
        var list = Attempts(accountId);
        lock (list) list.Add(clock.GetUtcNow());
    }

    /// <summary>A conversation is a verified exchange with the consumer, not a ring or a voicemail.</summary>
    public void RecordConversation(string accountId) => _lastConversation[accountId] = clock.GetUtcNow();

    public bool InQuietPeriod(string accountId) =>
        _lastConversation.TryGetValue(accountId, out var talked)
        && clock.GetUtcNow() - talked < TimeSpan.FromDays(options.QuietDaysAfterConversation);

    private List<DateTimeOffset> Attempts(string accountId) => _attempts.GetOrAdd(accountId, _ => []);
}
