using Vwm.Core.Script;
using Xunit;

namespace Vwm.Core.Tests;

public class ScriptParserTests
{
    [Fact]
    public void Parses_numbered_steps()
    {
        var steps = ScriptParser.Parse("""
            1. Open the dashboard and log in.
            2. Click the Reports tab. Wait for it to load.
            3) Export the file.
            """);

        Assert.Equal(3, steps.Count);
        Assert.Equal("Open the dashboard and log in.", steps[0].Text);
        Assert.Equal(2, steps[1].Sentences.Count);
        Assert.Equal("Export the file.", steps[2].Text);
    }

    [Fact]
    public void Parses_step_keyword_markers()
    {
        var steps = ScriptParser.Parse("""
            Step 1: Open the app.
            Step 2: Do the thing.
            """);

        Assert.Equal(2, steps.Count);
        Assert.Equal("Open the app.", steps[0].Text);
    }

    [Fact]
    public void Parses_blank_line_paragraphs()
    {
        var steps = ScriptParser.Parse("First we open the portal.\n\nThen we upload the document.\nIt takes a moment.\n\nFinally we press submit.");

        Assert.Equal(3, steps.Count);
        Assert.Equal("Then we upload the document. It takes a moment.", steps[1].Text);
        Assert.Equal(2, steps[1].Sentences.Count);
    }

    [Fact]
    public void Falls_back_to_one_sentence_per_step()
    {
        var steps = ScriptParser.Parse("Open the portal. Upload the file. Press submit.");

        Assert.Equal(3, steps.Count);
        Assert.All(steps, s => Assert.Single(s.Sentences));
    }

    [Fact]
    public void Parses_bulleted_steps()
    {
        var steps = ScriptParser.Parse("- Open the portal.\n- Upload the file, then double-check it.\n* Press submit.");

        Assert.Equal(3, steps.Count);
        Assert.Equal("Upload the file, then double-check it.", steps[1].Text);
    }

    [Fact]
    public void Hyphenated_word_at_line_start_is_not_a_bullet()
    {
        var steps = ScriptParser.Parse("First we log in.\n-checking is not needed here at all.\n\nThen we export.");

        Assert.Equal(2, steps.Count); // blank-line split, hyphen left intact
        Assert.StartsWith("First we log in. -checking", steps[0].Text);
    }

    [Fact]
    public void Empty_script_returns_no_steps()
    {
        Assert.Empty(ScriptParser.Parse("   \n\n  "));
    }
}
