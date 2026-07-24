using Vwm.Core.Script;

namespace Vwm.Core.Project;

/// <summary>
/// The v1 edit operations, as pure functions over the immutable project (each returns a
/// new <see cref="WalkthroughProject"/>). Keeping them pure makes them trivially testable
/// and gives undo/redo for free later (a stack of project states).
/// </summary>
public static class CueEditor
{
    public static WalkthroughProject SetText(this WalkthroughProject project, string cueId, string text)
        => Replace(project, cueId, c => c with { Text = text });

    public static WalkthroughProject SetVoice(this WalkthroughProject project, string cueId, string? voice)
        => Replace(project, cueId, c => c with { Voice = voice });

    public static WalkthroughProject SetGain(this WalkthroughProject project, string cueId, double gainDb)
        => Replace(project, cueId, c => c with { GainDb = gainDb });

    public static WalkthroughProject SetTiming(this WalkthroughProject project, string cueId, CueTiming timing)
        => Replace(project, cueId, c => c with { Timing = timing });

    public static WalkthroughProject MoveSource(this WalkthroughProject project, string cueId, double start, double end)
    {
        if (start < 0 || end > project.VideoDuration || start >= end)
            throw new ArgumentException(
                $"Source region [{start}, {end}] must satisfy 0 <= start < end <= {project.VideoDuration}.");
        return Replace(project, cueId, c => c with { SourceStart = start, SourceEnd = end });
    }

    /// <summary>Splits a cue at a sentence boundary into two cues. The source (freeze) region is
    /// divided in proportion to each part's text length so both halves keep sensible video.</summary>
    public static WalkthroughProject Split(this WalkthroughProject project, string cueId, int atSentenceIndex)
    {
        var idx = IndexOf(project, cueId);
        var cue = project.Cues[idx];
        var sentences = ScriptParser.SplitSentences(cue.Text);
        if (sentences.Count < 2)
            throw new InvalidOperationException("A cue needs at least two sentences to split.");
        if (atSentenceIndex < 1 || atSentenceIndex >= sentences.Count)
            throw new ArgumentOutOfRangeException(nameof(atSentenceIndex),
                $"Split index must be between 1 and {sentences.Count - 1}.");

        var text1 = string.Join(" ", sentences.Take(atSentenceIndex));
        var text2 = string.Join(" ", sentences.Skip(atSentenceIndex));
        var len1 = text1.Length;
        var total = len1 + text2.Length;
        var splitAt = cue.SourceStart + cue.SourceDuration * len1 / Math.Max(1, total);

        var first = cue with { Id = NewId(), Text = text1, SourceEnd = splitAt };
        var second = cue with { Id = NewId(), Text = text2, SourceStart = splitAt };

        var cues = project.Cues.ToList();
        cues[idx] = first;
        cues.Insert(idx + 1, second);
        return project with { Cues = cues };
    }

    /// <summary>Merges a cue with the one after it: text is joined and the source region spans both.</summary>
    public static WalkthroughProject Merge(this WalkthroughProject project, string cueId)
    {
        var idx = IndexOf(project, cueId);
        if (idx + 1 >= project.Cues.Count)
            throw new InvalidOperationException("There is no following cue to merge with.");

        var a = project.Cues[idx];
        var b = project.Cues[idx + 1];
        var merged = a with { Text = $"{a.Text} {b.Text}".Trim(), SourceEnd = b.SourceEnd };

        var cues = project.Cues.ToList();
        cues[idx] = merged;
        cues.RemoveAt(idx + 1);
        return project with { Cues = cues };
    }

    private static WalkthroughProject Replace(WalkthroughProject project, string cueId, Func<Cue, Cue> transform)
    {
        var idx = IndexOf(project, cueId);
        var cues = project.Cues.ToList();
        cues[idx] = transform(cues[idx]);
        return project with { Cues = cues };
    }

    private static int IndexOf(WalkthroughProject project, string cueId)
    {
        for (var i = 0; i < project.Cues.Count; i++)
            if (project.Cues[i].Id == cueId)
                return i;
        throw new ArgumentException($"No cue with id '{cueId}'.", nameof(cueId));
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
