"""Render each data/disclosures/*.txt to 8 kHz mu-law with xAI TTS, in the agent's voice.

    python tools/make_disclosures.py          # needs XAI_API_KEY and ffmpeg on PATH

The .txt is the source of truth; re-run after changing any wording.
"""
from __future__ import annotations

import json
import os
import subprocess
import tempfile
import urllib.request
from pathlib import Path

DIR = Path(__file__).resolve().parent.parent / "data" / "disclosures"
VOICE = os.environ.get("DISCLOSURE_VOICE", "leo")


def tts(text: str, key: str) -> bytes:
    body = json.dumps({"text": text, "voice_id": VOICE, "language": "en",
                       "output_format": {"codec": "mp3", "sample_rate": 24000, "bit_rate": 64000}}).encode()
    req = urllib.request.Request("https://api.x.ai/v1/tts", data=body, method="POST",
                                 headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read()


def main() -> None:
    key = os.environ["XAI_API_KEY"]
    for txt in sorted(DIR.glob("*.txt")):
        text = txt.read_text(encoding="utf-8").strip()
        with tempfile.NamedTemporaryFile(suffix=".mp3", delete=False) as mp3:
            mp3.write(tts(text, key))
        out = txt.with_suffix(".ulaw")
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", mp3.name, "-ar", "8000", "-ac", "1", "-f", "mulaw", str(out)], check=True)
        os.unlink(mp3.name)
        print(f"{out.name}: {out.stat().st_size / 8000:.2f} s  \"{text}\"")


if __name__ == "__main__":
    main()
