# Third-party notices

Video Walk-through Maker bundles and/or dynamically loads the third-party components
listed below. Their licenses are reproduced under `licenses/` in the installed
application folder (copied verbatim from each upstream distribution at build time).
A machine-readable inventory of the managed (.NET) dependencies ships alongside this
file as `sbom.xml` (CycloneDX).

| Component | Version | License | Linkage |
|---|---|---|---|
| FFmpeg (gyan.dev "essentials" build) | 8.1.2 | **GPL-3.0-or-later** | separate bundled executables (`ffmpeg.exe`, `ffprobe.exe`, `ffplay.exe`) |
| Piper | 2023.11.14-2 | MIT | separate bundled executable (`piper.exe`) |
| Piper voice models (rhasspy/piper-voices) | v1.0.0 | see per-voice license in `piper-voices` | data files (`*.onnx`) |
| libVLC (VideoLAN) | 3.x | LGPL-2.1-or-later | dynamically loaded native library (optional; preview only) |
| Avalonia, CommunityToolkit.Mvvm, LibVLCSharp, .NET runtime | see `sbom.xml` | MIT / LGPL | managed NuGet packages |

## FFmpeg — GPL-3.0 source offer

The bundled FFmpeg is a GPL build. In accordance with the GNU General Public License,
version 3, the complete corresponding source code is available:

- Upstream source: <https://ffmpeg.org/download.html> (release 8.1.2).
- Build configuration: the gyan.dev "essentials" build recipe,
  <https://github.com/GyanD/codexffmpeg> / <https://www.gyan.dev/ffmpeg/builds/>.

The full text of the GNU GPL v3 is included in `licenses/`. If you received this
application and want the corresponding FFmpeg source on physical media, that source is
identical to the upstream release above; contact the internal owner of this tool.

## Removing the bundled binaries

FFmpeg (GPL) and libVLC (LGPL) are third-party components. Selecting the
**Windows built-in voice** avoids the Piper binary; the app still requires FFmpeg to
render video. See `README.md` -> "For IT / security review".
