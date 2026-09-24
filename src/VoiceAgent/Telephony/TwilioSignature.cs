using System.Security.Cryptography;
using System.Text;

namespace VoiceAgent.Telephony;

/// <summary>
/// Validates X-Twilio-Signature: base64(HMAC-SHA1(authToken, url + sorted(key + value)...)).
///
/// Behind a proxy or tunnel on a non-standard port, Twilio can sign the URL without the port
/// even though the request arrives with it. A validator that only tries the literal URL then
/// rejects every real call with a 403, and the caller hears "an application error has
/// occurred". So the check also tries the URL with its port removed, and only then fails.
/// </summary>
public static class TwilioSignature
{
    public static string Compute(string authToken, string url, IEnumerable<KeyValuePair<string, string>> formParams)
    {
        var builder = new StringBuilder(url);
        foreach (var pair in formParams.OrderBy(p => p.Key, StringComparer.Ordinal))
            builder.Append(pair.Key).Append(pair.Value);

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public static bool IsValid(
        string authToken,
        string url,
        IEnumerable<KeyValuePair<string, string>> formParams,
        string? providedSignature)
    {
        if (string.IsNullOrEmpty(providedSignature) || string.IsNullOrEmpty(authToken))
            return false;

        var materialized = formParams.ToList();
        foreach (var candidate in CandidateUrls(url))
        {
            var expected = Encoding.ASCII.GetBytes(Compute(authToken, candidate, materialized));
            var provided = Encoding.ASCII.GetBytes(providedSignature);
            if (CryptographicOperations.FixedTimeEquals(expected, provided))
                return true;
        }
        return false;
    }

    internal static IEnumerable<string> CandidateUrls(string url)
    {
        yield return url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsDefaultPort)
        {
            var withoutPort = new UriBuilder(uri) { Port = -1 }.Uri.AbsoluteUri;
            if (!string.Equals(withoutPort, url, StringComparison.Ordinal))
                yield return withoutPort;
        }
    }
}
