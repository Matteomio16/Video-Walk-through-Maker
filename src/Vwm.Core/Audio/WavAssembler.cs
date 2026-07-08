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
    {
        if (clips.Count == 0)
            throw new ArgumentException("At least one clip is required.", nameof(clips));

        var (format, _) = WavFile.Read(clips[0].WavPath);
        var totalBytes = AlignDown((long)Math.Ceiling(totalSeconds * format.ByteRate), format.BlockAlign);
        var track = new byte[totalBytes];

        foreach (var (path, start) in clips)
        {
            var (clipFormat, pcm) = WavFile.Read(path);
            if (clipFormat != format)
                throw new InvalidDataException(
                    $"'{path}' has format {clipFormat}, expected {format}. All narration clips must come from the same voice.");

            var offset = AlignDown((long)(start * format.ByteRate), format.BlockAlign);
            var length = Math.Min(pcm.Length, track.Length - offset);
            if (length > 0)
                pcm.AsSpan(0, (int)length).CopyTo(track.AsSpan((int)offset));
        }

        WavFile.Write(outputPath, format, track);
    }

    private static long AlignDown(long value, int align) => value - value % align;
}
