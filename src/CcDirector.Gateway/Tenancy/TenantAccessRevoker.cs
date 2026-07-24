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
    private readonly Streaming.DirectorConnectionRegistry? _connections;

    public TenantAccessRevoker(Pairing.DeviceRegistry devices, Streaming.DirectorConnectionRegistry? connections = null)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _connections = connections;
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

        // Step 3 (live teardown): server-side abort of every live Director tunnel this instance holds for the
        // tenant, so an already-connected session actually stops rather than lingering until it next
        // re-authenticates. Per-instance and idempotent; browser terminal/file/screenshot streams ride over
        // the aborted tunnel and end with it. Ordered AFTER the durable tombstone so a racing reconnect loses
        // (it re-authenticates against the committed revoked status and is denied).
        _connections?.AbortForTenant(tenant, reason);

        return Task.CompletedTask;
    }
}
