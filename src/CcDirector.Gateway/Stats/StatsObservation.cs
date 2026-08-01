using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The failure-domain boundary around a statistics observation MADE FROM A HOT PATH.
///
/// WHAT THIS IS FOR. The roster read (<c>GET /sessions</c>) and the tunnel push (the Director hub) both fold
/// their assembled roster into statistics, inline, on the request thread. A statistics write that throws
/// there - a lock timeout, a lost database connection, a full disk - propagates out of the route and the
/// caller gets HTTP 500 on the two paths every user depends on, for a background concern neither of them
/// needs. The startup boundary already contains store selection, opening and migration; nothing contained
/// what happens AFTER startup, which is the state a failure review found and named. This closes it.
///
/// CONTAIN AND SHOUT. NEVER CONTAIN AND SWALLOW. A silent <c>catch</c> is forbidden here and would be worse
/// than the fault it hides: the roster would keep answering 200 while statistics quietly recorded nothing,
/// which is precisely the shape of the 2026-07-30 incident that took thirty-two minutes to see. So every
/// contained failure does three things and they are not optional - it writes a loud line to the file log
/// naming the observer, the call site and the exception's own type and message; it increments that
/// observer's failure count; and it stores the message as that observer's last error, where the health
/// surface reads it. A reader of the log, or of the health numbers, can see that statistics are failing.
///
/// WHY THE CATCH IS HERE AND NOT INSIDE THE OBSERVERS. Containment belongs to the CALLER, because it is the
/// caller's fate that is being protected. An aggregator that swallowed its own exceptions would be contained
/// for every caller including the ones that want to know - the write-path tests assert that a broken store
/// throws, and they should. Keeping the catch at the hot-path call site means the hot path is protected and
/// nothing else changes its behaviour.
///
/// WHAT IS NOT RECORDED HERE, stated so nobody reads more into the numbers than they carry. This records
/// FAILURES only. It does not stamp <see cref="IStatsFailureState.LastSuccessfulWrite"/>, because an idle
/// poll folds nothing and stores nothing - stamping every observation that did not throw would turn "when
/// this observer last actually stored something" into "when it was last called", which is a different fact
/// and the useless one. Nor does it count drops: a drop is an observation deliberately not attempted, and
/// the null-conditional call at each site is what does that, before reaching here.
/// </summary>
public static class StatsObservation
{
    /// <summary>
    /// Run <paramref name="observation"/>, and contain any failure it raises so the hot path survives it.
    /// </summary>
    /// <param name="health">The counters belonging to the observer being called - see the class remarks for
    /// what is recorded on them and what deliberately is not.</param>
    /// <param name="callSite">Where this observation is made from, in words an operator reading the log can
    /// place: "GET /sessions roster fold", "DirectorHub.PushDelta". It names the HOT PATH that was protected,
    /// which is the thing the log line exists to say.</param>
    /// <param name="observation">The observation itself.</param>
    public static void Contain(StatsFailureCounters health, string callSite, Action observation)
    {
        try
        {
            observation();
        }
        catch (Exception ex)
        {
            // Reduced to type and message deliberately: this text is stored as the observer's LastError and
            // that field is served, so it must never be able to carry a connection string or a credential.
            var error = $"{ex.GetType().Name}: {ex.Message}";
            health.RecordFailure(error);
            FileLog.Write(
                $"[StatsObservation] CONTAINED a statistics failure so the hot path could carry on: " +
                $"observer={health.Observer}, callSite={callSite}, failureCount={health.FailureCount}, error={error}");
        }
    }
}
