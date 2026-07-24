using Vwm.Core;
using Vwm.Core.Tts;
using Xunit;

namespace Vwm.Core.Tests;

public class WalkthroughPipelineGuardTests
{
    private sealed class UnusedEngine : ITtsEngine
    {
        public string Name => "unused";
        public Task<TtsClip> SynthesizeAsync(string text, string outputWavPath, CancellationToken ct = default)
            => throw new InvalidOperationException("engine should never run in a guard test");
    }

    private static PipelineOptions Opts(string video, string output, string font = "Arial", int size = 16) => new()
    {
        VideoPath = video,
        ScriptText = "Step one. Hello.",
        OutputPath = output,
        TtsEngine = new UnusedEngine(),
        SubtitleFont = font,
        SubtitleFontSize = size,
    };

    [Fact]
    public async Task Rejects_network_video_before_running_tools()
    {
        var output = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mp4");
        await Assert.ThrowsAsync<ArgumentException>(
            () => WalkthroughPipeline.RunAsync(Opts("http://evil.example/in.mp4", output)));
    }

    [Fact]
    public async Task Rejects_font_with_injection_characters()
    {
        var video = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mp4");
        File.WriteAllText(video, "x");
        var output = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mp4");
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => WalkthroughPipeline.RunAsync(Opts(video, output, font: "Arial',BorderStyle=1,x='")));
        }
        finally { File.Delete(video); }
    }

    [Fact]
    public async Task Rejects_output_equal_to_input()
    {
        var video = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mp4");
        File.WriteAllText(video, "x");
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => WalkthroughPipeline.RunAsync(Opts(video, video)));
        }
        finally { File.Delete(video); }
    }
}
