namespace Vwm.Core.Audio;

/// <summary>
/// Lays TTS clips onto a single silent track at their planned offsets, producing
/// one narration WAV that maps 1:1 onto the final video timeline.
/// </summary>
public static class WavAssembler
{
    public static void Assemble(
        IReadOnlyList<(string WavPath, double StartSeconds)> clips,
        double totalSeconds,
        string outputPath)
        => Assemble(clips.Select(c => (c.WavPath, c.StartSeconds, 0.0)).ToList(), totalSeconds, outputPath);

    public static void Assemble(
        IReadOnlyList<(string WavPath, double StartSeconds, double GainDb)> clips,
        double totalSeconds,
        string outputPath)
    {
        if (clips.Count == 0)
            throw new ArgumentException("At least one clip is required.", nameof(clips));

        var (format, _) = WavFile.Read(clips[0].WavPath);
        var totalBytes = AlignDown((long)Math.Ceiling(totalSeconds * format.ByteRate), format.BlockAlign);
        var track = new byte[totalBytes];

        foreach (var (path, start, gainDb) in clips)
        {
            var (clipFormat, pcm) = WavFile.Read(path);
            if (clipFormat != format)
                throw new InvalidDataException(
                    $"'{path}' has format {clipFormat}, expected {format}. All narration clips must come from the same voice.");

            if (gainDb != 0 && format.BitsPerSample == 16)
                ApplyGain16(pcm, gainDb);

            var offset = AlignDown((long)(start * format.ByteRate), format.BlockAlign);
            var length = Math.Min(pcm.Length, track.Length - offset);
            if (length > 0)
                pcm.AsSpan(0, (int)length).CopyTo(track.AsSpan((int)offset));
        }

        WavFile.Write(outputPath, format, track);
    }

    /// <summary>Scales 16-bit PCM samples in place by a dB gain, clamping to avoid wrap-around clipping.</summary>
    private static void ApplyGain16(byte[] pcm, double gainDb)
    {
        var factor = Math.Pow(10, gainDb / 20.0);
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var scaled = (int)Math.Round(sample * factor);
            scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
            pcm[i] = (byte)(scaled & 0xFF);
            pcm[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
    }

    private static long AlignDown(long value, int align) => value - value % align;
}
