using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VoiceAgent.Calls;

public enum BookingOutcome { None, Committed, CommittedViaFlush, PromisedNotCommitted }

/// <summary>
/// The model narrating a booking in speech and the booking tool actually running are two
/// different events, and a caller's goodbye can land between them. On the production line
/// that lost a real appointment: the agent said "you're all set for 3 PM tomorrow", the caller
/// hung up, and nothing was ever written to the calendar.
///
/// So before teardown, if the agent promised a booking that no tool committed, a system turn
/// tells it to call the tool now, and teardown waits (bounded) for the result. The trigger is
/// biased toward false negatives, because a flush can create a real appointment: it matches
/// commitments ("I've booked you") and never offers ("would you like me to book").
/// </summary>
public static partial class BookingIntegrity
{
    public const string FlushPrompt =
        "[BOOKING INTEGRITY CHECK — the call is ending. You told the caller their appointment was booked, " +
        "but the booking tool has not completed successfully, so NO appointment exists yet. " +
        "If, and only if, the caller agreed to a specific date and time AND you have already collected the " +
        "details the booking tool requires, call the booking tool NOW, in this turn, before anything else. " +
        "Do not ask the caller new questions. Do not invent a name, phone number, or time. If you do not already " +
        "have what the tool requires, do not call it. Keep anything you say to one short closing sentence.]";

    private static readonly HashSet<string> BookingTools = ["bookappointment", "book_appointment", "bookmeeting", "schedulemeeting"];

    public static bool IsBookingTool(string name) =>
        BookingTools.Contains(name.ToLowerInvariant().Replace(" ", "").Replace("-", ""));

    /// <summary>Anything not positively readable as success counts as not committed.</summary>
    public static bool IsBookingSuccess(JsonNode? result)
    {
        if (result is not JsonObject o) return false;
        if (o.ContainsKey("error")) return false;
        if (o["success"] is JsonValue s && s.TryGetValue<bool>(out var ok)) return ok;
        return o["eventId"] is JsonValue e && e.TryGetValue<string>(out _);
    }

    public static bool DetectsCommitment(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        // Per sentence, so an offer in one sentence can't mask a commitment in the next.
        return SentenceSplit().Split(text).Any(s => !Offer().IsMatch(s) && Commitment().IsMatch(s));
    }

    public static bool ShouldFlush(bool promised, bool committed, bool flushRequested) =>
        promised && !committed && !flushRequested;

    public static BookingOutcome Outcome(bool promised, bool committed, bool flushRequested) =>
        committed
            ? (flushRequested ? BookingOutcome.CommittedViaFlush : BookingOutcome.Committed)
            : (promised ? BookingOutcome.PromisedNotCommitted : BookingOutcome.None);

    [GeneratedRegex(@"(?<=[.?!])\s+")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(
        @"\bi(?:'ve| have)?\s+(?:just\s+|gone ahead and\s+)?(?:booked|scheduled|set up|put you down|got you down|penciled you in)\b" +
        @"|\bi(?:'ll| will|'m going to| am going to|'m)\s+(?:go ahead and\s+)?(?:book(?:ing)?|schedul(?:e|ing)|lock(?:ing)?)\b" +
        @"|\blet me (?:go ahead and )?(?:book|schedule|lock)\b" +
        @"|\byou(?:'re| are)\s+(?:all\s+)?(?:booked|scheduled|set(?: for| up)?|on (?:the|his|her|their) calendar)\b" +
        @"|\b(?:that'?s|it'?s|you'?re)\s+(?:now\s+)?(?:on (?:the|his|her|their) calendar|on the books|locked in|confirmed)\b" +
        @"|\bwe(?:'ve| have)\s+(?:got )?you\s+(?:down|booked|scheduled)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Commitment();

    [GeneratedRegex(
        @"\b(?:would you like|do you want|shall i|should i|want me to|i can|i could|i'd be happy to|happy to|i am able to|i'm able to)\b[^.?!]{0,60}\b(?:book|schedule)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Offer();
}

public static partial class CallerPhrases
{
    /// <summary>
    /// Closing phrases, tuned on the live line to avoid firing mid-call. "That's all" only counts
    /// when it ends the thought: in a live call through this bridge, "Yes, that's all correct",
    /// the caller confirming a read-back, matched the older pattern and hung the call up a turn early.
    /// </summary>
    public static bool IsGoodbye(string transcript) => Goodbye().IsMatch(transcript);

    [GeneratedRegex(
        @"(good\s?bye|\bbye\b|have a (nice|good|great) (day|one|evening|night)|that'?s all(?: i need| for (?:me|now))?(?=\s*(?:[.!,;]|$|thanks|thank you))|that'?s everything(?=\s*(?:[.!,;]|$|thanks|thank you))|that'?s it for (me|now)|i'?m all set|we'?re all set|nothing else( for (me|now))?|talk (to you )?(soon|later)|take care|appreciate your time)",
        RegexOptions.IgnoreCase)]
    private static partial Regex Goodbye();
}
