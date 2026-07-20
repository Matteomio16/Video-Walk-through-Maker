namespace Vwm.Core.Tts;

/// <summary>One installed Piper voice model (an .onnx file in the voices folder).</summary>
public sealed record PiperVoice(string Id, string DisplayName, string Language, string ModelPath);

/// <summary>
/// Discovers the Piper voice models bundled with the app. Voices are plain .onnx
/// files dropped into "tools/voices" next to the exe — adding a voice to a deployed
/// install is just copying two files there (.onnx + .onnx.json).
/// </summary>
public static class PiperVoiceCatalog
{
    public static string DefaultVoicesDir => Path.Combine(AppContext.BaseDirectory, "tools", "voices");

    /// <summary>Voices sorted with medium-quality models first (smaller, faster; the shipped default), then by name.</summary>
    public static IReadOnlyList<PiperVoice> Enumerate(string? voicesDir = null)
    {
        var dir = voicesDir ?? DefaultVoicesDir;
        if (!Directory.Exists(dir))
            return [];
        return Directory.EnumerateFiles(dir, "*.onnx")
            .Select(FromModelPath)
            .OrderBy(v => v.Id.EndsWith("-high") ? 1 : 0)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static PiperVoice? FindById(string id, string? voicesDir = null) =>
        Enumerate(voicesDir).FirstOrDefault(v =>
            string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds a voice entry from a model filename like "en_US-ryan-high.onnx".</summary>
    public static PiperVoice FromModelPath(string modelPath)
    {
        var id = Path.GetFileNameWithoutExtension(modelPath);
        // Piper's naming convention: {lang}_{REGION}-{voice_name}-{quality}
        var parts = id.Split('-');
        var language = parts.Length >= 2 ? parts[0] : "";
        var name = parts.Length >= 3 ? string.Join('-', parts[1..^1]) : id;
        var quality = parts.Length >= 3 ? parts[^1] : "";

        var display = FriendlyName(name) + LanguageLabel(language) + QualityLabel(quality);
        return new PiperVoice(id, display, language, modelPath);
    }

    private static string FriendlyName(string raw)
    {
        if (KnownNames.TryGetValue(raw, out var known))
            return known;
        var words = raw.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(' ', words);
    }

    private static string LanguageLabel(string code) => code switch
    {
        "" => "",
        "en_US" => " (English, US)",
        "en_GB" => " (English, UK)",
        _ => $" ({code.Replace('_', '-')})",
    };

    private static string QualityLabel(string quality) =>
        quality == "high" ? " — high quality" : "";

    private static readonly Dictionary<string, string> KnownNames = new()
    {
        // dataset names whose word-by-word title-casing reads wrong
        ["hfc_female"] = "Heather",
        ["hfc_male"] = "Harry",
        ["libritts_r"] = "LibriTTS-R",
    };
}
