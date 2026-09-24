using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;

namespace VoiceAgent.Telephony;

public sealed class TwilioOptions
{
    public string AccountSid { get; set; } = "";
    public string AuthToken { get; set; } = "";
    public string FromNumber { get; set; } = "";

    /// <summary>
    /// The public https origin Twilio calls (e.g. https://voice.example.com). Behind a proxy or a
    /// tunnel, the URL Kestrel sees is not the URL Twilio signed, so signatures are checked
    /// against this and the media WebSocket URL is built from it.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>Fail closed: only a local simulator run may turn this off.</summary>
    public bool ValidateSignatures { get; set; } = true;
}

public static class Twiml
{
    public static string ConnectStream(string websocketUrl, IReadOnlyDictionary<string, string> parameters)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response><Connect>");
        sb.Append("<Stream url=\"").Append(SecurityElement.Escape(websocketUrl)).Append("\">");
        foreach (var (name, value) in parameters)
            sb.Append("<Parameter name=\"").Append(SecurityElement.Escape(name))
              .Append("\" value=\"").Append(SecurityElement.Escape(value)).Append("\"/>");
        sb.Append("</Stream></Connect></Response>");
        return sb.ToString();
    }

    public static string Say(string text) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response><Say>{SecurityElement.Escape(text)}</Say><Hangup/></Response>";
}

/// <summary>The one Twilio REST call the agent needs: placing an outbound call.</summary>
public sealed class TwilioRestClient(HttpClient http, TwilioOptions options)
{
    public async Task<string> CreateCallAsync(string to, string twimlUrl, string statusCallbackUrl, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(options.AccountSid)}/Calls.json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["To"] = to,
                ["From"] = options.FromNumber,
                ["Url"] = twimlUrl,
                ["Method"] = "POST",
                ["StatusCallback"] = statusCallbackUrl,
                ["StatusCallbackEvent"] = "completed",
                ["MachineDetection"] = "Enable",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{options.AccountSid}:{options.AuthToken}")));

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Twilio returned {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("sid").GetString() ?? throw new InvalidDataException("Twilio response had no sid.");
    }
}
