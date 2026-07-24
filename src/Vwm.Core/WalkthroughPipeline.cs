using System.Text.RegularExpressions;
using Vwm.Core.Audio;
using Vwm.Core.Render;
using Vwm.Core.Script;
using Vwm.Core.Subtitles;
using Vwm.Core.Timeline;
using Vwm.Core.Tools;
using Vwm.Core.Tts;
using Vwm.Core.Video;

namespace Vwm.Core;

public sealed record PipelineOptions
{
    public required string VideoPath { get; init; }
    public required string ScriptText { get; init; }
    /// <summary>Pre-parsed steps (e.g. reviewed/edited in the UI). When null, <see cref="ScriptText"/> is parsed.</summary>
    public IReadOnlyList<ScriptStep>? Steps { get; init; }
    public required string OutputPath { get; init; }
    public required ITtsEngine TtsEngine { get; init; }
    /// <summary>Explicit step boundaries (seconds). When null, scene detection proposes them.</summary>
    public IReadOnlyList<double>? Boundaries { get; init; }
    public double SceneThreshold { get; init; } = 0.005;
    public bool KeepOriginalAudio { get; init; }
    public string SubtitleFont { get; init; } = "Arial";
    public int SubtitleFontSize { get; init; } = 16;
    public SubtitlePosition SubtitlePosition { get; init; } = SubtitlePosition.Bottom;
    public SubtitleBackgroundStyle SubtitleBackground { get; init; } = SubtitleBackgroundStyle.Box;
    /// <summary>Directory for intermediate files. A temp directory is created (and kept for debugging) when null.</summary>
    public string? WorkDir { get; init; }
    public PlannerOptions Planner { get; init; } = new();
}

public sealed record PipelineResult(
    string OutputPath,
    string SrtPath,
    IReadOnlyList<ScriptStep> Steps,
    IReadOnlyList<double> Boundaries,
    TimelinePlan Plan);

/// <summary>End-to-end orchestration: parse script → find boundaries → synthesize → plan → render.</summary>
public static class WalkthroughPipeline
{
    // Subtitle font is interpolated into ffmpeg/libass force_style; keep it to a plain
    // typeface name so it cannot smuggle in extra style directives.
    private static readonly Regex FontNamePattern = new("^[A-Za-z0-9 ._-]{1,64}$", RegexOptions.Compiled);

    public static async Task<PipelineResult> RunAsync(
        PipelineOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var videoPath = LocalPath.RequireInputFile(options.VideoPath, "Input video");
        var outputPath = LocalPath.RequireOutputFile(options.OutputPath, "Output video");
        if (string.Equals(videoPath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path must differ from the input video.");
        if (!FontNamePattern.IsMatch(options.SubtitleFont))
            throw new ArgumentException($"Subtitle font contains unsupported characters: '{options.SubtitleFont}'.");
        if (options.SubtitleFontSize is < 6 or > 200)
            throw new ArgumentException($"Subtitle font size {options.SubtitleFontSize} is out of range (6-200).");

        var workDir = options.WorkDir
            ?? Path.Combine(Path.GetTempPath(), "vwm", Path.GetRandomFileName());
        Directory.CreateDirectory(workDir);

        progress?.Report("Parsing script");
        var steps = options.Steps ?? ScriptParser.Parse(options.ScriptText);
        if (steps.Count == 0)
            throw new InvalidOperationException("The script is empty — nothing to narrate.");

        progress?.Report("Analyzing video");
        var duration = await SceneDetector.GetDurationAsync(videoPath, ct);
        var boundaries = options.Boundaries
            ?? SceneDetector.ProposeBoundaries(
                await SceneDetector.DetectAsync(videoPath, options.SceneThreshold, ct),
                duration, steps.Count,
                NarrationEstimator.EstimateWeights(steps));

        progress?.Report($"Generating voiceover ({options.TtsEngine.Name})");
        var stepNarrations = new List<IReadOnlyList<(string, TtsClip)>>();
        var clipIndex = 0;
        foreach (var step in steps)
        {
            var clips = new List<(string, TtsClip)>();
            foreach (var sentence in step.Sentences)
            {
                var wav = Path.Combine(workDir, $"tts_{clipIndex++:D3}.wav");
                var clip = await options.TtsEngine.SynthesizeAsync(sentence, wav, ct);
                // Trim the engine's leading/trailing silence so the clip's duration is the
                // actual speech length; the subtitle then starts on the voice, not before it.
                var trimmedDuration = SilenceTrimmer.Trim(wav);
                clips.Add((sentence, clip with { DurationSeconds = trimmedDuration }));
            }
            stepNarrations.Add(clips);
        }

        progress?.Report("Planning timeline");
        var plan = TimelinePlanner.Plan(duration, boundaries, stepNarrations, options.Planner);

        var narrationWav = Path.Combine(workDir, "narration.wav");
        WavAssembler.Assemble(
            plan.Narration.Select(n => (n.WavPath, n.OutputStart)).ToList(),
            plan.TotalDuration,
            narrationWav);

        var srt = SrtBuilder.Build(plan.Cues);
        const string srtFileName = "subtitles.srt";
        await File.WriteAllTextAsync(Path.Combine(workDir, srtFileName), srt, ct);

        var sidecarSrt = Path.ChangeExtension(outputPath, ".srt");
        await File.WriteAllTextAsync(sidecarSrt, srt, ct);

        var renderer = new Renderer(workDir, new RenderOptions
        {
            KeepOriginalAudio = options.KeepOriginalAudio,
            Fps = options.Planner.Fps,
            SubtitleFont = options.SubtitleFont,
            SubtitleFontSize = options.SubtitleFontSize,
            SubtitlePosition = options.SubtitlePosition,
            SubtitleBackground = options.SubtitleBackground,
        });
        await renderer.RenderAsync(videoPath, plan, narrationWav, srtFileName, outputPath, progress, ct);

        progress?.Report("Done");
        return new PipelineResult(outputPath, sidecarSrt, steps, boundaries, plan);
    }
}
