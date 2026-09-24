using System.Text.RegularExpressions;

namespace VoiceAgent.Compliance;

/// <summary>
/// Booking only after the agent has read the caller's number back and the caller has said yes,
/// enforced here rather than in the prompt. In the benchmark, one of the four models booked before
/// reading details back in both runs; an instruction is a request, this is a gate.
/// </summary>
public static partial class ReadBackGate
{
    public sealed record Verdict(bool Allowed, string? Reason);

    /// <param name="transcript">Lines of the call so far, each "agent: ..." or "caller: ...".</param>
    /// <param name="phone">The number the booking will be made under.</param>
    public static Verdict Check(IReadOnlyList<string> transcript, string phone)
    {
        string digits = new(phone.Where(char.IsDigit).ToArray());
        if (digits.Length < 7) return new(false, "a full callback number is required");
        string lastSeven = digits[^7..];

        int readBack = -1;
        for (int i = transcript.Count - 1; i >= 0; i--)
        {
            if (!transcript[i].StartsWith("agent:", StringComparison.Ordinal)) continue;
            if (SpokenDigits(transcript[i]).Contains(lastSeven, StringComparison.Ordinal))
            {
                readBack = i;
                break;
            }
        }
        if (readBack < 0)
            return new(false, "read the callback number back to the caller and get a yes before booking");

        bool confirmed = transcript.Skip(readBack + 1)
            .Any(l => l.StartsWith("caller:", StringComparison.Ordinal) && Affirmative().IsMatch(l) && !Negative().IsMatch(l));
        return confirmed
            ? new(true, null)
            : new(false, "the caller has not confirmed the read-back yet; ask and wait for a yes");
    }

    /// <summary>"three one oh, five five five" and "310-555" both become "310555".</summary>
    internal static string SpokenDigits(string text)
    {
        string lowered = NumberWord().Replace(text.ToLowerInvariant(), m => Words[m.Value].ToString());
        return new string(lowered.Where(char.IsDigit).ToArray());
    }

    private static readonly Dictionary<string, int> Words = new()
    {
        ["zero"] = 0, ["oh"] = 0, ["o"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4,
        ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
    };

    [GeneratedRegex(@"\b(zero|oh|o|one|two|three|four|five|six|seven|eight|nine)\b")]
    private static partial Regex NumberWord();

    [GeneratedRegex(@"\b(yes|yeah|yep|correct|that'?s right|right|sounds good|perfect|exactly)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Affirmative();

    [GeneratedRegex(@"\b(no|not|wrong|incorrect|actually)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Negative();
}
