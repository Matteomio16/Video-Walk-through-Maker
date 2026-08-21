using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Vwm.Core.Subtitles;
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
    /// <summary>Rectangles of the frame to obscure, each over its own slice of the recording
    /// (in source-video time). Empty means no part of the video is touched.</summary>
    public IReadOnlyList<BlurRegion> BlurRegions { get; init; } = [];

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
/// Renders the final video in debuggable passes: cut + freeze-extend each segment AND
/// burn that segment's own subtitle in the same encode, then stream-copy the segments
/// together and mux the narration (no full-video re-encode). Each segment is content-
/// addressed (<c>seg_&lt;hash&gt;.mp4</c>), so an unchanged cue is never re-encoded across
/// edits. All intermediate files live in (and relative paths resolve against) the work
/// dir, which sidesteps ffmpeg filter-path escaping across platforms.
/// </summary>
public sealed class Renderer(string workDir, RenderOptions? options = null)
{
    private readonly RenderOptions _opt = options ?? new RenderOptions();
    private static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

    public async Task RenderAsync(
        string videoPath,
        TimelinePlan plan,
        string narrationWavPath,
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

            // Cues that fall in this segment, shifted to segment-local time; burned into the
            // segment so the subtitle travels with its frames (concat/mux stay copy-only).
            var localCues = plan.Cues
                .Where(c => c.Start >= seg.OutputStart - 1e-6 && c.Start < seg.OutputStart + seg.OutputDuration - 1e-6)
                .Select(c => c with { Start = c.Start - seg.OutputStart })
                .ToList();
            var segSrt = localCues.Count > 0 ? SrtBuilder.Build(localCues) : "";

            // Blur regions are given in source-video time; map them onto this segment's own
            // output clock (which runs longer than the source slice when the video freezes).
            var blurs = BlurFilterBuilder.ForSegment(
                _opt.BlurRegions, seg.SourceStart, seg.SourceEnd, seg.OutputDuration);

            var hash = SegmentHash(seg, segSrt, blurs);
            var segFile = $"seg_{hash}.mp4";
            segmentFiles.Add(segFile);
            if (File.Exists(Path.Combine(workDir, segFile)))
                continue; // cache hit: identical content already rendered

            // Over-provision the freeze by a few frames, then cut to an exact frame count so
            // the segment is deterministically OutputDuration long (matching the quantized plan).
            var exactFrames = (int)Math.Round(seg.OutputDuration * _opt.Fps);
            var sourceChain = $"trim=duration={F(seg.SourceDuration)},setpts=PTS-STARTPTS," +
                              $"tpad=stop_mode=clone:stop_duration={F(seg.HoldSeconds + 0.25)}," +
                              $"fps={_opt.Fps},format=yuv420p";
            string? subtitleFilter = null;
            if (segSrt.Length > 0)
            {
                var srtFile = $"seg_{hash}.srt";
                await File.WriteAllTextAsync(Path.Combine(workDir, srtFile), segSrt, ct);
                subtitleFilter = $"subtitles={srtFile}:force_style='{_opt.SubtitleStyle}'";
            }
            // Blur first, subtitles last: the narration text sits on top of the redaction
            // rather than under it.
            var vf = BlurFilterBuilder.BuildGraph(sourceChain, blurs, subtitleFilter);

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

        progress?.Report("Adding voiceover");
        // Subtitles are already in the segment frames, so the mux copies the video stream
        // (-c:v copy) and only lays down (or mixes) the audio - no full-video re-encode.
        var finalArgs = new List<string>
        {
            "-hide_banner", "-y", "-protocol_whitelist", "file,pipe",
            "-i", "combined.mp4",
            "-i", Path.GetFullPath(narrationWavPath),
        };
        if (_opt.KeepOriginalAudio)
        {
            finalArgs.AddRange([
                "-filter_complex",
                "[0:a]volume=0.125[bg];[1:a][bg]amix=inputs=2:duration=first:normalize=0[a]",
                "-map", "0:v", "-map", "[a]",
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
            "-c:v", "copy",
            "-c:a", "aac", "-b:a", "160k",
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

    /// <summary>Content key for a segment's rendered file: everything that affects its pixels
    /// or audio. Identical segments across edits reuse the cached encode.</summary>
    private string SegmentHash(SegmentPlan seg, string segSrt, IReadOnlyList<AppliedBlur> blurs)
    {
        // The blur is described by the rectangle, its style/strength and the window it is on
        // for - exactly what changes the segment's pixels - so editing a region invalidates
        // only the segments it actually covers.
        var blurKey = string.Join(",", blurs.Select(b =>
            $"{F(b.Region.X)};{F(b.Region.Y)};{F(b.Region.Width)};{F(b.Region.Height)};" +
            $"{b.Region.Style};{b.Region.Strength};{F(b.LocalStart)};{F(b.LocalEnd)}"));
        var s = string.Join("|",
            F(seg.SourceStart), F(seg.SourceEnd), F(seg.HoldSeconds), F(seg.OutputDuration),
            _opt.Fps, _opt.KeepOriginalAudio, _opt.SubtitleStyle, segSrt, blurKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
    }
}
