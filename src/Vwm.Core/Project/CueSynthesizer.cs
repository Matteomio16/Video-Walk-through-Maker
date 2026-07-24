using System.Security.Cryptography;
using System.Text;
using Vwm.Core.Audio;
using Vwm.Core.Script;
using Vwm.Core.Timeline;
using Vwm.Core.Tts;

namespace Vwm.Core.Project;

/// <summary>
/// Synthesizes a project's cues into planner input, caching each sentence's WAV by content
/// (engine/voice + rate + text). Editing one cue re-synthesizes only its changed sentences;
/// every unchanged sentence is a cache hit with a byte-stable duration, which is what lets
/// the renderer's segment cache hit for the untouched cues.
/// </summary>
public sealed class CueSynthesizer(Func<string, ITtsEngine> engineForVoice, string workDir)
{
    public async Task<IReadOnlyList<CueNarration>> SynthesizeAsync(
        WalkthroughProject project, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDir);
        var engines = new Dictionary<string, ITtsEngine>();
        var result = new List<CueNarration>(project.Cues.Count);

        for (var i = 0; i < project.Cues.Count; i++)
        {
            var cue = project.Cues[i];
            progress?.Report($"Generating voiceover ({i + 1}/{project.Cues.Count})");

            var voice = string.IsNullOrEmpty(cue.Voice) ? project.GlobalVoice : cue.Voice;
            if (!engines.TryGetValue(voice, out var engine))
                engines[voice] = engine = engineForVoice(voice);

            var clips = new List<(string, TtsClip)>();
            foreach (var sentence in ScriptParser.SplitSentences(cue.Text))
            {
                var wav = Path.Combine(workDir, $"synth_{Key(engine.Name, cue.RatePct, sentence)}.wav");
                double duration;
                if (File.Exists(wav))
                {
                    duration = WavFile.GetDuration(wav); // already synthesized + trimmed
                }
                else
                {
                    await engine.SynthesizeAsync(sentence, wav, ct);
                    duration = SilenceTrimmer.Trim(wav); // trim once, on first synthesis
                }
                clips.Add((sentence, new TtsClip(wav, duration)));
            }

            result.Add(new CueNarration(
                cue.SourceStart, cue.SourceEnd, clips,
                cue.Timing.LeadIn, cue.Timing.Tail, cue.Timing.GapAfter, cue.GainDb));
        }

        return result;
    }

    private static string Key(string engineName, double? ratePct, string sentence)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{engineName}\n{ratePct}\n{sentence}"));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
