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

    [Fact]
    public void Segment_durations_are_whole_frames_so_the_timeline_cannot_drift()
    {
        var opt = new PlannerOptions { Fps = 30, LeadInSeconds = 0.3, TailSeconds = 0.5, SentenceGapSeconds = 0.35 };
        // Deliberately non-frame-aligned clip durations.
        var plan = TimelinePlanner.Plan(
            20, [7.123], [Step(2.111, 1.777), Step(3.333)], opt);

        var frame = 1.0 / opt.Fps;
        foreach (var seg in plan.Segments)
        {
            var frames = seg.OutputDuration / frame;
            Assert.Equal(Math.Round(frames), frames, 6); // exact whole number of frames
        }
        // Cumulative starts stay on the frame grid too.
        var totalFrames = plan.TotalDuration / frame;
        Assert.Equal(Math.Round(totalFrames), totalFrames, 6);
    }

    [Fact]
    public void Quantized_output_never_truncates_content()
    {
        var opt = new PlannerOptions { Fps = 30, LeadInSeconds = 0.3, TailSeconds = 0.5 };
        var plan = TimelinePlanner.Plan(10, [4.0], [Step(2.4), Step(1.0)], opt);

        // Each segment must be at least as long as the larger of its source/narration need.
        var seg0 = plan.Segments[0];
        var narration0 = 0.3 + 2.4 + 0.5;
        Assert.True(seg0.OutputDuration >= narration0 - 1e-9);
        Assert.True(seg0.OutputDuration >= seg0.SourceDuration - 1e-9);
    }
}
