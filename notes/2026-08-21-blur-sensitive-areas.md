# Blur / redact sensitive areas — notes (2026-08-21)

Matteo's request: an editing feature that hides sensitive parts of the screen — activated
on demand, asking for a set of *lengths* (time slices) and, per length, a draggable and
resizable grey box that then applies to every frame of that slice.

Verified in the Linux cloud sandbox: unit tests, real-ffmpeg pixel tests, the extended
e2e script, and the Avalonia app driven under Xvfb with xdotool (wizard → editor → drag →
real-blur preview → render, with the output frames inspected).

## Model

`BlurRegion` (`src/Vwm.Core/Render/BlurRegion.cs`) — a rectangle plus a slice of source
time, persisted on `WalkthroughProject` and accepted by `PipelineOptions`/`RenderOptions`.

- Geometry is **fractions of the frame (0-1)**, not pixels. That is what lets the editor
  place a box on a 480px-tall still and have it land on exactly the same content in a
  1080p render, in the 480p preview clip, and in the blurred-still preview — without
  probing the video anywhere.
- Time is **source-video seconds**, because that is the timeline the user picks against.
  The renderer maps it per segment.
- `Style` is `Blur` (boxblur, strength 1-10, default 10) or `Solid` (an opaque grey
  block). Solid is offered because a blur, however strong, still carries the original
  pixels' local averages — it is the honest choice for anything truly secret.
- `Validate` rejects anything that would render wrong or silently do nothing (off-frame,
  too small, empty/backwards slice, out-of-range strength) before ffmpeg is ever started.

## Rendering

`BlurFilterBuilder` writes the filtergraph; the renderer folds it into each segment's
existing `-vf`, **before** the subtitle burn so the narration text is never obscured.

```
…,fps=30,format=yuv420p,split[m0][c0];
[c0]crop=…,boxblur=…[b0];
[m0][b0]overlay=…:enable='between(t,S,E)',subtitles=seg.srt:force_style='…'
```

A `Solid` region needs no branch at all — one `drawbox` in place.

Three things this had to get right, each of which is now a test:

1. **The freeze-frames.** A segment whose narration runs long holds its last source frame.
   Those held frames are clones of the frame at the slice's end, so a region running to
   the end of its slice extends to cover the whole hold — otherwise the sensitive pixels
   reappear the instant the video freezes.
2. **Even-pixel snapping.** yuv420p subsamples chroma 2:1; an odd crop origin gets
   silently nudged by ffmpeg and lands a pixel away from the overlay covering it. Every
   origin and size is written as `2*floor(dim*fraction/2)`.
3. **The boxblur radius clamp.** boxblur *fails the encode* on a radius above half the
   side it applies to, and the chroma planes are half-size. A minimum-size area was enough
   to blow this up ("Invalid chroma_param radius value 1, must be >= 0 and <= 0") — found
   by rendering the smallest allowed area, not by reading the code. The cap now sits
   outside the minimum (`min(floor(min(w,h)/2), max(1, …))`) and `MinSize` is 2% of the
   frame so the clamp is never the binding constraint in practice.

The segment cache key includes the applied blur, so editing one area re-encodes only the
segments that area actually covers; everything else is still a cache hit.

## Editor (Review screen)

Opt-in: a **Blur sensitive areas** checkbox next to Back / Create. Ticking it opens a
full-window editor over the review page (no fourth wizard step for a feature most runs
will not use).

- Left: the list of areas, each with its own time range and a remove button.
- Right: `Controls/BlurCanvas` — the frame with the grey box on it. Drag the body to move,
  any of eight handles to resize, or drag on bare frame to draw a new box. The box is
  computed against the image's letterboxed rect so it never drifts from the content.
- Below: *Starts* / *Ends* sliders (each end pushes the other rather than crossing it), a
  *Show frame* scrub across the area's own slice, style + strength, and **Show the real
  blur** — which renders the still through the actual filters. Worth having: the first
  test render showed a letter's descender poking out below the box, which the placement
  box alone would not have revealed.

Step previews ("▶ Preview") now render with the blur too, so what you watch is what ships.

## CLI

`vwm make --blur areas.json` takes the same areas as a JSON array (`ProjectStore
.DeserializeBlurRegions`, same conventions as `.vwmproj`). `style` and `strength` are
optional.

## Tests

- `BlurRegionTests` — validation.
- `BlurFilterBuilderTests` — source-time → segment-local mapping (including the freeze
  case) and the generated graph: geometry expressions, the time switch, label uniqueness
  across chained regions, subtitles last, the radius clamp ordering.
- `BlurRenderTests` — real ffmpeg on a white-grid source, measuring the spread of pixel
  values in a patch: obscured only inside the window, rest of the frame untouched, the
  freeze covered, solid is flat, stronger settings obscure more, several areas at once,
  the cache invalidates on an edit and hits when nothing changed, smallest and largest
  areas both encode, blur alongside kept original audio.
- `scripts/e2e-test.sh` gained a second render with `--blur`, asserting the areas are
  obscured at several times, that the timeline did not shift, and that the subtitles are
  byte-identical to the unblurred run.

140 unit tests green; e2e green.

## Still to check on Windows

- The editor under the real Windows theme (Xvfb screenshots confirm layout and behaviour,
  not native font metrics).
- Nothing else is platform-specific: the filters go through the same bundled ffmpeg.
