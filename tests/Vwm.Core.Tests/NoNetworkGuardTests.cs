using Vwm.Core;
using Xunit;

namespace Vwm.Core.Tests;

/// <summary>
/// Enforces the product's headline guarantee: the pipeline engine makes no network
/// calls. Vwm.Core drives ffmpeg/Piper as local child processes and does all file
/// IO locally, so it must not reference any networking assembly. The C# compiler
/// records a direct reference to System.Net.* the moment code touches HttpClient,
/// sockets, WebClient, DNS, etc. - so a missing reference is proof the code cannot
/// open a connection. Vwm.Cli and Vwm.App are thin layers over this engine.
/// </summary>
public class NoNetworkGuardTests
{
    // Assemblies whose presence would mean the engine can talk to the network.
    // Kept as a strict "no System.Net.* at all" rule; add an entry here with a
    // written justification only if a non-IO primitive is ever genuinely needed.
    private static readonly string[] Allowed = System.Array.Empty<string>();

    [Fact]
    public void Core_engine_references_no_networking_assemblies()
    {
        var core = typeof(WalkthroughPipeline).Assembly;

        var networking = core.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("System.Net.", System.StringComparison.Ordinal)
                        && !Allowed.Contains(n))
            .Distinct()
            .ToArray();

        Assert.True(networking.Length == 0,
            "Vwm.Core must make no network calls but references: " + string.Join(", ", networking));
    }
}
