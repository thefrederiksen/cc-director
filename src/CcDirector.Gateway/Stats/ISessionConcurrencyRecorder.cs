using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The fleet concurrency record: observe a roster, and render what has been observed.
///
/// WHY THIS INTERFACE EXISTS (issue #1174). There are two implementations and there always were - the
/// JSON-backed <see cref="GatewaySessionConcurrencyStats"/> that self-host writes beside its statistics
/// file, and the database-backed <see cref="GatewaySessionConcurrencyStore"/> that replaces it on the
/// pooled statistics context. They already had byte-identical signatures for these two members, and
/// <c>GatewaySessionConcurrencyParityTests</c> already asserts they render the same snapshot from the same
/// observations. What was missing was a way for the roster path and the statistics endpoint to hold EITHER
/// one: both were typed to the concrete JSON class, so the hosted Gateway - which must not write that file -
/// could only be handed a null, and its concurrency panel had nothing behind it however healthy the
/// database was.
///
/// So the interface is not an abstraction added in case a third implementation appears. It is the smallest
/// change that lets the deployment decide which of two EXISTING recorders is in use, instead of the type
/// system deciding there can only be one. The choice is made once, in the Gateway host's constructor.
///
/// It carries exactly the two members the production call sites use. Everything else on either class -
/// the JSON store's file handling, the database store's statement dialect - stays off it deliberately: a
/// member on here is a member both implementations owe forever.
/// </summary>
public interface ISessionConcurrencyRecorder
{
    /// <summary>This recorder's own health counters, which the hot-path call sites record a contained
    /// failure on - see <see cref="StatsObservation"/>. On the interface because the containment is written
    /// once for whichever recorder the deployment picked.</summary>
    StatsFailureCounters Health { get; }

    /// <summary>
    /// Fold one assembled roster into the record: current and peak concurrency, and the distinct
    /// sessions, machines and repositories seen in this UTC clock hour.
    /// </summary>
    /// <param name="roster">The tenant's live sessions, or null/empty for an observation of nothing.</param>
    /// <param name="nowUtc">The observation instant, supplied so a caller can be deterministic.</param>
    /// <param name="tenant">The owning tenant. Null is the self-host / unit-test default
    /// (<see cref="TenantId.Local"/>); an explicitly-passed invalid tenant is a DENY, never a default.</param>
    void Observe(IReadOnlyCollection<SessionDto>? roster, DateTime nowUtc, TenantId? tenant = null);

    /// <summary>What to render for <paramref name="tenant"/>: the current and all-time figures, and the
    /// hourly series. Reads only - it never advances the record.</summary>
    ConcurrencySnapshot Snapshot(DateTime nowUtc, TenantId? tenant = null);
}
