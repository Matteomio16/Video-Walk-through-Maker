using System.Globalization;
using System.Text.RegularExpressions;
using Vwm.Core.Tools;

namespace Vwm.Core.Video;

public sealed record SceneCut(double TimeSeconds, double Score);

/// <summary>
/// Finds candidate step boundaries by scanning the video with ffmpeg's scene-change score.
/// Screen recordings rarely have hard cuts, so the threshold defaults low and the results
/// are ranked by score for the review UI to refine.
/// </summary>
public static partial class SceneDetector
{
    [GeneratedRegex(@"pts_time:(?<t>[\d.]+)")]
    private static partial Regex PtsTime();

    [GeneratedRegex(@"lavfi\.scene_score=(?<s>[\d.]+)")]
    private static partial Regex SceneScore();

    public static async Task<IReadOnlyList<SceneCut>> DetectAsync(
        string videoPath, double threshold = 0.005, CancellationToken ct = default)
    {
        var stdout = await ProcessRunner.RunAsync(
            ToolLocator.FfmpegPath,
            [
                "-hide_banner", "-i", videoPath,
                "-vf", $"select='gt(scene,{threshold.ToString(CultureInfo.InvariantCulture)})',metadata=print:file=-",
                "-an", "-f", "null", "-"
            ],
            ct: ct);

        var cuts = new List<SceneCut>();
        double? pendingTime = null;
        foreach (var line in stdout.Split('\n'))
        {
            var t = PtsTime().Match(line);
            if (t.Success)
                pendingTime = double.Parse(t.Groups["t"].Value, CultureInfo.InvariantCulture);

            var s = SceneScore().Match(line);
            if (s.Success && pendingTime is double time)
            {
                cuts.Add(new SceneCut(time, double.Parse(s.Groups["s"].Value, CultureInfo.InvariantCulture)));
                pendingTime = null;
            }
        }
        return cuts;
    }

    public static async Task<double> GetDurationAsync(string videoPath, CancellationToken ct = default)
    {
        var stdout = await ProcessRunner.RunAsync(
            ToolLocator.FfprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", videoPath],
            ct: ct);
        return double.Parse(stdout.Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Picks (stepCount - 1) boundary times from the detected cuts: highest-scoring cuts first,
    /// each kept at least <paramref name="minSegmentSeconds"/> away from every other boundary.
    /// If detection found too few cuts, the largest remaining span is bisected so the caller
    /// always gets exactly the number it asked for (the review UI lets a human fix the guesses).
    /// </summary>
    public static IReadOnlyList<double> ProposeBoundaries(
        IReadOnlyList<SceneCut> cuts, double videoDuration, int stepCount, double minSegmentSeconds = 1.0)
    {
        var needed = stepCount - 1;
        if (needed <= 0)
            return [];

        var anchors = new List<double> { 0, videoDuration };
        var chosen = new List<double>();

        foreach (var cut in cuts.OrderByDescending(c => c.Score))
        {
            if (chosen.Count == needed)
                break;
            if (anchors.Concat(chosen).All(a => Math.Abs(a - cut.TimeSeconds) >= minSegmentSeconds))
                chosen.Add(cut.TimeSeconds);
        }

        while (chosen.Count < needed)
        {
            var all = anchors.Concat(chosen).OrderBy(t => t).ToList();
            var (start, end) = all.Zip(all.Skip(1)).MaxBy(p => p.Second - p.First);
            chosen.Add((start + end) / 2);
        }

        chosen.Sort();
        return chosen;
    }
}
