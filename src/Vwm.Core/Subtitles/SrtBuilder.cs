using System.Globalization;
using System.Text;
using Vwm.Core.Timeline;

namespace Vwm.Core.Subtitles;

/// <summary>
/// Turns sentence cues into an SRT document. Long sentences are wrapped to at most
/// two lines per cue; anything longer is split into consecutive cues whose durations
/// are proportional to their share of the text.
/// </summary>
public static class SrtBuilder
{
    public const int MaxLineLength = 42;
    public const int MaxLinesPerCue = 2;
    /// <summary>Shortest time a cue stays on screen, so brief utterances remain readable.</summary>
    public const double MinCueSeconds = 1.2;
    /// <summary>Gap kept between one cue's end and the next cue's start when extending.</summary>
    public const double MinGapSeconds = 0.08;

    public static string Build(IReadOnlyList<SentenceCue> cues)
    {
        var flat = cues.SelectMany(SplitCue).ToList();
        var sb = new StringBuilder();
        for (var i = 0; i < flat.Count; i++)
        {
            var (text, start, duration) = flat[i];
            var speechEnd = start + duration;
            // Hold a short cue on screen up to MinCueSeconds, but never past the next cue.
            var end = Math.Max(speechEnd, start + MinCueSeconds);
            if (i + 1 < flat.Count)
                end = Math.Min(end, Math.Max(speechEnd, flat[i + 1].Start - MinGapSeconds));

            sb.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"{FormatTime(start)} --> {FormatTime(end)}");
            sb.AppendLine(text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    internal static IEnumerable<(string Text, double Start, double Duration)> SplitCue(SentenceCue cue)
    {
        var lines = Wrap(cue.Text, MaxLineLength);
        var chunks = lines
            .Chunk(MaxLinesPerCue)
            .Select(c => string.Join("\n", c))
            .ToList();

        if (chunks.Count <= 1)
        {
            yield return (chunks.FirstOrDefault() ?? cue.Text, cue.Start, cue.Duration);
            yield break;
        }

        var totalChars = chunks.Sum(c => c.Length);
        var t = cue.Start;
        foreach (var chunk in chunks)
        {
            var d = cue.Duration * chunk.Length / totalChars;
            yield return (chunk, t, d);
            t += d;
        }
    }

    internal static List<string> Wrap(string text, int maxLength)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in words)
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > maxLength)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0)
                current.Append(' ');
            current.Append(word);
        }
        if (current.Length > 0)
            lines.Add(current.ToString());
        return lines;
    }

    internal static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }
}
