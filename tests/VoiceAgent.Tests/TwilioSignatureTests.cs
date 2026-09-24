using VoiceAgent.Telephony;

namespace VoiceAgent.Tests;

/// <summary>
/// Expected signatures were produced by Twilio's official Node library (twilio 5.13.1,
/// getExpectedTwilioSignature), not by this code, so these tests check against an
/// independent reference. The generator script is tools/twilio-vectors.js.
/// </summary>
public class TwilioSignatureTests
{
    private static readonly Dictionary<string, string> DocsParams = new()
    {
        ["CallSid"] = "CA1234567890ABCDE",
        ["Caller"] = "+12349013030",
        ["Digits"] = "1234",
        ["From"] = "+12349013030",
        ["To"] = "+18005551212",
    };

    private static readonly Dictionary<string, string> CallParams = new()
    {
        ["AccountSid"] = "AC00000000000000000000000000000000",
        ["CallSid"] = "CA11111111111111111111111111111111",
        ["From"] = "+17752528333",
        ["To"] = "+13237466888",
        ["CallStatus"] = "ringing",
        ["Direction"] = "inbound",
    };

    private const string CallToken = "b5f1c0ffee0ddba11deadbeef0123456";

    [Fact]
    public void Matches_the_official_library_on_twilios_documented_example()
    {
        Assert.Equal("0/KCTR6DLpKmkAf8muzZqo1nDgQ=",
            TwilioSignature.Compute("12345", "https://mycompany.com/myapp.php?foo=1&bar=2", DocsParams));
    }

    [Fact]
    public void Matches_the_official_library_on_an_inbound_call_webhook()
    {
        Assert.Equal("5H+Mn75iGkoSfkFOnm2+LB1sC8A=",
            TwilioSignature.Compute(CallToken, "https://voice.example.com/voice/incoming", CallParams));
    }

    [Fact]
    public void Accepts_a_request_that_arrives_on_a_port_twilio_did_not_sign()
    {
        Assert.True(TwilioSignature.IsValid(CallToken, "https://voice.example.com:8443/voice/incoming",
            CallParams, "5H+Mn75iGkoSfkFOnm2+LB1sC8A="));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("5H+Mn75iGkoSfkFOnm2+LB1sC8B=")]
    public void Rejects_missing_or_forged_signatures(string? signature)
    {
        Assert.False(TwilioSignature.IsValid(CallToken, "https://voice.example.com/voice/incoming", CallParams, signature));
    }

    [Fact]
    public void Rejects_a_tampered_parameter()
    {
        var tampered = new Dictionary<string, string>(CallParams) { ["From"] = "+15555550100" };
        Assert.False(TwilioSignature.IsValid(CallToken, "https://voice.example.com/voice/incoming", tampered,
            "5H+Mn75iGkoSfkFOnm2+LB1sC8A="));
    }
}
