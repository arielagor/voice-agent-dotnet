# voice-agent-dotnet

A real-time voice-to-voice phone agent in C#/.NET 8: Twilio Media Streams on one side, a
speech-to-speech model on the other, and in between the parts that decide whether a call feels
natural. Those are audio transcoding, voice activity detection, barge-in, turn-taking, tool
calls, memory, and latency you can measure.

It is a port of the Node.js bridge behind a live AI phone line, **+1 (775) 252-8333**, and it
was used to test that design against four current voice models on the same call. Testing it
against live models found eight defects. Three of them were also in the production line, and the
fixes are now deployed there.

Built by directing Claude Code. The design, the tests and the measurements are the author's
responsibility; the C# is recent (September 2026), not years of it.

```
 caller ── PSTN ── Twilio ──WebSocket (8 kHz G.711 mu-law, 20 ms frames)──┐
                                                                          ▼
                     ┌──────────────────────── CallSession ─────────────────────────┐
                     │ one Channel<CallEvent>, one consumer: sockets, timers and     │
                     │ tool results are handled in a single deterministic order     │
                     │                                                              │
                     │ mu-law ⇄ PCM16 · 8k→24k interpolation · 24k→8k 31-tap FIR    │
                     │ energy VAD → local barge-in (clear Twilio's buffer)          │
                     │ goodbye detection · booking-integrity flush · wrap-up timer  │
                     │ tools: RAG (BM25) · availability · booking · verify · PTP    │
                     │ caller memory · latency metrics · per-turn timeline          │
                     └──────────────────────────────┬───────────────────────────────┘
                                                    ▼  IRealtimeConnector
               xAI realtime │ OpenAI Realtime │ OpenAI GPT-Live* │ Gemini Live*
                                                   (* protocol adapters)
```

## Benchmark: which model?

The same five-turn service call went through this bridge twice per provider. The caller was
TTS speech sent as 8 kHz mu-law at real-time pace: a price question, availability for tomorrow,
name and phone number, a confirmation, and a goodbye. Reply latency is measured where the caller
is, from the end of the caller's speech to the first **audible** agent audio, excluding the
carrier leg.

| Model | Median reply | p90 | Worst | Read details back before booking | First greeting |
|---|---|---|---|---|---|
| **xAI grok-voice-think-fast-2.0** | **1.36 s** | 1.65 s | 4.59 s | 2 / 2 | 3.6 s |
| OpenAI gpt-live-1 (+ gpt-5.6-luna for tools) | 1.60 s | 1.83 s | 2.79 s | 2 / 2 | 2.5 s |
| OpenAI gpt-realtime-2.1 | 1.69 s | 1.95 s | 2.14 s | **0 / 2** | 2.7 s |
| Google gemini-3.8-live | 1.88 s | 2.13 s | 2.27 s | 1 / 2 | 1.6 s |

Every call booked the appointment and ended itself on the caller's goodbye, with zero model
errors. OpenAI Realtime booked before confirming the caller's details in both runs, which a
finance line cannot accept. xAI was fastest and also the only one available in the line's cloned
voice, so the production line stays on it. This is 10 turns per model on one script, which is
enough to rank the models on this task and not enough to generalise beyond it. Data:
`docs/evidence/benchmark.json`, per-run logs, raw provider traces and one recording per model in
`docs/evidence/`.

## What testing against live models found

Each was fixed with a regression test. **P** marks the ones also present in the production line,
confirmed from its logs and fixed there.

1. **P** The force-reply timer ("if the model commits the turn but says nothing, prompt it")
   answered turns that had already been answered. xAI creates the reply ~300 ms *before* it
   commits the turn, so "no reply active 700 ms after the commit" was the wrong question. The
   production logs held 97 forced replies across ~70 calls, many seconds after a reply had just
   finished, while the caller was still talking.
2. **P** "Yes, that's all correct", a caller confirming a read-back, matched the goodbye pattern and
   hung up a turn early.
3. xAI re-sends one utterance's transcript as it firms up, so the goodbye fired 3x per utterance.
4. A streaming partial that stopped at "Yes, that's all" read as a goodbye on GPT-Live.
5. **P** A redundant `response.cancel` after the provider's own interrupt failed with "no active
   response" (167 times in the production logs).
6. Disarming the idle follow-up re-sent `turn_detection` without the tuned end-of-turn window.
7. GPT-Live is full-duplex and streams silence continuously; counting packets reported a fake
   ~300 ms latency and a barge-in on every utterance. Only audible audio counts now.
8. The first trace writer appended to disk synchronously per event (~38 ms at 50 frames/s), pushed
   the call loop 3.5 s behind and delayed a greeting. Runs from before the fix were discarded.

## Run it

```bash
dotnet test                                   # 116 tests: audio, VAD, protocol, tools, whole calls
dotnet run --project src/VoiceAgent           # Development: scripted model, no key needed
python tools/simulate_call.py --turns 3       # plays Twilio's side in real time, prints latencies

# Against a real model (the key comes from your environment):
pwsh tools/live-call.ps1 -Name demo -Provider xai        # or openai | gpt-live | gemini
pwsh tools/benchmark.ps1 -Runs 2                          # the table above
docker build -t voice-agent-dotnet .                      # tests run inside the build
```

For a real phone number, set `Twilio:AuthToken`, `Twilio:PublicBaseUrl`, `Realtime:ApiKey` and
`Security:CallTokenSecret`, and point the number's voice webhook at `POST /voice/incoming`.
Signatures are verified against the public URL (Twilio signs what it called, not what the proxy
forwarded), and the media socket only accepts streams carrying a token bound to their CallSid.

## Layout

| Path | What it is |
|---|---|
| `src/VoiceAgent/Calls/CallSession.cs` | The call: event loop, turn-taking, barge-in, ending, metrics |
| `src/VoiceAgent/Audio/` | G.711 codec, resamplers, VAD |
| `src/VoiceAgent/Realtime/` | Provider protocol, connectors, Gemini/GPT-Live adapters, tracer |
| `src/VoiceAgent/Telephony/` | Twilio signatures, TwiML, REST (outbound), call tokens |
| `src/VoiceAgent/Tools/` | Tool registry, dealership tools, BM25 knowledge index |
| `data/` | The demo business (fictional) and agent instructions, as files |
| `tools/` | Python call simulator, TTS caller generator, benchmark harness |
| `docs/decisions/` | Why it is built this way |

## Not production (yet)

Appointments, accounts and caller memory are in-process stores; a real deployment replaces them
at the `AppointmentBook`, `DemoBusiness` and `CallerMemoryStore` seams. The demo data is
fictional. Load beyond one host was not tested.
