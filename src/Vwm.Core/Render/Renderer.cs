using System.Globalization;
using Vwm.Core.Timeline;
using Vwm.Core.Tools;
using Vwm.Core.Video;

namespace Vwm.Core.Render;

/// <summary>Subtitle placement. Values are ffmpeg/libass legacy-SSA alignment codes
/// for the horizontally-centered column (2=bottom, 6=top, 10=middle) — verified by
/// render; note this is NOT the ASS "numpad" scheme where these would differ.</summary>
public enum SubtitlePosition { Bottom = 2, Top = 6, Middle = 10 }

/// <summary>Backing behind subtitle text: the semi-transparent grey box, or a plain
/// drop shadow + outline (no box) so the text sits directly on the video.</summary>
public enum SubtitleBackgroundStyle { Box, Shadow }

public sealed record RenderOptions
{
    public bool KeepOriginalAudio { get; init; }
    /// <summary>Output frame rate. Screen recordings are often variable-frame-rate; normalizing makes concat safe.</summary>
    public int Fps { get; init; } = 30;
    public string SubtitleFont { get; init; } = "Arial";
    public int SubtitleFontSize { get; init; } = 16;
    public SubtitlePosition SubtitlePosition { get; init; } = SubtitlePosition.Bottom;
    public SubtitleBackgroundStyle SubtitleBackground { get; init; } = SubtitleBackgroundStyle.Box;

    /// <summary>ASS force_style string built from the chosen font, size, placement and backing.</summary>
    public string SubtitleStyle
    {
        get
        {
            var s = $"FontName={SubtitleFont},FontSize={SubtitleFontSize},PrimaryColour=&H00FFFFFF," +
                    $"Alignment={(int)SubtitlePosition},MarginV=28,";
            // BorderStyle=4 draws the opaque box in BackColour; BorderStyle=1 draws a
            // coloured outline (Outline px) plus a drop shadow (Shadow px) instead.
            return s + (SubtitleBackground == SubtitleBackgroundStyle.Box
                ? "BackColour=&H90000000,BorderStyle=4,Outline=0,Shadow=0"
                : "OutlineColour=&H00000000,BackColour=&H80000000,BorderStyle=1,Outline=2,Shadow=2");
        }
    }
}

/// <summary>
/// Renders the final video in debuggable passes: cut + freeze-extend each segment,
/// concat the segments, then mux the narration track and burn the subtitles.
/// All intermediate files live in (and relative paths resolve against) the work dir,
/// which sidesteps ffmpeg filter-path escaping across platforms.
/// </summary>
public sealed class Renderer(string workDir, RenderOptions? options = null)
{
    private readonly RenderOptions _opt = options ?? new RenderOptions();
    private static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

    public async Task RenderAsync(
        string videoPath,
        TimelinePlan plan,
        string narrationWavPath,
        string srtFileName,
        string outputPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDir);
        var ffmpeg = ToolLocator.FfmpegPath;
        videoPath = Path.GetFullPath(videoPath);

        var segmentFiles = new List<string>();
        foreach (var seg in plan.Segments)
        {
            progress?.Report($"Rendering segment {seg.StepIndex + 1}/{plan.Segments.Count}");
            var segFile = $"seg_{seg.StepIndex:D3}.mp4";
            segmentFiles.Add(segFile);

            // Over-provision the freeze by a few frames, then cut to an exact frame count so
            // the segment is deterministically OutputDuration long (matching the quantized plan).
            var exactFrames = (int)Math.Round(seg.OutputDuration * _opt.Fps);
            var vf = $"trim=duration={F(seg.SourceDuration)},setpts=PTS-STARTPTS," +
                     $"tpad=stop_mode=clone:stop_duration={F(seg.HoldSeconds + 0.25)}," +
                     $"fps={_opt.Fps},format=yuv420p";

            var args = new List<string>
            {
                "-hide_banner", "-y", "-protocol_whitelist", "file,pipe",
                "-ss", F(seg.SourceStart),
                "-i", videoPath,
                "-vf", vf,
            };
            if (_opt.KeepOriginalAudio)
            {
                args.AddRange([
                    "-af",
                    $"atrim=duration={F(seg.SourceDuration)},asetpts=PTS-STARTPTS," +
                    $"apad=whole_dur={F(seg.OutputDuration)}",
                    "-c:a", "aac", "-b:a", "128k",
                ]);
            }
            else
            {
                args.Add("-an");
            }
            // Always bound the segment by an exact video frame count so its duration matches
            // the frame-quantized plan (and, in keep-audio mode, so audio and video stay locked).
            args.AddRange([
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                "-frames:v", exactFrames.ToString(CultureInfo.InvariantCulture), segFile,
            ]);

            await ProcessRunner.RunAsync(ffmpeg, args, workingDirectory: workDir, ct: ct);
        }

        progress?.Report("Joining segments");
        var listFile = Path.Combine(workDir, "concat.txt");
        await File.WriteAllLinesAsync(listFile, segmentFiles.Select(f => $"file '{f}'"), ct);
        await ProcessRunner.RunAsync(
            ffmpeg,
            ["-hide_banner", "-y", "-protocol_whitelist", "file,pipe", "-f", "concat", "-safe", "0", "-i", "concat.txt", "-c", "copy", "combined.mp4"],
            workingDirectory: workDir, ct: ct);

        progress?.Report("Adding voiceover and subtitles");
        var finalArgs = new List<string>
        {
            "-hide_banner", "-y", "-protocol_whitelist", "file,pipe",
            "-i", "combined.mp4",
            "-i", Path.GetFullPath(narrationWavPath),
            "-vf", $"subtitles={srtFileName}:force_style='{_opt.SubtitleStyle}'",
        };
        if (_opt.KeepOriginalAudio)
        {
            finalArgs.RemoveRange(finalArgs.Count - 2, 2);
            finalArgs.AddRange([
                "-filter_complex",
                $"[0:v]subtitles={srtFileName}:force_style='{_opt.SubtitleStyle}'[v];" +
                "[0:a]volume=0.125[bg];[1:a][bg]amix=inputs=2:duration=first:normalize=0[a]",
                "-map", "[v]", "-map", "[a]",
            ]);
        }
        else
        {
            finalArgs.AddRange(["-map", "0:v", "-map", "1:a"]);
        }
        // Render to a staging file in the output's own directory, then atomically move it
        // into place only after it validates. A failed or cancelled job can never leave a
        // half-written file at (or destroy a prior) outputPath.
        outputPath = Path.GetFullPath(outputPath);
        var outDir = Path.GetDirectoryName(outputPath)!;
        var staging = Path.Combine(outDir, $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}{Path.GetExtension(outputPath)}");
        finalArgs.AddRange([
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
            "-c:a", "aac", "-b:a", "160k",
            "-t", F(plan.TotalDuration),
            "-movflags", "+faststart",
            staging,
        ]);
        try
        {
            await ProcessRunner.RunAsync(ffmpeg, finalArgs, workingDirectory: workDir, ct: ct);
            if (!(await SceneDetector.GetDurationAsync(staging, ct) > 0))
                throw new InvalidOperationException("Rendered file failed validation (zero duration).");
            File.Move(staging, outputPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(staging)) File.Delete(staging); } catch { /* best effort */ }
            throw;
        }
    }
}
