namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A registered cc-launcher entry as served by GET /launchers and as embedded in the
/// machines listing. Issue #331.
///
/// Remove-the-network-port mission, phase 6: no port and no network address any more. The launcher
/// listens on nothing; commands reach it down the persistent stream it opens to the Gateway, so
/// there is no dial-back address to publish. Liveness is the heartbeat (<see cref="LastSeenAt"/>)
/// and, for command delivery, the stream connection being up.
/// </summary>
public sealed class LauncherDto
{
    /// <summary>Hostname of the machine.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>OS process id of the launcher.</summary>
    public int Pid { get; set; }

    /// <summary>Launcher version string.</summary>
    public string Version { get; set; } = "";

    /// <summary>UTC timestamp when the launcher process started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC timestamp of the last successful registration or heartbeat.</summary>
    public DateTime LastSeenAt { get; set; }
}
