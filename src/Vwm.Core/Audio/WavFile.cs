using System.Buffers.Binary;

namespace Vwm.Core.Audio;

public sealed record WavFormat(int SampleRate, short Channels, short BitsPerSample)
{
    public int BlockAlign => Channels * BitsPerSample / 8;
    public int ByteRate => SampleRate * BlockAlign;
}

/// <summary>Minimal RIFF/WAVE PCM reader-writer, enough to measure TTS clips and assemble the narration track.</summary>
public static class WavFile
{
    public static (WavFormat Format, byte[] Pcm) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 ||
            !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException($"'{path}' is not a RIFF/WAVE file.");
        }

        WavFormat? format = null;
        byte[]? pcm = null;
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = bytes.AsSpan(pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4, 4));
            var dataStart = pos + 8;
            // Some writers emit a bogus size when streaming; clamp to what is actually in the file.
            var available = bytes.Length - dataStart;
            var chunkLen = (int)Math.Min(size, (uint)available);

            if (id.SequenceEqual("fmt "u8) && chunkLen >= 16)
            {
                var audioFormat = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataStart, 2));
                if (audioFormat != 1)
                    throw new InvalidDataException($"'{path}': only PCM wav is supported (format tag {audioFormat}).");
                format = new WavFormat(
                    SampleRate: BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(dataStart + 4, 4)),
                    Channels: BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataStart + 2, 2)),
                    BitsPerSample: BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataStart + 14, 2)));
            }
            else if (id.SequenceEqual("data"u8))
            {
                pcm = bytes.AsSpan(dataStart, chunkLen).ToArray();
            }

            pos = dataStart + chunkLen + (chunkLen % 2); // chunks are word-aligned
        }

        if (format is null || pcm is null)
            throw new InvalidDataException($"'{path}': missing fmt/data chunk.");
        return (format, pcm);
    }

    public static double GetDuration(string path)
    {
        var (format, pcm) = Read(path);
        return (double)pcm.Length / format.ByteRate;
    }

    public static void Write(string path, WavFormat format, ReadOnlySpan<byte> pcm)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + pcm.Length);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], format.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], format.ByteRate);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)format.BlockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], format.BitsPerSample);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], pcm.Length);
        fs.Write(header);
        fs.Write(pcm);
    }
}
