using Vwm.Core.Audio;
using Vwm.Core.Tools;

namespace Vwm.Core.Tts;

/// <summary>
/// Piper neural TTS: a self-contained local binary, no network access at all.
/// Text goes in on stdin, a WAV comes out. The default engine for the packaged app.
/// </summary>
public sealed class PiperTtsEngine : ITtsEngine
{
    private readonly string _piperPath;
    private readonly string _modelPath;

    public string Name => "piper";

    public PiperTtsEngine(string? piperPath = null, string? modelPath = null)
    {
        _piperPath = piperPath
            ?? ToolLocator.Find("piper")
            ?? throw new ToolNotFoundException("piper", "bundle piper in the 'tools/piper' folder next to the app");
        _modelPath = modelPath
            ?? FindDefaultModel()
            ?? throw new ToolNotFoundException("piper voice model (.onnx)", "place a voice model in the 'tools/voices' folder next to the app");
    }

    private static string? FindDefaultModel()
    {
        var voicesDir = Path.Combine(AppContext.BaseDirectory, "tools", "voices");
        return Directory.Exists(voicesDir)
            ? Directory.EnumerateFiles(voicesDir, "*.onnx").OrderBy(f => f).FirstOrDefault()
            : null;
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
