# First real test — notes (2026-07-13)

Notes from Matteo's first end-to-end test of the packaged Windows app
(`VideoWalkthroughMaker.exe`, Piper voice). Captured for future reference —
**no code changes made yet**, these are observations and improvement ideas.

## Overall

Genuinely happy with it. It's **fast**, **quiet**, and **uploading/loading files works well**.
The end result is **really good** — not bad at all for a first version.

## What works well

- Speed and responsiveness.
- File upload / loading.
- **Preview voice** works (audio is fine) — but see the UX request below.
- **Subtitles are good.**
- **Voice and subtitles are synced well** in the output.
- Piper isn't the most advanced neural voice, but that's understood/expected and it's fine.

## ⭐ Strongest point — video/narration sync (keep this!)

The single best thing: when the narration for a step is still going but the
recording would move on to show something else, the tool **either slows the video
down or holds/pauses on the frame until the sentence finishes**. This keeps voice,
video and subtitles perfectly aligned. This is an *amazingly strong point* — it must
not regress. (This is the `TimelinePlanner` freeze-extension behaviour.)

## Improvements to make

### 1. "Preview voice" button behaviour (UX)
Currently, clicking **Preview voice** opens a video file / another tab.
Desired behaviour:
- **Preview voice** should *just play the voice audio* for that part — a simple play
  button, no new tab/window opening.
- Have a *separate* **Preview** button for previewing **video + voice** together; that
  one is allowed to open another tab/window.
- So: "Preview voice" = audio only, inline; "Preview" = full video+voice preview.

### 2. Step/boundary placement on uneven segments (sync)
The app doesn't always place each script part in the right spot when segment
lengths are very uneven. In this test the script had parts ordered/spaced so that
**parts 1 and 2 were very short (~1–2 seconds each)** while **part 3 was ~10–15
seconds**. That case was **not synced well** — boundary detection/placement needs to
handle very uneven step lengths better (or make the review-screen boundary nudging
easier for this case).

## Bottom line

Likes it. Needs some improvement (the two items above), but as of now it's a solid,
usable tool. 👍
