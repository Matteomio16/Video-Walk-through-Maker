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
                "-hide_banner", "-protocol_whitelist", "file,pipe", "-i", videoPath,
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
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", videoPath],
            ct: ct);
        return double.Parse(stdout.Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>Reward multiplier for a candidate's scene-change score.</summary>
    private const double ScoreWeight = 2.0;
    /// <summary>Penalty multiplier for deviating from the narration-length prior (per unit of video duration).</summary>
    private const double DeviationWeight = 4.0;

    /// <summary>
    /// Picks (stepCount - 1) boundary times from the detected cuts, guided by a prior of where
    /// each boundary is *expected* to fall. <paramref name="weights"/> gives each step's relative
    /// expected narration length (see <c>NarrationEstimator</c>); null means equal steps. The
    /// expected positions also act as zero-score fallback candidates, so when detection finds
    /// nothing the proposal degrades to narration-proportional placement rather than midpoints.
    /// A dynamic program balances cut strength against deviation from the prior, keeping
    /// successive boundaries at least a minimum segment apart (adaptive: half the shortest
    /// expected segment, clamped to 0.25–1.0s). The review UI remains the human safety net.
    /// </summary>
    public static IReadOnlyList<double> ProposeBoundaries(
        IReadOnlyList<SceneCut> cuts,
        double videoDuration,
        int stepCount,
        IReadOnlyList<double>? weights = null,
        double? minSegmentSeconds = null)
    {
        var needed = stepCount - 1;
        if (needed <= 0)
            return [];
        if (weights is not null && weights.Count != stepCount)
            throw new ArgumentException($"Expected {stepCount} weights, got {weights.Count}.", nameof(weights));

        var w = weights ?? Enumerable.Repeat(1.0, stepCount).ToList();
        var totalW = w.Sum();

        var expected = new double[needed];
        var cum = 0.0;
        for (var k = 0; k < needed; k++)
        {
            cum += w[k];
            expected[k] = videoDuration * cum / totalW;
        }

        var minSeg = minSegmentSeconds
            ?? Math.Clamp(0.5 * videoDuration * w.Min() / totalW, 0.25, 1.0);

        // Normalize scores by the strongest cut in the video. Screen-recording cuts have
        // small absolute scene scores (~0.01-0.05); normalizing lets the clearest real cut
        // still compete with the narration-length prior, independent of absolute magnitude.
        var maxScore = cuts.Count > 0 ? cuts.Max(c => c.Score) : 0.0;
        double Norm(double s) => maxScore > 1e-9 ? Math.Clamp(s / maxScore, 0, 1) : 0.0;

        // Candidate pool: real cuts + the expected positions as zero-score fallbacks.
        var candidates = cuts
            .Select(c => (Time: c.TimeSeconds, Score: Norm(c.Score)))
            .Concat(expected.Select(e => (Time: e, Score: 0.0)))
            .Where(c => c.Time >= minSeg && c.Time <= videoDuration - minSeg)
            .GroupBy(c => Math.Round(c.Time, 2))
            .Select(g => g.MaxBy(c => c.Score))
            .OrderBy(c => c.Time)
            .ToList();

        var chosen = SolveDp(candidates, expected, videoDuration, minSeg);
        return chosen ?? FallbackProportional(expected, videoDuration);
    }

    private static List<double>? SolveDp(
        List<(double Time, double Score)> candidates, double[] expected, double duration, double minSeg)
    {
        var n = candidates.Count;
        var needed = expected.Length;
        if (n == 0)
            return null;

        var dp = new double[needed, n];
        var prev = new int[needed, n];
        const double NegInf = double.NegativeInfinity;

        double Value(int i, int k) =>
            ScoreWeight * candidates[i].Score
            - DeviationWeight * Math.Abs(candidates[i].Time - expected[k]) / duration;

        for (var i = 0; i < n; i++)
            dp[0, i] = Value(i, 0);

        for (var k = 1; k < needed; k++)
        {
            // bestSoFar[j] = best dp[k-1, 0..j]; computed on the fly since candidates are time-sorted.
            var bestVal = NegInf;
            var bestIdx = -1;
            var j = 0;
            for (var i = 0; i < n; i++)
            {
                while (j < n && candidates[j].Time <= candidates[i].Time - minSeg)
                {
                    if (dp[k - 1, j] > bestVal)
                    {
                        bestVal = dp[k - 1, j];
                        bestIdx = j;
                    }
                    j++;
                }
                dp[k, i] = bestIdx < 0 ? NegInf : bestVal + Value(i, k);
                prev[k, i] = bestIdx;
            }
        }

        var endIdx = -1;
        var endVal = NegInf;
        for (var i = 0; i < n; i++)
        {
            if (dp[needed - 1, i] > endVal)
            {
                endVal = dp[needed - 1, i];
                endIdx = i;
            }
        }
        if (endIdx < 0 || double.IsNegativeInfinity(endVal))
            return null;

        var result = new List<double>();
        for (var k = needed - 1; k >= 0; k--)
        {
            result.Add(candidates[endIdx].Time);
            endIdx = k > 0 ? prev[k, endIdx] : endIdx;
        }
        result.Reverse();
        return result;
    }

    private static List<double> FallbackProportional(double[] expected, double duration)
    {
        // Guarantee a strictly increasing in-range sequence even under degenerate inputs.
        var result = new List<double>();
        var floor = 0.0;
        for (var k = 0; k < expected.Length; k++)
        {
            var remaining = expected.Length - k;
            var ceiling = duration - remaining * 0.05;
            var t = Math.Clamp(expected[k], floor + 0.05, ceiling);
            result.Add(t);
            floor = t;
        }
        return result;
    }
}
