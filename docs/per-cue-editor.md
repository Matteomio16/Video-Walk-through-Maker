# Design: per-cue voiceover & subtitle editor (milestone)

Status: in progress. Addresses audit blocker #3 ("does not yet provide actual
voiceover/subtitle editing"). Phases B and A are built; C, D, E remain.

## Goal

Turn the one-shot generator into an editor: after analysis, the user works on a
timeline of **cues** and can, per cue, edit the text, re-synthesize just that cue,
split/merge it, nudge its timing, change its voice/gain, and override its subtitle
style — then export with only the changed parts re-rendered.

Non-goals (v1): multi-track audio, background music, transitions/effects, reordering
cues out of video order, collaborative editing.

## Why the current pipeline can't do this

Today everything is derived in a single pass from `(scriptText, boundaries, engine)`
in `WalkthroughPipeline.RunAsync`. There is no editable, persistable representation of
"the narration as a set of cues", and the final render burns subtitles in one global
pass, so any change forces a full re-synthesis + full re-encode. An editor needs (1) a
mutable project model and (2) incremental rendering.

## 1. Data model — `WalkthroughProject`

A persistable source of truth (`*.vwmproj`, JSON), replacing the transient
`(script, boundaries)` inputs as the thing the UI edits.

```
WalkthroughProject
  videoPath              canonical local file (LocalPath-guarded)
  keepOriginalAudio      bool
  globalSubtitle         { font, size, position, background }   // defaults for cues
  globalVoice            voice id                                // default for cues
  cues                   Cue[]                                   // in video order

Cue
  id                     stable guid (cache key + UI identity)
  text                   narration + subtitle text
  sourceStart/sourceEnd  slice of the recording this cue narrates over (the freeze region)
  voice?                 per-cue voice override (else globalVoice)
  gainDb                 per-cue narration gain (0 = unchanged)
  ratePct?               per-cue speaking rate, if the engine supports it
  subtitleOverride?      per-cue { font?, size?, position?, background? }
  timing                 { leadIn, tail, gapAfter }             // fine placement knobs
  --- derived / cached (not persisted, or cached by hash) ---
  synthWav               path to the cue's synthesized WAV
  synthKey               hash(engine, voice, ratePct, text)     // synth cache key
  measuredDuration       trimmed speech length
```

A **step** in today's model becomes a cue (or a run of cues). The existing
`ScriptParser` + `SceneDetector.ProposeBoundaries` become a **project seeder**:
`Project FromScript(video, scriptText, engine)` produces the initial cue list — so the
"analyze" flow is unchanged, it just yields an editable project instead of a plan.

## 2. Timeline planning from cues

`TimelinePlanner` is refactored to consume `Cue[]` instead of `(boundaries,
stepNarrations)`. It already does the core work (freeze-extend each segment so it
covers its narration, place narration, emit subtitle cues); the change is that its
input is the explicit cue list and per-cue timing knobs. Re-planning is pure CPU
(milliseconds) and runs after every edit to keep the timeline live.

## 3. Incremental rendering (the key change)

**Move subtitle burn-in out of the final global pass and into each per-segment
encode.** Because every cue's subtitle is shown entirely within its own segment's
output window (the freeze guarantees segment length >= narration length), a segment can
be encoded *with its own subtitle already burned in*. That unlocks:

```
per cue/segment:   cut + freeze + burn THIS cue's subtitle + encode  -> seg_<hash>.mp4
                   cached by hash(sourceSlice, hold, fps, subtitleStyle, text, keepAudio)
join:              concat -c copy            (no re-encode)
finish:            mux narration (-c:v copy) + optional bg-audio mix  (no video re-encode)
```

Consequences:
- Editing one cue (text, timing, source region, subtitle style) re-encodes **only that
  one segment**; concat + mux are stream-copies. Export goes from "re-encode the whole
  video every time" to "re-encode one segment".
- Narration-only changes (gain, or swapping the voice WAV) skip video entirely — just
  the mux step.
- This is a worthwhile refactor even without the editor: the current one-shot render
  gets faster and simpler (final pass stops re-encoding video).

Segment cache lives in the project workspace, content-addressed, so it survives edits
and app restarts. (Retention: this is a user-owned project workspace, distinct from the
ephemeral temp workspace cleaned in Phase 2 — cleared on "close project"/explicit purge.)

Preview stays as-is: `PreviewBuilder` already does voice-only + low-res clip per step;
it gets pointed at a cue and honors the cue's timing/gain/style for instant feedback.

## 4. Edit operations (project mutations + cache invalidation)

| Operation | Effect | Re-work |
|---|---|---|
| Edit text | new `synthKey` -> re-synth cue; re-plan | 1 synth + 1 segment |
| Split cue | text/source split into two cues | 2 synth + 2 segments |
| Merge cues | concat text/source | 1 synth + 1 segment |
| Nudge timing (lead/tail/gap) | re-plan only | 1 segment (timing) |
| Move source in/out | change freeze region | 1 segment |
| Change voice / rate | re-synth cue | 1 synth + 1 segment (mux) |
| Gain | remux only | mux |
| Subtitle style override | re-burn segment | 1 segment |

Each mutation returns a new project state (records/`with`), enabling straightforward
**undo/redo** as a state stack.

## 5. UI (new Edit page)

Sits between Review and Generate (or replaces the Review boundary editor):
- **Timeline**: existing thumbnail strip + a narration waveform + cue blocks aligned to
  output time; drag a block edge to change source in/out or timing.
- **Cue list**: rows of {text, duration, voice badge, style badge}; select -> inspector.
- **Inspector**: text box, voice, gain slider, rate, subtitle-style override, source
  in/out, timing; "Preview cue" (voice + clip); Split / Merge.
- **Transport**: scrub the assembled low-res proxy (cached segments) in the embedded
  libVLC player.
- **Export**: incremental final render with progress + Cancel (Phase 2 plumbing reused).

## 6. CLI

`vwm make --project walkthrough.vwmproj --out out.mp4` renders a saved project
(incremental cache reused). `vwm edit` stays GUI-only.

## Phased delivery

| Phase | Scope | Est. | Status |
|---|---|---|---|
| A | `WalkthroughProject`/`Cue` model, JSON save/load, `FromScript` seeder, planner consumes cues. Unit-tested, no UI. | 2-3 d | **done** |
| B | Render refactor: per-segment subtitle burn + content-hash segment cache + incremental export + `-c copy` concat/mux. Tests + one real-media check. | 3-4 d | **done** |
| C | Per-cue synth cache + edit operations (text/split/merge/timing/source/voice/gain/style) with invalidation; CLI `--project`. | 3-4 d | next |
| D | GUI Edit page (timeline, cue list, inspector, preview, export). | 1.5-2 wk | todo |
| E | Polish: undo/redo, waveform rendering, keyboard nav. | 3-5 d | todo |

Roughly 4-5 weeks total. **A and B are valuable on their own** (cleaner model + faster
render) and de-risk the rest, so they're the recommended first increment even if D is
scheduled later.

## Decisions (settled)

1. **v1 per-cue knobs** = **core set**: edit text, split/merge, timing nudge, per-cue
   voice, per-cue gain. Deferred to a later pass: per-cue speaking rate, per-cue
   subtitle-style override.
2. **Editor placement**: the new timeline/cue editor **replaces the Review page's
   boundary editor** (one richer editing surface).
3. **Build order**: **Phase B first** (render refactor — valuable standalone), then A,
   C, D, E.
4. `.vwmproj` project format is introduced in Phase A with a versioned JSON schema.
