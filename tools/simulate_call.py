"""Drive the voice bridge the way Twilio does, in real time, and measure what a caller feels.

Plays Twilio's side of a Media Streams call against a running bridge: 20 ms frames of 8 kHz
mu-law at wall-clock pace, marks echoed only once the agent's audio would have finished
playing, then a caller who talks over the agent. Reports reply latency and barge-in latency
from the caller's side of the socket, next to the bridge's own /metrics.

    dotnet run --project src/VoiceAgent --environment Development --urls http://localhost:5080
    python tools/simulate_call.py --turns 3

Against Realtime:Provider=scripted this measures the BRIDGE plus the stub's 500 ms end-of-turn
window; it says nothing about a real model's inference time.
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import hashlib
import hmac
import json
import math
import random
import statistics
import time
import urllib.request
import uuid

import websockets

RATE = 8000
FRAME = 160  # 20 ms
FRAME_SECONDS = FRAME / RATE


# --- G.711 mu-law, written out rather than imported: audioop is gone in Python 3.13 ------------

BIAS, CLIP = 0x84, 32635


def mulaw_encode(sample: int) -> int:
    sign = 0x80 if sample < 0 else 0
    sample = min(abs(sample), CLIP) + BIAS
    exponent = 7
    mask = 0x4000
    while exponent > 0 and not sample & mask:
        exponent -= 1
        mask >>= 1
    mantissa = (sample >> (exponent + 3)) & 0x0F
    return ~(sign | (exponent << 4) | mantissa) & 0xFF


def mulaw_decode(byte: int) -> int:
    u = ~byte & 0xFF
    sample = ((((u & 0x0F) << 3) + BIAS) << ((u >> 4) & 0x07)) - BIAS
    return -sample if u & 0x80 else sample


def audible(ulaw: bytes, dbfs: float = -45.0) -> bool:
    if not ulaw:
        return False
    rms = math.sqrt(sum(mulaw_decode(b) ** 2 for b in ulaw) / len(ulaw))
    return rms > 32768 * 10 ** (dbfs / 20)


def frames_of(samples: list[int]) -> list[str]:
    out = []
    for i in range(0, len(samples) - FRAME + 1, FRAME):
        out.append(base64.b64encode(bytes(mulaw_encode(s) for s in samples[i:i + FRAME])).decode())
    return out


def speech(seconds: float, level: float = 9000.0) -> list[str]:
    """A voiced 180 Hz tone with a 4 Hz syllable envelope: loud enough, and shaped enough, to read as talk."""
    n = int(seconds * RATE)
    return frames_of([
        int(level * (0.55 + 0.45 * math.sin(2 * math.pi * 4 * t / RATE)) * math.sin(2 * math.pi * 180 * t / RATE))
        for t in range(n)
    ])


def line_noise(seconds: float, level: float = 25.0) -> list[str]:
    rng = random.Random(7)
    return frames_of([int(rng.gauss(0, level)) for _ in range(int(seconds * RATE))])


def call_token(secret: str, call_sid: str, direction: str = "inbound", purpose: str = "") -> str:
    mac = hmac.new(secret.encode(), f"{call_sid}|{direction}|{purpose}".encode(), hashlib.sha256).digest()
    return base64.urlsafe_b64encode(mac).decode().rstrip("=")


class TwilioSide:
    """Twilio's half of the socket: paced sends, a playback clock, and mark echoes."""

    def __init__(self, ws):
        self.ws = ws
        self.stream_sid = "MZ" + uuid.uuid4().hex
        self.call_sid = "CA" + uuid.uuid4().hex
        self.playback_ends = 0.0           # when the audio queued so far finishes "playing"
        self.first_media_after: float | None = None
        self.media_event = asyncio.Event()
        self.clear_at: float | None = None
        self.closed = asyncio.Event()

    async def send(self, message: dict) -> None:
        await self.ws.send(json.dumps(message))

    async def play(self, frames: list[str]) -> float:
        """Send frames at wall-clock pace; return the time the last one left."""
        start = time.perf_counter()
        for i, payload in enumerate(frames):
            await self.send({"event": "media", "streamSid": self.stream_sid, "media": {"payload": payload}})
            delay = start + (i + 1) * FRAME_SECONDS - time.perf_counter()
            if delay > 0:
                await asyncio.sleep(delay)
        return time.perf_counter()

    async def receive(self) -> None:
        try:
            async for raw in self.ws:
                msg = json.loads(raw)
                now = time.perf_counter()
                event = msg.get("event")
                if event == "media":
                    seconds = len(base64.b64decode(msg["media"]["payload"])) / RATE
                    self.playback_ends = max(self.playback_ends, now) + seconds
                    if self.first_media_after is None:
                        self.first_media_after = now
                        self.media_event.set()
                elif event == "clear":
                    self.playback_ends = now
                    self.clear_at = now
                elif event == "mark":
                    asyncio.create_task(self.echo_mark(msg["mark"]["name"]))
        finally:
            self.closed.set()

    async def echo_mark(self, name: str) -> None:
        await asyncio.sleep(max(0.0, self.playback_ends - time.perf_counter()))
        if not self.closed.is_set():
            await self.send({"event": "mark", "streamSid": self.stream_sid, "mark": {"name": name}})

    async def wait_first_media(self, timeout: float = 5.0) -> float:
        self.first_media_after = None
        self.media_event.clear()
        await asyncio.wait_for(self.media_event.wait(), timeout)
        return self.first_media_after  # type: ignore[return-value]


async def run(args: argparse.Namespace) -> None:
    ws_url = args.base.replace("http://", "ws://").replace("https://", "wss://") + "/media"
    async with websockets.connect(ws_url, max_size=None) as ws:
        call = TwilioSide(ws)
        receiver = asyncio.create_task(call.receive())

        await call.send({"event": "connected", "protocol": "Call", "version": "1.0.0"})
        t_start = time.perf_counter()
        await call.send({
            "event": "start",
            "streamSid": call.stream_sid,
            "start": {
                "streamSid": call.stream_sid,
                "callSid": call.call_sid,
                "customParameters": {
                    "t": call_token(args.secret, call.call_sid),
                    "dir": "inbound",
                    "from": args.caller,
                },
            },
        })

        # Line noise while the agent greets; the bridge calibrates its VAD on it.
        noise = asyncio.create_task(call.play(line_noise(1.0)))
        greeting_at = await call.wait_first_media()
        await noise
        print(f"greeting: first audio {1000 * (greeting_at - t_start):.0f} ms after stream start")
        await asyncio.sleep(max(0.0, call.playback_ends - time.perf_counter()) + 0.2)

        replies = []
        for turn in range(1, args.turns + 1):
            call.first_media_after = None
            call.media_event.clear()
            spoke_until = await call.play(speech(1.2))
            silence = asyncio.create_task(call.play(line_noise(2.0)))
            first = await call.wait_first_media()
            replies.append(1000 * (first - spoke_until))
            print(f"turn {turn}: reply audio {replies[-1]:.0f} ms after the caller stopped talking")
            await silence
            await asyncio.sleep(max(0.0, call.playback_ends - time.perf_counter()) + 0.2)

        # Barge-in: the caller starts talking 200 ms into the agent's reply, while it is still playing.
        call.clear_at = None
        await call.play(speech(1.0))
        waiting = asyncio.create_task(call.play(line_noise(2.0)))
        await call.wait_first_media()
        waiting.cancel()
        await call.play(line_noise(0.2))
        talk_over_start = time.perf_counter()
        await call.play(speech(0.6))
        await asyncio.sleep(0.3)
        if call.clear_at is not None:
            print(f"barge-in: Twilio buffer cleared {1000 * (call.clear_at - talk_over_start):.0f} ms after the caller started talking over the agent")
        else:
            print("barge-in: NO clear received (the agent kept talking over the caller)")

        await call.send({"event": "stop", "streamSid": call.stream_sid})
        await asyncio.wait_for(call.closed.wait(), 5)
        receiver.cancel()

    if replies:
        print(f"\nreply latency over {len(replies)} turns: median {statistics.median(replies):.0f} ms, max {max(replies):.0f} ms")
    with urllib.request.urlopen(args.base + "/metrics", timeout=5) as r:
        print("bridge /metrics:", json.dumps(json.load(r), indent=2))


# --- Scripted call: real recorded speech, one paced sender, a recording of both sides -------------

class ScriptedCall(TwilioSide):
    """One sender loop owns the socket's audio clock: utterance frames when queued, line noise otherwise."""

    def __init__(self, ws):
        super().__init__(ws)
        self.queue: list[str] = []
        self.utterance_sent = asyncio.Event()
        self.last_media_at = 0.0
        self.t0 = time.perf_counter()
        self.caller_track: dict[int, bytes] = {}
        self.agent_track: list[tuple[float, bytes]] = []
        rng = random.Random(3)
        self.noise = frames_of([int(rng.gauss(0, 25)) for _ in range(RATE * 2)])

    async def sender(self) -> None:
        tick, i = time.perf_counter(), 0
        while not self.closed.is_set():
            if self.queue:
                payload = self.queue.pop(0)
                if not self.queue:
                    self.utterance_sent.set()
            else:
                payload = self.noise[i % len(self.noise)]
            i += 1
            self.caller_track[int((time.perf_counter() - self.t0) / FRAME_SECONDS)] = base64.b64decode(payload)
            try:
                await self.send({"event": "media", "streamSid": self.stream_sid, "media": {"payload": payload}})
            except websockets.ConnectionClosed:
                return
            tick += FRAME_SECONDS
            await asyncio.sleep(max(0.0, tick - time.perf_counter()))

    async def receive(self) -> None:
        try:
            async for raw in self.ws:
                msg = json.loads(raw)
                now = time.perf_counter()
                event = msg.get("event")
                if event == "media":
                    audio = base64.b64decode(msg["media"]["payload"])
                    start = max(self.playback_ends, now)
                    self.agent_track.append((start - self.t0, audio))
                    self.playback_ends = start + len(audio) / RATE
                    # Only AUDIBLE agent audio counts. A full-duplex model streams silence too, and
                    # timing to the first packet of silence reports a latency nobody heard.
                    if audible(audio):
                        audible_at = start  # when the caller would actually hear it
                        self.last_media_at = now
                        if self.first_media_after is None:
                            self.first_media_after = max(now, audible_at)
                            self.media_event.set()
                elif event == "clear":
                    self.playback_ends = now
                    self.clear_at = now
                    cut = now - self.t0
                    self.agent_track = [(s, a[: max(0, int((cut - s) * RATE))]) if s + len(a) / RATE > cut else (s, a)
                                        for s, a in self.agent_track]
                elif event == "mark":
                    asyncio.create_task(self.echo_mark(msg["mark"]["name"]))
        except websockets.ConnectionClosed:
            pass
        finally:
            self.closed.set()

    async def agent_quiet(self, gap: float = 2.5, timeout: float = 30.0) -> None:
        # 2.5 s, not less: after "let me check that" the tool round trip plus the model's second
        # pass is a silent gap of 1-2 s, and a caller who fills it cancels the real answer.
        """Wait until the agent's audio has finished playing and nothing new has arrived for `gap` s."""
        deadline = time.perf_counter() + timeout
        while time.perf_counter() < deadline and not self.closed.is_set():
            now = time.perf_counter()
            if now > self.playback_ends + 0.3 and now - self.last_media_at > gap:
                return
            await asyncio.sleep(0.05)

    async def say(self, ulaw: bytes) -> float:
        self.utterance_sent.clear()
        self.first_media_after = None
        self.media_event.clear()
        frames = [ulaw[i:i + FRAME] for i in range(0, len(ulaw) - FRAME + 1, FRAME)]
        self.queue = [base64.b64encode(f).decode() for f in frames]
        await self.utterance_sent.wait()
        # TTS clips end in silence. Measure from the end of the SPEECH, not the end of the file,
        # or the trailing silence gets quietly subtracted from the latency.
        trailing = 0
        for f in reversed(frames):
            rms = math.sqrt(sum(mulaw_decode(b) ** 2 for b in f) / len(f))
            if rms > 32768 * 10 ** (-45 / 20):
                break
            trailing += 1
        return time.perf_counter() - trailing * FRAME_SECONDS

    def write_recording(self, path: str) -> None:
        import wave
        total = max([k + 1 for k in self.caller_track] +
                    [int((s + len(a) / RATE) / FRAME_SECONDS) + 1 for s, a in self.agent_track] + [1]) * FRAME
        mix = [0] * total
        for k, frame in self.caller_track.items():
            for j, b in enumerate(frame):
                if k * FRAME + j < total:
                    mix[k * FRAME + j] += mulaw_decode(b)
        for s, audio in self.agent_track:
            base = int(s * RATE)
            for j, b in enumerate(audio):
                if base + j < total:
                    mix[base + j] += mulaw_decode(b)
        with wave.open(path, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(RATE)
            w.writeframes(b"".join(max(-32768, min(32767, v)).to_bytes(2, "little", signed=True) for v in mix))


async def run_script(args: argparse.Namespace) -> None:
    import pathlib
    clips = sorted(pathlib.Path(args.script).glob("*.ulaw"))
    ws_url = args.base.replace("http://", "ws://").replace("https://", "wss://") + "/media"
    async with websockets.connect(ws_url, max_size=None) as ws:
        call = ScriptedCall(ws)
        receiver = asyncio.create_task(call.receive())
        await call.send({"event": "connected", "protocol": "Call", "version": "1.0.0"})
        t_start = time.perf_counter()
        params = {"t": call_token(args.secret, call.call_sid), "dir": "inbound", "from": args.caller}
        if args.params:  # a different bridge's own stream parameters, e.g. the Node line's ctok
            params = json.loads(args.params)
        await call.send({
            "event": "start",
            "streamSid": call.stream_sid,
            "start": {"streamSid": call.stream_sid, "callSid": call.call_sid, "customParameters": params},
        })
        sender = asyncio.create_task(call.sender())

        try:
            greeting = await call.wait_first_media(timeout=10)
            print(f"greeting: first audio {1000 * (greeting - t_start):.0f} ms after stream start")
            await call.agent_quiet()
        except asyncio.TimeoutError:
            print("greeting: none within 10 s; the caller speaks first")

        latencies = []
        for clip in clips:
            if call.closed.is_set():
                break
            spoke_until = await call.say(clip.read_bytes())
            try:
                first = await call.wait_first_media(timeout=15)
                latencies.append(1000 * (first - spoke_until))
                print(f"{clip.stem}: agent audio {latencies[-1]:.0f} ms after the caller finished")
            except asyncio.TimeoutError:
                print(f"{clip.stem}: no reply within 15 s")
            await call.agent_quiet()

        try:
            await asyncio.wait_for(call.closed.wait(), 20)
            print("call ended by the bridge (goodbye flow)")
        except asyncio.TimeoutError:
            print("bridge did not end the call; sending stop")
            await call.send({"event": "stop", "streamSid": call.stream_sid})
        sender.cancel()
        receiver.cancel()
        call.write_recording(args.record)
        print(f"recording: {args.record}")

    if latencies:
        print(f"\nreply latency over {len(latencies)} turns: median {statistics.median(latencies):.0f} ms, "
              f"min {min(latencies):.0f} ms, max {max(latencies):.0f} ms")
    if not args.no_metrics:
        with urllib.request.urlopen(args.base + "/metrics", timeout=5) as r:
            print("bridge /metrics:", json.dumps(json.load(r)))


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--base", default="http://localhost:5080")
    p.add_argument("--secret", default="local-development-only-secret", help="Security:CallTokenSecret of the bridge")
    p.add_argument("--caller", default="+13105550142")
    p.add_argument("--turns", type=int, default=3)
    p.add_argument("--script", help="directory of .ulaw caller clips (see make_caller_audio.py); plays them in order")
    p.add_argument("--record", default="call-recording.wav", help="with --script: where to write a mixed recording")
    p.add_argument("--params", help="with --script: JSON customParameters for the start event (overrides t/dir/from)")
    p.add_argument("--no-metrics", action="store_true", help="skip fetching /metrics (bridges without that endpoint)")
    args = p.parse_args()
    asyncio.run(run_script(args) if args.script else run(args))


if __name__ == "__main__":
    main()
