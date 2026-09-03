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
    /// <remarks>
    /// A SUPERVISED SESSION IS NEVER SWITCHED ON (owner's ruling, 2026-09-02). Voice mode reads a finished
    /// turn aloud to the owner, and a session that answers to another session, to a design seat, or to a
    /// schedule is not his to be read. Before this, "voice mode for all" meant literally all: every worker's
    /// turn was narrated at him even though the roster had already receded the row - the suppression was in
    /// the colour and nowhere else.
    ///
    /// THE ROLES ARE RESOLVED HERE, AND THAT IS NOT BELT-AND-BRACES - IT IS THE WHOLE CORRECTNESS OF THE
    /// CHECK. <c>PushedSessionStore</c> NULLS <see cref="SessionDto.SessionRole"/> at ingest, deliberately, so
    /// that only this Gateway can ever decide a role (see DiscardInboundRole). So the roster handed to this
    /// method arrives with EVERY role null, and a bare <c>IsSupervised</c> on it would answer "no" for every
    /// session on the fleet and narrate exactly as before - a check that passes because it is blind, which is
    /// the worst kind. Resolving first is what makes the answer real, and doing it INSIDE this method rather
    /// than asking the caller to do it first is what stops the next caller forgetting.
    ///
    /// THE UNIVERSE IS THE ROSTER THIS METHOD WAS GIVEN, AND IT IS NARROWER THAN THE FLEET. The caller passes
    /// the tenant's FRESH pushed sessions, so a supervisor whose stream has gone quiet past the freshness
    /// horizon is absent from the liveness set, its worker resolves Standalone rather than Worker, and that
    /// worker gets switched on. That is a REAL GAP and it is stated rather than hidden. It fails toward the
    /// behaviour that shipped before this change (narrate), which is the conservative direction for a sweep;
    /// resolving from the wider connected snapshot would need a second store read whose entries can disagree
    /// with the first between the two calls, and the resolver fails LOUD on a session missing from its
    /// universe - an exception inside a background sweep, to close a narrow window. Not worth it here.
    /// </remarks>
    /// <summary>
    /// The sessions to switch OUT of voice mode: the ones that ARE voice sessions and are SUPERVISED.
    ///
    /// WHY THERE IS AN OFF DIRECTION AT ALL. <see cref="Plan"/> stops a supervised session being switched ON,
    /// which fixes the fleet going forward and does nothing whatever for the sessions already marked. Voice
    /// marking is PERSISTED and survives a Gateway restart, so without this the workers enrolled before the
    /// rule existed would have gone on being narrated at the owner indefinitely - the change would have read
    /// as "no effect" on exactly the fleet that prompted it.
    ///
    /// DELIBERATELY NOT GATED ON <c>voiceModeOn</c>. The fleet switch answers "does this tenant want voice?";
    /// this answers "is this session the owner's to be read?", and a supervised session is not his to be read
    /// whether the fleet switch is on or off. Gating it would leave a tenant who has since turned voice mode
    /// off with a set of marked workers that nothing ever clears.
    ///
    /// NARROW ON PURPOSE, and this is the trade worth naming. The sweep's on-direction is documented as
    /// deliberately one-way so it never fights someone who put ONE session on voice by hand while the fleet
    /// flag was off. This does fight that person, in exactly one case: they put voice on a supervised session
    /// deliberately and this turns it back off. That is accepted rather than overlooked - the owner's ruling
    /// is that these sessions are not his to be read aloud, and being able to hand-mark one back into a state
    /// the rule forbids is not a capability worth preserving. It touches NOTHING else: a Manager or a
    /// Standalone marked for voice is never in this list.
    /// </summary>
    /// <param name="roster">The tenant's live sessions, as the push store reports them. Roles are resolved
    /// here, for the same reason they are in <see cref="Plan"/> - see the remark on that method.</param>
    /// <param name="isVoiceSession">Whether a session is currently marked as a voice session.</param>
    public static IReadOnlyList<(string DirectorId, string SessionId)> PlanOff(
        IReadOnlyList<(string DirectorId, SessionDto Session)> roster,
        Func<string, bool> isVoiceSession)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(isVoiceSession);

        Fleet.FleetRoleResolver.Stamp(roster.Select(r => r.Session).Where(x => x is not null).ToList());

        var plan = new List<(string DirectorId, string SessionId)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (directorId, session) in roster)
        {
            var sid = session?.SessionId;
            if (string.IsNullOrWhiteSpace(sid)) continue;
            if (string.IsNullOrWhiteSpace(directorId)) continue;
            if (!seen.Add(sid)) continue;
            if (!isVoiceSession(sid)) continue;                    // already off: nothing to do
            if (!SessionOrdering.IsSupervised(session!)) continue;  // the owner's own sessions are untouched
            plan.Add((directorId, sid));
        }
        return plan;
    }

    public static IReadOnlyList<(string DirectorId, string SessionId)> Plan(
        bool voiceModeOn,
        IReadOnlyList<(string DirectorId, SessionDto Session)> roster,
        Func<string, bool> isVoiceSession)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(isVoiceSession);

        if (!voiceModeOn) return Array.Empty<(string, string)>();

        // Resolve the roles across this roster before any of them is read - see the remark above for why a
        // check without this line would be blind rather than merely incomplete. Stamps in place; the store
        // hands out deep copies (PushedSessionStore.SnapshotFresh), so nothing cached is touched.
        Fleet.FleetRoleResolver.Stamp(roster.Select(r => r.Session).Where(x => x is not null).ToList());

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
            // Supervised: not the owner's to be read aloud. Asked through SessionOrdering so the sweep and
            // the roster fold answer this from ONE definition - a second copy here would drift from the dot
            // on the screen, and the two disagreeing is precisely how a receded row still spoke.
            if (SessionOrdering.IsSupervised(session!)) continue;
            plan.Add((directorId, sid));
        }
        return plan;
    }
}
