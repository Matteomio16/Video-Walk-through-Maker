using Vwm.Core.Render;
using Xunit;

namespace Vwm.Core.Tests;

public class BlurFilterBuilderTests
{
    private static BlurRegion Region(double start, double end, BlurStyle style = BlurStyle.Blur) => new()
    {
        StartSeconds = start,
        EndSeconds = end,
        X = 0.25,
        Y = 0.5,
        Width = 0.5,
        Height = 0.2,
        Style = style,
    };

    // --- mapping source time onto a segment ----------------------------------

    [Fact]
    public void Region_outside_the_segment_is_dropped()
    {
        var applied = BlurFilterBuilder.ForSegment(
            [Region(0, 2)], sourceStart: 5, sourceEnd: 10, outputDuration: 5);
        Assert.Empty(applied);
    }

    [Fact]
    public void Region_touching_only_the_segment_boundary_is_dropped()
    {
        // Ends exactly where the segment starts: no frame of it is in this segment.
        var applied = BlurFilterBuilder.ForSegment(
            [Region(0, 5)], sourceStart: 5, sourceEnd: 10, outputDuration: 5);
        Assert.Empty(applied);
    }

    [Fact]
    public void Region_inside_the_segment_maps_to_segment_local_time()
    {
        var applied = BlurFilterBuilder.ForSegment(
            [Region(6, 8)], sourceStart: 5, sourceEnd: 10, outputDuration: 5);

        var one = Assert.Single(applied);
        Assert.Equal(1, one.LocalStart, 6);
        Assert.Equal(3, one.LocalEnd, 6);
    }

    [Fact]
    public void Region_starting_before_the_segment_is_clipped_to_its_start()
    {
        var applied = BlurFilterBuilder.ForSegment(
            [Region(0, 7)], sourceStart: 5, sourceEnd: 10, outputDuration: 5);

        var one = Assert.Single(applied);
        Assert.Equal(0, one.LocalStart, 6);
        Assert.Equal(2, one.LocalEnd, 6);
    }

    [Fact]
    public void Region_reaching_the_end_of_the_slice_also_covers_the_freeze_frames()
    {
        // The segment's source slice is 5s but it is held for 12s while narration finishes;
        // every frozen frame is a copy of the last source frame, so it needs covering too.
        var applied = BlurFilterBuilder.ForSegment(
            [Region(8, 10)], sourceStart: 5, sourceEnd: 10, outputDuration: 12);

        var one = Assert.Single(applied);
        Assert.Equal(3, one.LocalStart, 6);
        Assert.Equal(12, one.LocalEnd, 6);
    }

    [Fact]
    public void Region_stopping_short_of_the_slice_end_does_not_cover_the_freeze()
    {
        var applied = BlurFilterBuilder.ForSegment(
            [Region(6, 9)], sourceStart: 5, sourceEnd: 10, outputDuration: 12);

        var one = Assert.Single(applied);
        Assert.Equal(4, one.LocalEnd, 6);
    }

    [Fact]
    public void Every_overlapping_region_is_mapped()
    {
        var applied = BlurFilterBuilder.ForSegment(
            [Region(0, 1), Region(6, 7), Region(8, 9)], sourceStart: 5, sourceEnd: 10, outputDuration: 5);
        Assert.Equal(2, applied.Count);
    }

    [Fact]
    public void For_frame_keeps_only_regions_live_at_that_instant()
    {
        var applied = BlurFilterBuilder.ForFrame([Region(2, 6), Region(7, 9)], timeSeconds: 4);
        var one = Assert.Single(applied);
        Assert.True(one.AlwaysOn);
    }

    [Fact]
    public void For_frame_tolerates_no_regions()
    {
        Assert.Empty(BlurFilterBuilder.ForFrame(null, 1));
        Assert.Empty(BlurFilterBuilder.ForFrame([Region(2, 6)], timeSeconds: 12));
    }

    // --- the generated filtergraph -------------------------------------------

    [Fact]
    public void No_regions_leaves_the_source_chain_untouched()
    {
        var graph = BlurFilterBuilder.BuildGraph("fps=30,format=yuv420p", []);
        Assert.Equal("fps=30,format=yuv420p", graph);
    }

    [Fact]
    public void No_regions_still_appends_the_trailing_filters()
    {
        var graph = BlurFilterBuilder.BuildGraph("fps=30", [], "subtitles=a.srt");
        Assert.Equal("fps=30,subtitles=a.srt", graph);
    }

    [Fact]
    public void Blur_region_becomes_split_crop_blur_overlay()
    {
        var applied = BlurFilterBuilder.ForSegment([Region(1, 3)], 0, 5, 5);
        var graph = BlurFilterBuilder.BuildGraph("format=yuv420p", applied);

        Assert.Contains("split[vblm0][vblc0]", graph);
        Assert.Contains("[vblc0]crop=", graph);
        Assert.Contains("boxblur=", graph);
        Assert.Contains("[vblm0][vblb0]overlay=", graph);
    }

    [Fact]
    public void Geometry_is_written_as_frame_relative_expressions_not_pixels()
    {
        var applied = BlurFilterBuilder.ForSegment([Region(1, 3)], 0, 5, 5);
        var graph = BlurFilterBuilder.BuildGraph("format=yuv420p", applied);

        // Even-snapped so the yuv420p chroma planes line up and the overlay lands exactly
        // where the crop was taken from.
        Assert.Contains(@"x='2*floor(iw*0.2500/2)'", graph);
        Assert.Contains(@"y='2*floor(ih*0.5000/2)'", graph);
        Assert.Contains(@"x='2*floor(main_w*0.2500/2)'", graph);
        Assert.Contains(@"w='max(2\,2*floor(iw*0.5000/2))'", graph);
    }

    [Fact]
    public void Time_window_is_carried_on_the_overlay()
    {
        var applied = BlurFilterBuilder.ForSegment([Region(1, 3)], 0, 5, 5);
        var graph = BlurFilterBuilder.BuildGraph("format=yuv420p", applied);
        Assert.Contains(@"enable='between(t\,1.0000\,3.0000)'", graph);
    }

    [Fact]
    public void Solid_region_becomes_a_single_grey_drawbox()
    {
        var applied = BlurFilterBuilder.ForSegment([Region(1, 3, BlurStyle.Solid)], 0, 5, 5);
        var graph = BlurFilterBuilder.BuildGraph("format=yuv420p", applied);

        Assert.Contains("drawbox=", graph);
        Assert.Contains("color=0x808080@1.0:t=fill", graph);
        Assert.DoesNotContain("split", graph);
        Assert.DoesNotContain("boxblur", graph);
        Assert.DoesNotContain(";", graph); // one chain, no side branch needed
    }

    [Fact]
    public void An_always_on_region_drops_the_time_switch()
    {
        var graph = BlurFilterBuilder.BuildGraph(
            "format=yuv420p", BlurFilterBuilder.ForFrame([Region(2, 6)], 4));
        Assert.DoesNotContain("enable=", graph);
    }

    [Fact]
    public void Subtitles_are_burned_after_the_blur_so_they_stay_readable()
    {
        var applied = BlurFilterBuilder.ForSegment([Region(1, 3)], 0, 5, 5);
        var graph = BlurFilterBuilder.BuildGraph("format=yuv420p", applied, "subtitles=seg.srt");

        Assert.True(graph.IndexOf("overlay=", StringComparison.Ordinal)
                    < graph.IndexOf("subtitles=", StringComparison.Ordinal));
        Assert.EndsWith("subtitles=seg.srt", graph);
    }

    [Fact]
    public void Several_regions_chain_without_reusing_a_label()
    {
        var applied = BlurFilterBuilder.ForSegment(
            [Region(0, 2), Region(1, 4, BlurStyle.Solid), Region(3, 5)], 0, 5, 5);
        var graph = BlurFilterBuilder.BuildGraph("format=yuv420p", applied);

        // Each blur gets its own label set, and the second blur consumes the first's output.
        Assert.Contains("split[vblm0][vblc0]", graph);
        Assert.Contains("split[vblm2][vblc2]", graph);
        Assert.Contains("[vblm0][vblb0]overlay=", graph);
        Assert.Contains("[vblm2][vblb2]overlay=", graph);

        var labels = System.Text.RegularExpressions.Regex.Matches(graph, @"\[(vbl\w+)\]")
            .Select(m => m.Groups[1].Value)
            .ToList();
        // Every label is written once and read once.
        Assert.All(labels.Distinct(), label => Assert.Equal(2, labels.Count(l => l == label)));
    }

    [Fact]
    public void Blur_radius_caps_below_what_boxblur_will_accept()
    {
        var applied = BlurFilterBuilder.ForSegment([Region(1, 3)], 0, 5, 5);
        var graph = BlurFilterBuilder.BuildGraph("format=yuv420p", applied);

        // boxblur fails the whole render on a radius over half the side it applies to, so the
        // cap has to sit outside the minimum, not inside it. Chroma is capped on its own
        // (half-size) dimensions.
        Assert.Contains(@"luma_radius='min(floor(min(w\,h)/2)\,max(1\,min(w\,h)*10/20))'", graph);
        Assert.Contains(@"chroma_radius='min(floor(min(cw\,ch)/2)\,max(1\,min(cw\,ch)*10/20))'", graph);
    }

    [Fact]
    public void A_labelled_source_chain_stays_usable_in_a_filter_complex()
    {
        var applied = BlurFilterBuilder.ForSegment([Region(1, 3)], 0, 5, 5);
        var graph = BlurFilterBuilder.BuildGraph("[0:v]fps=30,format=yuv420p", applied);
        Assert.StartsWith("[0:v]fps=30", graph);
    }
}
