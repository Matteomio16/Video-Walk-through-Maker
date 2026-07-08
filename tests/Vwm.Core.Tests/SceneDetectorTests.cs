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
}
