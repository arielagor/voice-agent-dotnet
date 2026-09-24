using System.Text.Json.Nodes;

namespace VoiceAgent.Realtime;

/// <summary>Client events for an OpenAI-compatible realtime session (xAI's voice API speaks this shape).</summary>
public static class RealtimeMessages
{
    public static string SessionUpdate(RealtimeOptions options, string instructions, JsonArray tools, int? idleFollowupMs) =>
        IsOpenAi(options) ? OpenAiSessionUpdate(options, instructions, tools, idleFollowupMs) : XaiSessionUpdate(options, instructions, tools, idleFollowupMs);

    private static bool IsOpenAi(RealtimeOptions options) => options.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// OpenAI's GA shape: a typed session with everything audio under audio.input / audio.output.
    /// xAI accepts the older flat fields as well; OpenAI rejects them as unknown parameters.
    /// </summary>
    private static string OpenAiSessionUpdate(RealtimeOptions options, string instructions, JsonArray tools, int? idleFollowupMs) =>
        new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["type"] = "realtime",
                ["instructions"] = instructions,
                ["tools"] = tools,
                ["audio"] = new JsonObject
                {
                    ["input"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
                        ["transcription"] = new JsonObject { ["model"] = options.TranscriptionModel },
                        ["turn_detection"] = TurnDetection(options, idleFollowupMs),
                    },
                    ["output"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
                        ["voice"] = options.Voice,
                        ["speed"] = options.SpeechSpeed,
                    },
                },
            },
        }.ToJsonString();

    private static string XaiSessionUpdate(RealtimeOptions options, string instructions, JsonArray tools, int? idleFollowupMs) =>
        new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["voice"] = options.Voice,
                ["instructions"] = instructions,
                ["tools"] = tools,
                ["input_audio_transcription"] = new JsonObject { ["model"] = options.TranscriptionModel },
                ["turn_detection"] = TurnDetection(options, idleFollowupMs),
                ["audio"] = new JsonObject
                {
                    ["input"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
                        ["turn_detection"] = TurnDetection(options, null, includeIdleKey: false),
                    },
                    ["output"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
                        ["speed"] = options.SpeechSpeed,
                    },
                },
            },
        }.ToJsonString();

    /// <summary>
    /// Arms (a number) or disarms (null) the model's silence follow-up mid-call. The whole
    /// turn_detection object is re-sent, tuning included: a partial one would reset the
    /// end-of-turn window to the provider default halfway through the call.
    /// </summary>
    public static string IdleFollowup(RealtimeOptions options, int? idleFollowupMs) =>
        new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = IsOpenAi(options)
                ? new JsonObject
                {
                    ["type"] = "realtime",
                    ["audio"] = new JsonObject { ["input"] = new JsonObject { ["turn_detection"] = TurnDetection(options, idleFollowupMs) } },
                }
                : new JsonObject { ["turn_detection"] = TurnDetection(options, idleFollowupMs) },
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
    private static JsonObject TurnDetection(RealtimeOptions options, int? idleFollowupMs, bool includeIdleKey = true)
    {
        var td = new JsonObject
        {
            ["type"] = "server_vad",
            ["create_response"] = true,
            ["interrupt_response"] = true,
        };
        if (options.SilenceDurationMs is { } silence) td["silence_duration_ms"] = silence;
        if (options.VadThreshold is { } threshold) td["threshold"] = threshold;
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
