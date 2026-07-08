namespace Vwm.Core.Tts;

public sealed record TtsClip(string WavPath, double DurationSeconds);

/// <summary>
/// Text-to-speech engine contract. Every implementation writes a PCM WAV file and
/// reports its exact duration — the whole sync model is built on those durations,
/// so no engine-specific timing metadata is ever needed.
/// </summary>
public interface ITtsEngine
{
    string Name { get; }
    Task<TtsClip> SynthesizeAsync(string text, string outputWavPath, CancellationToken ct = default);
}
