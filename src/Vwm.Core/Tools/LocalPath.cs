using System.Runtime.InteropServices;

namespace Vwm.Core.Tools;

/// <summary>
/// Guards that media paths are real local files, not network URLs or UNC shares.
/// This is what backs the "no network at runtime" guarantee for the ffmpeg/Piper
/// child processes, which would otherwise happily open http://, rtsp://, smb://, etc.
/// </summary>
public static class LocalPath
{
    public static string RequireInputFile(string path, string label)
    {
        var full = Canonical(path, label);
        if (!File.Exists(full))
            throw new ArgumentException($"{label} not found: {full}");
        return full;
    }

    public static string RequireOutputFile(string path, string label)
    {
        var full = Canonical(path, label);
        var dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            throw new ArgumentException($"{label} directory does not exist: {dir}");
        return full;
    }

    private static string Canonical(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"{label} is empty.");
        // Reject anything that carries a URL scheme (http, https, rtsp, smb, data, …).
        // A bare Windows path like C:\foo.mp4 parses as an absolute file URI, so IsFile lets it through.
        if (path.Contains("://", StringComparison.Ordinal) ||
            (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile))
            throw new ArgumentException($"{label} must be a local file, not a URL: {path}");

        var full = Path.GetFullPath(path);
        // Reject UNC network shares (\\server\share).
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException($"{label} must be on a local drive, not a network share: {full}");
        return full;
    }
}
