using Vwm.Core.Render;
using Xunit;

namespace Vwm.Core.Tests;

public class BlurRegionTests
{
    private static BlurRegion Sample() => new()
    {
        StartSeconds = 2,
        EndSeconds = 6,
        X = 0.1,
        Y = 0.2,
        Width = 0.4,
        Height = 0.15,
    };

    [Fact]
    public void Defaults_to_a_strong_blur()
    {
        var region = new BlurRegion();
        Assert.Equal(BlurStyle.Blur, region.Style);
        Assert.Equal(BlurRegion.MaxStrength, region.Strength);
    }

    [Fact]
    public void Accepts_a_well_formed_region()
    {
        Sample().Validate(videoDuration: 10);
    }

    [Fact]
    public void Accepts_a_region_that_fills_the_frame()
    {
        var full = Sample() with { X = 0, Y = 0, Width = 1, Height = 1 };
        full.Validate(videoDuration: 10);
    }

    [Fact]
    public void Rejects_a_region_hanging_off_the_frame()
    {
        var off = Sample() with { X = 0.8, Width = 0.4 };
        var ex = Assert.Throws<ArgumentException>(() => off.Validate(10));
        Assert.Contains("outside the frame", ex.Message);
    }

    [Fact]
    public void Rejects_a_negative_origin()
    {
        Assert.Throws<ArgumentException>(() => (Sample() with { Y = -0.01 }).Validate(10));
    }

    [Fact]
    public void Rejects_a_region_too_small_to_hide_anything()
    {
        var tiny = Sample() with { Width = 0.001 };
        var ex = Assert.Throws<ArgumentException>(() => tiny.Validate(10));
        Assert.Contains("too small", ex.Message);
    }

    [Fact]
    public void Rejects_an_empty_or_backwards_time_slice()
    {
        Assert.Throws<ArgumentException>(() => (Sample() with { EndSeconds = 2 }).Validate(10));
        Assert.Throws<ArgumentException>(() => (Sample() with { StartSeconds = 7, EndSeconds = 6 }).Validate(10));
    }

    [Fact]
    public void Rejects_a_slice_that_starts_past_the_end_of_the_recording()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => (Sample() with { StartSeconds = 12, EndSeconds = 14 }).Validate(10));
        Assert.Contains("past the end", ex.Message);
    }

    [Fact]
    public void Allows_a_slice_running_past_the_end_of_the_recording()
    {
        // "From here to the end" is a natural thing to ask for; the renderer clips it.
        (Sample() with { StartSeconds = 8, EndSeconds = 999 }).Validate(videoDuration: 10);
    }

    [Fact]
    public void Rejects_an_out_of_range_strength()
    {
        Assert.Throws<ArgumentException>(() => (Sample() with { Strength = 0 }).Validate(10));
        Assert.Throws<ArgumentException>(() => (Sample() with { Strength = 11 }).Validate(10));
    }

    [Fact]
    public void Rejects_a_non_numeric_rectangle()
    {
        Assert.Throws<ArgumentException>(() => (Sample() with { X = double.NaN }).Validate(10));
    }

    [Fact]
    public void Validate_all_checks_every_region_and_tolerates_null()
    {
        BlurRegion.ValidateAll(null, 10);
        BlurRegion.ValidateAll([], 10);
        Assert.Throws<ArgumentException>(
            () => BlurRegion.ValidateAll([Sample(), Sample() with { Width = 0 }], 10));
    }
}
