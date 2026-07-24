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
