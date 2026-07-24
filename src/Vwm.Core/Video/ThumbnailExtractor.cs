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
}
