using Vwm.Core.Script;
using Vwm.Core.Timeline;
using Xunit;

namespace Vwm.Core.Tests;

public class NarrationEstimatorTests
{
    [Fact]
    public void Longer_steps_get_proportionally_larger_weights()
    {
        var steps = ScriptParser.Parse("""
            1. Click save.
            2. Now review every field on the confirmation page carefully, comparing the values against the source document before you approve anything.
            """);

        var weights = NarrationEstimator.EstimateWeights(steps);

        Assert.Equal(2, weights.Count);
        Assert.True(weights[1] > weights[0] * 4,
            $"expected step 2 ({weights[1]}) to weigh much more than step 1 ({weights[0]})");
    }

    [Fact]
    public void Weights_are_always_positive()
    {
        var steps = ScriptParser.Parse("A. B. C.");
        Assert.All(NarrationEstimator.EstimateWeights(steps), w => Assert.True(w > 0));
    }
}
