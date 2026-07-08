using Vwm.Core.Audio;
using Vwm.Core.Render;
using Vwm.Core.Script;
using Vwm.Core.Subtitles;
using Vwm.Core.Timeline;
using Vwm.Core.Tts;
using Vwm.Core.Video;

namespace Vwm.Core;

public sealed record PipelineOptions
{
    public required string VideoPath { get; init; }
    public required string ScriptText { get; init; }
    public required string OutputPath { get; init; }
    public required ITtsEngine TtsEngine { get; init; }
    /// <summary>Explicit step boundaries (seconds). When null, scene detection proposes them.</summary>
    public IReadOnlyList<double>? Boundaries { get; init; }
    public double SceneThreshold { get; init; } = 0.04;
    public bool KeepOriginalAudio { get; init; }
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
    public static async Task<PipelineResult> RunAsync(
        PipelineOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var workDir = options.WorkDir
            ?? Path.Combine(Path.GetTempPath(), "vwm", Path.GetRandomFileName());
        Directory.CreateDirectory(workDir);

        progress?.Report("Parsing script");
        var steps = ScriptParser.Parse(options.ScriptText);
        if (steps.Count == 0)
            throw new InvalidOperationException("The script is empty — nothing to narrate.");

        progress?.Report("Analyzing video");
        var duration = await SceneDetector.GetDurationAsync(options.VideoPath, ct);
        var boundaries = options.Boundaries
            ?? SceneDetector.ProposeBoundaries(
                await SceneDetector.DetectAsync(options.VideoPath, options.SceneThreshold, ct),
                duration, steps.Count);

        progress?.Report($"Generating voiceover ({options.TtsEngine.Name})");
        var stepNarrations = new List<IReadOnlyList<(string, TtsClip)>>();
        var clipIndex = 0;
        foreach (var step in steps)
        {
            var clips = new List<(string, TtsClip)>();
            foreach (var sentence in step.Sentences)
            {
                var wav = Path.Combine(workDir, $"tts_{clipIndex++:D3}.wav");
                clips.Add((sentence, await options.TtsEngine.SynthesizeAsync(sentence, wav, ct)));
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

        var sidecarSrt = Path.ChangeExtension(options.OutputPath, ".srt");
        await File.WriteAllTextAsync(sidecarSrt, srt, ct);

        var renderer = new Renderer(workDir, new RenderOptions { KeepOriginalAudio = options.KeepOriginalAudio });
        await renderer.RenderAsync(options.VideoPath, plan, narrationWav, srtFileName, options.OutputPath, progress, ct);

        progress?.Report("Done");
        return new PipelineResult(options.OutputPath, sidecarSrt, steps, boundaries, plan);
    }
}
