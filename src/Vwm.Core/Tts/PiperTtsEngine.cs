using Vwm.Core.Audio;
using Vwm.Core.Tools;

namespace Vwm.Core.Tts;

/// <summary>
/// Piper neural TTS: a self-contained local binary, no network access at all.
/// Text goes in on stdin, a WAV comes out. The default engine for the packaged app.
/// Several voice models can be bundled (see <see cref="PiperVoiceCatalog"/>); each
/// engine instance is bound to one model.
/// </summary>
public sealed class PiperTtsEngine : ITtsEngine
{
    private readonly string _piperPath;
    private readonly string _modelPath;

    /// <summary>Includes the voice id so per-voice outputs (e.g. preview caches) never collide.</summary>
    public string Name => $"piper-{Path.GetFileNameWithoutExtension(_modelPath)}";

    public PiperTtsEngine(string? piperPath = null, string? modelPath = null)
    {
        _piperPath = piperPath
            ?? ToolLocator.Find("piper")
            ?? throw new ToolNotFoundException("piper", "bundle piper in the 'tools/piper' folder next to the app");
        _modelPath = modelPath
            ?? PiperVoiceCatalog.Enumerate().FirstOrDefault()?.ModelPath
            ?? throw new ToolNotFoundException("piper voice model (.onnx)", "place a voice model in the 'tools/voices' folder next to the app");
    }

    public PiperTtsEngine(PiperVoice voice, string? piperPath = null)
        : this(piperPath, voice.ModelPath)
    {
    }

    public async Task<TtsClip> SynthesizeAsync(string text, string outputWavPath, CancellationToken ct = default)
    {
        await ProcessRunner.RunAsync(
            _piperPath,
            ["--model", _modelPath, "--output_file", outputWavPath],
            workingDirectory: Path.GetDirectoryName(Path.GetFullPath(outputWavPath)),
            stdin: text,
            ct: ct);
        return new TtsClip(outputWavPath, WavFile.GetDuration(outputWavPath));
    }
}
