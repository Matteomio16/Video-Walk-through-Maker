using Windows.Media.SpeechSynthesis;
using Vwm.Core.Audio;
using Vwm.Core.Tts;

namespace Vwm.Tts.Windows;

/// <summary>
/// Windows built-in speech engine (WinRT). Fully on-device, ships with the OS —
/// the zero-extra-software policy fallback when bundling Piper isn't allowed.
/// </summary>
public sealed class WindowsTtsEngine : ITtsEngine
{
    private readonly VoiceInformation? _voice;

    public string Name => "windows";

    public WindowsTtsEngine(string? voiceName = null)
    {
        var voices = SpeechSynthesizer.AllVoices;
        _voice = voiceName is not null
            ? voices.FirstOrDefault(v => v.DisplayName.Contains(voiceName, StringComparison.OrdinalIgnoreCase))
            : voices.FirstOrDefault(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
              ?? voices.FirstOrDefault();
    }

    public static IReadOnlyList<string> AvailableVoices() =>
        SpeechSynthesizer.AllVoices.Select(v => $"{v.DisplayName} ({v.Language})").ToList();

    public async Task<TtsClip> SynthesizeAsync(string text, string outputWavPath, CancellationToken ct = default)
    {
        using var synth = new SpeechSynthesizer();
        if (_voice is not null)
            synth.Voice = _voice;

        var stream = await synth.SynthesizeTextToStreamAsync(text);
        await using (var reader = stream.AsStreamForRead())
        await using (var file = File.Create(outputWavPath))
        {
            await reader.CopyToAsync(file, ct);
        }
        return new TtsClip(outputWavPath, WavFile.GetDuration(outputWavPath));
    }
}
