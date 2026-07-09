namespace CcDirector.Gateway.Contracts;

/// <summary>
/// launcher-persistent-join: the first message a cc-launcher sends after opening its persistent stream to
/// the Gateway, declaring which machine this connection speaks for. The <see cref="LauncherHub"/> binds the
/// connection to this identity; from then on every command the Gateway pushes DOWN this connection reaches
/// only this machine's launcher, so one connection can never drive another machine's launcher.
///
/// This is the launcher twin of <see cref="DirectorStreamHello"/> (the Director's UP-channel Hello). A
/// launcher only ever RECEIVES commands after Hello - it pushes no session state - so this is the only
/// message it sends up the stream.
/// </summary>
public sealed class LauncherStreamHello
{
    /// <summary>The machine name (the same key the launcher registers under via POST /launchers/register).</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The launcher's loopback REST port, for diagnostics and cross-referencing the registry entry.</summary>
    public int Port { get; set; }

    /// <summary>Launcher build version, for diagnostics.</summary>
    public string Version { get; set; } = "";
}
