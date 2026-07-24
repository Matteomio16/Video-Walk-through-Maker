using System.Diagnostics;
using System.Text;

namespace Vwm.Core.Tools;

public static class ProcessRunner
{
    /// <summary>Runs a process to completion, throwing (with the stderr tail) on a non-zero exit code.</summary>
    public static async Task<string> RunAsync(
        string exePath,
        IEnumerable<string> args,
        string? workingDirectory = null,
        string? stdin = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            // Without this, every ffmpeg/piper invocation from the GUI app flashes a
            // console window on Windows.
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{exePath}'.");

        // Cancelling a .NET await does not stop the child; kill the whole ffmpeg/Piper
        // process tree so a cancelled or abandoned job stops consuming CPU and disk.
        await using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        });

        if (stdin is not null)
        {
            await proc.StandardInput.WriteAsync(stdin);
            proc.StandardInput.Close();
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
        {
            var tail = Tail(stderr, 30);
            throw new ExternalToolException(
                $"'{Path.GetFileName(exePath)} {string.Join(' ', psi.ArgumentList)}' exited with code {proc.ExitCode}.\n{tail}");
        }
        return stdout;
    }

    private static string Tail(string text, int lines)
    {
        var all = text.Split('\n');
        var sb = new StringBuilder();
        foreach (var line in all.Skip(Math.Max(0, all.Length - lines)))
            sb.AppendLine(line);
        return sb.ToString();
    }
}

public sealed class ExternalToolException(string message) : Exception(message);
