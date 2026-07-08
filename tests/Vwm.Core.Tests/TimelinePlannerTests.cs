using Vwm.Core.Timeline;
using Vwm.Core.Tts;
using Xunit;

namespace Vwm.Core.Tests;

public class TimelinePlannerTests
{
    private static readonly PlannerOptions Opt = new()
    {
        LeadInSeconds = 0.5,
        TailSeconds = 0.5,
        SentenceGapSeconds = 0.25,
    };

    private static IReadOnlyList<(string, TtsClip)> Step(params double[] durations) =>
        durations.Select((d, i) => ($"sentence {i}", new TtsClip($"clip_{i}.wav", d))).ToList();

    [Fact]
    public void Video_freezes_when_narration_is_longer()
    {
        // Segment is 3s of video, narration needs 0.5 + 8 + 0.5 = 9s.
        var plan = TimelinePlanner.Plan(10, [3.0], [Step(8.0), Step(1.0)], Opt);

        Assert.Equal(6.0, plan.Segments[0].HoldSeconds, 3);
        Assert.Equal(9.0, plan.Segments[0].OutputDuration, 3);
        Assert.Equal(9.0, plan.Segments[1].OutputStart, 3);
        // Second segment: 7s of video vs 2s narration → no hold.
        Assert.Equal(0.0, plan.Segments[1].HoldSeconds, 3);
        Assert.Equal(16.0, plan.TotalDuration, 3);
    }

    [Fact]
    public void Narration_and_cues_are_placed_sequentially_with_gaps()
    {
        var plan = TimelinePlanner.Plan(10, [5.0], [Step(2.0, 3.0), Step(1.0)], Opt);

        Assert.Equal(0.5, plan.Narration[0].OutputStart, 3);
        Assert.Equal(0.5 + 2.0 + 0.25, plan.Narration[1].OutputStart, 3);
        Assert.Equal(plan.Segments[1].OutputStart + 0.5, plan.Narration[2].OutputStart, 3);
        Assert.Equal(plan.Narration.Count, plan.Cues.Count);
        Assert.Equal(plan.Narration[1].OutputStart, plan.Cues[1].Start, 3);
        Assert.Equal(3.0, plan.Cues[1].Duration, 3);
    }

    [Fact]
    public void Rejects_wrong_boundary_count()
    {
        Assert.Throws<ArgumentException>(() =>
            TimelinePlanner.Plan(10, [3.0, 6.0], [Step(1.0), Step(1.0)], Opt));
    }

    [Fact]
    public void Rejects_out_of_range_boundaries()
    {
        Assert.Throws<ArgumentException>(() =>
            TimelinePlanner.Plan(10, [12.0], [Step(1.0), Step(1.0)], Opt));
    }
}
