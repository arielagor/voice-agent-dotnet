using System.Globalization;
using VoiceAgent.Tools;

namespace VoiceAgent.Calls;

public sealed class CallOptions
{
    public int MaxCallSeconds { get; set; } = 600;
    public int WrapUpLeadSeconds { get; set; } = 45;
    public int StartTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// If the model commits the caller's turn but never starts a reply, force one. The first
    /// call on the production line went silent for exactly this reason.
    /// </summary>
    public int ForceResponseAfterCommitMs { get; set; } = 700;
    public int EndPlaybackTimeoutMs { get; set; } = 15_000;
    public int BookingFlushTimeoutMs { get; set; } = 12_000;
    public int MaxPendingFrames { get; set; } = 500;
    public BargeInMode BargeIn { get; set; } = BargeInMode.Both;

    /// <summary>Play the recorded-call notice, verbatim, before the model speaks.</summary>
    public bool RecordingNotice { get; set; } = true;

    /// <summary>
    /// Extra margin a local barge-in needs over the turn VAD. On speakerphones the agent's own
    /// voice leaks back into the caller's microphone; the higher bar keeps it from interrupting itself.
    /// </summary>
    public double BargeInExtraSnrDb { get; set; } = 6.0;
}

/// <summary>
/// Server: rely on the model's server-side VAD event, one network round trip after the caller
/// starts talking. Local: the bridge's own VAD clears Twilio's buffer within a few frames.
/// Both: whichever fires first; the other is a no-op.
/// </summary>
public enum BargeInMode { Server, Local, Both }

public sealed class AgentProfile(DemoBusiness business, string baseInstructions)
{
    public string BusinessName => business.BusinessName;

    public string BuildInstructions(string direction, string? purpose, string? memoryPreamble, DateTime localNow)
    {
        var parts = new List<string>
        {
            "PACING: Speak briskly and efficiently, like a sharp human rep who values the caller's time. " +
            "Keep each turn to one or two sentences. Skip filler.",
            direction == "outbound"
                ? $"CALL CONTEXT: This is an OUTBOUND call that YOU placed on behalf of {BusinessName}. Purpose: {Purpose(purpose)}. " +
                  "Do not say 'thanks for calling'. Ask for the account holder by first name only, and do not mention any " +
                  "balance, payment or account detail to anyone until verify_account succeeds. If the person is not the " +
                  "account holder, leave only a request to call back and end the call."
                : $"CALL CONTEXT: This is an INBOUND call; the caller dialed {BusinessName}. Greet with 'Thanks for calling' and ask how you can help.",
        };
        if (memoryPreamble is not null) parts.Add(memoryPreamble);
        parts.Add(baseInstructions.Replace("{{business}}", BusinessName));
        parts.Add(
            $"VOICE CHANNEL RULES — this is a real phone call for {BusinessName}:\n" +
            $"- Open with a short greeting that names {BusinessName} and says you are an AI assistant.\n" +
            "- Short sentences, one question at a time, no lists or markdown.\n" +
            "- Say numbers, prices, dates and times the way a person would.\n" +
            "- Read back names, phone numbers and appointment times before acting on them.\n" +
            "- Use your tools for anything you can act on. Never state a policy, price or account detail your tools did not return; offer to take a message instead.\n" +
            "- If the caller says goodbye, give one warm closing sentence and stop.");
        parts.Add($"CURRENT DATE AND TIME at the business: {localNow.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} ({business.TimeZone}).");
        return string.Join("\n\n", parts);
    }

    private static string Purpose(string? purpose) => purpose switch
    {
        "payment_reminder" => "a courtesy reminder about an upcoming or past-due loan payment; offer to record a promise to pay",
        "service_reminder" => "a reminder that the caller's vehicle is due for service; offer to book it",
        _ => "a follow-up from the dealership",
    };
}
