using System.Globalization;

namespace Vwm.Core.Render;

/// <summary>A blur region mapped onto one rendered segment: the same rectangle, plus the
/// window of *segment-local* output time over which it must be switched on. An infinite
/// <c>LocalEnd</c> means "every frame", and drops the time switch entirely.</summary>
public sealed record AppliedBlur(BlurRegion Region, double LocalStart, double LocalEnd)
{
    public bool AlwaysOn => LocalStart <= 0 && double.IsPositiveInfinity(LocalEnd);
}

/// <summary>
/// Turns blur regions into ffmpeg filtergraph text.
///
/// Every geometry value is written as an expression over the frame size (<c>iw</c>/<c>ih</c>),
/// never as pixels, so one graph is correct for the full-size render and the scaled-down
/// preview clip alike — and nothing has to probe the video first. Sizes and origins snap to
/// even pixels because yuv420p subsamples chroma 2:1: an odd crop would be silently nudged by
/// ffmpeg and land a pixel away from the overlay that covers it.
/// </summary>
public static class BlurFilterBuilder
{
    /// <summary>Comma escaped for the filtergraph parser, which would otherwise read it as
    /// the start of the next filter. Option values are quoted as well — belt and braces.</summary>
    private const string C = @"\,";

    private static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>
    /// Maps regions onto one segment of the plan, dropping those it does not touch.
    /// <paramref name="outputDuration"/> is the segment's length in the output, which is
    /// longer than its source slice whenever the video is freeze-extended to fit narration.
    /// </summary>
    public static IReadOnlyList<AppliedBlur> ForSegment(
        IReadOnlyList<BlurRegion>? regions, double sourceStart, double sourceEnd, double outputDuration)
    {
        if (regions is null || regions.Count == 0)
            return [];

        const double eps = 1e-6;
        var applied = new List<AppliedBlur>();
        foreach (var region in regions)
        {
            var from = Math.Max(region.StartSeconds, sourceStart);
            var to = Math.Min(region.EndSeconds, sourceEnd);
            if (to - from <= eps)
                continue; // this segment shows none of the region's slice

            // Anything past the source slice is a freeze-clone of the segment's last source
            // frame, so a region running to the end of the slice has to cover the hold too —
            // otherwise the sensitive pixels come back the instant the video freezes.
            var localEnd = to >= sourceEnd - eps
                ? Math.Max(outputDuration, to - sourceStart)
                : to - sourceStart;
            applied.Add(new AppliedBlur(region, from - sourceStart, localEnd));
        }
        return applied;
    }

    /// <summary>
    /// Maps the regions that are live at one instant onto a still frame. The enable window
    /// spans the whole (one-frame) output, so the editor's "show the real blur" preview goes
    /// through exactly the same filters as the render.
    /// </summary>
    public static IReadOnlyList<AppliedBlur> ForFrame(
        IReadOnlyList<BlurRegion>? regions, double timeSeconds)
    {
        if (regions is null || regions.Count == 0)
            return [];
        const double eps = 1e-6;
        return regions
            .Where(r => timeSeconds >= r.StartSeconds - eps && timeSeconds <= r.EndSeconds + eps)
            .Select(r => new AppliedBlur(r, 0, double.PositiveInfinity))
            .ToList();
    }

    /// <summary>
    /// Builds a complete single-in / single-out filtergraph: <paramref name="sourceChain"/>,
    /// then the blur graph, then <paramref name="trailingFilters"/> (the subtitle burn) last so
    /// the burned-in text is never itself obscured. Returns <paramref name="sourceChain"/>
    /// unchanged (plus any trailing filters) when nothing applies.
    /// </summary>
    /// <param name="sourceChain">Filter chain producing the segment's video. Its input may be
    /// unlabeled (for <c>-vf</c>) or labeled (e.g. <c>[0:v]…</c> for <c>-filter_complex</c>).</param>
    /// <param name="labelPrefix">Prefix for the graph's internal link labels. Must not collide
    /// with labels the caller uses elsewhere in the same <c>-filter_complex</c>.</param>
    public static string BuildGraph(
        string sourceChain,
        IReadOnlyList<AppliedBlur> applied,
        string? trailingFilters = null,
        string labelPrefix = "vbl")
    {
        // Side chains (crop + blur of the source frame) are emitted separately; `head` is the
        // main chain being extended, and stays the last chain so its output is the graph's.
        var chains = new List<string>();
        var head = sourceChain;

        for (var i = 0; i < applied.Count; i++)
        {
            var a = applied[i];
            if (a.Region.Style == BlurStyle.Solid)
            {
                // An opaque block needs no copy of the frame — one filter, in place.
                head += "," + DrawBox(a);
                continue;
            }

            var main = $"{labelPrefix}m{i}";
            var copy = $"{labelPrefix}c{i}";
            var blurred = $"{labelPrefix}b{i}";
            chains.Add($"{head},split[{main}][{copy}]");
            chains.Add($"[{copy}]{Crop(a.Region)},{BoxBlur(a.Region)}[{blurred}]");
            // The overlay starts the new head: a chain may open with input labels and then
            // carry on with more filters (the next region's split, or the subtitle burn).
            head = $"[{main}][{blurred}]{Overlay(a)}";
        }

        if (!string.IsNullOrEmpty(trailingFilters))
            head += "," + trailingFilters;

        chains.Add(head);
        return string.Join(";", chains);
    }

    /// <summary>Cuts the region out of the full frame, ready to be blurred and pasted back.</summary>
    private static string Crop(BlurRegion r) =>
        $"crop=w='{EvenSize("iw", r.Width)}':h='{EvenSize("ih", r.Height)}'" +
        $":x='{EvenOrigin("iw", r.X)}':y='{EvenOrigin("ih", r.Y)}'";

    /// <summary>
    /// Blur radius scales with the region's shorter side, so one strength setting reads the
    /// same over a small badge and a full-width banner.
    ///
    /// The clamp is not cosmetic: boxblur *fails the render* on a radius above half the side
    /// it applies to, and the yuv420p chroma planes are half-size, so luma and chroma are
    /// each capped against their own dimensions. The cap wins over the minimum — a region too
    /// thin to blur must still encode.
    /// </summary>
    private static string BoxBlur(BlurRegion r) =>
        $"boxblur=luma_radius='{Radius("w", "h", r.Strength)}':luma_power=2" +
        $":chroma_radius='{Radius("cw", "ch", r.Strength)}':chroma_power=2";

    private static string Radius(string w, string h, int strength) =>
        $"min(floor(min({w}{C}{h})/2){C}max(1{C}min({w}{C}{h})*{strength}/20))";

    /// <summary>Pastes the blurred copy back over the frame, but only inside the region's
    /// time window. <c>main_w</c>/<c>main_h</c> are the frame's size, matching the crop.</summary>
    private static string Overlay(AppliedBlur a) =>
        $"overlay=x='{EvenOrigin("main_w", a.Region.X)}':y='{EvenOrigin("main_h", a.Region.Y)}'" +
        Enable(a);

    private static string DrawBox(AppliedBlur a) =>
        $"drawbox=x='{EvenOrigin("iw", a.Region.X)}':y='{EvenOrigin("ih", a.Region.Y)}'" +
        $":w='{EvenSize("iw", a.Region.Width)}':h='{EvenSize("ih", a.Region.Height)}'" +
        $":color=0x808080@1.0:t=fill" + Enable(a);

    /// <summary>The <c>:enable=…</c> timeline switch, or nothing at all when the region
    /// covers every frame it is handed (a still-frame preview).</summary>
    private static string Enable(AppliedBlur a) =>
        a.AlwaysOn ? "" : $":enable='between(t{C}{F(a.LocalStart)}{C}{F(a.LocalEnd)})'";

    private static string EvenOrigin(string dimension, double fraction) =>
        $"2*floor({dimension}*{F(fraction)}/2)";

    private static string EvenSize(string dimension, double fraction) =>
        $"max(2{C}2*floor({dimension}*{F(fraction)}/2))";
}
