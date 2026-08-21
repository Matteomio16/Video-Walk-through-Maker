using System.Text.RegularExpressions;
using Vwm.Core.Audio;
using Vwm.Core.Project;
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
    /// <summary>Parts of the frame to obscure (blur or grey block), each over its own slice
    /// of the recording. Empty leaves every frame untouched.</summary>
    public IReadOnlyList<BlurRegion> BlurRegions { get; init; } = [];
    /// <summary>Directory for intermediate files. A temp directory is created when null.</summary>
    public string? WorkDir { get; init; }
    /// <summary>Keep the intermediate work directory (raw speech, clips, combined video) after the
    /// run. Off by default so sensitive recordings are not left on disk.</summary>
    public bool KeepIntermediates { get; init; }
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
        // Checked before a single frame is encoded: a bad region should be a sentence the
        // user can act on, not an ffmpeg filter-graph error minutes into the render.
        BlurRegion.ValidateAll(options.BlurRegions, videoDuration: 0);

        var workDir = options.WorkDir
            ?? Path.Combine(Path.GetTempPath(), "vwm", Path.GetRandomFileName());
        Directory.CreateDirectory(workDir);
        try
        {
            return await RunCoreAsync(options, videoPath, outputPath, workDir, progress, ct);
        }
        finally
        {
            if (!options.KeepIntermediates)
                TryDeleteDir(workDir);
        }
    }

    private static async Task<PipelineResult> RunCoreAsync(
        PipelineOptions options, string videoPath, string outputPath, string workDir,
        IProgress<string>? progress, CancellationToken ct)
    {
        // Rough disk preflight: intermediates (re-encoded segments + combined video) plus the
        // final output run a few times the source size. Fail early with a clear message rather
        // than deep into rendering.
        RequireFreeSpace(workDir, Math.Max(new FileInfo(videoPath).Length * 4, 200L * 1024 * 1024));

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

        var renderer = new Renderer(workDir, new RenderOptions
        {
            KeepOriginalAudio = options.KeepOriginalAudio,
            Fps = options.Planner.Fps,
            SubtitleFont = options.SubtitleFont,
            SubtitleFontSize = options.SubtitleFontSize,
            SubtitlePosition = options.SubtitlePosition,
            SubtitleBackground = options.SubtitleBackground,
            BlurRegions = options.BlurRegions,
        });
        await renderer.RenderAsync(videoPath, plan, narrationWav, outputPath, progress, ct);

        // Write the sidecar only once the render has succeeded, so a failed job can never
        // clobber a previous subtitle file next to the (untouched) previous output.
        var sidecarSrt = Path.ChangeExtension(outputPath, ".srt");
        await File.WriteAllTextAsync(sidecarSrt, srt, ct);

        progress?.Report("Done");
        return new PipelineResult(outputPath, sidecarSrt, steps, boundaries, plan);
    }

    /// <summary>
    /// Renders from an editable project: cached per-cue synthesis → cue-driven plan → render.
    /// <paramref name="engineForVoice"/> maps a voice id to an engine (so Core stays free of the
    /// GUI/CLI engine wiring). Intermediates (synth + segment caches) are kept when
    /// <paramref name="keepIntermediates"/> is set, which the editor does for a persistent
    /// project workspace so re-renders reuse unchanged work.
    /// </summary>
    public static async Task<PipelineResult> RunProjectAsync(
        WalkthroughProject project,
        Func<string, ITtsEngine> engineForVoice,
        string outputPath,
        string? workDir = null,
        bool keepIntermediates = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var videoPath = LocalPath.RequireInputFile(project.VideoPath, "Input video");
        var output = LocalPath.RequireOutputFile(outputPath, "Output video");
        if (string.Equals(videoPath, output, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path must differ from the input video.");
        if (project.Cues.Count == 0)
            throw new InvalidOperationException("The project has no cues.");
        if (!FontNamePattern.IsMatch(project.GlobalSubtitle.Font))
            throw new ArgumentException($"Subtitle font contains unsupported characters: '{project.GlobalSubtitle.Font}'.");
        if (project.GlobalSubtitle.Size is < 6 or > 200)
            throw new ArgumentException($"Subtitle font size {project.GlobalSubtitle.Size} is out of range (6-200).");
        BlurRegion.ValidateAll(project.BlurRegions, project.VideoDuration);

        var wd = workDir ?? Path.Combine(Path.GetTempPath(), "vwm", Path.GetRandomFileName());
        Directory.CreateDirectory(wd);
        try
        {
            RequireFreeSpace(wd, Math.Max(new FileInfo(videoPath).Length * 4, 200L * 1024 * 1024));

            var cues = await new CueSynthesizer(engineForVoice, wd).SynthesizeAsync(project, progress, ct);

            progress?.Report("Planning timeline");
            var plan = TimelinePlanner.Plan(project.VideoDuration, cues, new PlannerOptions().Fps);

            var narrationWav = Path.Combine(wd, "narration.wav");
            WavAssembler.Assemble(
                plan.Narration.Select(n => (n.WavPath, n.OutputStart, n.GainDb)).ToList(),
                plan.TotalDuration, narrationWav);

            var srt = SrtBuilder.Build(plan.Cues);
            var renderer = new Renderer(wd, new RenderOptions
            {
                KeepOriginalAudio = project.KeepOriginalAudio,
                SubtitleFont = project.GlobalSubtitle.Font,
                SubtitleFontSize = project.GlobalSubtitle.Size,
                SubtitlePosition = project.GlobalSubtitle.Position,
                SubtitleBackground = project.GlobalSubtitle.Background,
                BlurRegions = project.BlurRegions,
            });
            await renderer.RenderAsync(videoPath, plan, narrationWav, output, progress, ct);

            var sidecar = Path.ChangeExtension(output, ".srt");
            await File.WriteAllTextAsync(sidecar, srt, ct);

            progress?.Report("Done");
            var steps = project.Cues
                .Select((c, i) => new ScriptStep(i, c.Text, ScriptParser.SplitSentences(c.Text)))
                .ToList();
            var boundaries = project.Cues.Skip(1).Select(c => c.SourceStart).ToList();
            return new PipelineResult(output, sidecar, steps, boundaries, plan);
        }
        finally
        {
            if (!keepIntermediates)
                TryDeleteDir(wd);
        }
    }

    private static void RequireFreeSpace(string dir, long bytesNeeded)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root))
                return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < bytesNeeded)
                throw new IOException(
                    $"Not enough free disk space to render: need about {bytesNeeded / (1024 * 1024)} MB, " +
                    $"{free / (1024 * 1024)} MB available on {root}.");
        }
        catch (Exception ex) when (ex is not IOException)
        {
            // Drive not queryable (e.g. an unusual mount) — skip the preflight rather than block the run.
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort: leftover temp files are cleaned by the OS eventually */ }
    }
}
