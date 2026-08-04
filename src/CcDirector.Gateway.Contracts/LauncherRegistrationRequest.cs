namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The launcher's self-registration body (POST /launchers/register), sent on startup and re-sent
/// whenever a heartbeat answers 410.
///
/// Remove-the-network-port mission, phase 6: this used to carry a loopback PORT, a bearer TOKEN and a
/// NETWORK ADDRESS, because the Gateway dialed the launcher's REST interface back over them. That
/// interface no longer exists - the launcher listens on nothing - so a command reaches a launcher only
/// by riding DOWN the persistent stream the launcher itself opened (<see cref="LauncherStreamHello"/>).
/// The fields were removed rather than left optional: a stored address and credential for a surface
/// that is gone is exactly the kind of live-looking dead door a future caller would wire itself to.
/// </summary>
public sealed class LauncherRegistrationRequest
{
    /// <summary>Hostname of the machine the launcher is running on (Environment.MachineName).</summary>
    public string MachineName { get; set; } = "";

    /// <summary>OS process id of the launcher (informational / diagnostics).</summary>
    public int Pid { get; set; }

    /// <summary>Launcher version string.</summary>
    public string Version { get; set; } = "";

    /// <summary>UTC timestamp when the launcher process started.</summary>
    public DateTime StartedAt { get; set; }
}
