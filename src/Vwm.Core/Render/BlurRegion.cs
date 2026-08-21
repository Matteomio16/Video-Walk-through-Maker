namespace Vwm.Core.Render;

/// <summary>How a region is obscured. <see cref="Solid"/> paints an opaque grey block and is
/// the safest redaction — even a heavy blur still carries the original pixels' local averages,
/// so it should not be relied on for high-value secrets.</summary>
public enum BlurStyle { Blur, Solid }

/// <summary>
/// One rectangle of the frame to obscure over a slice of the recording (a "length").
/// The rectangle is stored as a fraction of the frame (0-1) rather than in pixels, so it is
/// resolution-independent: the editor places it on a scaled-down preview frame and it lands
/// on exactly the same content in the full-size render, and in the low-res preview clip.
/// </summary>
public sealed record BlurRegion
{
    /// <summary>
    /// Smallest side a region may have, as a fraction of the frame. Small enough for a single
    /// form field, large enough that the blur has something to work with: the radius scales
    /// with the region, and on the chroma planes (half-size under yuv420p) a thinner area
    /// would round down to no blur at all. A <see cref="BlurStyle.Solid"/> block has no such
    /// floor — it covers whatever it is given.
    /// </summary>
    public const double MinSize = 0.02;
    public const int MinStrength = 1;
    public const int MaxStrength = 10;
    /// <summary>Strong by default: the feature exists for sensitive information, so the
    /// safe end of the scale is the one that should need no thought.</summary>
    public const int DefaultStrength = 10;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Start of the obscured slice, in source-video seconds.</summary>
    public double StartSeconds { get; init; }
    /// <summary>End of the obscured slice, in source-video seconds (exclusive of nothing —
    /// every frame in [start, end] is covered, including the freeze-frames that extend the
    /// slice in the output).</summary>
    public double EndSeconds { get; init; }
    /// <summary>Left edge, as a fraction of frame width (0-1).</summary>
    public double X { get; init; }
    /// <summary>Top edge, as a fraction of frame height (0-1).</summary>
    public double Y { get; init; }
    /// <summary>Width, as a fraction of frame width (0-1).</summary>
    public double Width { get; init; }
    /// <summary>Height, as a fraction of frame height (0-1).</summary>
    public double Height { get; init; }
    public BlurStyle Style { get; init; } = BlurStyle.Blur;
    /// <summary>Blur radius as a proportion of the region's shorter side: 1 is a light
    /// smudge, 10 is unreadable. Ignored for <see cref="BlurStyle.Solid"/>.</summary>
    public int Strength { get; init; } = DefaultStrength;

    public double Duration => EndSeconds - StartSeconds;

    /// <summary>
    /// Rejects a region that would render wrong or silently do nothing. Called before any
    /// ffmpeg invocation, so a bad region is a clear message rather than a filter-graph error.
    /// </summary>
    public void Validate(double videoDuration)
    {
        const double eps = 1e-9;
        foreach (var (value, name) in new[] { (X, "x"), (Y, "y"), (Width, "width"), (Height, "height") })
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException($"Blur area {name} is not a number.");
        }
        if (Width < MinSize - eps || Height < MinSize - eps)
            throw new ArgumentException(
                $"Blur area is too small ({Width:P1} x {Height:P1} of the frame); " +
                $"each side must be at least {MinSize:P0}.");
        if (X < -eps || Y < -eps || X + Width > 1 + eps || Y + Height > 1 + eps)
            throw new ArgumentException(
                $"Blur area [{X:F3}, {Y:F3}, {Width:F3}, {Height:F3}] falls outside the frame.");
        if (Strength is < MinStrength or > MaxStrength)
            throw new ArgumentException(
                $"Blur strength {Strength} is out of range ({MinStrength}-{MaxStrength}).");
        if (double.IsNaN(StartSeconds) || double.IsNaN(EndSeconds) || StartSeconds < -eps)
            throw new ArgumentException("Blur area start time must be zero or later.");
        if (EndSeconds <= StartSeconds + eps)
            throw new ArgumentException(
                $"Blur area end ({EndSeconds:F2}s) must come after its start ({StartSeconds:F2}s).");
        if (videoDuration > 0 && StartSeconds >= videoDuration - eps)
            throw new ArgumentException(
                $"Blur area starts at {StartSeconds:F2}s, past the end of the {videoDuration:F2}s recording.");
    }

    public static void ValidateAll(IReadOnlyList<BlurRegion>? regions, double videoDuration)
    {
        if (regions is null)
            return;
        foreach (var r in regions)
            r.Validate(videoDuration);
    }
}
