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

    public static string Build(IReadOnlyList<SentenceCue> cues)
    {
        var sb = new StringBuilder();
        var index = 1;
        foreach (var cue in cues)
        {
            foreach (var (text, start, duration) in SplitCue(cue))
            {
                sb.AppendLine(index.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine($"{FormatTime(start)} --> {FormatTime(start + duration)}");
                sb.AppendLine(text);
                sb.AppendLine();
                index++;
            }
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
