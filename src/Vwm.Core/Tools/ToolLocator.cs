using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vwm.Core.Tools;

/// <summary>
/// Finds external executables. Looks in the app's bundled "tools" directory first
/// (that is how the packaged Windows zip ships ffmpeg and piper).
///
/// Packaged builds ship <c>tools/tools.manifest.json</c> (exe filename -> SHA-256).
/// When that manifest is present the locator runs "sealed": it resolves ONLY bundled
/// binaries, verifies each against its manifest hash, and fails closed on any mismatch
/// or unlisted tool. Without a manifest (developer builds) it falls back to PATH so
/// tests and local runs still work. <c>VWM_SEALED=1</c> forces sealed resolution
/// (bundled-only, no PATH) even when no manifest is present.
/// </summary>
public static class ToolLocator
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Verified = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string>? _manifest;
    private static bool _manifestLoaded;

    public static string FfmpegPath => Find("ffmpeg")
        ?? throw new ToolNotFoundException("ffmpeg", "bundle ffmpeg.exe in the 'tools' folder next to the app, or install ffmpeg on PATH");

    public static string FfprobePath => Find("ffprobe")
        ?? throw new ToolNotFoundException("ffprobe", "bundle ffprobe.exe in the 'tools' folder next to the app, or install ffmpeg on PATH");

    public static bool Sealed =>
        Manifest is not null ||
        string.Equals(Environment.GetEnvironmentVariable("VWM_SEALED"), "1", StringComparison.Ordinal);

    public static string? Find(string name)
    {
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? name + ".exe" : name;

        lock (Gate)
        {
            if (Verified.TryGetValue(exe, out var cached))
                return cached;
        }

        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
        {
            Path.Combine(baseDir, "tools", exe),
            Path.Combine(baseDir, "tools", name, exe),
            Path.Combine(baseDir, exe),
        })
        {
            if (File.Exists(candidate))
                return Accept(exe, candidate);
        }

        // Sealed mode never trusts PATH: a substituted ffmpeg/piper on a corporate
        // endpoint must not be reachable through the product.
        if (Sealed)
            return null;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), exe);
            if (File.Exists(candidate))
                return Accept(exe, candidate);
        }
        return null;
    }

    private static string Accept(string exe, string path)
    {
        var manifest = Manifest;
        if (manifest is not null)
        {
            if (!manifest.TryGetValue(exe, out var expected))
                throw new ToolIntegrityException(exe, "it is not listed in tools.manifest.json");
            var actual = Sha256(path);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new ToolIntegrityException(exe, $"SHA-256 mismatch (expected {expected}, got {actual})");
        }
        lock (Gate)
            Verified[exe] = path;
        return path;
    }

    private static Dictionary<string, string>? Manifest
    {
        get
        {
            lock (Gate)
            {
                if (!_manifestLoaded)
                {
                    _manifestLoaded = true;
                    var manifestPath = Path.Combine(AppContext.BaseDirectory, "tools", "tools.manifest.json");
                    if (File.Exists(manifestPath))
                    {
                        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath));
                        if (raw is not null)
                            _manifest = new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase);
                    }
                }
                return _manifest;
            }
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public sealed class ToolNotFoundException(string tool, string hint)
    : Exception($"Required tool '{tool}' was not found. {hint}.");

public sealed class ToolIntegrityException(string tool, string reason)
    : Exception($"Refusing to run '{tool}': {reason}. The bundled tools may be corrupt or tampered with; reinstall the app.");
