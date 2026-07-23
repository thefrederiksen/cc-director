using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// Ends a tenant's hosted access when the entitlement read is a successful NotEntitled (cancelled / unpaid /
/// period elapsed). The order is load-bearing: the DURABLE device tombstone commits FIRST, so from that instant
/// - on every Gateway replica - the next credential resolution returns revoked for the tenant's keys,
/// independent of any cached lease. That durable status is the tiebreaker that denies even a request racing an
/// as-yet-unexpired positive lease. Live transport teardown (Director tunnels, browser streams) is layered on
/// top; it makes revocation prompt but is never what makes it CORRECT - the tombstone already is.
///
/// Idempotent: a second call re-tombstones nothing (the update matches only still-active rows) and is safe.
/// A resubscribe NEVER silently un-revokes a tombstoned key: reactivation requires a fresh enrollment that
/// mints a new credential row.
///
/// The credential row is TOMBSTONED, not deleted: the tombstone survives a Gateway restart, supports a reasoned
/// 402, and anchors the "explicit re-enrollment" policy. No PII is logged - only the count and the fixed reason.
/// </summary>
public sealed class TenantAccessRevoker : ITenantAccessRevoker
{
    private readonly Pairing.DeviceRegistry _devices;

    public TenantAccessRevoker(Pairing.DeviceRegistry devices)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    }

    public Task RevokeAsync(TenantId tenant, string reason, CancellationToken ct = default)
    {
        if (!tenant.IsValid)
            return Task.CompletedTask;

        // Step 1 (durable, load-bearing): tombstone every ACTIVE device credential for this tenant in one
        // transaction (MTR-14B's tenant-wide revoke). From the commit, ResolveCredential returns revoked for
        // those keys on every replica, so no new HTTP request and no new Director Hello can authenticate -
        // regardless of any cached lease. This is what actually cuts the tenant off.
        var revoked = _devices.RevokeTenant(tenant, reason);
        FileLog.Write($"[TenantAccessRevoker] tombstoned {revoked} active device credential(s) for a tenant (reason={reason})");

        // Steps 3-4 (live teardown): server-side abort of the tenant's Director connections and close of its
        // browser terminal / file / screenshot streams. These plug in here with the tenant-indexed
        // DirectorConnectionRegistry / GatewayStreamRegistry. Until they land, the durable tombstone above
        // denies every NEW authentication and the 60s sweep re-checks each cycle, so a live stream ends when it
        // next re-authenticates or the sweep tears it down - never past the published cutoff bound.

        return Task.CompletedTask;
    }
}
