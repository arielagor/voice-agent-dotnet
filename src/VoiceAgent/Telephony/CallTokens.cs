using System.Security.Cryptography;
using System.Text;

namespace VoiceAgent.Telephony;

/// <summary>
/// The media WebSocket is publicly reachable and arms real tools, so it must not accept a
/// stream it did not hand out. Twilio strips query strings from a Stream URL, so identity
/// cannot ride on the URL: the signed webhook mints a token bound to the CallSid, TwiML passes
/// it as a Stream parameter, and the socket verifies it on the "start" event.
/// </summary>
public sealed class CallTokens(string secret)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(
        string.IsNullOrWhiteSpace(secret) ? throw new ArgumentException("Call token secret is required.") : secret);

    /// <summary>Binds the call, its direction, and (outbound) its purpose, so none can be swapped.</summary>
    public string Mint(string callSid, string direction, string purpose = "")
    {
        using var hmac = new HMACSHA256(_key);
        var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{callSid}|{direction}|{purpose}"));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public bool Verify(string? token, string? callSid, string? direction, string? purpose = "")
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(callSid) || string.IsNullOrEmpty(direction))
            return false;
        var expected = Encoding.ASCII.GetBytes(Mint(callSid, direction, purpose ?? ""));
        var provided = Encoding.ASCII.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
