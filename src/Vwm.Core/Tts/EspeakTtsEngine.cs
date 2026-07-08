using Vwm.Core.Audio;
using Vwm.Core.Tools;

namespace Vwm.Core.Tts;

/// <summary>
/// espeak-ng engine. Development/testing only: it exercises the identical
/// spawn-binary-to-WAV contract as Piper on machines where Piper isn't available.
/// </summary>
public sealed class EspeakTtsEngine(string voice = "en-us", int wordsPerMinute = 165) : ITtsEngine
{
    private readonly string _espeakPath = ToolLocator.Find("espeak-ng")
        ?? throw new ToolNotFoundException("espeak-ng", "install espeak-ng (dev/test engine)");

    public string Name => "espeak";

    public async Task<TtsClip> SynthesizeAsync(string text, string outputWavPath, CancellationToken ct = default)
    {
        await ProcessRunner.RunAsync(
            _espeakPath,
            ["-v", voice, "-s", wordsPerMinute.ToString(), "-w", outputWavPath, "--stdin"],
            stdin: text,
            ct: ct);
        return new TtsClip(outputWavPath, WavFile.GetDuration(outputWavPath));
    }
}
