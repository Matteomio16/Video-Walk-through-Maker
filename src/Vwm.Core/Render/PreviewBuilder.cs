using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Vwm.Core.Audio;
using Vwm.Core.Script;
using Vwm.Core.Tools;
using Vwm.Core.Tts;

namespace Vwm.Core.Render;

/// <summary>
/// Builds the review-screen previews: a voice-only WAV for one step (played inline
/// by the app), and a fast low-res video+voice clip for one step (opened in the OS
/// player). Both are cached by content so repeated previews are instant.
/// </summary>
public static class PreviewBuilder
{
    private const double SentenceGapSeconds = 0.35;
    private const double LeadInSeconds = 0.3;
    private const double TailSeconds = 0.5;

    public static async Task<string> BuildVoicePreviewAsync(
        ScriptStep step, ITtsEngine engine, string workDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDir);
        var key = CacheKey(engine.Name, step.Text);
        var outPath = Path.Combine(workDir, $"voice_{key}.wav");
        if (File.Exists(outPath))
            return outPath;

        var clips = new List<(string WavPath, double StartSeconds)>();
        var t = 0.0;
        for (var i = 0; i < step.Sentences.Count; i++)
        {
            var wav = Path.Combine(workDir, $"voice_{key}_{i}.wav");
            await engine.SynthesizeAsync(step.Sentences[i], wav, ct);
            var duration = SilenceTrimmer.Trim(wav); // match the final render's tightened timing
            clips.Add((wav, t));
            t += duration + SentenceGapSeconds;
        }
        if (clips.Count == 0)
            throw new InvalidOperationException("The step has no sentences to preview.");

        WavAssembler.Assemble(clips, t - SentenceGapSeconds, outPath);
        return outPath;
    }

    /// <summary>
    /// One-step preview clip using the same freeze-extension formula as the full render:
    /// the segment holds its last frame until the narration (plus lead-in/tail) finishes.
    /// </summary>
    public static async Task<string> BuildClipPreviewAsync(
        string videoPath,
        double sourceStart,
        double sourceEnd,
        string narrationWavPath,
        string workDir,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDir);
        var key = CacheKey(Path.GetFileName(narrationWavPath),
            $"{videoPath}|{sourceStart:F3}|{sourceEnd:F3}");
        var outPath = Path.Combine(workDir, $"clip_{key}.mp4");
        if (File.Exists(outPath))
            return outPath;

        var sourceDuration = sourceEnd - sourceStart;
        var narrationDuration = LeadInSeconds + WavFile.GetDuration(narrationWavPath) + TailSeconds;
        var outputDuration = Math.Max(sourceDuration, narrationDuration);
        var hold = outputDuration - sourceDuration;

        string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
        var leadMs = (int)(LeadInSeconds * 1000);

        await ProcessRunner.RunAsync(
            ToolLocator.FfmpegPath,
            [
                "-hide_banner", "-y",
                "-ss", F(sourceStart), "-i", Path.GetFullPath(videoPath),
                "-i", Path.GetFullPath(narrationWavPath),
                "-filter_complex",
                $"[0:v]trim=duration={F(sourceDuration)},setpts=PTS-STARTPTS," +
                $"tpad=stop_mode=clone:stop_duration={F(hold)},fps=30,scale=-2:480,format=yuv420p[v];" +
                $"[1:a]adelay={leadMs}:all=1[a]",
                "-map", "[v]", "-map", "[a]",
                "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28",
                "-c:a", "aac", "-b:a", "128k",
                "-t", F(outputDuration),
                outPath,
            ],
            ct: ct);
        return outPath;
    }

    private static string CacheKey(string engine, string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{engine}\n{text}"));
        return $"{Sanitize(engine)}_{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    private static string Sanitize(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
