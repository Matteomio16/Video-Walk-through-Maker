using System.Runtime.InteropServices;

namespace Vwm.Core.Tools;

/// <summary>
/// Finds external executables. Looks in the app's bundled "tools" directory first
/// (that is how the packaged Windows zip ships ffmpeg and piper), then falls back to PATH.
/// </summary>
public static class ToolLocator
{
    public static string FfmpegPath => Find("ffmpeg")
        ?? throw new ToolNotFoundException("ffmpeg", "bundle ffmpeg.exe in the 'tools' folder next to the app, or install ffmpeg on PATH");

    public static string FfprobePath => Find("ffprobe")
        ?? throw new ToolNotFoundException("ffprobe", "bundle ffprobe.exe in the 'tools' folder next to the app, or install ffmpeg on PATH");

    public static string? Find(string name)
    {
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? name + ".exe" : name;

        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
        {
            Path.Combine(baseDir, "tools", exe),
            Path.Combine(baseDir, "tools", name, exe),
            Path.Combine(baseDir, exe),
        })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), exe);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}

public sealed class ToolNotFoundException(string tool, string hint)
    : Exception($"Required tool '{tool}' was not found. {hint}.");
