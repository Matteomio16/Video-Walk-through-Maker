using Vwm.Core.Tts;

namespace Vwm.Core.Timeline;

public sealed record PlannerOptions
{
    /// <summary>Silence before the first sentence of each step, so narration doesn't start on the exact frame of a cut.</summary>
    public double LeadInSeconds { get; init; } = 0.3;
    /// <summary>Silence after the last sentence of each step.</summary>
    public double TailSeconds { get; init; } = 0.5;
    public double SentenceGapSeconds { get; init; } = 0.35;
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

public sealed record NarrationPlacement(string WavPath, double OutputStart);

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

        var segments = new List<SegmentPlan>();
        var narration = new List<NarrationPlacement>();
        var cues = new List<SentenceCue>();
        var cursor = 0.0;

        for (var i = 0; i < stepCount; i++)
        {
            var sourceStart = i == 0 ? 0 : boundaries[i - 1];
            var sourceEnd = i == stepCount - 1 ? videoDuration : boundaries[i];
            var sourceDuration = sourceEnd - sourceStart;

            var sentences = stepNarrations[i];
            var narrationDuration = sentences.Count == 0
                ? 0
                : opt.LeadInSeconds
                  + sentences.Sum(s => s.Clip.DurationSeconds)
                  + opt.SentenceGapSeconds * (sentences.Count - 1)
                  + opt.TailSeconds;

            var outputDuration = Math.Max(sourceDuration, narrationDuration);
            var hold = outputDuration - sourceDuration;

            var t = cursor + opt.LeadInSeconds;
            foreach (var (text, clip) in sentences)
            {
                narration.Add(new NarrationPlacement(clip.WavPath, t));
                cues.Add(new SentenceCue(text, t, clip.DurationSeconds));
                t += clip.DurationSeconds + opt.SentenceGapSeconds;
            }

            segments.Add(new SegmentPlan(i, sourceStart, sourceEnd, hold, cursor, outputDuration));
            cursor += outputDuration;
        }

        return new TimelinePlan(segments, narration, cues, cursor);
    }
}
