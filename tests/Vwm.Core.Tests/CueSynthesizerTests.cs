using Vwm.Core.Audio;
using Vwm.Core.Project;
using Vwm.Core.Tts;
using Xunit;

namespace Vwm.Core.Tests;

public class CueSynthesizerTests
{
    private sealed class FakeEngine(string name) : ITtsEngine
    {
        public string Name { get; } = name;
        public int Calls { get; private set; }

        public Task<TtsClip> SynthesizeAsync(string text, string outputWavPath, CancellationToken ct = default)
        {
            Calls++;
            var format = new WavFormat(22050, 1, 16);
            var samples = (int)(0.4 * format.SampleRate);
            var pcm = new byte[samples * 2];
            for (var i = 0; i < samples; i++)
            {
                const short v = 5000; // well above the silence-trim threshold
                pcm[i * 2] = v & 0xFF;
                pcm[i * 2 + 1] = (v >> 8) & 0xFF;
            }
            WavFile.Write(outputWavPath, format, pcm);
            return Task.FromResult(new TtsClip(outputWavPath, (double)samples / format.SampleRate));
        }
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vwm-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task Caches_sentences_and_reuses_on_second_run()
    {
        var work = TempDir();
        try
        {
            var engine = new FakeEngine("piper-x");
            var project = new WalkthroughProject
            {
                VideoPath = "x", VideoDuration = 30, GlobalVoice = "v",
                Cues = [new Cue { Text = "One. Two." }, new Cue { Text = "Three." }],
            };
            var synth = new CueSynthesizer(_ => engine, work);

            var first = await synth.SynthesizeAsync(project);
            Assert.Equal(2, first.Count);
            Assert.Equal(2, first[0].Sentences.Count);
            Assert.Equal(3, engine.Calls); // One, Two, Three

            var second = await synth.SynthesizeAsync(project);
            Assert.Equal(3, engine.Calls); // all cache hits, no re-synthesis
            Assert.Equal(first[0].Sentences[0].Clip.DurationSeconds,
                         second[0].Sentences[0].Clip.DurationSeconds, 6);
        }
        finally { Directory.Delete(work, true); }
    }

    [Fact]
    public async Task Resolves_global_and_per_cue_voices()
    {
        var work = TempDir();
        try
        {
            var requested = new List<string>();
            var project = new WalkthroughProject
            {
                VideoPath = "x", VideoDuration = 30, GlobalVoice = "glob",
                Cues = [new Cue { Text = "A." }, new Cue { Text = "B.", Voice = "special" }],
            };
            await new CueSynthesizer(v => { requested.Add(v); return new FakeEngine(v); }, work)
                .SynthesizeAsync(project);
            Assert.Contains("glob", requested);
            Assert.Contains("special", requested);
        }
        finally { Directory.Delete(work, true); }
    }

    [Fact]
    public void WavAssembler_applies_per_clip_gain()
    {
        var dir = TempDir();
        try
        {
            var format = new WavFormat(22050, 1, 16);
            var samples = 2205;
            var pcm = new byte[samples * 2];
            for (var i = 0; i < samples; i++) { pcm[i * 2] = 5000 & 0xFF; pcm[i * 2 + 1] = (5000 >> 8) & 0xFF; }
            var clip = Path.Combine(dir, "clip.wav");
            WavFile.Write(clip, format, pcm);

            var outPath = Path.Combine(dir, "out.wav");
            WavAssembler.Assemble([(clip, 0.0, -6.0206)], 0.1, outPath); // -6 dB ~= x0.5

            var (_, outPcm) = WavFile.Read(outPath);
            var sample = (short)(outPcm[200] | (outPcm[201] << 8));
            Assert.InRange(sample, 2400, 2600); // ~2500
        }
        finally { Directory.Delete(dir, true); }
    }
}
