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
SCRIPT = [
    ("01-oil-change", "Hi there. How much is a synthetic oil change?"),
    ("02-availability", "Okay. Could I bring my car in for one tomorrow morning?"),
    ("03-details", "Nine o'clock works. My name is Jordan Reyes, and my number is three one oh, five five five, zero one four two."),
    ("04-confirm", "Yes, that's all correct."),
    ("05-goodbye", "Great, that's all I need. Thanks, bye."),
]


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
    key = os.environ["XAI_API_KEY"]
    voice = os.environ.get("CALLER_VOICE", "ara")
    OUT.mkdir(exist_ok=True)
    for name, text in SCRIPT:
        mp3 = OUT / f"{name}.mp3"
        ulaw = OUT / f"{name}.ulaw"
        if not mp3.exists():
            mp3.write_bytes(tts(text, voice, key))
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(mp3),
                        "-ar", "8000", "-ac", "1", "-f", "mulaw", str(ulaw)], check=True)
        print(f"{ulaw.name}: {ulaw.stat().st_size / 8000:.2f} s  \"{text}\"")


if __name__ == "__main__":
    main()
