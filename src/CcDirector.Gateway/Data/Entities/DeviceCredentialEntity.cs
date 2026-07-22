namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One enrolled device's per-device-key credential record, persisted in the <c>device_credentials</c> table
/// (MTR-14). This is the durable, per-row replacement for the single shared <c>devices.json</c> file the
/// <see cref="Pairing.DeviceRegistry"/> rewrites in place on every enrollment: on the hosted Gateway that one
/// file holds every tenant's device binding, so a full-file rewrite on each enrollment is non-atomic, guarded
/// only by a process-local lock, and unbounded in size - one tenant can corrupt or exhaust EVERY tenant's auth
/// state, and two replicas split-brain over the same file. A table makes each enrollment an insert/update of
/// its OWN row inside a database transaction, which is what removes that shared-blast-radius failure. Increment
/// MTR-14A adds this schema and a one-time importer; the runtime read/write cutover is MTR-14B.
///
/// It carries exactly what issue #1899 stores per device: the one-way SHA-256 hash of the key (never the key),
/// the NON-SECRET masked key identity (prefix / last-four), the machine name, issued-at, status, the mirror
/// attributes (platform / device type / cloud id), and the hosted account binding (account subject / tenant).
///
/// It is a GLOBAL table, deliberately NOT derived from <see cref="TenantScopedEntity"/>: like
/// <see cref="TenantEntity"/>, it carries no scoping query filter, because a presented key is resolved to its
/// device by the key hash BEFORE any tenant is known - the tenant is READ OFF the matched record. Scoping the
/// table to a tenant would make that resolution circular. The <see cref="TenantId"/> here is therefore a plain
/// data column (the device's account binding), not the ambient-scoping column of a tenant-scoped entity.
/// </summary>
public sealed class DeviceCredentialEntity
{
    /// <summary>The device's stable identity (its device GUID) - the natural primary key, globally unique, the
    /// same key the in-memory registry indexes by. Ordinally compared (SQLite BINARY / Postgres "C").</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>The device's machine name.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The lower-case hexadecimal SHA-256 of the device's key (issue #1878) - the ONLY form of the key
    /// ever persisted, verified against and never reversed. Indexed for the O(1) key-to-device lookup; ordinally
    /// compared so equality matches SQLite's BINARY collation exactly on both providers.</summary>
    public string DeviceKeyHash { get; set; } = "";

    /// <summary>The NON-SECRET first few characters of the key (issue #1899) - display metadata so a
    /// host-readable listing can tell devices apart by key without ever holding the raw key. Not a credential.</summary>
    public string KeyPrefix { get; set; } = "";

    /// <summary>The NON-SECRET last few characters of the key (issue #1899). Display metadata, not a credential.</summary>
    public string KeyLast4 { get; set; } = "";

    /// <summary>When the per-device key was issued (UTC).</summary>
    public DateTime IssuedAtUtc { get; set; }

    /// <summary>The device's status (e.g. <c>active</c>).</summary>
    public string Status { get; set; } = "";

    /// <summary>The child's self-reported platform (mirror attribute). "unknown" when not supplied.</summary>
    public string Platform { get; set; } = "";

    /// <summary>The child's self-reported device type (mirror attribute). "workstation" when not supplied.</summary>
    public string DeviceType { get; set; } = "";

    /// <summary>The cloud roster id assigned when this child was mirrored up, or null until mirrored.</summary>
    public string? CloudDeviceId { get; set; }

    /// <summary>The verified Supabase subject this device's account resolved to at hosted enrollment (Hosted
    /// Multi-Tenancy increment 1), or null on the single-tenant local install. Personally identifying - never
    /// logged.</summary>
    public string? AccountSubject { get; set; }

    /// <summary>The tenant id this device is bound to (its account tenant), or null on the single-tenant local
    /// install (which resolves to "local" without a per-device binding). A plain data column here, NOT the
    /// ambient-scoping tenant column of a tenant-scoped entity - this table is global. Never logged.</summary>
    public string? TenantId { get; set; }

    /// <summary>When this device credential was revoked (UTC), or null while it is still valid. Added in MTR-14A
    /// ahead of the MTR-15 tombstone work so revocation needs no second migration: a revoked row is retained as
    /// a tombstone rather than deleted, so a revoked key stays revoked instead of a same-id re-enrollment
    /// silently reviving it.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Why the device credential was revoked (free-text reason), or null while it is still valid. The
    /// tombstone companion to <see cref="RevokedAtUtc"/>, added in MTR-14A ahead of MTR-15.</summary>
    public string? RevokedReason { get; set; }
}
