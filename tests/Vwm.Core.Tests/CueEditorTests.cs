using Vwm.Core.Project;
using Xunit;

namespace Vwm.Core.Tests;

public class CueEditorTests
{
    private static WalkthroughProject TwoCues() => new()
    {
        VideoPath = @"C:\v.mp4",
        VideoDuration = 30,
        GlobalVoice = "v",
        Cues =
        [
            new Cue { Id = "a", Text = "First one. Second one.", SourceStart = 0, SourceEnd = 12 },
            new Cue { Id = "b", Text = "Third.", SourceStart = 12, SourceEnd = 30 },
        ],
    };

    [Fact]
    public void SetText_gain_voice_are_isolated_to_one_cue()
    {
        var p = TwoCues().SetText("a", "Changed.").SetGain("a", -3).SetVoice("a", "other");
        Assert.Equal("Changed.", p.Cues[0].Text);
        Assert.Equal(-3, p.Cues[0].GainDb);
        Assert.Equal("other", p.Cues[0].Voice);
        // Cue b untouched.
        Assert.Equal("Third.", p.Cues[1].Text);
        Assert.Equal(0, p.Cues[1].GainDb);
    }

    [Fact]
    public void MoveSource_validates_bounds()
    {
        Assert.Throws<ArgumentException>(() => TwoCues().MoveSource("a", -1, 5));
        Assert.Throws<ArgumentException>(() => TwoCues().MoveSource("a", 5, 40));
        Assert.Throws<ArgumentException>(() => TwoCues().MoveSource("a", 8, 8));
        var ok = TwoCues().MoveSource("a", 2, 9);
        Assert.Equal((2, 9), (ok.Cues[0].SourceStart, ok.Cues[0].SourceEnd));
    }

    [Fact]
    public void Split_divides_text_and_source_region()
    {
        var p = TwoCues().Split("a", atSentenceIndex: 1);
        Assert.Equal(3, p.Cues.Count);
        Assert.Equal("First one.", p.Cues[0].Text);
        Assert.Equal("Second one.", p.Cues[1].Text);
        // Contiguous, non-overlapping source coverage preserved.
        Assert.Equal(0, p.Cues[0].SourceStart);
        Assert.Equal(p.Cues[0].SourceEnd, p.Cues[1].SourceStart, 6);
        Assert.Equal(12, p.Cues[1].SourceEnd);
        Assert.NotEqual(p.Cues[0].Id, p.Cues[1].Id);
    }

    [Fact]
    public void Split_rejects_single_sentence_cue()
    {
        Assert.Throws<InvalidOperationException>(() => TwoCues().Split("b", 1));
    }

    [Fact]
    public void Merge_joins_with_the_following_cue()
    {
        var p = TwoCues().Merge("a");
        Assert.Single(p.Cues);
        Assert.Equal("a", p.Cues[0].Id);
        Assert.Equal("First one. Second one. Third.", p.Cues[0].Text);
        Assert.Equal((0, 30), (p.Cues[0].SourceStart, p.Cues[0].SourceEnd));
    }

    [Fact]
    public void Merge_rejects_last_cue()
    {
        Assert.Throws<InvalidOperationException>(() => TwoCues().Merge("b"));
    }

    [Fact]
    public void Unknown_cue_id_throws()
    {
        Assert.Throws<ArgumentException>(() => TwoCues().SetText("missing", "x"));
    }

    [Fact]
    public void Operations_do_not_mutate_the_original()
    {
        var original = TwoCues();
        _ = original.SetText("a", "Changed.");
        Assert.Equal("First one. Second one.", original.Cues[0].Text);
    }
}
