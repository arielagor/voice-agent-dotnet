# 0001: A C#/.NET voice-agent bridge, and choosing its speech-to-speech model by measurement

Date: 2026-09-24. Status: accepted.

## Context

The production phone agents (the digital-twin line at +1 775 252-8333 and the Agor Agents
runtime) are Node.js bridges between Twilio Media Streams and xAI's realtime model. A role
applied for on 2026-09-23 builds voice agents in Python and C#/.NET. This repo ports the proven
call loop to ASP.NET Core 8 and uses the port to test the design against four current models.

## Decisions

1. **One call loop, many providers.** `CallSession` speaks an OpenAI-shaped realtime event
   stream. xAI and OpenAI's realtime API speak it natively (with a session-shape dialect);
   Gemini Live and OpenAI GPT-Live are reached through adapters that translate both
   directions. Provider choice is configuration (`Realtime:Provider`), not code.

2. **Serialize the call on a single-consumer Channel.** Node's event loop serialized the two
   sockets and tool completions for free. In .NET they arrive on separate tasks, so every input
   becomes an event on one channel read by one consumer. No locks, deterministic ordering.

3. **Speech-to-speech models, not an STT -> LLM -> TTS cascade.** A cascade adds two network
   hops and loses prosody. The bridge still gets text (provider transcription) for logging,
   goodbye detection and the booking-integrity check.

4. **Two barge-in paths.** Server VAD needs a network round trip; the bridge's own VAD clears
   Twilio's buffer after three voiced 20 ms frames (roughly 30 to 50 ms at the bridge in local
   simulation; a real call cannot beat about 40 ms, since the frames arrive in real time).
   Whichever fires first wins; with `interrupt_response=true` the provider cancels its own reply,
   so the server path does not send a second cancel.

5. **Measure what the caller hears.** Latency is end of caller speech to first AUDIBLE agent
   audio. Counting packets counted GPT-Live's continuous silence and reported a fake ~300 ms.

6. **Tracing must never sit on the call path.** A synchronous per-event file append cost ~38 ms
   at 50 frames/s and delayed a greeting by 3.5 s. Traces go through a background queue.

## Provider evaluation

Method: the same five-turn service call (TTS caller, 8 kHz mu-law at real-time pace: a price
question, availability, name and number, confirmation, goodbye), through this bridge, twice per
provider. Results and per-turn timelines: `docs/evidence/benchmark.json`, `live-bench-*/`.

See the README for the table and the decision it drove.

## Consequences

- Swapping providers is a config change plus, for new protocols, one adapter class with tests.
- GPT-Live exposes no reply-boundary or turn events; replies are segmented by audio energy, and
  mid-session system turns (used by the booking-integrity flush) have no documented equivalent.
- Gemini Live exposes no speech-stopped/commit events, so its turn timelines are shorter.
