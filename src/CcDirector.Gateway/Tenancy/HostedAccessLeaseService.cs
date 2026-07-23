using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Tenancy;

/// <summary>What a hosted-access check concluded for one request or stream.</summary>
public enum HostedAccessDecision
{
    /// <summary>Entitled (a live lease, or a fresh Entitled read). Serve the request.</summary>
    Allow,

    /// <summary>The read SUCCEEDED and the tenant is not entitled (cancelled / unpaid / period elapsed). A
    /// terminal deny - the caller returns 402 and the tenant's devices are being revoked. Never retry into a
    /// grant.</summary>
    DenyNotEntitled,

    /// <summary>The read FAILED (database unreadable) and no unexpired lease covers the caller. A TEMPORARY
    /// deny - the caller returns 503 + Retry-After. NOTHING is tombstoned: a failed read is not proof of
    /// cancellation.</summary>
    RetryUnknown,
}

/// <summary>The revocation the lease service and the sweep trigger when a tenant reads NotEntitled. Kept as an
/// interface so the lease logic is testable without the full teardown, and so the durable device tombstone
/// (the load-bearing step) can be exercised in isolation.</summary>
public interface ITenantAccessRevoker
{
    Task RevokeAsync(TenantId tenant, string reason, CancellationToken ct = default);
}

/// <summary>
/// The per-tenant positive-lease cache that turns "is this tenant still entitled?" into an O(1) in-memory
/// check on the hot path, and is the moment the cancellation cutoff (MTR-15) enforces on every hosted request.
///
/// Keyed by <see cref="TenantId"/> (NOT by device or subject) so all of a tenant's devices, requests, and
/// streams share ONE lease - a busy tenant causes one entitlement read per <see cref="_leaseTtl"/>, not one
/// per request. The database is always the authority; the lease is only an optimization and is always
/// subordinate to the durable device-credential status (a revoked device is denied regardless of any lease).
///
/// The three outcomes are kept strictly apart (see <see cref="EntitlementOutcome"/>): a successful
/// NotEntitled read revokes; a FAILED read (Unknown) never revokes and never shortens an existing paid lease.
///
/// The clip is the load-bearing line: a positive lease expires at <c>min(now + ttl, current_period_end)</c>,
/// so caching can never extend access one moment past the paid boundary.
/// </summary>
public sealed class HostedAccessLeaseService
{
    private readonly record struct Lease(EntitlementOutcome Outcome, DateTimeOffset ExpiresAtUtc);

    private readonly EntitlementRegistry _entitlements;
    private readonly TenantRegistry _tenants;
    private readonly ITenantAccessRevoker _revoker;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _leaseTtl;

    private readonly ConcurrentDictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    // Per-tenant single-flight: coalesce concurrent refreshes so an expiry does not stampede the database with
    // one read per in-flight request. One gate per tenant; created on demand, never removed (bounded by the
    // tenant census).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates = new(StringComparer.Ordinal);

    public HostedAccessLeaseService(
        EntitlementRegistry entitlements,
        TenantRegistry tenants,
        ITenantAccessRevoker revoker,
        TimeProvider? clock = null,
        TimeSpan? leaseTtl = null)
    {
        _entitlements = entitlements ?? throw new ArgumentNullException(nameof(entitlements));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _revoker = revoker ?? throw new ArgumentNullException(nameof(revoker));
        _clock = clock ?? TimeProvider.System;
        _leaseTtl = leaseTtl ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Authorize a tenant for hosted access. Serves from a live positive lease with no database touch; on a
    /// miss (no lease, or an expired one) it single-flights one entitlement read and applies the three-way
    /// rule. On a successful NotEntitled it triggers revocation before denying. On a failed read it denies
    /// only if no unexpired lease covers the caller, and never tombstones.
    /// </summary>
    public async Task<HostedAccessDecision> AuthorizeAsync(TenantId tenant, CancellationToken ct = default)
    {
        if (!tenant.IsValid)
            return HostedAccessDecision.DenyNotEntitled;

        var key = tenant.Value;
        var now = _clock.GetUtcNow();

        // Fast path: a live positive lease, no database.
        if (_leases.TryGetValue(key, out var cached)
            && cached.Outcome == EntitlementOutcome.Entitled
            && now < cached.ExpiresAtUtc)
            return HostedAccessDecision.Allow;

        var gate = _refreshGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate: a concurrent refresh may have just filled a fresh lease.
            now = _clock.GetUtcNow();
            if (_leases.TryGetValue(key, out cached)
                && cached.Outcome == EntitlementOutcome.Entitled
                && now < cached.ExpiresAtUtc)
                return HostedAccessDecision.Allow;

            // The lease is tenant-keyed but the reader reads by subject: bridge tenant -> subject first. A
            // tenant with no subject mapping is not a failed read - it is "no such entitled tenant" -> deny.
            var subject = _tenants.SubjectForTenant(tenant);
            if (string.IsNullOrWhiteSpace(subject))
                return HostedAccessDecision.DenyNotEntitled;

            var decision = _entitlements.Evaluate(subject, now.UtcDateTime);
            switch (decision.Outcome)
            {
                case EntitlementOutcome.Entitled:
                {
                    // The clip: never past the paid boundary. A null period end means the row recorded none
                    // (an active row need not), so fall back to the plain ttl.
                    var ttlExpiry = now + _leaseTtl;
                    var expiry = decision.CurrentPeriodEnd is { } end && new DateTimeOffset(end, TimeSpan.Zero) < ttlExpiry
                        ? new DateTimeOffset(end, TimeSpan.Zero)
                        : ttlExpiry;
                    _leases[key] = new Lease(EntitlementOutcome.Entitled, expiry);
                    return HostedAccessDecision.Allow;
                }

                case EntitlementOutcome.NotEntitled:
                {
                    // A successful non-granting read. Mark the lease terminal-negative and revoke the tenant's
                    // durable device credentials (the tiebreaker that denies even a racing stale lease), then
                    // deny. Revocation is idempotent.
                    _leases[key] = new Lease(EntitlementOutcome.NotEntitled, now);
                    await _revoker.RevokeAsync(tenant, TenantAccessRevokeReasons.EntitlementLost, ct).ConfigureAwait(false);
                    return HostedAccessDecision.DenyNotEntitled;
                }

                default: // Unknown - a FAILED read. Ignorance is never a verdict.
                {
                    // If a positive lease is still unexpired, honour the remainder (ignorance never shortens a
                    // paid lease). Otherwise deny temporarily (503) and DO NOT tombstone - a failed read is not
                    // proof of cancellation.
                    if (_leases.TryGetValue(key, out var existing)
                        && existing.Outcome == EntitlementOutcome.Entitled
                        && now < existing.ExpiresAtUtc)
                        return HostedAccessDecision.Allow;
                    return HostedAccessDecision.RetryUnknown;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Drop any cached lease for a tenant (used by the revoker so a torn-down tenant cannot ride a
    /// stale positive lease). Idempotent.</summary>
    public void InvalidateLease(TenantId tenant)
    {
        if (tenant.IsValid)
            _leases.TryRemove(tenant.Value, out _);
    }

    /// <summary>The tenants that currently hold an unexpired positive lease - part of the active-tenant set the
    /// sweep re-reads (a tenant that made a hosted request in the last ttl is swept even with no live stream).</summary>
    public IReadOnlyList<TenantId> TenantsWithLiveLease()
    {
        var now = _clock.GetUtcNow();
        var result = new List<TenantId>();
        foreach (var kv in _leases)
            if (kv.Value.Outcome == EntitlementOutcome.Entitled && now < kv.Value.ExpiresAtUtc)
                result.Add(new TenantId(kv.Key));
        return result;
    }
}

/// <summary>The fixed reason strings a revocation records - a closed enum of causes, never PII.</summary>
public static class TenantAccessRevokeReasons
{
    public const string EntitlementLost = "entitlement_lost";
}
