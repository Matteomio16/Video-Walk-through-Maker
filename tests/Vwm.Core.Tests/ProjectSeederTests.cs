using Vwm.Core.Project;
using Xunit;

namespace Vwm.Core.Tests;

public class ProjectSeederTests
{
    private static readonly SubtitleStyleSettings Style = new();

    [Fact]
    public void Seeds_a_cue_per_step_with_correct_source_regions()
    {
        var script = "1. Open the app.\n2. Change a setting.\n3. Save and close.";
        var project = ProjectSeeder.FromScript(
            @"C:\v.mp4", videoDuration: 30, script, boundaries: [10, 20],
            globalVoice: "v", globalSubtitle: Style);

        Assert.Equal(3, project.Cues.Count);
        Assert.Equal((0, 10), (project.Cues[0].SourceStart, project.Cues[0].SourceEnd));
        Assert.Equal((10, 20), (project.Cues[1].SourceStart, project.Cues[1].SourceEnd));
        Assert.Equal((20, 30), (project.Cues[2].SourceStart, project.Cues[2].SourceEnd));
        Assert.Contains("Open the app", project.Cues[0].Text);
        Assert.All(project.Cues, c => Assert.NotEqual("", c.Id));
    }

    [Fact]
    public void Rejects_boundary_count_mismatch()
    {
        Assert.Throws<ArgumentException>(() => ProjectSeeder.FromScript(
            @"C:\v.mp4", 30, "1. A.\n2. B.\n3. C.", boundaries: [10], "v", Style));
    }

    [Fact]
    public void Rejects_empty_script()
    {
        Assert.Throws<InvalidOperationException>(() => ProjectSeeder.FromScript(
            @"C:\v.mp4", 30, "   ", boundaries: [], "v", Style));
    }
}
