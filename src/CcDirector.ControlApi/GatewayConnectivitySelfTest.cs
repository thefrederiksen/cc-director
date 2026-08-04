using System.Runtime.CompilerServices;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>Outcome of one rung of the troubleshooting ladder (issue #223).</summary>
public enum RungStatus
{
    Pass,
    Fail,
    /// <summary>Informational - never the root cause verdict (versions).</summary>
    Info,
    /// <summary>Not run because an earlier rung already failed - the ladder stops at the first
    /// failing rung; that rung IS the diagnosis.</summary>
    Skipped,
}

/// <summary>One rung's result: what was checked, what was found, and the exact fix.</summary>
public sealed class LadderRung
{
    public string Title { get; init; } = "";
    public RungStatus Status { get; init; }
    /// <summary>What was checked and what was found, one or two short lines.</summary>
    public string Found { get; init; } = "";
    /// <summary>The exact command or action that fixes this rung. Null when passing/info.</summary>
    public string? Fix { get; init; }
}

/// <summary>
/// The Gateway-connectivity troubleshooting ladder, rebuilt for the outbound-only Director.
///
/// The original ladder (issue #223) diagnosed the INBOUND model: is Tailscale up, is the Serve
/// mapping present, does the local listener answer, does the advertised URL dial back. Every one of
/// those questions died with that model - the tunnel-only cut ended the Gateway dialling Directors,
/// and the Remove-the-network-port mission deleted the local listener itself - so the old rungs
/// could only fail on perfectly healthy machines and send their owner to fix things that are not
/// there. What can actually be wrong now, in the order it should be checked:
///
///   1. Is a Gateway configured at all?          (no gateway.url -> nothing to connect to)
///   2. Does the Gateway answer from here?       (outbound GET /healthz - network / Gateway down)
///   3. Is the tunnel connected?                 (the Director's own live connection state)
///   4. Versions                                 (info: this build, and whether /healthz answered)
///
/// The ladder stops at the first failing rung: that rung is the root cause; everything after it is
/// noise. Runs on demand from the troubleshooting dialog; each rung is yielded as it completes so
/// the dialog fills in live (responsive-UI rule). All checks are read-only - fixes are offered,
/// never auto-applied.
/// </summary>
public sealed class GatewayConnectivitySelfTest
{
    private readonly string _directorId;
    private readonly string? _gatewayUrl;
    private readonly Func<GatewayConnectionStatus> _tunnelStatus;

    /// <summary>Test seam: HTTP GET returning (2xx ok, body-or-error). Production: real HTTP.</summary>
    internal Func<string, CancellationToken, Task<(bool ok, string detail)>> HttpProbe { get; set; } = ProbeHttpAsync;

    /// <param name="gatewayUrl">The configured Gateway base URL, or null when none is configured.</param>
    /// <param name="tunnelStatus">Reads the Director's LIVE tunnel state at the moment the rung
    /// runs (the host's GatewayConnectionMonitor), never a copy captured earlier.</param>
    public GatewayConnectivitySelfTest(string directorId, string? gatewayUrl, Func<GatewayConnectionStatus> tunnelStatus)
    {
        _directorId = directorId ?? throw new ArgumentNullException(nameof(directorId));
        _gatewayUrl = gatewayUrl;
        _tunnelStatus = tunnelStatus ?? throw new ArgumentNullException(nameof(tunnelStatus));
    }

    /// <summary>Run the ladder, yielding each rung as it completes.</summary>
    public async IAsyncEnumerable<LadderRung> RunAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        FileLog.Write($"[GatewayConnectivitySelfTest] RunAsync: gateway={_gatewayUrl ?? "(none)"}");
        var failed = false;

        // ----- Rung 1: Is a Gateway configured? -----
        if (string.IsNullOrWhiteSpace(_gatewayUrl))
        {
            yield return new LadderRung
            {
                Title = "Is a Gateway configured?",
                Status = RungStatus.Fail,
                Found = "No gateway.url is configured, so this Director has nothing to connect to. "
                        + "Without a Gateway there is no agent tooling and no remote access - that is "
                        + "the designed standalone state, not a fault, but nothing below can pass.",
                Fix = "Connect a Gateway in Settings (the Gateway tab), or run the Gateway tray app on this machine.",
            };
            failed = true;
        }
        else
        {
            yield return new LadderRung
            {
                Title = "Is a Gateway configured?",
                Status = RungStatus.Pass,
                Found = $"gateway.url = {_gatewayUrl}.",
            };
        }

        // ----- Rung 2: Does the Gateway answer from this machine? -----
        if (failed)
        {
            yield return Skip("Does the Gateway answer from this machine?");
        }
        else
        {
            var url = $"{_gatewayUrl!.TrimEnd('/')}/healthz";
            var (ok, detail) = await HttpProbe(url, ct);
            yield return ok
                ? new LadderRung
                {
                    Title = "Does the Gateway answer from this machine?",
                    Status = RungStatus.Pass,
                    Found = $"GET {url} answered 2xx.",
                }
                : new LadderRung
                {
                    Title = "Does the Gateway answer from this machine?",
                    Status = RungStatus.Fail,
                    Found = $"GET {url} failed from this machine: {detail}. The Gateway is down, the "
                            + "address is wrong, or the network between here and it is broken.",
                    Fix = "If this machine hosts the Gateway, start the Gateway tray app. Otherwise check "
                          + "the address in Settings and this machine's network connection.",
                };
            failed = failed || !ok;
        }

        // ----- Rung 3: Is the tunnel connected? -----
        if (failed)
        {
            yield return Skip("Is the tunnel connected?");
        }
        else
        {
            var status = _tunnelStatus();
            yield return status switch
            {
                GatewayConnectionStatus.Connected => new LadderRung
                {
                    Title = "Is the tunnel connected?",
                    Status = RungStatus.Pass,
                    Found = "The Director's outbound tunnel to the Gateway is connected - this IS the "
                            + "fleet link; there is nothing else to reach.",
                },
                GatewayConnectionStatus.Connecting => new LadderRung
                {
                    Title = "Is the tunnel connected?",
                    Status = RungStatus.Fail,
                    Found = "The Gateway answers (rung 2) but the tunnel is still CONNECTING. Usually this "
                            + "settles within seconds; if it does not, the token this Director holds may "
                            + "no longer be accepted.",
                    Fix = "Give it half a minute and re-run. If it still will not settle, re-save the Gateway "
                          + "settings (which re-dials with the current token), or re-enrol this machine.",
                },
                _ => new LadderRung
                {
                    Title = "Is the tunnel connected?",
                    Status = RungStatus.Fail,
                    Found = $"The Gateway answers (rung 2) but this Director's tunnel is {status}. "
                            + "The Gateway may be refusing this Director's token.",
                    Fix = "Re-save the Gateway settings to re-dial, and check the Gateway's log for a refusal "
                          + "naming this Director.",
                },
            };
        }

        // ----- Rung 4: Versions (info) -----
        yield return new LadderRung
        {
            Title = "Build versions",
            Status = RungStatus.Info,
            Found = $"This Director: {AppVersion.Display} (id {_directorId[..Math.Min(8, _directorId.Length)]}...). "
                    + "See the dialog header for the Gateway's version. This Director accepts no inbound "
                    + "connections at all - a firewall or port question can never be the cause here.",
        };

        FileLog.Write($"[GatewayConnectivitySelfTest] RunAsync complete: rootCauseFound={failed}");
    }

    private static LadderRung Skip(string title) => new()
    {
        Title = title,
        Status = RungStatus.Skipped,
        Found = "Skipped - an earlier rung already failed; fix that one first.",
    };

    /// <summary>One short HTTP GET: (2xx?, body-snippet-or-error). 5s budget per probe.</summary>
    private static async Task<(bool ok, string detail)> ProbeHttpAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var resp = await http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return resp.IsSuccessStatusCode
                ? (true, body.Length > 500 ? body[..500] : body)
                : (false, $"HTTP {(int)resp.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "timeout after 5s");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
