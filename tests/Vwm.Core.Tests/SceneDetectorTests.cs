using Vwm.Core.Video;
using Xunit;

namespace Vwm.Core.Tests;

public class SceneDetectorTests
{
    [Fact]
    public void Picks_highest_scoring_cuts()
    {
        var cuts = new List<SceneCut>
        {
            new(2.0, 0.1), new(5.0, 0.9), new(7.5, 0.6), new(9.0, 0.2),
        };

        var boundaries = SceneDetector.ProposeBoundaries(cuts, 12.0, stepCount: 3);

        Assert.Equal([5.0, 7.5], boundaries);
    }

    [Fact]
    public void Respects_minimum_segment_length()
    {
        var cuts = new List<SceneCut>
        {
            new(5.0, 0.9), new(5.3, 0.8), new(8.0, 0.5),
        };

        var boundaries = SceneDetector.ProposeBoundaries(cuts, 12.0, stepCount: 3, minSegmentSeconds: 1.0);

        // 5.3 is too close to 5.0, so 8.0 is the second pick.
        Assert.Equal([5.0, 8.0], boundaries);
    }

    [Fact]
    public void Bisects_when_detection_finds_too_few_cuts()
    {
        var boundaries = SceneDetector.ProposeBoundaries([], 12.0, stepCount: 3);

        Assert.Equal(2, boundaries.Count);
        Assert.True(boundaries[0] > 0 && boundaries[1] < 12.0 && boundaries[0] < boundaries[1]);
    }

    [Fact]
    public void Single_step_needs_no_boundaries()
    {
        Assert.Empty(SceneDetector.ProposeBoundaries([new SceneCut(3, 0.9)], 10.0, stepCount: 1));
    }

    [Fact]
    public void Uneven_weights_pick_early_cuts_over_stronger_late_decoy()
    {
        // The reported case: steps of ~1.5s, ~1.5s, then ~12s. True cuts at 1.5 and 3.0
        // score modestly; a strong decoy sits mid-video where equal-step logic would look.
        var cuts = new List<SceneCut>
        {
            new(1.5, 0.5), new(3.0, 0.5), new(7.5, 0.9), new(12.0, 0.3),
        };

        var boundaries = SceneDetector.ProposeBoundaries(
            cuts, 15.0, stepCount: 3, weights: [1.0, 1.0, 8.0]);

        Assert.Equal([1.5, 3.0], boundaries);
    }

    [Fact]
    public void No_cuts_with_uneven_weights_falls_back_to_proportional_positions()
    {
        var boundaries = SceneDetector.ProposeBoundaries(
            [], 15.0, stepCount: 3, weights: [1.0, 1.0, 8.0]);

        Assert.Equal(2, boundaries.Count);
        Assert.Equal(1.5, boundaries[0], 2);
        Assert.Equal(3.0, boundaries[1], 2);
    }

    [Fact]
    public void Adaptive_min_segment_allows_short_steps()
    {
        // Two adjacent cuts 1.5s apart must both be selectable when the weights say
        // those steps really are that short (old fixed 1.0s minimum stays satisfied,
        // but 0.5s steps need the adaptive floor).
        var cuts = new List<SceneCut> { new(0.5, 0.6), new(1.0, 0.6) };

        var boundaries = SceneDetector.ProposeBoundaries(
            cuts, 20.0, stepCount: 3, weights: [1.0, 1.0, 38.0]);

        Assert.Equal([0.5, 1.0], boundaries);
    }

    [Fact]
    public void Rejects_wrong_weight_count()
    {
        Assert.Throws<ArgumentException>(() =>
            SceneDetector.ProposeBoundaries([], 10.0, stepCount: 3, weights: [1.0, 2.0]));
    }
}
