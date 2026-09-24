using System.Text.Json.Nodes;

namespace VoiceAgent.Realtime;

/// <summary>Client events for an OpenAI-compatible realtime session (xAI's voice API speaks this shape).</summary>
public static class RealtimeMessages
{
    public static string SessionUpdate(RealtimeOptions options, string instructions, JsonArray tools, int? idleFollowupMs) =>
        new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["voice"] = options.Voice,
                ["instructions"] = instructions,
                ["tools"] = tools,
                ["input_audio_transcription"] = new JsonObject { ["model"] = options.TranscriptionModel },
                ["turn_detection"] = TurnDetection(idleFollowupMs),
                ["audio"] = new JsonObject
                {
                    ["input"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
                        ["turn_detection"] = TurnDetection(null, includeIdleKey: false),
                    },
                    ["output"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
                        ["speed"] = options.SpeechSpeed,
                    },
                },
            },
        }.ToJsonString();

    /// <summary>Arms (a number) or disarms (null) the model's silence follow-up mid-call.</summary>
    public static string IdleFollowup(int? idleFollowupMs) =>
        new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject { ["turn_detection"] = TurnDetection(idleFollowupMs) },
        }.ToJsonString();

    public static string AppendAudio(string base64Pcm24k) =>
        new JsonObject { ["type"] = "input_audio_buffer.append", ["audio"] = base64Pcm24k }.ToJsonString();

    public static string ResponseCreate() => """{"type":"response.create"}""";

    public static string ResponseCancel() => """{"type":"response.cancel"}""";

    public static string SystemMessage(string text) =>
        new JsonObject
        {
            ["type"] = "conversation.item.create",
            ["item"] = new JsonObject
            {
                ["type"] = "message",
                ["role"] = "system",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }),
            },
        }.ToJsonString();

    public static string FunctionCallOutput(string callId, JsonNode? output) =>
        new JsonObject
        {
            ["type"] = "conversation.item.create",
            ["item"] = new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = callId,
                ["output"] = output?.ToJsonString() ?? "null",
            },
        }.ToJsonString();

    /// <summary>An explicit null idle_timeout_ms is how the follow-up is disarmed, so it is sent, not omitted.</summary>
    private static JsonObject TurnDetection(int? idleFollowupMs, bool includeIdleKey = true)
    {
        var td = new JsonObject
        {
            ["type"] = "server_vad",
            ["create_response"] = true,
            ["interrupt_response"] = true,
        };
        if (includeIdleKey) td["idle_timeout_ms"] = idleFollowupMs;
        return td;
    }
}

/// <summary>Outbound messages on a Twilio Media Streams socket.</summary>
public static class TwilioMessages
{
    public static string Media(string streamSid, string base64MuLaw) =>
        new JsonObject
        {
            ["event"] = "media",
            ["streamSid"] = streamSid,
            ["media"] = new JsonObject { ["payload"] = base64MuLaw },
        }.ToJsonString();

    /// <summary>Drops every frame Twilio has buffered but not yet played. The barge-in primitive.</summary>
    public static string Clear(string streamSid) =>
        new JsonObject { ["event"] = "clear", ["streamSid"] = streamSid }.ToJsonString();

    /// <summary>Twilio echoes a mark back once all audio queued before it has finished playing.</summary>
    public static string Mark(string streamSid, string name) =>
        new JsonObject
        {
            ["event"] = "mark",
            ["streamSid"] = streamSid,
            ["mark"] = new JsonObject { ["name"] = name },
        }.ToJsonString();
}
