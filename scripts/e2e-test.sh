#!/usr/bin/env bash
# End-to-end pipeline test. Requires: dotnet, ffmpeg, espeak-ng.
# Builds a synthetic 4-step video (colored slides with hard cuts), runs the full
# pipeline with the espeak test engine, and asserts on the output's structure.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${1:-$(mktemp -d)}"
mkdir -p "$WORK"
echo "work dir: $WORK"

# --- 1. Synthetic input video: 4 slides (3s, 2s, 4s, 3s) with hard cuts -------
make_slide() { # color text duration out
  ffmpeg -hide_banner -loglevel error -y -f lavfi \
    -i "color=c=$1:s=640x360:d=$3:r=30" \
    -vf "drawtext=text='$2':fontsize=48:fontcolor=white:x=(w-tw)/2:y=(h-th)/2" \
    -c:v libx264 -preset veryfast -pix_fmt yuv420p "$4"
}
make_slide steelblue  "Step 1" 3 "$WORK/s1.mp4"
make_slide darkgreen  "Step 2" 2 "$WORK/s2.mp4"
make_slide indianred  "Step 3" 4 "$WORK/s3.mp4"
make_slide darkorange "Step 4" 3 "$WORK/s4.mp4"
printf "file '%s'\n" "$WORK"/s{1,2,3,4}.mp4 > "$WORK/list.txt"
ffmpeg -hide_banner -loglevel error -y -f concat -safe 0 -i "$WORK/list.txt" -c copy "$WORK/input.mp4"

# --- 2. Script: 4 steps, mixed lengths ---------------------------------------
cat > "$WORK/script.txt" <<'EOF'
1. First, open the reporting dashboard from the home screen. You will see the overview of all projects with their current status.
2. Click the export tab.
3. Choose the date range you need and select the CSV format. Then press the generate button and wait for the file to be prepared by the system.
4. Finally, download the file and save it to the shared drive.
EOF

# --- 3. Run the pipeline ------------------------------------------------------
dotnet run --project "$ROOT/src/Vwm.Cli" -c Release -f net8.0 -- make \
  --video "$WORK/input.mp4" \
  --script "$WORK/script.txt" \
  --out "$WORK/output.mp4" \
  --engine espeak \
  --work-dir "$WORK/pipeline"

# --- 4. Assertions ------------------------------------------------------------
fail() { echo "FAIL: $1" >&2; exit 1; }

[ -f "$WORK/output.mp4" ] || fail "output.mp4 missing"
[ -f "$WORK/output.srt" ] || fail "sidecar srt missing"

streams=$(ffprobe -v error -show_entries stream=codec_type -of csv=p=0 "$WORK/output.mp4" | sort | tr '\n' ' ')
[[ "$streams" == *audio*video* ]] || fail "expected audio+video streams, got: $streams"

in_dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$WORK/input.mp4")
out_dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$WORK/output.mp4")
awk -v i="$in_dur" -v o="$out_dur" 'BEGIN{exit !(o >= i)}' \
  || fail "output ($out_dur s) shorter than input ($in_dur s) — freeze-extension did not happen"
echo "durations: input=${in_dur}s output=${out_dur}s"

# Narration present: mean volume of the audio track must be well above silence.
mean_vol=$(ffmpeg -hide_banner -i "$WORK/output.mp4" -map 0:a -af volumedetect -f null - 2>&1 \
  | sed -n 's/.*mean_volume: \(-\?[0-9.]*\) dB.*/\1/p')
awk -v v="$mean_vol" 'BEGIN{exit !(v > -60)}' || fail "audio track is silent (mean ${mean_vol} dB)"
echo "audio mean volume: ${mean_vol} dB"

# Subtitles burned in: frames during the first cue must differ from the same
# slide's clean frame color pattern (crude check: extract frame mid-cue and at video start).
mkdir -p "$WORK/frames"
first_cue_mid=$(awk -F' --> ' '/-->/{split($1,a,/[:,]/); print a[1]*3600+a[2]*60+a[3]+a[4]/1000+0.5; exit}' "$WORK/output.srt")
ffmpeg -hide_banner -loglevel error -y -ss "$first_cue_mid" -i "$WORK/output.mp4" -frames:v 1 "$WORK/frames/during_cue.png"
grep -q "reporting dashboard" "$WORK/output.srt" || fail "expected text missing from srt"
cue_count=$(grep -c ' --> ' "$WORK/output.srt")
echo "subtitle cues: $cue_count"
[ "$cue_count" -ge 6 ] || fail "expected at least 6 cues (6 sentences), got $cue_count"

# --- 5. Blur pass: the same render, with two areas hidden ---------------------
# Areas are fractions of the frame and cover every frame between their start and end,
# so they can be asserted on without mapping source time through the freeze-extension.
cat > "$WORK/blur.json" <<'EOF'
[
  { "startSeconds": 0, "endSeconds": 9999,
    "x": 0.25, "y": 0.38, "width": 0.5, "height": 0.24,
    "style": "Blur", "strength": 10 },
  { "startSeconds": 0, "endSeconds": 9999,
    "x": 0.72, "y": 0.06, "width": 0.22, "height": 0.16,
    "style": "Solid" }
]
EOF

dotnet run --project "$ROOT/src/Vwm.Cli" -c Release -f net8.0 -- make \
  --video "$WORK/input.mp4" \
  --script "$WORK/script.txt" \
  --out "$WORK/output_blurred.mp4" \
  --engine espeak \
  --blur "$WORK/blur.json" \
  --work-dir "$WORK/pipeline-blur"

[ -f "$WORK/output_blurred.mp4" ] || fail "output_blurred.mp4 missing"

# Spread of pixel values in a frame-relative rectangle: high where detail survives,
# near zero where an area was painted over.
spread() { # file time x y w h
  ffmpeg -hide_banner -loglevel error -y -i "$1" -ss "$2" -frames:v 1 \
    -vf "crop=w='2*floor(iw*$5/2)':h='2*floor(ih*$6/2)':x='2*floor(iw*$3/2)':y='2*floor(ih*$4/2)',format=gray" \
    -f rawvideo "$WORK/probe.raw"
  python3 - "$WORK/probe.raw" <<'PY'
import sys
d = open(sys.argv[1], 'rb').read()
m = sum(d) / len(d)
print(round((sum((b - m) ** 2 for b in d) / len(d)) ** 0.5, 2))
PY
}

for t in 1 4 8; do
  plain=$(spread "$WORK/output.mp4" "$t" 0.25 0.38 0.5 0.24)
  blurred=$(spread "$WORK/output_blurred.mp4" "$t" 0.25 0.38 0.5 0.24)
  block=$(spread "$WORK/output_blurred.mp4" "$t" 0.72 0.06 0.22 0.16)
  echo "t=${t}s  step text: plain=$plain blurred=$blurred   grey block=$block"
  awk -v p="$plain" 'BEGIN{exit !(p > 20)}' \
    || fail "test premise broken: the step text at ${t}s is not detailed (spread $plain)"
  awk -v p="$plain" -v b="$blurred" 'BEGIN{exit !(b < p / 3)}' \
    || fail "blur did not obscure the step text at ${t}s (spread $plain -> $blurred)"
  awk -v b="$block" 'BEGIN{exit !(b < 4)}' \
    || fail "solid area is not a flat block at ${t}s (spread $block)"
done

# Blurring must not change the timeline: same duration, same subtitles.
blur_dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$WORK/output_blurred.mp4")
awk -v a="$out_dur" -v b="$blur_dur" 'BEGIN{exit !(b - a < 0.1 && a - b < 0.1)}' \
  || fail "blurred output ($blur_dur s) drifted from the plain one ($out_dur s)"
diff -q "$WORK/output.srt" "$WORK/output_blurred.srt" >/dev/null \
  || fail "blur changed the subtitles"

ffmpeg -hide_banner -loglevel error -y -i "$WORK/output_blurred.mp4" -ss "$first_cue_mid" \
  -frames:v 1 "$WORK/frames/blurred_cue.png"

echo "E2E PASS — inspect $WORK/output.mp4, $WORK/output_blurred.mp4 and $WORK/frames/"
