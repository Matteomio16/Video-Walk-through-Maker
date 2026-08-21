# Video Walk-through Maker

Turn a screen recording plus a written script into a narrated walkthrough video —
AI voiceover and burned-in subtitles, fully synced — **without any data leaving the
machine**.

You drop in a video and a plain-text script describing the steps shown. The tool:

1. splits the script into steps (numbered lists, blank-line paragraphs, or sentences),
2. finds where each step starts in the video (scene-change detection, adjustable in a review screen),
3. generates the voiceover locally (a choice of bundled Piper neural voices, or the
   Windows built-in voice),
4. **freeze-frames the video** wherever the narration needs more time than the recording gives it,
5. **blurs out anything sensitive** you box off in the editor,
6. burns subtitles into the frames (an `.srt` sidecar is written too), and
7. produces a single `.mp4`.

## For end users

Double-click `VideoWalkthroughMaker-Setup.exe` and follow the short wizard. It installs
per-user (no admin rights, no console windows), adds a Start Menu shortcut, and can start
the app right away. No Python, no internet connection needed — ever.

1. **Choose files** — pick (or drag in) the recording and paste or load the script.
   Number your script steps (`1.`, `2.` or `Step 1:`) or separate them with blank lines.
   Pick a voice — two bundled neural voices (Heather and Ryan), plus the Windows voice.
   Optionally set the subtitle font, size, position (top, middle or bottom) and
   background (a grey box behind the text, or just a drop shadow).
2. **Review** — the app proposes where each step starts in the video. Check the step
   texts, listen to each step's voice, and watch a video+voice preview of any step right
   inside the app. Nudge the boundary sliders if a guess is off. Tick **Blur sensitive
   areas** to hide parts of the screen (see below).
3. **Create** — wait for rendering, then open the output folder.

### Hiding sensitive parts of the screen

Recordings often catch something that should not leave the building — a customer name, an
account number, a licence key. Tick **Blur sensitive areas** on the Review screen and the
editor opens:

- **Add an area** for each stretch of the recording that shows something private. Each area
  has its own *start* and *end*, so a name that is only on screen between 0:12 and 0:30 is
  covered for exactly those seconds and no others.
- **Drag the grey box** over what should be hidden, and pull any of its eight handles to
  resize it. Dragging on bare frame draws a fresh box from scratch. The box applies to
  **every frame between the area's start and end** — including the freeze-frames the tool
  adds when narration runs long.
- **Scrub** the *Show frame* slider through the area to check the box still covers the
  content as the screen changes, and press **Show the real blur** to see the frame put
  through the actual render filters rather than the placement box.
- **Cover with** a *Blur* (strength Light / Medium / Strong) or a *Solid grey block*. For
  anything truly secret, prefer the solid block: a blur, however strong, still carries the
  original pixels' local averages.

Add as many areas as you need — they can overlap in time and in place, and each keeps its own
rectangle, style and strength. Areas are burned into the rendered video, so the sensitive
pixels are gone from the file you hand over, not merely hidden by a player.

Tips
- The output video is *longer* than the recording whenever narration needs more time —
  the video holds on the current frame until the voiceover for that step finishes.
- "Keep original audio" mixes the recording's own sound quietly under the narration.

## For IT / security review

- **No network access at runtime.** Voice synthesis (Piper, running as a bundled local
  process, or the Windows speech engine that ships with the OS), video processing
  (bundled ffmpeg) and in-app preview playback (bundled libvlc) all happen on the
  user's machine. The app contains no telemetry, no cloud calls, no API keys.
  This is enforced on two levels, not just asserted:
  - The `NoNetworkGuard` unit test fails the build if the core engine (`Vwm.Core`)
    ever gains a reference to any `System.Net.*` networking assembly, so a stray
    `HttpClient`/socket call cannot ship undetected.
  - The child processes are constrained too: every media path is required to be a
    real local file (network URLs and UNC shares are rejected before ffmpeg/Piper
    ever see them), and ffmpeg is invoked with `-protocol_whitelist file,pipe`, so
    it cannot be steered into opening `http://`, `rtsp://`, `smb://`, etc.
- **Sealed, tamper-checked tools.** In a packaged build the app resolves *only* the
  executables bundled next to it and verifies each against a SHA-256 manifest
  (`tools/tools.manifest.json`, generated at package time), refusing to run on any
  mismatch or unlisted tool. It never falls back to a `ffmpeg`/`piper` found on `PATH`,
  so a substituted binary on a corporate endpoint cannot execute through the product.
- Bundled third-party binaries (all fetched at *package* time by `packaging/build-release.ps1`
  or NuGet): [ffmpeg](https://ffmpeg.org) (GPL build from gyan.dev),
  [Piper](https://github.com/rhasspy/piper) (MIT) with voice models from
  [rhasspy/piper-voices](https://huggingface.co/rhasspy/piper-voices) (MIT), and
  [libvlc](https://www.videolan.org/vlc/libvlc.html) (LGPL, loaded dynamically) for the
  embedded preview player.
- **Verified, pinned downloads.** The build fetches specific pinned versions and checks
  each against a known SHA-256 before bundling it, aborting on any mismatch. A reviewer
  can confirm exactly what ships:

  | Artifact | SHA-256 |
  |---|---|
  | `ffmpeg-8.1.2-essentials_build.zip` | `db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec` |
  | `piper_windows_amd64.zip` (2023.11.14-2) | `f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea` |
  | `en_US-hfc_female-medium.onnx` | `914c473788fc1fa8b63ace1cdcdb44588f4ae523d3ab37df1536616835a140b7` |
  | `en_US-hfc_female-medium.onnx.json` | `03f1fa0622b80463283592d97aca9f6e89aec345a5c56b7257723e0093c58b6c` |
  | `en_US-ryan-high.onnx` | `b3990d7606e183ec8dbfba70a4607074f162de1a0c412e0180d1ff60bb154eca` |
  | `en_US-ryan-high.onnx.json` | `c6d3b98f08315cb4bebf0d49d50fc4ff491b503c64b940cd3d5ca28543b48011` |
- **Redaction is burned in, locally.** Blurred and blocked-out areas are applied by the
  bundled ffmpeg while each segment is encoded, before the subtitle burn — the sensitive
  pixels are not present in the delivered file at all, rather than being covered by an
  overlay a player could turn off. A region reaching the end of its video slice also covers
  the freeze-frames that extend it, so the content cannot resurface while the video holds.
- **Ephemeral intermediates.** Raw synthesized speech, subtitle text and intermediate
  clips live in a per-run temp workspace that is deleted when the job finishes (and the
  app's workspace is deleted when the window closes), so corporate recordings are not
  left on disk. Pass `--keep-diagnostics` (or `--work-dir`) to the CLI to retain them.
  The final video is written to a staging file and only moved into place after it
  validates, so a failed or cancelled render can never clobber a previous output.
- If policy forbids the bundled binaries, users can select **"Windows built-in voice"**
  (100% Microsoft-shipped software); ffmpeg and libvlc remain as third-party components
  (previews fall back to the user's default video player if libvlc is removed).

## Building the installer

On a Windows machine with the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0),
[Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`)
and internet access (build time only):

```powershell
powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1 -Version 1.2.3
```

The script publishes the app + `vwm` CLI self-contained, downloads ffmpeg, Piper and
two voice models into `tools/`, then compiles `packaging/installer.iss` into
`dist/VideoWalkthroughMaker-Setup.exe` — a per-user, no-admin, no-console installer
with a Start Menu shortcut. Without Inno Setup installed it falls back to producing
the old plain zip.

To bundle different or extra voices, edit the `$voices` list in
`packaging/build-release.ps1` (or drop `.onnx` + `.onnx.json` files into an installed
app's `tools/voices` folder) — every voice found there appears in the app's dropdown.

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
      [--engine piper|espeak|windows] [--voice <voice id>] \
      [--boundaries b.json] [--keep-original-audio] \
      [--sub-font Arial] [--sub-size 16] [--sub-position bottom|middle|top] \
      [--sub-background box|shadow] [--blur areas.json]
vwm detect --video in.mp4 --steps 4      # print proposed step boundaries as JSON
vwm voices                               # list the bundled Piper voice ids
```

`--blur` takes the same areas the editor produces, as a JSON array. Position and size are
fractions of the frame, so one file works whatever the recording's resolution:

```json
[
  { "startSeconds": 12, "endSeconds": 30,
    "x": 0.10, "y": 0.22, "width": 0.35, "height": 0.08,
    "style": "Blur", "strength": 10 },
  { "startSeconds": 0, "endSeconds": 9999,
    "x": 0.72, "y": 0.02, "width": 0.26, "height": 0.06,
    "style": "Solid" }
]
```

`style` is `Blur` or `Solid`; `strength` is 1-10 and applies to `Blur` only (default 10).
Both are optional. An end time past the recording simply runs to the end.

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
| `src/Vwm.Core` | The pipeline engine: script parsing, scene detection, TTS abstraction, timeline planning, SRT building, blur/redaction filters, ffmpeg rendering |
| `src/Vwm.Cli` | `vwm` command-line interface |
| `src/Vwm.App` | Avalonia desktop app (3-step wizard) |
| `src/Vwm.Tts.Windows` | Windows built-in voice engine (WinRT) |
| `tests/Vwm.Core.Tests` | Unit tests |
| `scripts/e2e-test.sh` | End-to-end pipeline test with a generated video |
| `packaging/build-release.ps1` | Publishes the app and fetches the bundled tools/voices |
| `packaging/installer.iss` | Inno Setup script compiled by `build-release.ps1` into the setup exe |
