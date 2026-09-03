using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// THE PRODUCTION READ OF THE SCREEN A RULE IS WRITTEN AGAINST, as one testable type (fix round E, ruling
/// E3). This is where the caller's tenant, the pushed roster location, the Director route, the tunnel read
/// and the roster-owned origin JOIN - the exact code that establishes provenance for ruling D2. It used to
/// be a private method on the host, which no test called: every hosted test substituted the whole reader,
/// so the green tests proved the handoff to the seam and replaced the code that mattered.
///
/// So the composition lives here, behind two narrow seams the host supplies - "locate this session on
/// this tenant's roster" and "read this session's rows from that Director" - and the tests keep THIS type
/// in the path and observe its far side: the exact rows, the origin off the roster row, and each refusal.
///
/// TWO REFUSALS THAT MUST NEVER LOOK ALIKE. A Director that vanished between the roster locate and the
/// read answers no rows at all, and that is a FAILURE with its own sentence. A session whose screen reads
/// empty answers rows that are blank, and that is a reading - the author refuses it as "an empty screen
/// is not a capture", which is a state a rule could be authored against once something is on the screen.
/// Collapsing the two would let a dropped tunnel present as a quiet terminal.
/// </summary>
internal sealed class GatewayRuleScreenReader
{
    /// <summary>Locate a session on ONE tenant's pushed roster: its owning Director and its roster row,
    /// or null when this tenant has no such session.</summary>
    public delegate (string DirectorId, SessionDto Session)? LocateSession(TenantId tenant, string sessionId);

    /// <summary>Read a session's screen rows from its Director through the tunnel, or null when they
    /// cannot be read - the Director is not connected, or dropped between the locate and this read.</summary>
    public delegate Task<IReadOnlyList<string>?> ReadRows(TenantId tenant, string directorId, string sessionId, CancellationToken ct);

    private readonly LocateSession _locate;
    private readonly ReadRows _readRows;

    /// <exception cref="ArgumentNullException">A seam is null.</exception>
    public GatewayRuleScreenReader(LocateSession locate, ReadRows readRows)
    {
        _locate = locate ?? throw new ArgumentNullException(nameof(locate));
        _readRows = readRows ?? throw new ArgumentNullException(nameof(readRows));
    }

    /// <summary>
    /// The session's screen, read in the caller's tenant, with the agent and machine off the roster row -
    /// or a stated refusal. Nothing here is taken from a request.
    /// </summary>
    public async Task<RuleScreenResult> ReadAsync(TenantId tenant, string sessionId, CancellationToken ct)
    {
        var sid = (sessionId ?? "").Trim();
        var located = _locate(tenant, sid);
        if (located is null)
        {
            FileLog.Write($"[GatewayRuleScreenReader] session {sid} is not on this tenant's roster");
            return RuleScreenResult.Refused(
                $"session {sid} is not on this account's roster, so its screen cannot be read and no " +
                "rule can be written against it.");
        }

        var (directorId, session) = located.Value;
        var rows = await _readRows(tenant, directorId, sid, ct).ConfigureAwait(false);
        if (rows is null)
        {
            // THE DIRECTOR IS GONE, OR NEVER ANSWERED. Not an empty screen: a failure, said as one.
            FileLog.Write($"[GatewayRuleScreenReader] session {sid} on director {directorId} could not be read");
            return RuleScreenResult.Refused(
                $"the screen of session {sid} could not be read - the machine running it may not be " +
                "connected - and unreadable is not evidence.");
        }

        return RuleScreenResult.Read(new RuleScreenReading(
            sid,
            new RuleSessionOrigin(session.Agent ?? "", session.MachineName ?? ""),
            string.Join("\n", rows.Select(r => (r ?? "").TrimEnd()))));
    }
}
