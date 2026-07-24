using Vwm.Core.Timeline;
using Vwm.Core.Tts;
using Xunit;

namespace Vwm.Core.Tests;

public class CuePlannerTests
{
    private static IReadOnlyList<(string, TtsClip)> Step(params double[] d) =>
        d.Select((x, i) => ($"s{i}", new TtsClip($"c{i}.wav", x))).ToList();

    private static CueNarration Cue(double s, double e, IReadOnlyList<(string, TtsClip)> sentences) =>
        new(s, e, sentences, LeadInSeconds: 0.5, TailSeconds: 0.5, SentenceGapSeconds: 0.25);

    [Fact]
    public void Cue_path_matches_boundary_path()
    {
        var opt = new PlannerOptions { LeadInSeconds = 0.5, TailSeconds = 0.5, SentenceGapSeconds = 0.25 };
        var viaBoundaries = TimelinePlanner.Plan(10, [5.0], [Step(2.0, 3.0), Step(1.0)], opt);
        var viaCues = TimelinePlanner.Plan(10,
            [Cue(0, 5, Step(2.0, 3.0)), Cue(5, 10, Step(1.0))], opt.Fps);

        Assert.Equal(viaBoundaries.TotalDuration, viaCues.TotalDuration, 6);
        Assert.Equal(viaBoundaries.Segments.Count, viaCues.Segments.Count);
        for (var i = 0; i < viaBoundaries.Segments.Count; i++)
        {
            Assert.Equal(viaBoundaries.Segments[i].OutputStart, viaCues.Segments[i].OutputStart, 6);
            Assert.Equal(viaBoundaries.Segments[i].OutputDuration, viaCues.Segments[i].OutputDuration, 6);
            Assert.Equal(viaBoundaries.Segments[i].HoldSeconds, viaCues.Segments[i].HoldSeconds, 6);
        }
        for (var i = 0; i < viaBoundaries.Cues.Count; i++)
            Assert.Equal(viaBoundaries.Cues[i].Start, viaCues.Cues[i].Start, 6);
    }

    [Fact]
    public void Per_cue_timing_is_honored_independently()
    {
        // Second cue gets a longer lead-in; its first narration should start that much later.
        var cues = new[]
        {
            new CueNarration(0, 5, Step(1.0), 0.3, 0.5, 0.35),
            new CueNarration(5, 10, Step(1.0), 1.2, 0.5, 0.35),
        };
        var plan = TimelinePlanner.Plan(10, cues);
        Assert.Equal(0.3, plan.Narration[0].OutputStart, 6);
        Assert.Equal(plan.Segments[1].OutputStart + 1.2, plan.Narration[1].OutputStart, 6);
    }

    [Fact]
    public void Rejects_source_region_outside_video()
    {
        Assert.Throws<ArgumentException>(() =>
            TimelinePlanner.Plan(10, [Cue(0, 12, Step(1.0))]));
    }

    [Fact]
    public void Rejects_empty_cue_list()
    {
        Assert.Throws<ArgumentException>(() => TimelinePlanner.Plan(10, Array.Empty<CueNarration>()));
    }
}
