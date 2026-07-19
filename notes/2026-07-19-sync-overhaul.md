# Sync & subtitle overhaul — notes (2026-07-19)

Full pass over the pipeline aimed at near-perfect sync, driven by an objective
measurement harness (color-coded steps → check each subtitle/voice lands on the
right step's pixels, plus per-boundary drift and subtitle-vs-voice onset).

## Measured, 10-step uneven synthetic video (espeak)

| Metric | Before | After |
|---|---|---|
| Video boundary vs planned timeline | 1 ms (lucky cancellation) | 1 ms, now **deterministic** |
| Subtitle start vs actual voice onset | mean 25 ms / max 53 ms | **mean 12 ms / max 20 ms** |
| Cues landing on the wrong step | 0 / 10 | 0 / 10 |

## Changes

1. **Frame-quantized timeline** (`TimelinePlanner`): each segment's length is rounded
   up to a whole frame at the render fps, so the concatenated video's internal
   boundaries coincide with the planned offsets exactly. Cross-segment drift was a
   random walk (usually small, but not guaranteed on long / variable-frame-rate
   recordings); it is now deterministically zero.
2. **Exact-frame segment render** (`Renderer`): each segment is cut to an exact frame
   count (`-frames:v`) rather than a floating-point `-t`, guaranteeing the quantized
   duration regardless of ffmpeg rounding.
3. **TTS silence trimming** (`SilenceTrimmer`): leading/trailing near-silence is removed
   from every synthesized clip, so a clip's duration is the real speech length. The
   subtitle now starts on the voice instead of ~25 ms before it, and inter-sentence
   gaps are the intended length. Applied in both the render pipeline and the previews.
4. **Subtitle minimum on-screen time** (`SrtBuilder`): a brief utterance is held for at
   least 1.2 s (never overlapping the next cue), so short lines stay readable. Long
   cues are unchanged; start times (synced to speech) are never moved.

The freeze-frame sync behaviour flagged as the strongest point in the first test is
untouched in intent — only made frame-exact. Detection benchmarks and the e2e test
still pass; 38 unit tests green.
