using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// No test in this assembly may obtain a port by opening a listener, reading the assigned number, and then
/// CLOSING it before the port is used (issue #1156).
///
/// WHY THIS IS PINNED AS SOURCE TEXT. The defect is not a wrong value - the port returned by a
/// probe-and-release helper is perfectly valid at the instant it is read. The defect is the WINDOW after it
/// is released, during which any other process on the machine can bind it. No assertion on the returned port
/// can detect that, because the port is correct until somebody else takes it, and whether anybody does is a
/// race. So the pattern itself is what has to be forbidden, and the only place to forbid it is the source.
///
/// This mattered enough to be worth a guard: probe-and-release was the mechanism behind the historical
/// cross-run collisions in this suite, and it is an easy pattern to reintroduce because it looks careful -
/// it asks the operating system for a free port rather than hardcoding one. The safe replacement is
/// <see cref="DeadPortReservation"/>, which HOLDS the port for as long as the address must stay dead.
///
/// The single permitted exception is the explicit-port Kestrel contract test, which must pass a real number
/// to a real listener to prove the explicit-port path works at all. It is named here so the exemption is a
/// deliberate, visible decision rather than a hole anyone can widen.
/// </summary>
public sealed class NoProbeAndReleasePortsTests
{
    /// <summary>
    /// The one file allowed to hand a probed port to a real bind: it exists to prove that GatewayHost
    /// honours an explicitly chosen port, which cannot be demonstrated without one.
    /// </summary>
    private static readonly string[] AllowedFiles = ["GatewayHostAssignedPortTests.cs"];

    [Fact]
    public void No_test_probes_a_port_and_releases_it_before_use()
    {
        var testsDir = Path.Combine(RepoRoot(), "src", "CcDirector.Gateway.Tests");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (AllowedFiles.Contains(name)) continue;
            if (name == nameof(NoProbeAndReleasePortsTests) + ".cs") continue;
            if (name == nameof(DeadPortReservation) + ".cs") continue;

            var text = File.ReadAllText(file);

            // The shape: a TcpListener is created, then Stop()/Dispose() is called on it. A test that needs a
            // listener for real work keeps it running; one that stops it is releasing the port to get a
            // number, which is the pattern being banned.
            if (!text.Contains("new TcpListener(", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(text, @"\.Stop\(\)\s*;", RegexOptions.None, TimeSpan.FromSeconds(5))
                || text.Contains("using var l = new TcpListener(", StringComparison.Ordinal))
            {
                offenders.Add(name);
            }
        }

        Assert.True(offenders.Count == 0,
            "These files obtain a port from a TcpListener and then release it before the port is used. "
            + "That port is unowned from the moment it is released, so another process - notably a second "
            + "run of this suite - can bind it and turn an 'unreachable' assertion into a conversation with "
            + "somebody else's listener. Use DeadPortReservation, which holds the port for as long as the "
            + "address must stay dead: " + string.Join(", ", offenders));
    }

    /// <summary>The repository root, located from this source file's own path - the tests always run
    /// from a checkout, and bin-relative paths would break under different runners.</summary>
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        // this file: <repo>/src/CcDirector.Gateway.Tests/NoProbeAndReleasePortsTests.cs
        var dir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }
}
