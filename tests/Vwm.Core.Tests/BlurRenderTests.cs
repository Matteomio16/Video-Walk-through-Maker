using System.Globalization;
using Vwm.Core.Render;
using Vwm.Core.Timeline;
using Vwm.Core.Tools;
using Xunit;

namespace Vwm.Core.Tests;

/// <summary>
/// The blur, end to end through real ffmpeg: the filtergraph the builder writes is run on an
/// actual recording and the resulting pixels are measured. Needs ffmpeg on PATH (skipped
/// otherwise).
///
/// The synthetic source is a fine white grid on black — every part of every frame is
/// high-contrast detail — so "was this area obscured?" is simply "did the spread of pixel
/// values in it collapse?", with no dependence on fonts or codecs.
/// </summary>
public class BlurRenderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vwm-blur-tests").FullName;
    private static bool ToolsAvailable => ToolLocator.Find("ffmpeg") is not null;

    /// <summary>Spread of pixel values in a detailed area. The grid source sits far above
    /// this unblurred; a strong blur or a solid block drops it far below.</summary>
    private const double DetailThreshold = 30;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static BlurRegion Region(
        double start, double end, BlurStyle style = BlurStyle.Blur, int strength = BlurRegion.MaxStrength) => new()
        {
            StartSeconds = start,
            EndSeconds = end,
            X = 0.1,
            Y = 0.25,
            Width = 0.5,
            Height = 0.3,
            Style = style,
            Strength = strength,
        };

    [SkippableFact]
    public async Task Blur_hides_its_area_only_while_it_is_switched_on()
    {
        Skip.IfNot(ToolsAvailable);
        var source = await MakeGridVideoAsync(seconds: 6);
        var output = await RenderAsync(source, sourceStart: 0, sourceEnd: 6, outputDuration: 6, [Region(2, 4)]);

        var before = await DetailAsync(output, 1.0, inside: true);
        var during = await DetailAsync(output, 3.0, inside: true);
        var after = await DetailAsync(output, 5.0, inside: true);

        Assert.True(before > DetailThreshold, $"the source area should be detailed, measured {before:F1}");
        Assert.True(during < DetailThreshold / 3, $"the area should be obscured at 3s, measured {during:F1}");
        Assert.True(after > DetailThreshold, $"the area should be sharp again at 5s, measured {after:F1}");
    }

    [SkippableFact]
    public async Task Blur_leaves_the_rest_of_the_frame_alone()
    {
        Skip.IfNot(ToolsAvailable);
        var source = await MakeGridVideoAsync(seconds: 6);
        var output = await RenderAsync(source, 0, 6, 6, [Region(2, 4)]);

        var before = await DetailAsync(output, 1.0, inside: false);
        var during = await DetailAsync(output, 3.0, inside: false);

        Assert.True(during > DetailThreshold, $"outside the area should stay sharp, measured {during:F1}");
        Assert.Equal(before, during, 0);
    }

    [SkippableFact]
    public async Task Blur_covers_the_freeze_frames_that_extend_the_segment()
    {
        Skip.IfNot(ToolsAvailable);
        // 3s of source held for 8s, as it would be while a long narration finishes. The held
        // frames are clones of the last source frame, so the area must stay covered.
        var source = await MakeGridVideoAsync(seconds: 4);
        var output = await RenderAsync(source, 0, 3, 8, [Region(2, 3)]);

        Assert.True(await DetailAsync(output, 1.0, inside: true) > DetailThreshold);
        Assert.True(await DetailAsync(output, 2.5, inside: true) < DetailThreshold / 3);

        var frozen = await DetailAsync(output, 6.0, inside: true);
        Assert.True(frozen < DetailThreshold / 3,
            $"the frozen tail must stay covered, measured {frozen:F1}");
    }

    [SkippableFact]
    public async Task A_region_stopping_before_the_slice_ends_does_not_bleed_into_the_freeze()
    {
        Skip.IfNot(ToolsAvailable);
        var source = await MakeGridVideoAsync(seconds: 4);
        var output = await RenderAsync(source, 0, 3, 8, [Region(0.5, 1.5)]);

        Assert.True(await DetailAsync(output, 1.0, inside: true) < DetailThreshold / 3);
        Assert.True(await DetailAsync(output, 6.0, inside: true) > DetailThreshold,
            "the freeze should be untouched when the area ended earlier");
    }

    [SkippableFact]
    public async Task Solid_style_paints_a_flat_block()
    {
        Skip.IfNot(ToolsAvailable);
        var source = await MakeGridVideoAsync(seconds: 4);
        var output = await RenderAsync(source, 0, 4, 4, [Region(1, 3, BlurStyle.Solid)]);

        var covered = await DetailAsync(output, 2.0, inside: true);
        Assert.True(covered < 4, $"a solid block should be near-uniform, measured {covered:F1}");
    }

    [SkippableFact]
    public async Task Stronger_settings_obscure_more()
    {
        Skip.IfNot(ToolsAvailable);
        var source = await MakeGridVideoAsync(seconds: 4);
        var light = await RenderAsync(source, 0, 4, 4, [Region(1, 3, strength: 1)], "light.mp4");
        var strong = await RenderAsync(source, 0, 4, 4, [Region(1, 3, strength: 10)], "strong.mp4", "work-strong");

        Assert.True(await DetailAsync(light, 2.0, inside: true) > await DetailAsync(strong, 2.0, inside: true));
    }

    [SkippableFact]
    public async Task Several_areas_are_applied_together()
    {
        Skip.IfNot(ToolsAvailable);
        var source = await MakeGridVideoAsync(seconds: 4);
        var second = new BlurRegion
        {
            StartSeconds = 1, EndSeconds = 3,
            X = 0.7, Y = 0.7, Width = 0.25, Height = 0.2,
            Style = BlurStyle.Solid,
        };
        var output = await RenderAsync(source, 0, 4, 4, [Region(1, 3), second]);

        Assert.True(await DetailAsync(output, 2.0, inside: true) < DetailThreshold / 3);
        Assert.True(await DetailAsync(output, 2.0, 0.7, 0.7, 0.25, 0.2) < 4);
        // A patch covered by neither area keeps its detail.
        Assert.True(await DetailAsync(output, 2.0, 0.05, 0.8, 0.15, 0.15) > DetailThreshold);
    }

    [SkippableFact]
    public async Task Editing_an_area_re_encodes_the_segment_instead_of_reusing_the_cache()
    {
        Skip.IfNot(ToolsAvailable);
        var source = await MakeGridVideoAsync(seconds: 4);
        var work = Path.Combine(_dir, "shared-work");

        await RenderAsync(source, 0, 4, 4, [Region(1, 3)], "a.mp4", work);
        var afterFirst = Directory.GetFiles(work, "seg_*.mp4").Length;

        await RenderAsync(source, 0, 4, 4, [Region(1, 3) with { X = 0.3 }], "b.mp4", work);
        var afterSecond = Directory.GetFiles(work, "seg_*.mp4").Length;

        Assert.Equal(1, afterFirst);
        Assert.Equal(2, afterSecond);
    }

    [SkippableFact]
    public async Task An_unchanged_area_reuses_the_cached_segment()
    {
        Skip.IfNot(ToolsAvailable);
        var source = await MakeGridVideoAsync(seconds: 4);
        var work = Path.Combine(_dir, "cache-work");

        await RenderAsync(source, 0, 4, 4, [Region(1, 3)], "a.mp4", work);
        var stamp = Directory.GetFiles(work, "seg_*.mp4").Select(File.GetLastWriteTimeUtc).Single();

        await RenderAsync(source, 0, 4, 4, [Region(1, 3)], "b.mp4", work);

        var segment = Assert.Single(Directory.GetFiles(work, "seg_*.mp4"));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(segment));
    }

    [SkippableFact]
    public async Task Blur_works_alongside_the_kept_original_audio()
    {
        Skip.IfNot(ToolsAvailable);
        // Keeping the original audio adds a second filter chain to the same ffmpeg call; the
        // blur turns the video side into a multi-branch graph, so the two are worth pairing.
        var source = await MakeGridVideoWithSoundAsync(seconds: 4);
        var output = await RenderAsync(
            source, 0, 4, 4, [Region(1, 3)], "kept.mp4", keepOriginalAudio: true);

        Assert.True(await DetailAsync(output, 2.0, inside: true) < DetailThreshold / 3);
        Assert.True(await DetailAsync(output, 0.5, inside: true) > DetailThreshold);

        var streams = await ProcessRunner.RunAsync(ToolLocator.FfprobePath,
            ["-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", output]);
        Assert.Contains("video", streams);
        Assert.Contains("audio", streams);
    }

    [SkippableFact]
    public async Task The_smallest_and_largest_allowed_areas_both_render()
    {
        Skip.IfNot(ToolsAvailable);
        // A minimal area leaves the chroma planes only a pixel or two to work with, and a
        // full-frame one pushes the blur radius to its ceiling; boxblur aborts the encode on
        // a radius outside its range, so both ends have to be exercised for real.
        var source = await MakeGridVideoAsync(seconds: 4);

        var smallest = Region(1, 3) with { Width = BlurRegion.MinSize, Height = BlurRegion.MinSize };
        var whole = Region(1, 3) with { X = 0, Y = 0, Width = 1, Height = 1 };

        var tiny = await RenderAsync(source, 0, 4, 4, [smallest], "tiny.mp4");
        var full = await RenderAsync(source, 0, 4, 4, [whole], "full.mp4", "work-full");

        Assert.True(new FileInfo(tiny).Length > 0);
        var covered = await DetailAsync(full, 2.0, 0.1, 0.25, 0.5, 0.3);
        Assert.True(covered < DetailThreshold / 3,
            $"a full-frame area should still obscure, measured {covered:F1}");
    }

    [SkippableFact]
    public async Task The_editor_preview_frame_shows_the_real_blur()
    {
        Skip.IfNot(ToolsAvailable);
        var source = await MakeGridVideoAsync(seconds: 6);

        var plain = await PreviewBuilder.BuildBlurredFrameAsync(source, 3, [Region(2, 4)], _dir, height: 360);
        var untouched = await PreviewBuilder.BuildBlurredFrameAsync(source, 5, [Region(2, 4)], _dir, height: 360);

        Assert.True(await DetailAsync(plain, 0, inside: true) < DetailThreshold / 3);
        Assert.True(await DetailAsync(untouched, 0, inside: true) > DetailThreshold,
            "a time outside every area should come back as a plain frame");
    }

    // --- helpers --------------------------------------------------------------

    /// <summary>A static, uniformly detailed test recording: a fine white grid on black.</summary>
    private async Task<string> MakeGridVideoAsync(double seconds)
    {
        var path = Path.Combine(_dir, $"grid_{seconds}.mp4");
        if (File.Exists(path))
            return path;
        await ProcessRunner.RunAsync(ToolLocator.FfmpegPath,
        [
            "-hide_banner", "-y", "-f", "lavfi",
            "-i", $"color=c=black:s=640x360:r=30:d={Num(seconds)}",
            "-vf", "drawgrid=w=8:h=8:t=3:color=white,format=yuv420p",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12", path,
        ]);
        return path;
    }

    /// <summary>The same grid recording, with a tone on it, for the keep-original-audio path.</summary>
    private async Task<string> MakeGridVideoWithSoundAsync(double seconds)
    {
        var path = Path.Combine(_dir, $"grid_sound_{seconds}.mp4");
        if (File.Exists(path))
            return path;
        await ProcessRunner.RunAsync(ToolLocator.FfmpegPath,
        [
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", $"color=c=black:s=640x360:r=30:d={Num(seconds)}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={Num(seconds)}",
            "-vf", "drawgrid=w=8:h=8:t=3:color=white,format=yuv420p",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12", "-c:a", "aac", path,
        ]);
        return path;
    }

    private async Task<string> RenderAsync(
        string source, double sourceStart, double sourceEnd, double outputDuration,
        IReadOnlyList<BlurRegion> regions, string outputName = "out.mp4", string? workDir = null,
        bool keepOriginalAudio = false)
    {
        var work = workDir ?? Path.Combine(_dir, $"work_{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);

        var silence = Path.Combine(_dir, $"silence_{Num(outputDuration)}.wav");
        if (!File.Exists(silence))
        {
            await ProcessRunner.RunAsync(ToolLocator.FfmpegPath,
            [
                "-hide_banner", "-y", "-f", "lavfi", "-i", "anullsrc=r=22050:cl=mono",
                "-t", Num(outputDuration), silence,
            ]);
        }

        var plan = new TimelinePlan(
            [new SegmentPlan(0, sourceStart, sourceEnd, outputDuration - (sourceEnd - sourceStart), 0, outputDuration)],
            [], [], outputDuration);

        var output = Path.Combine(_dir, outputName);
        await new Renderer(work, new RenderOptions
            {
                BlurRegions = regions,
                KeepOriginalAudio = keepOriginalAudio,
            })
            .RenderAsync(source, plan, silence, output);
        return output;
    }

    private Task<double> DetailAsync(string mediaPath, double timeSeconds, bool inside) =>
        inside
            ? DetailAsync(mediaPath, timeSeconds, 0.1, 0.25, 0.5, 0.3)
            : DetailAsync(mediaPath, timeSeconds, 0.05, 0.75, 0.3, 0.2);

    /// <summary>
    /// Standard deviation of the luma in one frame-relative rectangle, read back as raw
    /// greyscale bytes rather than parsed out of ffmpeg's log — a direct measure of how much
    /// detail survives in that patch.
    /// </summary>
    private async Task<double> DetailAsync(
        string mediaPath, double timeSeconds, double x, double y, double width, double height)
    {
        var raw = Path.Combine(_dir, $"probe_{Guid.NewGuid():N}.raw");
        var crop = $"crop=w='2*floor(iw*{Num(width)}/2)':h='2*floor(ih*{Num(height)}/2)'" +
                   $":x='2*floor(iw*{Num(x)}/2)':y='2*floor(ih*{Num(y)}/2)'";
        // -ss after -i is frame-accurate, which matters when asserting on either side of a
        // switch-on time; these clips are seconds long, so decoding from the start is cheap.
        await ProcessRunner.RunAsync(ToolLocator.FfmpegPath,
        [
            "-hide_banner", "-y", "-i", mediaPath, "-ss", Num(timeSeconds),
            "-frames:v", "1", "-vf", $"{crop},format=gray", "-f", "rawvideo", raw,
        ]);

        var pixels = await File.ReadAllBytesAsync(raw);
        File.Delete(raw);
        Assert.NotEmpty(pixels);

        var mean = pixels.Average(p => (double)p);
        return Math.Sqrt(pixels.Average(p => (p - mean) * (p - mean)));
    }

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
