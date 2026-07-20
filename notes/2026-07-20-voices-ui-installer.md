# Voices, UI overhaul, installer — notes (2026-07-20)

Three features built this session, from Matteo's follow-up requests after the sync
overhaul. All C#/XAML/script logic verified in the Linux cloud sandbox (unit tests,
e2e test, Xvfb screenshots, a fake `piper` binary wrapping espeak-ng for the engine
contract, real libvlc playback via snapshot API). The items below marked **Windows**
still need a check on a real Windows machine because the sandbox can't download
Piper/voice models (network policy) or exercise Windows-specific behavior.

## 1. Multiple, better Piper voices

- `PiperVoiceCatalog` enumerates `tools/voices/*.onnx`; every voice appears
  individually in the app's dropdown and in `vwm voices` / `vwm make --voice <id>`.
- Bundled voices (chosen with Matteo, ~175 MB total): `en_US-hfc_female-medium`
  ("Heather", natural female, the default) and `en_US-ryan-high` ("Ryan", best
  quality male). Windows built-in voice stays as the policy fallback.
- `PiperTtsEngine.Name` now includes the voice id — preview caches are per-voice.
- **Windows**: run `packaging\build-release.ps1`, listen to both voices vs. the old
  lessac-medium, confirm the quality jump.

## 2. UI redesign + in-app preview

- Design system in `App.axaml` (indigo accent, cards, stepper header); review screen
  is two-column with an embedded preview player; generate screen has a success banner.
- Step previews play inside the app via LibVLCSharp (`Controls/ClipPlayerView`), with
  play/pause/seek. Falls back to the OS player only if libvlc can't load.
- Avalonia upgraded 11.0.10 → 11.3.18 (needed by LibVLCSharp.Avalonia 3.10.0).
- Verified in sandbox: wizard end-to-end under Xvfb, player attaches/plays/seeks,
  decoded frames confirmed via VLC snapshot API (Xvfb can't composite the vout
  overlay into screenshots — expected).
- **Windows**: confirm video actually renders in the panel (Direct3D vout), audio
  plays, and `libvlc\win-x64` lands in the publish output (VideoLAN.LibVLC.Windows
  NuGet package, publishes alongside the single-file exe).

## 3. Installer + no console flashes

- `packaging/installer.iss` (Inno Setup 6): per-user install (no admin/UAC), Start
  Menu + optional desktop shortcut, launch-after-install, no console at any point.
  `build-release.ps1 -Version x.y.z` compiles it when ISCC is installed; otherwise
  falls back to the old zip with a warning.
- `ProcessRunner` now sets `CreateNoWindow = true` — previously every ffmpeg/piper
  call from the GUI could flash a console window on Windows. Audited all other
  `Process.Start` sites: ffplay already hidden; Explorer/OS-player launches are
  intentionally visible.
- **Windows**: build the installer, install on a clean machine, and watch for any
  console flash during Analyze / voice preview / clip preview / Generate.

## Sync engine

Untouched. `TimelinePlanner`/`Renderer`/`SrtBuilder` have no changes this session;
the committed e2e test and all 48 unit tests pass.
