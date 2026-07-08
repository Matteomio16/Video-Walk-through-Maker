# Video Walk-through Maker

Turn a screen recording plus a written script into a narrated walkthrough video —
AI voiceover and burned-in subtitles, fully synced — **without any data leaving the
machine**.

You drop in a video and a plain-text script describing the steps shown. The tool:

1. splits the script into steps (numbered lists, blank-line paragraphs, or sentences),
2. finds where each step starts in the video (scene-change detection, adjustable in a review screen),
3. generates the voiceover locally (Piper neural TTS, or the Windows built-in voice),
4. **freeze-frames the video** wherever the narration needs more time than the recording gives it,
5. burns subtitles into the frames (an `.srt` sidecar is written too), and
6. produces a single `.mp4`.

## For end users

Extract the distribution zip anywhere and run `VideoWalkthroughMaker.exe`. No installer,
no admin rights, no Python, no internet connection needed.

1. **Choose files** — pick (or drag in) the recording and paste or load the script.
   Number your script steps (`1.`, `2.` or `Step 1:`) or separate them with blank lines.
2. **Review** — the app proposes where each step starts in the video. Check the step
   texts, nudge the boundary sliders if a guess is off, preview the voice.
3. **Create** — wait for rendering, then open the output folder.

Tips
- The output video is *longer* than the recording whenever narration needs more time —
  the video holds on the current frame until the voiceover for that step finishes.
- "Keep original audio" mixes the recording's own sound quietly under the narration.

## For IT / security review

- **No network access at runtime.** Voice synthesis (Piper, running as a bundled local
  process, or the Windows speech engine that ships with the OS) and video processing
  (bundled ffmpeg) all happen on the user's machine. The app contains no telemetry,
  no cloud calls, no API keys.
- Bundled third-party binaries (all fetched at *package* time by `packaging/build-release.ps1`):
  [ffmpeg](https://ffmpeg.org) (GPL build from gyan.dev),
  [Piper](https://github.com/rhasspy/piper) (MIT) with a voice model from
  [rhasspy/piper-voices](https://huggingface.co/rhasspy/piper-voices) (MIT).
- If policy forbids the bundled binaries, users can select **"Windows built-in voice"**
  (100% Microsoft-shipped software); only ffmpeg remains as a third-party component.

## Building the distribution zip

On a Windows machine with the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
and internet access (build time only):

```powershell
powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1
```

Produces `dist/VideoWalkthroughMaker-win-x64.zip` containing the app, the `vwm` CLI,
ffmpeg, Piper and an English voice. Hand the zip to users.

## Development

Cross-platform: everything except the Windows voice engine builds and runs on Linux/macOS.

```bash
dotnet test                 # unit tests (parser, planner, subtitles, wav handling)
./scripts/e2e-test.sh       # full pipeline against a synthetic video (needs ffmpeg + espeak-ng)
dotnet run --project src/Vwm.App   # the desktop app
```

CLI, useful for scripting and debugging:

```bash
vwm make --video in.mp4 --script script.txt --out out.mp4 \
      [--engine piper|espeak|windows] [--boundaries b.json] [--keep-original-audio]
vwm detect --video in.mp4 --steps 4      # print proposed step boundaries as JSON
```

### How the sync works

Everything hangs off exact WAV durations — no fragile word-timestamp APIs:

- Each sentence is synthesized to its own WAV; its duration is read from the file.
- `TimelinePlanner` (src/Vwm.Core/Timeline) gives every step an output slot of
  `max(video segment length, narration length)`: video is freeze-extended
  (`tpad=stop_mode=clone`), narration is padded with silence.
- Subtitle cues reuse the same offsets, so audio, video and subtitles cannot drift.

### Repo layout

| Path | What |
|---|---|
| `src/Vwm.Core` | The pipeline engine: script parsing, scene detection, TTS abstraction, timeline planning, SRT building, ffmpeg rendering |
| `src/Vwm.Cli` | `vwm` command-line interface |
| `src/Vwm.App` | Avalonia desktop app (3-step wizard) |
| `src/Vwm.Tts.Windows` | Windows built-in voice engine (WinRT) |
| `tests/Vwm.Core.Tests` | Unit tests |
| `scripts/e2e-test.sh` | End-to-end pipeline test with a generated video |
| `packaging/build-release.ps1` | Builds the distributable Windows zip |
