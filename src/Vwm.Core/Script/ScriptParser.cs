using System.Text.RegularExpressions;

namespace Vwm.Core.Script;

/// <summary>One narrated step of the walkthrough, matched 1:1 to a video segment.</summary>
public sealed record ScriptStep(int Index, string Text, IReadOnlyList<string> Sentences);

public static partial class ScriptParser
{
    [GeneratedRegex(@"^\s*(?:step\s+\d+\s*[:.\)]?|\d+\s*[.\)])\s*", RegexOptions.IgnoreCase)]
    private static partial Regex StepMarker();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z0-9""'(])")]
    private static partial Regex SentenceBoundary();

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
        return SentenceBoundary().Split(normalized)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }
}
