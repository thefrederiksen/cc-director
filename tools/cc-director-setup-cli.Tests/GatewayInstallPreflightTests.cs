using CcDirector.Setup.Cli;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Cli.Tests;

/// <summary>
/// Guards the managed Gateway install/update path (D2a) at the PRODUCTION SEAM: the real
/// <see cref="Commands.UpdateAsync"/> gateway branch, not the extracted helper. There is NO
/// OPENAI_API_KEY demand - inference routes through the account-minted dt_live_ key the managed
/// Gateway runtime mints itself. Demanding an OpenAI key here used to block a Gateway refresh on
/// machines that never had one.
/// </summary>
public class GatewayInstallPreflightTests
{
    /// <summary>
    /// The headline D2a guard, driven through the ACTUAL <c>install --role gateway</c> dispatch. With
    /// no OPENAI_API_KEY in the environment, the real gateway branch runs its pre-flight and then
    /// proceeds to release resolution - it does NOT demand a key. We point it at an empty offline
    /// release dir so resolution fails fast (no network, no install work): reaching that failure proves
    /// the branch got PAST pre-flight without a key demand.
    ///
    /// Revert-proof: re-insert an executable OPENAI_API_KEY demand in the REAL Commands gateway branch
    /// (Commands.cs, the `if (isGatewayInstall)` block ~:276-285) - e.g. reject when
    /// Environment.GetEnvironmentVariable("OPENAI_API_KEY") is blank -> UpdateAsync returns Error
    /// instead of reaching release resolution, so this test's ThrowsAny (FileNotFoundException) goes red.
    /// </summary>
    [Fact]
    public async Task InstallRoleGateway_NoOpenAiKey_ReachesReleaseResolution_NoKeyDemand()
    {
        if (!OperatingSystem.IsWindows())
            return; // the managed Gateway is Windows-only; the no-key assertion applies where it installs.

        var root = Directory.CreateTempSubdirectory("d2a-cli-").FullName;
        var emptyReleaseDir = Path.Combine(root, "release");
        Directory.CreateDirectory(emptyReleaseDir); // no release-manifest.json -> offline resolution fails fast

        // Run the real branch with NO OpenAI key in the process environment (User-scope is left untouched;
        // the fixed pre-flight never reads either, so this only matters for a reverted key demand).
        var savedProcessKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null, EnvironmentVariableTarget.Process);
        try
        {
            var args = CliArgs.Parse(new[] { "install", "--role", "gateway", "--release-dir", emptyReleaseDir });
            var layout = new InstallLayout(Path.Combine(root, "local"));

            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => Commands.UpdateAsync(args, layout, json: false, installMode: true));

            // It failed resolving the offline release (past pre-flight), NOT on a key demand.
            Assert.IsType<FileNotFoundException>(ex);
            Assert.DoesNotContain("OPENAI", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", savedProcessKey, EnvironmentVariableTarget.Process);
            Directory.Delete(root, recursive: true);
        }
    }

    // The pre-flight's only hard requirement is the platform (the managed Gateway is Windows-only).
    [Fact]
    public void Check_NonWindows_FailsWindowsOnly()
    {
        var result = GatewayInstallPreflight.Check(isWindows: false);
        Assert.NotNull(result);
        Assert.Contains("Windows-only", result);
    }
}
