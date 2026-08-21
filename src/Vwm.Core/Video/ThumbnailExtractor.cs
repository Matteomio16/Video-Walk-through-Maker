using System.Globalization;
using Vwm.Core.Tools;

namespace Vwm.Core.Video;

/// <summary>Extracts evenly spaced JPEG thumbnails for the review screen's timeline strip.</summary>
public static class ThumbnailExtractor
{
    public static async Task<IReadOnlyList<(double TimeSeconds, string Path)>> ExtractAsync(
        string videoPath, double videoDuration, int count, string outputDir, int height = 90,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var results = new List<(double, string)>();
        for (var i = 0; i < count; i++)
        {
            var time = videoDuration * (i + 0.5) / count;
            var path = Path.Combine(outputDir, $"thumb_{i:D3}.jpg");
            await ProcessRunner.RunAsync(
                ToolLocator.FfmpegPath,
                [
                    "-hide_banner", "-y", "-protocol_whitelist", "file,pipe",
                    "-ss", time.ToString("F3", CultureInfo.InvariantCulture),
                    "-i", videoPath,
                    "-frames:v", "1", "-vf", $"scale=-2:{height}", path
                ],
                ct: ct);
            results.Add((time, path));
        }
        return results;
    }

    /// <summary>
    /// Grabs a single frame at an exact time, for the blur editor's canvas. Cached by
    /// (video, time, height) so scrubbing back over a time already looked at is instant.
    /// </summary>
    public static async Task<string> ExtractFrameAsync(
        string videoPath, double timeSeconds, string outputDir, int height = 480,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var stamp = Math.Max(0, timeSeconds).ToString("F2", CultureInfo.InvariantCulture);
        var path = Path.Combine(outputDir, $"frame_{stamp.Replace('.', '_')}_{height}.jpg");
        if (File.Exists(path))
            return path;

        await ProcessRunner.RunAsync(
            ToolLocator.FfmpegPath,
            [
                "-hide_banner", "-y", "-protocol_whitelist", "file,pipe",
                "-ss", stamp,
                "-i", LocalPath.RequireInputFile(videoPath, "Input video"),
                "-frames:v", "1", "-vf", $"scale=-2:{height}", path
            ],
            ct: ct);
        return path;
    }
}
