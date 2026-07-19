using System.Globalization;
using Vwm.Core.Timeline;
using Vwm.Core.Tools;

namespace Vwm.Core.Render;

public sealed record RenderOptions
{
    public bool KeepOriginalAudio { get; init; }
    /// <summary>Output frame rate. Screen recordings are often variable-frame-rate; normalizing makes concat safe.</summary>
    public int Fps { get; init; } = 30;
    public string SubtitleStyle { get; init; } =
        "FontName=Arial,FontSize=16,PrimaryColour=&H00FFFFFF,BackColour=&H90000000,BorderStyle=4,Outline=0,Shadow=0,MarginV=28";
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
                "-hide_banner", "-y",
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
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-t", F(seg.OutputDuration), segFile,
                ]);
            }
            else
            {
                args.AddRange([
                    "-an",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-frames:v", exactFrames.ToString(CultureInfo.InvariantCulture), segFile,
                ]);
            }

            await ProcessRunner.RunAsync(ffmpeg, args, workingDirectory: workDir, ct: ct);
        }

        progress?.Report("Joining segments");
        var listFile = Path.Combine(workDir, "concat.txt");
        await File.WriteAllLinesAsync(listFile, segmentFiles.Select(f => $"file '{f}'"), ct);
        await ProcessRunner.RunAsync(
            ffmpeg,
            ["-hide_banner", "-y", "-f", "concat", "-safe", "0", "-i", "concat.txt", "-c", "copy", "combined.mp4"],
            workingDirectory: workDir, ct: ct);

        progress?.Report("Adding voiceover and subtitles");
        var finalArgs = new List<string>
        {
            "-hide_banner", "-y",
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
        finalArgs.AddRange([
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
            "-c:a", "aac", "-b:a", "160k",
            "-t", F(plan.TotalDuration),
            "-movflags", "+faststart",
            Path.GetFullPath(outputPath),
        ]);
        await ProcessRunner.RunAsync(ffmpeg, finalArgs, workingDirectory: workDir, ct: ct);
    }
}
