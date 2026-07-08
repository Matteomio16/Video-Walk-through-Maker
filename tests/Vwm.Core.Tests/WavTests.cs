using Vwm.Core.Audio;
using Xunit;

namespace Vwm.Core.Tests;

public class WavTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vwm-wav-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly WavFormat Mono16k = new(SampleRate: 16000, Channels: 1, BitsPerSample: 16);

    private string WriteTone(string name, double seconds)
    {
        var pcm = new byte[(int)(seconds * Mono16k.ByteRate)];
        for (var i = 0; i < pcm.Length; i += 2)
            pcm[i] = 100; // constant non-zero sample
        var path = Path.Combine(_dir, name);
        WavFile.Write(path, Mono16k, pcm);
        return path;
    }

    [Fact]
    public void Roundtrips_format_and_duration()
    {
        var path = WriteTone("a.wav", 1.5);

        var (format, pcm) = WavFile.Read(path);

        Assert.Equal(Mono16k, format);
        Assert.Equal(1.5, WavFile.GetDuration(path), 3);
        Assert.Equal((int)(1.5 * Mono16k.ByteRate), pcm.Length);
    }

    [Fact]
    public void Assembles_clips_at_offsets_with_silence_between()
    {
        var a = WriteTone("a.wav", 1.0);
        var b = WriteTone("b.wav", 0.5);
        var outPath = Path.Combine(_dir, "out.wav");

        WavAssembler.Assemble([(a, 0.5), (b, 2.0)], totalSeconds: 3.0, outPath);

        var (format, pcm) = WavFile.Read(outPath);
        Assert.Equal(Mono16k, format);
        Assert.Equal(3.0, WavFile.GetDuration(outPath), 2);

        bool NonSilentAt(double t) => pcm[(int)(t * format.ByteRate) & ~1] != 0;
        Assert.False(NonSilentAt(0.2)); // before first clip
        Assert.True(NonSilentAt(1.0));  // inside first clip
        Assert.False(NonSilentAt(1.8)); // gap
        Assert.True(NonSilentAt(2.2));  // inside second clip
        Assert.False(NonSilentAt(2.8)); // tail
    }

    [Fact]
    public void Rejects_mismatched_clip_formats()
    {
        var a = WriteTone("a.wav", 0.5);
        var otherPath = Path.Combine(_dir, "other.wav");
        WavFile.Write(otherPath, new WavFormat(22050, 1, 16), new byte[22050]);

        Assert.Throws<InvalidDataException>(() =>
            WavAssembler.Assemble([(a, 0), (otherPath, 1.0)], 2.0, Path.Combine(_dir, "out.wav")));
    }
}
