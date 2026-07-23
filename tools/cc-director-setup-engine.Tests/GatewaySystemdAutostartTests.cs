using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for <see cref="GatewaySystemdAutostart"/> - the pure systemd --user unit generation (issue #2022).
/// The systemctl interaction is not exercised here (it would mutate the real user systemd session); these
/// tests pin the unit format the installer, the uninstaller, and the `devthrottle-setup-cli autostart`
/// command all rely on. That the unit actually brings the Gateway up at login on a headless Linux server is
/// proven by a RUN recorded in the QA document - a passing test is not that evidence.
/// </summary>
public sealed class GatewaySystemdAutostartTests
{
    private const string Exe = "/home/tester/.local/share/cc-director/gateway/devthrottle-gateway";

    [Fact]
    public void UnitContent_ContainsExecStartWithExeAndArguments()
    {
        var unit = GatewaySystemdAutostart.UnitContent(Exe, "--managed");

        Assert.Contains($"ExecStart=\"{Exe}\" --managed", unit);
        Assert.Contains("Description=DevThrottle Gateway", unit);
    }

    [Fact]
    public void UnitContent_StartsAtLoginViaDefaultTarget()
    {
        // WantedBy=default.target is the whole point: the unit is pulled in when the user session starts,
        // with no person present - the systemd analogue of the Run key and RunAtLoad.
        var unit = GatewaySystemdAutostart.UnitContent(Exe, null);

        Assert.Contains("[Install]", unit);
        Assert.Contains("WantedBy=default.target", unit);
    }

    [Fact]
    public void UnitContent_RestartsOnFailureOnly()
    {
        // Restart=on-failure resurrects the Gateway after a crash but leaves a CLEAN exit (POST /shutdown)
        // exited - the systemd analogue of launchd's KeepAlive SuccessfulExit=false. Restart=always would
        // relaunch a Gateway that was deliberately stopped.
        var unit = GatewaySystemdAutostart.UnitContent(Exe, "--managed");

        Assert.Contains("Restart=on-failure", unit);
        Assert.DoesNotContain("Restart=always", unit);
    }

    [Fact]
    public void UnitContent_NoArguments_QuotesExeAlone()
    {
        var unit = GatewaySystemdAutostart.UnitContent(Exe, null);

        Assert.Contains($"ExecStart=\"{Exe}\"", unit);
        Assert.DoesNotContain("--managed", unit);
    }

    [Fact]
    public void UnitContent_QuotesExeSoASpacedPathStaysOneToken()
    {
        // A path with a space must stay one ExecStart token, or systemd would hand the Gateway a torn path.
        var spaced = "/home/tester/My Apps/devthrottle-gateway";
        var unit = GatewaySystemdAutostart.UnitContent(spaced, "--managed");

        Assert.Contains($"ExecStart=\"{spaced}\" --managed", unit);
    }

    [Fact]
    public void UnitContent_EmptyExe_Throws()
    {
        Assert.Throws<ArgumentException>(() => GatewaySystemdAutostart.UnitContent("", "--managed"));
    }

    [Fact]
    public void UnitPath_IsUserSystemdUnitWithTheUnitName()
    {
        Assert.EndsWith(
            Path.Combine(".config", "systemd", "user", "devthrottle-gateway.service"),
            GatewaySystemdAutostart.UnitPath,
            StringComparison.Ordinal);
    }
}
