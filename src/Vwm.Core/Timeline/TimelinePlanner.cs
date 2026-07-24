using Vwm.Core.Tts;

namespace Vwm.Core.Timeline;

public sealed record PlannerOptions
{
    /// <summary>Silence before the first sentence of each step, so narration doesn't start on the exact frame of a cut.</summary>
    public double LeadInSeconds { get; init; } = 0.3;
    /// <summary>Silence after the last sentence of each step.</summary>
    public double TailSeconds { get; init; } = 0.5;
    public double SentenceGapSeconds { get; init; } = 0.35;
    /// <summary>Output frame rate. Segment durations are quantized to whole frames at this rate
    /// so the concatenated video timeline exactly equals the planned timeline (zero drift).
    /// Must match the renderer's frame rate.</summary>
    public int Fps { get; init; } = 30;
}

/// <summary>A source-video segment and how long to freeze its last frame in the output.</summary>
public sealed record SegmentPlan(
    int StepIndex,
    double SourceStart,
    double SourceEnd,
    double HoldSeconds,
    double OutputStart,
    double OutputDuration)
{
    public double SourceDuration => SourceEnd - SourceStart;
}

public sealed record NarrationPlacement(string WavPath, double OutputStart, double GainDb = 0);

/// <summary>A cue to place on the timeline: its source-video slice, its synthesized
/// sentence clips, and its own timing knobs (per-cue, so the editor can tune one cue
/// without touching the rest).</summary>
public sealed record CueNarration(
    double SourceStart,
    double SourceEnd,
    IReadOnlyList<(string SentenceText, TtsClip Clip)> Sentences,
    double LeadInSeconds,
    double TailSeconds,
    double SentenceGapSeconds,
    double GainDb = 0);

/// <summary>A subtitle cue, in final-output time.</summary>
public sealed record SentenceCue(string Text, double Start, double Duration);

public sealed record TimelinePlan(
    IReadOnlyList<SegmentPlan> Segments,
    IReadOnlyList<NarrationPlacement> Narration,
    IReadOnlyList<SentenceCue> Cues,
    double TotalDuration);

/// <summary>
/// The heart of the sync model. Each step owns a video segment and a sequence of
/// narrated sentences; the output segment lasts as long as the longer of the two —
/// video is frozen on its last frame while narration finishes, narration is padded
/// with silence while video finishes. Everything downstream (narration track,
/// subtitles, rendering) is derived from the offsets computed here.
/// </summary>
public static class TimelinePlanner
{
    /// <summary>Boundary-driven planning: step source regions come from <paramref name="boundaries"/>
    /// and every step shares the global timing in <paramref name="options"/>. A thin adapter over
    /// the cue-driven core below.</summary>
    public static TimelinePlan Plan(
        double videoDuration,
        IReadOnlyList<double> boundaries,
        IReadOnlyList<IReadOnlyList<(string SentenceText, TtsClip Clip)>> stepNarrations,
        PlannerOptions? options = null)
    {
        var opt = options ?? new PlannerOptions();
        var stepCount = stepNarrations.Count;
        if (boundaries.Count != stepCount - 1)
            throw new ArgumentException(
                $"Expected {stepCount - 1} boundaries for {stepCount} steps, got {boundaries.Count}.");
        if (boundaries.Zip(boundaries.Skip(1)).Any(p => p.Second <= p.First) ||
            boundaries.Any(b => b <= 0 || b >= videoDuration))
        {
            throw new ArgumentException("Boundaries must be strictly increasing and inside (0, videoDuration).");
        }

        var cues = new List<CueNarration>(stepCount);
        for (var i = 0; i < stepCount; i++)
        {
            var sourceStart = i == 0 ? 0 : boundaries[i - 1];
            var sourceEnd = i == stepCount - 1 ? videoDuration : boundaries[i];
            cues.Add(new CueNarration(
                sourceStart, sourceEnd, stepNarrations[i],
                opt.LeadInSeconds, opt.TailSeconds, opt.SentenceGapSeconds));
        }
        return Plan(videoDuration, cues, opt.Fps);
    }

    /// <summary>Cue-driven planning: each cue carries its own source region and timing, so the
    /// editor can place and tune cues independently. The freeze/quantize/placement math is the
    /// same as the boundary path.</summary>
    public static TimelinePlan Plan(double videoDuration, IReadOnlyList<CueNarration> cues, int fps = 30)
    {
        if (cues.Count == 0)
            throw new ArgumentException("At least one cue is required.", nameof(cues));
        foreach (var c in cues)
        {
            if (c.SourceStart < 0 || c.SourceEnd > videoDuration || c.SourceStart >= c.SourceEnd)
                throw new ArgumentException(
                    $"Cue source region [{c.SourceStart}, {c.SourceEnd}] must satisfy 0 <= start < end <= {videoDuration}.");
        }

        var segments = new List<SegmentPlan>();
        var narration = new List<NarrationPlacement>();
        var subtitleCues = new List<SentenceCue>();
        var cursor = 0.0;
        var frameSeconds = 1.0 / fps;

        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var sourceDuration = cue.SourceEnd - cue.SourceStart;
            var sentences = cue.Sentences;
            var narrationDuration = sentences.Count == 0
                ? 0
                : cue.LeadInSeconds
                  + sentences.Sum(s => s.Clip.DurationSeconds)
                  + cue.SentenceGapSeconds * (sentences.Count - 1)
                  + cue.TailSeconds;

            // Round the segment length UP to a whole number of frames: the output timeline
            // is then an exact frame grid, so concatenated segment boundaries coincide with
            // these planned offsets to the sample — no accumulating drift. Rounding up never
            // truncates the narration or the source segment.
            var rawOutputDuration = Math.Max(sourceDuration, narrationDuration);
            var outputDuration = Math.Ceiling(rawOutputDuration / frameSeconds - 1e-9) * frameSeconds;
            var hold = outputDuration - sourceDuration;

            var t = cursor + cue.LeadInSeconds;
            foreach (var (text, clip) in sentences)
            {
                narration.Add(new NarrationPlacement(clip.WavPath, t, cue.GainDb));
                subtitleCues.Add(new SentenceCue(text, t, clip.DurationSeconds));
                t += clip.DurationSeconds + cue.SentenceGapSeconds;
            }

            segments.Add(new SegmentPlan(i, cue.SourceStart, cue.SourceEnd, hold, cursor, outputDuration));
            cursor += outputDuration;
        }

        return new TimelinePlan(segments, narration, subtitleCues, cursor);
    }
}
