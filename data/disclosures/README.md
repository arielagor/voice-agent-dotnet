# Verbatim disclosures

Each `.txt` is wording that must be said exactly; each `.ulaw` is that wording rendered to 8 kHz
mu-law by `tools/make_disclosures.py`. The bridge plays the audio itself and holds the model until
Twilio confirms playback, so a speech-to-speech model cannot paraphrase it.

- `recording-notice` — at the start of every call.
- `servicing-notice` — on payment-reminder calls, only after `verify_account` succeeds, so nothing
  about the debt is said to anyone but the verified account holder.

The wording here is illustrative for the demo business. A lender's compliance team owns the real
text; change the `.txt`, re-render, done.
