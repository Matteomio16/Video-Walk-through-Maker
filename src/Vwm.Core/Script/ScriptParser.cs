using System.Text.RegularExpressions;

namespace Vwm.Core.Script;

/// <summary>One narrated step of the walkthrough, matched 1:1 to a video segment.</summary>
public sealed record ScriptStep(int Index, string Text, IReadOnlyList<string> Sentences);

public static partial class ScriptParser
{
    [GeneratedRegex(@"^\s*(?:step\s+\d+\s*[:.\)]?|\d+\s*[.\)]|[-*•](?=\s))\s*", RegexOptions.IgnoreCase)]
    private static partial Regex StepMarker();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z0-9""'(])")]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"([A-Za-z]+)\.$")]
    private static partial Regex TrailingAbbrev();

    /// <summary>Words that end in a period without ending a sentence, so a following
    /// capitalized word must not trigger a split (e.g. "Dr. Smith", "Fig. 2").</summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "dr", "mr", "mrs", "ms", "prof", "sr", "jr", "st", "mt", "vs", "etc",
        "inc", "ltd", "co", "corp", "dept", "no", "fig", "vol", "gen", "sgt",
        "capt", "lt", "col", "rev", "hon", "pres", "gov", "sen", "rep",
        "ave", "blvd", "rd", "approx", "min", "max", "est", "dept",
    };

    /// <summary>
    /// Splits a plain-prose script into steps. Strategies, in order of preference:
    /// numbered markers ("1.", "Step 2:") > blank-line paragraphs > one sentence per step.
    /// </summary>
    public static IReadOnlyList<ScriptStep> Parse(string raw)
    {
        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Length == 0)
            return [];

        var blocks = SplitOnStepMarkers(text);
        if (blocks.Count < 2)
            blocks = SplitOnBlankLines(text);
        if (blocks.Count < 2)
            blocks = SplitSentences(text).ToList();

        return blocks
            .Select(b => Regex.Replace(b, @"\s+", " ").Trim())
            .Where(b => b.Length > 0)
            .Select((b, i) => new ScriptStep(i, b, SplitSentences(b)))
            .ToList();
    }

    private static List<string> SplitOnStepMarkers(string text)
    {
        var lines = text.Split('\n');
        var blocks = new List<string>();
        var current = new List<string>();
        var sawMarker = false;

        foreach (var line in lines)
        {
            if (StepMarker().IsMatch(line))
            {
                sawMarker = true;
                if (current.Count > 0)
                {
                    blocks.Add(string.Join(" ", current));
                    current.Clear();
                }
                current.Add(StepMarker().Replace(line, "", 1));
            }
            else
            {
                current.Add(line);
            }
        }
        if (current.Count > 0)
            blocks.Add(string.Join(" ", current));

        return sawMarker ? blocks : [];
    }

    private static List<string> SplitOnBlankLines(string text) =>
        Regex.Split(text, @"\n\s*\n")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

    public static IReadOnlyList<string> SplitSentences(string text)
    {
        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        if (normalized.Length == 0)
            return [];

        // Split on sentence boundaries, then merge back any split that fell after an
        // abbreviation or a single-letter initial ("Dr. Smith", "J. Doe", "Fig. 2").
        var pieces = SentenceBoundary().Split(normalized);
        var merged = new List<string>();
        foreach (var piece in pieces)
        {
            var trimmed = piece.Trim();
            if (trimmed.Length == 0)
                continue;
            if (merged.Count > 0 && EndsWithAbbreviation(merged[^1]))
                merged[^1] = merged[^1] + " " + trimmed;
            else
                merged.Add(trimmed);
        }
        return merged;
    }

    private static bool EndsWithAbbreviation(string sentence)
    {
        var m = TrailingAbbrev().Match(sentence);
        if (!m.Success)
            return false;
        var word = m.Groups[1].Value;
        return word.Length == 1 || Abbreviations.Contains(word);
    }
}
