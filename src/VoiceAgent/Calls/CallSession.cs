using System.Text.Json;
using System.Threading.Channels;
using VoiceAgent.Audio;
using VoiceAgent.Memory;
using VoiceAgent.Metrics;
using VoiceAgent.Realtime;
using VoiceAgent.Telephony;
using VoiceAgent.Tools;

namespace VoiceAgent.Calls;

/// <summary>
/// One phone call: a Twilio Media Streams socket bridged to a realtime speech-to-speech model.
///
/// The Node.js bridge this ports got serialized state for free from the event loop. In .NET
/// the two sockets are read on separate tasks and tools complete on the thread pool, so every
/// input becomes a <see cref="CallEvent"/> on a single channel with a single consumer. All call
/// state is touched by that consumer and nothing else, which removes the need for locks and
/// makes the ordering of barge-in, tool results and hang-up deterministic.
/// </summary>
public sealed class CallSession(
    IMessageChannel twilio,
    IRealtimeConnector connector,
    ToolRegistry tools,
    CallerMemoryStore memory,
    MetricsRegistry metrics,
    CallTokens tokens,
    AgentProfile agent,
    AppointmentBook appointments,
    RealtimeOptions realtime,
    CallOptions options,
    TimeProvider clock,
    ILogger<CallSession> log)
{
    private abstract record CallEvent;
    private sealed record TwilioText(string Json) : CallEvent;
    private sealed record ModelText(string Json) : CallEvent;
    private sealed record TwilioClosed : CallEvent;
    private sealed record ModelClosed : CallEvent;
    private sealed record TimerFired(string Kind, int Generation) : CallEvent;
    private sealed record ToolDone(string FunctionCallId, ToolResult Result) : CallEvent;

    private readonly Channel<CallEvent> _events = Channel.CreateUnbounded<CallEvent>(new() { SingleReader = true });
    private readonly SemaphoreSlim _toolGate = new(1, 1);
    private readonly List<string> _transcript = [];
    private readonly Dictionary<string, int> _callerLines = [];
    private readonly HashSet<string> _goodbyeItems = [];
    private readonly List<string> _turnMarks = [];
    private bool _firstAudioThisResponse;
    private int _responsesAtTurnStart;
    private readonly List<string> _pendingAudio = [];
    private CancellationToken _ct;

    // Media plumbing
    private readonly EnergyVad _turnVad = new();
    private readonly EnergyVad _bargeVad = new(new VadOptions { SnrThresholdDb = new VadOptions().SnrThresholdDb + options.BargeInExtraSnrDb });
    private readonly Upsampler8To24 _up = new();
    private readonly Downsampler24To8 _down = new();
    private readonly Pcm16ByteCarry _carry = new();
    private readonly List<short> _vadRemainder = [];

    // Call state
    private IMessageChannel? _model;
    private string? _streamSid;
    private string? _from;
    private ToolContext? _toolContext;
    private DateTimeOffset? _connectedAt;
    private bool _modelReady;
    private bool _closed;
    private bool _responseActive;
    private bool _cancelSent;
    private bool _agentAudioQueued;
    private int _responseCount;
    private string? _lastResponseMark;
    private string _agentTurn = "";

    // Ending
    private bool _endRequested;
    private bool _ending;
    private bool _wrapUpPending;
    private int _endFallbackGeneration;

    // Booking integrity
    private bool _bookingPromised;
    private bool _bookingCommitted;
    private bool _flushRequested;
    private bool _flushPending;
    private DateTimeOffset _flushDeadline;
    private bool _holdingForBooking;
    private string _holdReason = "";

    // Latency
    private bool _greetingMeasured;
    private DateTimeOffset? _lastVoicedAt;
    private DateTimeOffset? _turnEndedAt;
    private bool _awaitingReply;
    private DateTimeOffset? _bargeOnsetAt;

    public string CallId { get; } = Guid.NewGuid().ToString("N")[..8];
    public string? HangupReason { get; private set; }
    public BookingOutcome Outcome { get; private set; }
    public IReadOnlyList<string> Transcript => _transcript;

    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ct = cts.Token;
        _ = Pump(twilio, json => new TwilioText(json), new TwilioClosed());
        Schedule(TimeSpan.FromMilliseconds(options.StartTimeoutMs), "start-timeout");
        Schedule(TimeSpan.FromSeconds(options.MaxCallSeconds), "max-duration");
        Schedule(TimeSpan.FromSeconds(Math.Max(1, options.MaxCallSeconds - options.WrapUpLeadSeconds)), "wrap-up");

        try
        {
            while (!_closed && await _events.Reader.WaitToReadAsync(_ct))
            {
                while (!_closed && _events.Reader.TryRead(out var evt))
                    await HandleAsync(evt);
            }
        }
        catch (OperationCanceledException)
        {
            Hangup("cancelled");
        }
        finally
        {
            cts.Cancel();
            await twilio.CloseAsync(HangupReason ?? "done", CancellationToken.None);
            if (_model is not null)
            {
                await _model.CloseAsync("call ended", CancellationToken.None);
                await _model.DisposeAsync();
            }
        }
    }

    private async Task HandleAsync(CallEvent evt)
    {
        switch (evt)
        {
            case TwilioText t: await OnTwilioAsync(t.Json); break;
            case ModelText m: await OnModelAsync(m.Json); break;
            case TwilioClosed: await HangupWhenBookingSettledAsync("twilio socket closed"); break;
            case ModelClosed: Hangup("model socket closed"); break;
            case TimerFired f: await OnTimerAsync(f); break;
            case ToolDone d: await OnToolDoneAsync(d); break;
        }
    }

    // ---------------------------------------------------------------- Twilio leg

    private async Task OnTwilioAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        switch (Str(root, "event"))
        {
            case "start":
                await OnStartAsync(root);
                break;
            case "media":
                if (_streamSid is null || !root.TryGetProperty("media", out var media)) break;
                var payload = Str(media, "payload");
                if (payload is not null) await OnCallerAudioAsync(Convert.FromBase64String(payload));
                break;
            case "mark":
                string? name = root.TryGetProperty("mark", out var mark) ? Str(mark, "name") : null;
                if (name == "end-call" && _endRequested) await HangupWhenBookingSettledAsync("caller said goodbye");
                else if (name is not null && name == _lastResponseMark) _agentAudioQueued = false;
                break;
            case "stop":
                await HangupWhenBookingSettledAsync("caller hung up");
                break;
        }
    }

    private async Task OnStartAsync(JsonElement root)
    {
        var start = root.GetProperty("start");
        var p = start.TryGetProperty("customParameters", out var cp) ? cp : default;
        string? callSid = Str(start, "callSid");
        string direction = Str(p, "dir") ?? "inbound";
        string purpose = Str(p, "purpose") ?? "";

        if (!tokens.Verify(Str(p, "t"), callSid, direction, purpose))
        {
            metrics.Increment("streams_rejected");
            log.LogWarning("[{Call}] media stream rejected: bad or missing call token", CallId);
            Hangup("unauthorized media stream");
            return;
        }

        _streamSid = Str(start, "streamSid") ?? Str(root, "streamSid");
        _from = Str(p, "from");
        _connectedAt = clock.GetUtcNow();
        _toolContext = new ToolContext(CallId, _from);
        metrics.Increment("calls_started");

        string instructions = agent.BuildInstructions(direction, purpose, CallerMemoryStore.Preamble(memory.Find(_from)), appointments.LocalNow);
        try
        {
            _model = await connector.ConnectAsync(_ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            metrics.Increment("model_connect_failures");
            log.LogError(ex, "[{Call}] could not reach the realtime model", CallId);
            Hangup("model connect failed");
            return;
        }

        _ = Pump(_model, json => new ModelText(json), new ModelClosed());
        await SendModelAsync(RealtimeMessages.SessionUpdate(realtime, instructions, tools.Declarations(), realtime.IdleFollowupMs));
        log.LogInformation("[{Call}] stream {Sid} started ({Dir}{Purpose})", CallId, _streamSid, direction,
            purpose.Length > 0 ? ", " + purpose : "");
    }

    private async Task OnCallerAudioAsync(byte[] muLaw)
    {
        var pcm8 = new short[muLaw.Length];
        MuLaw.Decode(muLaw, pcm8);

        _vadRemainder.AddRange(pcm8);
        int frame = 160;
        while (_vadRemainder.Count >= frame)
        {
            var samples = _vadRemainder.GetRange(0, frame).ToArray();
            _vadRemainder.RemoveRange(0, frame);
            await RunVadAsync(samples);
        }

        string appended = Convert.ToBase64String(ToBytes(_up.Process(pcm8)));
        if (_modelReady) await SendModelAsync(RealtimeMessages.AppendAudio(appended));
        else if (_pendingAudio.Count < options.MaxPendingFrames) _pendingAudio.Add(appended);
    }

    private async Task RunVadAsync(short[] frame)
    {
        var now = clock.GetUtcNow();

        var turn = _turnVad.Process(frame);
        if (_turnVad.LastFrameVoiced) _lastVoicedAt = now;
        if (turn == VadEvent.SpeechStarted)
        {
            _awaitingReply = false;
            _responsesAtTurnStart = _responseCount;
        }
        if (turn == VadEvent.SpeechStopped)
        {
            _turnEndedAt = _lastVoicedAt;
            _awaitingReply = true;
        }

        bool wasVoiced = _bargeVad.LastFrameVoiced;
        var barge = _bargeVad.Process(frame);
        if (_bargeVad.LastFrameVoiced && !wasVoiced) _bargeOnsetAt = now;
        if (barge == VadEvent.SpeechStarted && options.BargeIn != BargeInMode.Server && _agentAudioQueued)
            await BargeInAsync("local", _bargeOnsetAt);
    }

    // ---------------------------------------------------------------- Model leg

    private async Task OnModelAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var ev = doc.RootElement;
        switch (Str(ev, "type"))
        {
            case "session.updated" when !_modelReady:
                _modelReady = true;
                foreach (var frame in _pendingAudio) await SendModelAsync(RealtimeMessages.AppendAudio(frame));
                _pendingAudio.Clear();
                await SendModelAsync(RealtimeMessages.ResponseCreate()); // the agent speaks first
                break;

            case "input_audio_buffer.speech_started":
                _responsesAtTurnStart = _responseCount;
                // interrupt_response=true means the provider cancels its own response here, so the
                // server path only has to silence Twilio. Sending response.cancel as well races the
                // provider's cancel and fails with "no active response" (seen on the live call).
                if (options.BargeIn != BargeInMode.Local && _agentAudioQueued)
                    await BargeInAsync("server", null, cancelResponse: false);
                await ReengageIfEndingAsync();
                break;

            case "input_audio_buffer.speech_stopped":
                MarkTurn("speech_stopped");
                break;

            case "input_audio_buffer.committed":
                MarkTurn("committed");
                // The deadline asks "has any reply been created since the caller began this turn",
                // not "is one active right now" and not "since the commit": on xAI the reply is
                // created ~300 ms BEFORE the commit (measured), and a short reply can finish inside
                // the window. Either wrong question makes the agent answer twice.
                Schedule(TimeSpan.FromMilliseconds(options.ForceResponseAfterCommitMs), "force-response");
                break;

            case "response.created":
                _responseActive = true;
                _cancelSent = false;
                _responseCount++;
                _firstAudioThisResponse = true;
                MarkTurn("response_created");
                break;

            case "response.output_audio.delta" or "response.audio.delta":
                if (Str(ev, "delta") is { } delta) await OnAgentAudioAsync(delta);
                break;

            case "response.output_audio_transcript.delta" or "response.audio_transcript.delta":
                _agentTurn += Str(ev, "delta");
                break;

            // xAI re-sends the transcript for one utterance several times as it firms up
            // ("Hi there." -> "Hi there, how much is a" -> the full question), and grok-transcribe
            // uses ".updated" for the same cumulative text. Both are keyed to one item.
            case "conversation.item.input_audio_transcription.completed" or "conversation.item.input_audio_transcription.updated":
                if (Str(ev, "transcript") is { Length: > 0 } heard)
                    await OnCallerTranscriptAsync(Str(ev, "item_id") ?? $"turn-{_responseCount}", heard);
                break;

            case "response.done":
                await OnResponseDoneAsync();
                break;

            case "response.cancelled":
                _responseActive = false;
                break;

            case "response.function_call_arguments.done":
                MarkTurn("tool_requested");
                StartTool(Str(ev, "call_id"), Str(ev, "name"), Str(ev, "arguments"));
                break;

            case "error":
                metrics.Increment("model_errors");
                log.LogWarning("[{Call}] model error: {Error}", CallId, Truncate(json, 400));
                break;
        }
    }

    private async Task OnAgentAudioAsync(string base64Pcm24k)
    {
        if (_streamSid is null) return;
        var pcm24 = _carry.Push(Convert.FromBase64String(base64Pcm24k));
        var pcm8 = _down.Process(pcm24);
        if (pcm8.Length == 0) return;

        var muLaw = new byte[pcm8.Length];
        MuLaw.Encode(pcm8, muLaw);
        await SendTwilioAsync(TwilioMessages.Media(_streamSid, Convert.ToBase64String(muLaw)));
        _agentAudioQueued = true;

        if (_firstAudioThisResponse)
        {
            _firstAudioThisResponse = false;
            MarkTurn("first_audio");
            FlushTurnTimeline();
        }

        var now = clock.GetUtcNow();
        if (!_greetingMeasured && _connectedAt is { } started)
        {
            _greetingMeasured = true;
            metrics.Observe(LatencyKind.GreetingFirstAudio, (now - started).TotalMilliseconds);
        }
        else if (_awaitingReply && _turnEndedAt is { } ended)
        {
            _awaitingReply = false;
            metrics.Observe(LatencyKind.TurnResponse, (now - ended).TotalMilliseconds);
        }
    }

    private async Task OnCallerTranscriptAsync(string itemId, string heard)
    {
        if (_callerLines.TryGetValue(itemId, out int index))
        {
            if (_transcript[index] == "caller: " + heard) return; // a repeat, not new information
            _transcript[index] = "caller: " + heard;
        }
        else
        {
            _callerLines[itemId] = _transcript.Count;
            _transcript.Add("caller: " + heard);
        }
        log.LogInformation("[{Call}] caller: {Text}", CallId, Truncate(heard, 200));

        // One goodbye per utterance, however many times its transcript is revised.
        if (_ending || _goodbyeItems.Contains(itemId) || !CallerPhrases.IsGoodbye(heard)) return;
        _goodbyeItems.Add(itemId);

        log.LogInformation("[{Call}] caller goodbye; ending after the closing reply", CallId);
        _endRequested = true;
        await SendModelAsync(RealtimeMessages.IdleFollowup(realtime, null)); // no "still there?" during the hang-up
        await MaybeFlushBookingAsync("caller goodbye");
    }

    private async Task OnResponseDoneAsync()
    {
        _responseActive = false;

        if (_streamSid is not null && _agentAudioQueued)
        {
            _lastResponseMark = $"resp-{_responseCount}";
            await SendTwilioAsync(TwilioMessages.Mark(_streamSid, _lastResponseMark));
        }

        string turn = _agentTurn.Trim();
        _agentTurn = "";
        if (turn.Length > 0)
        {
            _transcript.Add("agent: " + turn);
            log.LogInformation("[{Call}] agent: {Text}", CallId, Truncate(turn, 200));
            if (!_bookingPromised && BookingIntegrity.DetectsCommitment(turn))
            {
                _bookingPromised = true;
                log.LogInformation("[{Call}] booking promised in speech", CallId);
            }
        }

        if (_wrapUpPending || _flushPending)
        {
            _wrapUpPending = _flushPending = false;
            await SendModelAsync(RealtimeMessages.ResponseCreate());
            return;
        }

        // The finished turn IS the closing line. Mark it; Twilio echoes the mark when the audio
        // has actually played, and only then does the call end. Never cut off mid-sentence.
        if (_endRequested && !_ending)
        {
            _ending = true;
            if (_streamSid is not null)
            {
                await SendTwilioAsync(TwilioMessages.Mark(_streamSid, "end-call"));
                Schedule(TimeSpan.FromMilliseconds(options.EndPlaybackTimeoutMs), "end-fallback", ++_endFallbackGeneration);
            }
            else
            {
                await HangupWhenBookingSettledAsync("caller said goodbye");
            }
        }
    }

    private async Task BargeInAsync(string source, DateTimeOffset? onsetAt, bool cancelResponse = true)
    {
        _responsesAtTurnStart = _responseCount;
        if (_streamSid is not null && _agentAudioQueued)
        {
            await SendTwilioAsync(TwilioMessages.Clear(_streamSid));
            _agentAudioQueued = false;
            metrics.Increment($"barge_in_{source}");
            if (onsetAt is { } onset)
                metrics.Observe(LatencyKind.BargeInClear, (clock.GetUtcNow() - onset).TotalMilliseconds);
        }
        if (cancelResponse && _responseActive && !_cancelSent)
        {
            _cancelSent = true;
            await SendModelAsync(RealtimeMessages.ResponseCancel());
        }
        await ReengageIfEndingAsync();
    }

    /// <summary>"Bye... oh wait, one more thing" keeps the line open.</summary>
    private async Task ReengageIfEndingAsync()
    {
        if (!_endRequested && !_ending) return;
        _endRequested = _ending = false;
        _endFallbackGeneration++;
        await SendModelAsync(RealtimeMessages.IdleFollowup(realtime, realtime.IdleFollowupMs));
    }

    // ---------------------------------------------------------------- Tools

    private void StartTool(string? functionCallId, string? name, string? arguments)
    {
        if (functionCallId is null || name is null || _toolContext is null) return;
        var context = _toolContext;
        log.LogInformation("[{Call}] tool call {Tool}", CallId, name);

        // Off the call loop so audio keeps flowing while a tool waits on a network hop; one at a
        // time per call because tools share the call's ToolContext.
        _ = Task.Run(async () =>
        {
            await _toolGate.WaitAsync(_ct);
            try
            {
                var result = await tools.DispatchAsync(name, arguments, context, _ct);
                _events.Writer.TryWrite(new ToolDone(functionCallId, result));
            }
            finally
            {
                _toolGate.Release();
            }
        }, _ct);
    }

    private async Task OnToolDoneAsync(ToolDone done)
    {
        var r = done.Result;
        log.LogInformation("[{Call}] tool {Tool} -> {Result} ({Ms:F1} ms)", CallId, r.Name,
            Truncate(r.Output.ToJsonString(), 240), r.Elapsed.TotalMilliseconds);
        metrics.Observe(LatencyKind.Tool, r.Elapsed.TotalMilliseconds);
        metrics.Increment(r.Failed ? $"tool_{r.Name}_failed" : $"tool_{r.Name}_ok");

        if (BookingIntegrity.IsBookingTool(r.Name))
        {
            if (BookingIntegrity.IsBookingSuccess(r.Output)) _bookingCommitted = true;
            else _bookingPromised = true; // an attempted booking is a promise to the caller
        }

        if (!_closed && _model is { IsOpen: true })
        {
            await SendModelAsync(RealtimeMessages.FunctionCallOutput(done.FunctionCallId, r.Output));
            await SendModelAsync(RealtimeMessages.ResponseCreate());
        }

        if (_holdingForBooking && _bookingCommitted) Hangup($"{_holdReason} (booking flushed)");
    }

    // ---------------------------------------------------------------- Booking integrity + ending

    private async Task MaybeFlushBookingAsync(string why)
    {
        if (!BookingIntegrity.ShouldFlush(_bookingPromised, _bookingCommitted, _flushRequested)) return;
        if (_closed || _model is not { IsOpen: true }) return;

        _flushRequested = true;
        _flushDeadline = clock.GetUtcNow().AddMilliseconds(options.BookingFlushTimeoutMs);
        log.LogWarning("[{Call}] booking promised but never committed ({Why}); forcing the tool call", CallId, why);
        await SendModelAsync(RealtimeMessages.SystemMessage(BookingIntegrity.FlushPrompt));
        if (!_responseActive) await SendModelAsync(RealtimeMessages.ResponseCreate());
        else _flushPending = true;
    }

    /// <summary>Teardown that will not drop a booking: waits, bounded, for an in-flight flush.</summary>
    private async Task HangupWhenBookingSettledAsync(string why)
    {
        if (_closed) return;
        await MaybeFlushBookingAsync(why); // a promise made in the closing turn itself lands here
        var now = clock.GetUtcNow();
        bool outstanding = _flushRequested && !_bookingCommitted && now < _flushDeadline;
        if (!outstanding)
        {
            Hangup(why);
            return;
        }
        if (_holdingForBooking) return;
        _holdingForBooking = true;
        _holdReason = why;
        log.LogInformation("[{Call}] holding teardown ({Why}) for the booking flush", CallId, why);
        Schedule(_flushDeadline - now, "booking-deadline");
    }

    private async Task OnTimerAsync(TimerFired timer)
    {
        switch (timer.Kind)
        {
            case "start-timeout" when _connectedAt is null:
                Hangup("no verified start event");
                break;
            case "max-duration":
                Hangup("max call duration");
                break;
            case "wrap-up" when _model is { IsOpen: true }:
                await SendModelAsync(RealtimeMessages.SystemMessage(
                    $"[Time is up on this call. In your next utterance deliver a warm closing on behalf of {agent.BusinessName}: " +
                    "thank the caller and say goodbye in under 10 seconds. The line disconnects shortly.]"));
                if (!_responseActive) await SendModelAsync(RealtimeMessages.ResponseCreate());
                else _wrapUpPending = true;
                _endRequested = true;
                await SendModelAsync(RealtimeMessages.IdleFollowup(realtime, null));
                await MaybeFlushBookingAsync("wrap-up window");
                break;
            case "force-response" when _responseCount == _responsesAtTurnStart && !_responseActive && _model is { IsOpen: true }:
                metrics.Increment("forced_responses");
                log.LogInformation("[{Call}] no reply after the turn was committed; forcing one", CallId);
                await SendModelAsync(RealtimeMessages.ResponseCreate());
                break;
            case "end-fallback" when timer.Generation == _endFallbackGeneration && _endRequested:
                await HangupWhenBookingSettledAsync("goodbye (playback timeout)");
                break;
            case "booking-deadline" when _holdingForBooking:
                Hangup($"{_holdReason} (booking flush timed out)");
                break;
        }
    }

    private void Hangup(string why)
    {
        if (_closed) return;
        _closed = true;
        HangupReason = why;
        Outcome = BookingIntegrity.Outcome(_bookingPromised, _bookingCommitted, _flushRequested);

        metrics.Increment("calls_ended");
        if (Outcome != BookingOutcome.None) metrics.Increment($"booking_{Outcome}");
        if (_toolContext is not null)
        {
            try
            {
                memory.Record(_from, _toolContext.CallerName, _toolContext.Outcomes, clock.GetUtcNow());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Losing a memory write must never stop a call from tearing down.
                metrics.Increment("memory_write_failures");
                log.LogError(ex, "[{Call}] caller memory could not be saved", CallId);
            }
        }

        double seconds = _connectedAt is { } c ? (clock.GetUtcNow() - c).TotalSeconds : 0;
        log.LogInformation("[{Call}] hangup: {Why} after {Seconds:F1}s, booking {Outcome}", CallId, why, seconds, Outcome);
    }

    // ---------------------------------------------------------------- Turn timeline

    /// <summary>
    /// Where a reply's latency goes, measured from the caller's last voiced frame at the bridge:
    /// the model's end-of-turn window (speech_stopped, committed), its time to start a response,
    /// any tool round trip, and first audio. One log line per reply.
    /// </summary>
    private void MarkTurn(string point)
    {
        if (_lastVoicedAt is not { } voiced || _turnMarks.Count > 12) return;
        _turnMarks.Add($"{point}=+{(clock.GetUtcNow() - voiced).TotalMilliseconds:F0}");
    }

    private void FlushTurnTimeline()
    {
        if (_turnMarks.Count > 1)
            log.LogInformation("[{Call}] turn timeline, ms after caller's last voiced frame: {Marks}", CallId, string.Join(" ", _turnMarks));
        _turnMarks.Clear();
    }

    // ---------------------------------------------------------------- Plumbing

    private Task Pump(IMessageChannel channel, Func<string, CallEvent> wrap, CallEvent onClose) => Task.Run(async () =>
    {
        try
        {
            await foreach (var message in channel.ReadAsync(_ct))
                _events.Writer.TryWrite(wrap(message));
        }
        catch (OperationCanceledException) { }
        finally
        {
            _events.Writer.TryWrite(onClose);
        }
    });

    private void Schedule(TimeSpan delay, string kind, int generation = 0)
    {
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        _ = Task.Delay(delay, clock, _ct).ContinueWith(
            t => { if (!t.IsCanceled) _events.Writer.TryWrite(new TimerFired(kind, generation)); },
            TaskScheduler.Default);
    }

    private Task SendTwilioAsync(string json) => twilio.SendAsync(json, _ct);

    private Task SendModelAsync(string json) => _model is null ? Task.CompletedTask : _model.SendAsync(json, _ct);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static byte[] ToBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length); // little-endian on every .NET target
        return bytes;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
