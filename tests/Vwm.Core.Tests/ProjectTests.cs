using Vwm.Core.Project;
using Vwm.Core.Render;
using Xunit;

namespace Vwm.Core.Tests;

public class ProjectTests
{
    private static WalkthroughProject Sample() => new()
    {
        VideoPath = @"C:\videos\demo.mp4",
        VideoDuration = 42.5,
        KeepOriginalAudio = true,
        GlobalVoice = "en_US-hfc_female-medium",
        GlobalSubtitle = new SubtitleStyleSettings { Font = "Verdana", Size = 20, Position = SubtitlePosition.Top },
        Cues =
        [
            new Cue { Text = "First step.", SourceStart = 0, SourceEnd = 10, GainDb = -2 },
            new Cue { Text = "Second step.", SourceStart = 10, SourceEnd = 42.5, Voice = "en_US-ryan-high" },
        ],
        BlurRegions =
        [
            new BlurRegion
            {
                StartSeconds = 3, EndSeconds = 12.5,
                X = 0.1, Y = 0.2, Width = 0.4, Height = 0.15,
                Style = BlurStyle.Solid, Strength = 7,
            },
        ],
    };

    [Fact]
    public void Round_trips_through_json()
    {
        var original = Sample();
        var reloaded = ProjectStore.Deserialize(ProjectStore.Serialize(original));
        // Re-serialize both to compare by value (records don't deep-compare list contents).
        Assert.Equal(ProjectStore.Serialize(original), ProjectStore.Serialize(reloaded));
        Assert.Equal(2, reloaded.Cues.Count);
        Assert.Equal("en_US-ryan-high", reloaded.Cues[1].Voice);
        Assert.Equal(SubtitlePosition.Top, reloaded.GlobalSubtitle.Position);
    }

    [Fact]
    public void Enums_serialize_by_name()
    {
        var json = ProjectStore.Serialize(Sample());
        Assert.Contains("\"Top\"", json);
        Assert.Contains("\"Box\"", json);
        Assert.DoesNotContain("\"position\": 6", json); // not the raw legacy-SSA int
    }

    [Fact]
    public void Rejects_unsupported_version()
    {
        var json = ProjectStore.Serialize(Sample()).Replace("\"version\": 1", "\"version\": 99");
        Assert.Throws<NotSupportedException>(() => ProjectStore.Deserialize(json));
    }

    [Fact]
    public void Rejects_garbage_json()
    {
        Assert.Throws<InvalidDataException>(() => ProjectStore.Deserialize("{ not json"));
    }

    [Fact]
    public void Blur_areas_survive_a_round_trip()
    {
        var reloaded = ProjectStore.Deserialize(ProjectStore.Serialize(Sample()));

        var region = Assert.Single(reloaded.BlurRegions);
        Assert.Equal(3, region.StartSeconds);
        Assert.Equal(12.5, region.EndSeconds);
        Assert.Equal(0.4, region.Width);
        Assert.Equal(BlurStyle.Solid, region.Style);
        Assert.Equal(7, region.Strength);
    }

    [Fact]
    public void A_project_without_blur_areas_loads_with_none()
    {
        // A project saved before the feature existed carries no blurRegions key at all.
        var json = ProjectStore.Serialize(Sample() with { BlurRegions = [] });
        Assert.Empty(ProjectStore.Deserialize(json).BlurRegions);
    }

    [Fact]
    public void Blur_areas_can_be_read_from_a_standalone_file()
    {
        var regions = ProjectStore.DeserializeBlurRegions("""
            [{ "startSeconds": 1, "endSeconds": 4, "x": 0.2, "y": 0.3,
               "width": 0.5, "height": 0.2, "style": "Blur", "strength": 10 }]
            """);

        var region = Assert.Single(regions);
        Assert.Equal(BlurStyle.Blur, region.Style);
        Assert.Equal(10, region.Strength);
        region.Validate(videoDuration: 10);
    }

    [Fact]
    public void A_standalone_blur_file_may_leave_style_and_strength_out()
    {
        var regions = ProjectStore.DeserializeBlurRegions("""
            [{ "startSeconds": 1, "endSeconds": 4, "x": 0.2, "y": 0.3, "width": 0.5, "height": 0.2 }]
            """);

        var region = Assert.Single(regions);
        Assert.Equal(BlurStyle.Blur, region.Style);
        Assert.Equal(BlurRegion.DefaultStrength, region.Strength);
    }

    [Fact]
    public void Garbage_in_a_blur_file_is_reported_as_such()
    {
        Assert.Throws<InvalidDataException>(() => ProjectStore.DeserializeBlurRegions("{ not json"));
    }

    [Fact]
    public void Save_and_load_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".vwmproj");
        try
        {
            ProjectStore.Save(Sample(), path);
            var loaded = ProjectStore.Load(path);
            Assert.Equal(42.5, loaded.VideoDuration);
        }
        finally { File.Delete(path); }
    }
}
