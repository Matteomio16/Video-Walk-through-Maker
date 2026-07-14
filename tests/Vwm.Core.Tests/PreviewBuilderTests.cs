using Vwm.Core.Audio;
using Vwm.Core.Render;
using Vwm.Core.Script;
using Vwm.Core.Tools;
using Vwm.Core.Tts;
using Xunit;

namespace Vwm.Core.Tests;

/// <summary>Integration tests — need espeak-ng and ffmpeg on PATH (skipped otherwise).</summary>
public class PreviewBuilderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vwm-preview-tests").FullName;
    private static bool ToolsAvailable =>
        ToolLocator.Find("espeak-ng") is not null && ToolLocator.Find("ffmpeg") is not null;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [SkippableFact]
    public async Task Voice_preview_joins_all_sentences_and_caches()
    {
        Skip.IfNot(ToolsAvailable);
        var step = new ScriptStep(0, "Open the portal. Then upload the file.",
            ["Open the portal.", "Then upload the file."]);

        var wav = await PreviewBuilder.BuildVoicePreviewAsync(step, new EspeakTtsEngine(), _dir);

        var duration = WavFile.GetDuration(wav);
        Assert.True(duration > 1.5, $"expected two joined sentences, got {duration}s");

        var stamp = File.GetLastWriteTimeUtc(wav);
        var again = await PreviewBuilder.BuildVoicePreviewAsync(step, new EspeakTtsEngine(), _dir);
        Assert.Equal(wav, again);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(wav)); // cache hit, not re-synthesized
    }

    [SkippableFact]
    public async Task Clip_preview_freeze_extends_to_narration_length()
    {
        Skip.IfNot(ToolsAvailable);
        // 2s source segment, narration much longer → clip duration ≈ lead + narration + tail.
        await ProcessRunner.RunAsync(ToolLocator.FfmpegPath,
        [
            "-hide_banner", "-y", "-f", "lavfi", "-i", "color=c=blue:s=320x180:d=4:r=30",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            Path.Combine(_dir, "src.mp4"),
        ]);
        var step = new ScriptStep(0,
            "This narration is deliberately much longer than the two second video segment it belongs to.",
            ["This narration is deliberately much longer than the two second video segment it belongs to."]);
        var wav = await PreviewBuilder.BuildVoicePreviewAsync(step, new EspeakTtsEngine(), _dir);
        var narration = WavFile.GetDuration(wav);
        Assert.True(narration > 2.5, $"test premise broken: narration only {narration}s");

        var clip = await PreviewBuilder.BuildClipPreviewAsync(
            Path.Combine(_dir, "src.mp4"), sourceStart: 1.0, sourceEnd: 3.0, wav, _dir);

        var probed = await ProcessRunner.RunAsync(ToolLocator.FfprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", clip]);
        var clipDuration = double.Parse(probed.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(clipDuration, narration + 0.8 - 0.3, narration + 0.8 + 0.3);
    }
}
