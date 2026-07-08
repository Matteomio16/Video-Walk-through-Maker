using Vwm.Core.Subtitles;
using Vwm.Core.Timeline;
using Xunit;

namespace Vwm.Core.Tests;

public class SrtBuilderTests
{
    [Fact]
    public void Formats_a_simple_cue()
    {
        var srt = SrtBuilder.Build([new SentenceCue("Open the dashboard.", 1.5, 2.0)]);

        Assert.Contains("1\n00:00:01,500 --> 00:00:03,500\nOpen the dashboard.\n",
            srt.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Wraps_long_text_to_two_lines()
    {
        var text = "Click the export button in the upper right corner of the page.";
        var srt = SrtBuilder.Build([new SentenceCue(text, 0, 4.0)]);

        var lines = srt.Replace("\r\n", "\n").Split('\n');
        // index, timing, then 2 wrapped text lines
        Assert.True(lines[2].Length <= SrtBuilder.MaxLineLength);
        Assert.True(lines[3].Length <= SrtBuilder.MaxLineLength);
    }

    [Fact]
    public void Splits_very_long_sentences_into_sequential_cues()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 60)); // way over 2 lines
        var cues = SrtBuilder.SplitCue(new SentenceCue(text, 10.0, 12.0)).ToList();

        Assert.True(cues.Count > 1);
        Assert.Equal(10.0, cues[0].Start, 3);
        Assert.Equal(22.0, cues[^1].Start + cues[^1].Duration, 2);
        // Sequential, non-overlapping
        for (var i = 1; i < cues.Count; i++)
            Assert.Equal(cues[i - 1].Start + cues[i - 1].Duration, cues[i].Start, 3);
    }

    [Fact]
    public void Formats_hours()
    {
        Assert.Equal("01:02:03,450", SrtBuilder.FormatTime(3723.45));
    }
}
