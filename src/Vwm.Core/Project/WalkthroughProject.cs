using Vwm.Core.Render;

namespace Vwm.Core.Project;

/// <summary>Subtitle appearance (global default, or a per-cue override).</summary>
public sealed record SubtitleStyleSettings
{
    public string Font { get; init; } = "Arial";
    public int Size { get; init; } = 16;
    public SubtitlePosition Position { get; init; } = SubtitlePosition.Bottom;
    public SubtitleBackgroundStyle Background { get; init; } = SubtitleBackgroundStyle.Box;
}

/// <summary>Fine placement knobs for a cue's narration.</summary>
public sealed record CueTiming
{
    /// <summary>Silence before the cue's first sentence.</summary>
    public double LeadIn { get; init; } = 0.3;
    /// <summary>Silence after the cue's last sentence.</summary>
    public double Tail { get; init; } = 0.5;
    /// <summary>Gap between sentences inside the cue.</summary>
    public double GapAfter { get; init; } = 0.35;
}

/// <summary>
/// One editable unit: the narration text shown over a slice of the recording, plus its
/// freeze region and per-cue overrides. Maps 1:1 to a rendered segment.
/// </summary>
public sealed record Cue
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Text { get; init; } = "";
    /// <summary>Source-video slice this cue narrates over (the freeze region), in seconds.</summary>
    public double SourceStart { get; init; }
    public double SourceEnd { get; init; }
    /// <summary>Per-cue voice id; null means use the project's global voice.</summary>
    public string? Voice { get; init; }
    /// <summary>Per-cue narration gain in dB (0 = unchanged).</summary>
    public double GainDb { get; init; }
    /// <summary>Per-cue speaking rate as a percentage (deferred from v1 UI; carried for schema stability).</summary>
    public double? RatePct { get; init; }
    /// <summary>Per-cue subtitle-style override (deferred from v1 UI; null = use global).</summary>
    public SubtitleStyleSettings? SubtitleOverride { get; init; }
    public CueTiming Timing { get; init; } = new();

    public double SourceDuration => SourceEnd - SourceStart;
}

/// <summary>
/// The editable source of truth for a walkthrough (persisted as <c>*.vwmproj</c>).
/// Replaces the transient (script, boundaries) inputs as the thing the editor mutates.
/// </summary>
public sealed record WalkthroughProject
{
    /// <summary>Schema version. Bumped only on a breaking change to this shape.</summary>
    public int Version { get; init; } = 1;
    public required string VideoPath { get; init; }
    public double VideoDuration { get; init; }
    public bool KeepOriginalAudio { get; init; }
    public string GlobalVoice { get; init; } = "";
    public SubtitleStyleSettings GlobalSubtitle { get; init; } = new();
    public IReadOnlyList<Cue> Cues { get; init; } = [];
}
