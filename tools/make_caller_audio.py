"""Synthesize a scripted caller with xAI TTS and convert it to what Twilio actually sends:
8 kHz mono G.711 mu-law. The output feeds `simulate_call.py --script`.

    python tools/make_caller_audio.py            # needs XAI_API_KEY and ffmpeg on PATH
"""
from __future__ import annotations

import json
import os
import subprocess
import urllib.request
from pathlib import Path

OUT = Path(__file__).parent / "caller-audio"

# A realistic service call: a policy question, a scheduling question, the details, a
# confirmation, and a goodbye. Numbers are fictional (555).
SERVICE_CALL = [
    ("01-oil-change", "Hi there. How much is a synthetic oil change?"),
    ("02-availability", "Okay. Could I bring my car in for one tomorrow morning?"),
    ("03-details", "Nine o'clock works. My name is Jordan Reyes, and my number is three one oh, five five five, zero one four two."),
    ("04-confirm", "Yes, that's all correct."),
    ("05-goodbye", "Great, that's all I need. Thanks, bye."),
]

# A reference check against the live digital-twin line: what a hiring manager would ask.
RECRUITER_CALL = [
    ("01-intro", "Hi, I'm hiring for a voice AI engineer role in Los Angeles. Can you tell me about Ariel's experience building voice agents?"),
    ("02-csharp", "Has he worked in C sharp or dot net?"),
    ("03-confirm", "Okay. Yes, that's all correct as far as I know."),
    ("04-goodbye", "Great, that's all I need. Thanks, bye."),
]

SCRIPTS = {"service": (SERVICE_CALL, OUT), "recruiter": (RECRUITER_CALL, OUT.parent / "caller-audio-recruiter")}


def tts(text: str, voice: str, key: str) -> bytes:
    body = json.dumps({
        "text": text,
        "voice_id": voice,
        "language": "en",
        "output_format": {"codec": "mp3", "sample_rate": 24000, "bit_rate": 64000},
    }).encode()
    request = urllib.request.Request("https://api.x.ai/v1/tts", data=body, method="POST",
                                     headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read()


def main() -> None:
    import sys
    script, out = SCRIPTS[sys.argv[1] if len(sys.argv) > 1 else "service"]
    key = os.environ["XAI_API_KEY"]
    voice = os.environ.get("CALLER_VOICE", "ara")
    out.mkdir(exist_ok=True)
    for name, text in script:
        mp3 = out / f"{name}.mp3"
        ulaw = out / f"{name}.ulaw"
        if not mp3.exists():
            mp3.write_bytes(tts(text, voice, key))
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(mp3),
                        "-ar", "8000", "-ac", "1", "-f", "mulaw", str(ulaw)], check=True)
        print(f"{ulaw.name}: {ulaw.stat().st_size / 8000:.2f} s  \"{text}\"")


if __name__ == "__main__":
    main()
