using Vwm.Core.Audio;
using Xunit;

namespace Vwm.Core.Tests;

public class SilenceTrimmerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vwm-trim-tests").FullName;
    private static readonly WavFormat Mono = new(SampleRate: 16000, Channels: 1, BitsPerSample: 16);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteClip(double leadSilence, double tone, double trailSilence)
    {
        int Samples(double s) => (int)(s * Mono.SampleRate);
        var total = Samples(leadSilence) + Samples(tone) + Samples(trailSilence);
        var pcm = new byte[total * 2];
        var toneStart = Samples(leadSilence);
        var toneEnd = toneStart + Samples(tone);
        for (var i = toneStart; i < toneEnd; i++)
        {
            short v = 8000; // well above threshold
            pcm[i * 2] = (byte)(v & 0xff);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        var path = Path.Combine(_dir, "clip.wav");
        WavFile.Write(path, Mono, pcm);
        return path;
    }

    [Fact]
    public void Trims_leading_and_trailing_silence()
    {
        var path = WriteClip(leadSilence: 0.5, tone: 1.0, trailSilence: 0.5);
        Assert.Equal(2.0, WavFile.GetDuration(path), 2);

        var newDuration = SilenceTrimmer.Trim(path);

        // ~1.0s of tone plus small head/tail guards (0.02 + 0.06), nowhere near the original 2.0s.
        Assert.InRange(newDuration, 1.0, 1.2);
        Assert.Equal(newDuration, WavFile.GetDuration(path), 2);
    }

    [Fact]
    public void Keeps_speech_onset_near_the_start_after_trim()
    {
        var path = WriteClip(leadSilence: 0.8, tone: 0.5, trailSilence: 0.2);
        SilenceTrimmer.Trim(path);

        var (format, pcm) = WavFile.Read(path);
        // Within the head guard (~20ms) the tone should already be present.
        var probe = (int)(0.03 * format.SampleRate);
        var sample = (short)(pcm[probe * 2] | (pcm[probe * 2 + 1] << 8));
        Assert.True(Math.Abs(sample) > 1000, "expected speech near the start after trimming lead silence");
    }

    [Fact]
    public void All_silence_clip_is_left_untouched()
    {
        var path = WriteClip(leadSilence: 0.3, tone: 0.0, trailSilence: 0.3);
        var before = WavFile.GetDuration(path);
        var after = SilenceTrimmer.Trim(path);
        Assert.Equal(before, after, 3);
    }
}
