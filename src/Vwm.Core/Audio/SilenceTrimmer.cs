namespace Vwm.Core.Audio;

/// <summary>
/// Trims near-silent head and tail from a PCM WAV in place. TTS engines emit a
/// variable sliver of silence before speech begins; removing it makes each clip's
/// duration reflect actual speech, so the planned subtitle/gap timing lines up with
/// the audible voice instead of leading it. Small guard margins are kept so the first
/// consonant and trailing breath are never clipped.
/// </summary>
public static class SilenceTrimmer
{
    private const double ThresholdRatio = 0.010;   // ~ -40 dBFS
    private const double HeadGuardSeconds = 0.02;
    private const double TailGuardSeconds = 0.06;

    /// <summary>Trims <paramref name="path"/> in place and returns its new duration in seconds.</summary>
    public static double Trim(string path)
    {
        var (format, pcm) = WavFile.Read(path);
        if (format.BitsPerSample != 16 || pcm.Length == 0)
            return (double)pcm.Length / format.ByteRate;

        var samples = pcm.Length / 2;
        var threshold = (int)(ThresholdRatio * short.MaxValue);

        int Amp(int s)
        {
            var v = (short)(pcm[s * 2] | (pcm[s * 2 + 1] << 8));
            return Math.Abs(v);
        }

        var first = 0;
        while (first < samples && Amp(first) <= threshold)
            first++;
        if (first == samples)
            return (double)pcm.Length / format.ByteRate; // all silence — leave untouched

        var last = samples - 1;
        while (last > first && Amp(last) <= threshold)
            last--;

        var headGuard = (int)(HeadGuardSeconds * format.SampleRate) * format.Channels;
        var tailGuard = (int)(TailGuardSeconds * format.SampleRate) * format.Channels;
        var startSample = Math.Max(0, first - headGuard);
        var endSample = Math.Min(samples - 1, last + tailGuard);

        var startByte = AlignDown(startSample * 2, format.BlockAlign);
        var endByte = Math.Min(pcm.Length, AlignUp((endSample + 1) * 2, format.BlockAlign));
        var trimmed = pcm.AsSpan(startByte, endByte - startByte);

        WavFile.Write(path, format, trimmed);
        return (double)trimmed.Length / format.ByteRate;
    }

    private static int AlignDown(int value, int align) => value - value % align;
    private static int AlignUp(int value, int align) => value % align == 0 ? value : value + (align - value % align);
}
