"""Aggregate docs/evidence/live-bench-<provider>-<run>/ into one comparison table.

Reply latency is measured at the simulated caller: end of the caller's SPEECH (trailing
silence removed) to the first AUDIBLE agent audio, i.e. what a person on the line would feel,
minus the carrier leg. Behaviour columns are read from the bridge's own log.
"""
from __future__ import annotations

import json
import re
import statistics
import sys
from collections import defaultdict
from pathlib import Path

MODELS = {
    "xai": "xAI grok-voice-think-fast-2.0 (realtime API)",
    "openai": "OpenAI gpt-realtime-2.1 (realtime API)",
    "gpt-live": "OpenAI gpt-live-1 + gpt-5.6-luna (Live API, delegated tools)",
    "gemini": "Google gemini-3.8-live (Live API)",
}


def parse_run(run_dir: Path) -> dict:
    sim = (run_dir / "simulator.txt").read_text(encoding="utf-8", errors="ignore")
    log = (run_dir / "server.log").read_text(encoding="utf-8", errors="ignore")
    turns = [int(m) for m in re.findall(r"agent audio (\d+) ms after the caller finished", sim)]
    greeting = re.search(r"greeting: first audio (\d+) ms", sim)
    tools = re.findall(r"tool call (\w+)", log)
    agent_lines = re.findall(r"\] agent: (.*)", log)
    booked_after_readback = False
    if "book_appointment" in tools:
        before = log.split("tool call book_appointment")[0]
        booked_after_readback = bool(re.search(r"agent: .*(read that back|to confirm|is that (all )?correct|shall i book)", before, re.I))
    return {
        "turns": turns,
        "greeting": int(greeting.group(1)) if greeting else None,
        "tools": tools,
        "booking_committed": "booking Committed" in log,
        "readback_before_booking": booked_after_readback,
        "ended_by_goodbye": "call ended by the bridge" in sim,
        "model_errors": len(re.findall(r"model error", log)),
        "agent_turns": len(agent_lines),
    }


def main(evidence: str) -> None:
    runs = defaultdict(list)
    for d in sorted(Path(evidence).glob("live-bench-*-*")):
        provider = d.name[len("live-bench-"):].rsplit("-", 1)[0]
        if (d / "simulator.txt").exists():
            runs[provider].append(parse_run(d))

    rows = []
    for provider, results in runs.items():
        turns = [t for r in results for t in r["turns"]]
        greetings = [r["greeting"] for r in results if r["greeting"] is not None]
        rows.append({
            "provider": provider,
            "model": MODELS.get(provider, provider),
            "runs": len(results),
            "turns_measured": len(turns),
            "reply_median_ms": round(statistics.median(turns)) if turns else None,
            "reply_p90_ms": round(sorted(turns)[int(0.9 * (len(turns) - 1))]) if turns else None,
            "reply_min_ms": min(turns) if turns else None,
            "reply_max_ms": max(turns) if turns else None,
            "greeting_median_ms": round(statistics.median(greetings)) if greetings else None,
            "greeted_first": f"{len(greetings)}/{len(results)}",
            "bookings_committed": f"{sum(r['booking_committed'] for r in results)}/{len(results)}",
            "read_back_before_booking": f"{sum(r['readback_before_booking'] for r in results)}/{len(results)}",
            "goodbye_ended_call": f"{sum(r['ended_by_goodbye'] for r in results)}/{len(results)}",
            "model_errors": sum(r["model_errors"] for r in results),
        })

    out = Path(evidence) / "benchmark.json"
    out.write_text(json.dumps(rows, indent=2))
    print(f"{'provider':10} {'runs':>4} {'turns':>5} {'median':>7} {'p90':>6} {'min':>6} {'max':>6} {'greet':>6}  booked readback goodbye errors")
    for r in rows:
        print(f"{r['provider']:10} {r['runs']:>4} {r['turns_measured']:>5} {r['reply_median_ms'] or '-':>7} {r['reply_p90_ms'] or '-':>6} "
              f"{r['reply_min_ms'] or '-':>6} {r['reply_max_ms'] or '-':>6} {r['greeting_median_ms'] or '-':>6}  "
              f"{r['bookings_committed']:>6} {r['read_back_before_booking']:>8} {r['goodbye_ended_call']:>7} {r['model_errors']:>6}")
    print(f"\nwritten: {out}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "docs/evidence")
