using Vwm.Core.Tts;
using Xunit;

namespace Vwm.Core.Tests;

public class PiperVoiceCatalogTests
{
    [Theory]
    [InlineData("en_US-ryan-high", "Ryan (English, US) — high quality", "en_US")]
    [InlineData("en_US-hfc_female-medium", "Heather (English, US)", "en_US")]
    [InlineData("en_GB-alan-medium", "Alan (English, UK)", "en_GB")]
    [InlineData("de_DE-thorsten-medium", "Thorsten (de-DE)", "de_DE")]
    public void FromModelPath_parses_id_language_and_display_name(string id, string display, string language)
    {
        var voice = PiperVoiceCatalog.FromModelPath($"/tools/voices/{id}.onnx");
        Assert.Equal(id, voice.Id);
        Assert.Equal(display, voice.DisplayName);
        Assert.Equal(language, voice.Language);
    }

    [Fact]
    public void FromModelPath_survives_a_filename_outside_the_convention()
    {
        var voice = PiperVoiceCatalog.FromModelPath("/tools/voices/custom.onnx");
        Assert.Equal("custom", voice.Id);
        Assert.Equal("Custom", voice.DisplayName);
    }

    [Fact]
    public void Enumerate_lists_medium_voices_before_high_and_sorts_by_id()
    {
        var dir = Directory.CreateTempSubdirectory("vwm-voices").FullName;
        try
        {
            foreach (var name in new[] { "en_US-ryan-high.onnx", "en_US-hfc_female-medium.onnx", "en_GB-alan-medium.onnx" })
                File.WriteAllText(Path.Combine(dir, name), "");
            File.WriteAllText(Path.Combine(dir, "en_GB-alan-medium.onnx.json"), "{}"); // sidecar must be ignored

            var ids = PiperVoiceCatalog.Enumerate(dir).Select(v => v.Id).ToList();
            Assert.Equal(["en_GB-alan-medium", "en_US-hfc_female-medium", "en_US-ryan-high"], ids);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Enumerate_returns_empty_for_missing_directory()
    {
        Assert.Empty(PiperVoiceCatalog.Enumerate("/nonexistent/voices"));
    }

    [Fact]
    public void FindById_is_case_insensitive()
    {
        var dir = Directory.CreateTempSubdirectory("vwm-voices").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "en_US-ryan-high.onnx"), "");
            Assert.NotNull(PiperVoiceCatalog.FindById("EN_us-RYAN-high", dir));
            Assert.Null(PiperVoiceCatalog.FindById("en_US-amy-medium", dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
