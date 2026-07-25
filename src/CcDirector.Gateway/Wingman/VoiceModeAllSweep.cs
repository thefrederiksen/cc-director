using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The decision half of the voice-mode sweep: given a tenant that is IN VOICE MODE, which of its sessions are
/// not yet voice sessions and therefore still need switching on.
///
/// This exists because voice mode is a standing intent, not a one-time action. The fan-out at
/// <c>POST /sessions/voice-mode/all</c> reaches the sessions that exist at the moment it runs; a session
/// created a minute later, or one whose computer was offline and has since come back, was never told. Before
/// this, nothing ever told them - which is why sessions quietly failed to appear in the voice queue and looked
/// like a queue bug rather than what it was: they were never voice sessions.
///
/// Kept as a pure function, separate from the timer and the tunnel calls that act on it, so the rule can be
/// tested without a Gateway: the interesting behaviour is entirely in WHICH sessions come back, and the two
/// directions that must both hold - a tenant that is not in voice mode yields nothing at all, and a tenant
/// that is yields only the sessions that are actually still off.
/// </summary>
public static class VoiceModeAllSweep
{
    /// <summary>
    /// The sessions to switch into voice mode for this tenant, as (director id, session id) pairs.
    /// </summary>
    /// <param name="voiceModeOn">Whether this tenant is in voice mode. When false the result is always empty -
    /// the sweep NEVER switches a session on for a tenant that did not ask for voice mode.</param>
    /// <param name="roster">The tenant's live sessions, as the push store reports them: director id + session.</param>
    /// <param name="isVoiceSession">Whether a session is already marked as a voice session on the Gateway.
    /// Sessions that are already on are left alone, so a steady fleet produces an empty sweep and no traffic.</param>
    public static IReadOnlyList<(string DirectorId, string SessionId)> Plan(
        bool voiceModeOn,
        IReadOnlyList<(string DirectorId, SessionDto Session)> roster,
        Func<string, bool> isVoiceSession)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(isVoiceSession);

        if (!voiceModeOn) return Array.Empty<(string, string)>();

        var plan = new List<(string DirectorId, string SessionId)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (directorId, session) in roster)
        {
            var sid = session?.SessionId;
            if (string.IsNullOrWhiteSpace(sid)) continue;
            if (string.IsNullOrWhiteSpace(directorId)) continue;
            // A session belongs to exactly one Director, but a duplicated roster entry must not be switched
            // (and counted) twice - the same guard the fan-out endpoint applies.
            if (!seen.Add(sid)) continue;
            if (isVoiceSession(sid)) continue;
            plan.Add((directorId, sid));
        }
        return plan;
    }
}
