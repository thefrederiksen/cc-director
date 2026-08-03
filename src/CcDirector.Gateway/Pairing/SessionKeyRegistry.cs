using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Pairing;

/// <summary>
/// The authoritative registry of per-SESSION Gateway credentials (Remove-the-network-port mission,
/// phase 1b).
///
/// WHY THIS EXISTS. Until now the Gateway had three credential shapes - a shared machine token, a
/// browser cookie, and a per-device key from enrollment - and not one of them is bound to a session or
/// limited in what it may call. The mission moves every agent command off the Director's local network
/// port and onto the Gateway, and doing that with the credentials that exist today would mean handing
/// every agent process the Director's own Gateway key: authority over the whole account, on every
/// machine. That is a strictly larger hole than the port being removed. This registry is the credential
/// that makes the move possible - one key per session, belonging to one tenant, allowed to call the
/// agent routes and nothing else, and ended when the session is.
///
/// WHAT IT STORES. The session id, the owning tenant, the Director that owns the session, a one-way
/// SHA-256 HASH of the key, and an expiry. NEVER the key: the Director hashes it on the machine that
/// minted it and registers only the hash, so nothing here can be replayed as a session.
///
/// WHERE THE TENANT COMES FROM. The caller passes the tenant the registering Director's tunnel bound to
/// at Hello, which was resolved from that Director's authenticated device key. It is never read from a
/// registration payload. A tenant a client can name is a tenant a client can choose.
///
/// It mirrors <see cref="DeviceRegistry"/> deliberately - same database, same hash format, same typed
/// resolution that distinguishes "unknown", "revoked" and "the registry could not be read". A database
/// failure is a typed unavailable result, never an unknown-key result and NEVER a grant.
/// </summary>
public sealed class SessionKeyRegistry
{
    /// <summary>The reason stamped on a key revoked because its session was reaped.</summary>
    public const string ReasonSessionReaped = "session_reaped";

    /// <summary>The reason stamped on a key revoked by the expiry sweep.</summary>
    public const string ReasonExpired = "expired";

    private readonly GatewayDatabase _db;
    private readonly Func<DateTime> _clock;

    /// <param name="db">The host-owned database shared by every Gateway replica.</param>
    /// <param name="clock">UTC clock seam for tests; production omits it.</param>
    public SessionKeyRegistry(GatewayDatabase db, Func<DateTime>? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Register (or rotate) one session's key. The row is keyed by session id, so a Director that
    /// re-registers a live session on a tunnel reseed replaces its own row and extends its expiry rather
    /// than adding a second credential.
    ///
    /// A REVOKED session is NOT resurrected. Revocation is deliberate and final - the session was reaped -
    /// and a reseed that raced the revocation must not undo it. The attempt is refused and logged, never
    /// silently applied.
    /// </summary>
    /// <returns>True when the row was written; false when the session is revoked or the write was refused.</returns>
    public bool Register(TenantId tenant, string directorId, string sessionId, string keyHash, DateTime expiresAtUtc)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(keyHash))
            throw new ArgumentException("keyHash is required", nameof(keyHash));

        var id = sessionId.Trim();
        var hash = keyHash.Trim().ToLowerInvariant();

        try
        {
            using var ctx = _db.CreateUnscopedContext();
            using var transaction = ctx.Database.BeginTransaction(IsolationLevel.Serializable);
            var row = ctx.SessionKeys.SingleOrDefault(s => s.SessionId == id);

            if (row is not null && row.RevokedAtUtc is not null)
            {
                FileLog.Write($"[SessionKeyRegistry] Register REFUSED: session={id} was revoked ({row.RevokedReason}) and is not revived by a re-registration");
                return false;
            }

            // A hash already owned by ANOTHER session is refused rather than stolen. The unique index would
            // refuse it anyway; catching it here names the reason in the log instead of surfacing a bare
            // database constraint violation.
            if (ctx.SessionKeys.AsNoTracking().Any(s => s.KeyHash == hash && s.SessionId != id))
            {
                FileLog.Write($"[SessionKeyRegistry] Register REFUSED: the presented key hash is already registered to another session (session={id})");
                return false;
            }

            if (row is null)
            {
                row = new SessionKeyEntity { SessionId = id };
                ctx.SessionKeys.Add(row);
            }

            row.TenantId = tenant.Value;
            row.DirectorId = directorId?.Trim() ?? "";
            row.KeyHash = hash;
            row.IssuedAtUtc = _clock();
            row.ExpiresAtUtc = expiresAtUtc;
            row.RevokedAtUtc = null;
            row.RevokedReason = null;

            ctx.SaveChanges();
            transaction.Commit();

            FileLog.Write($"[SessionKeyRegistry] Register: session={id}, director={row.DirectorId}, expires={expiresAtUtc:O} (key value never received)");
            return true;
        }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        {
            FileLog.Write($"[SessionKeyRegistry] Register FAILED: session={id}, {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resolve a presented key into a typed session identity. No raw key and no stored hash is returned.
    ///
    /// The verdict is deliberately four-valued, exactly as <see cref="DeviceRegistry.ResolveCredential"/>:
    /// "I do not know this key", "I know it and it is over" (revoked or lapsed), "I could not read the
    /// registry", and "here is who it is". Collapsing the middle two into "unknown" would answer an
    /// expired credential with the same words as a guessed one, and collapsing the third into any of them
    /// would turn a database outage into a silent security decision.
    /// </summary>
    public SessionCredentialResolution ResolveCredential(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return SessionCredentialResolution.Unknown;

        try
        {
            var suppliedBytes = GatewaySessionKey.HashBytes(key);
            var suppliedHash = Convert.ToHexString(suppliedBytes).ToLowerInvariant();

            using var ctx = _db.CreateUnscopedContext();
            var matches = ctx.SessionKeys
                .AsNoTracking()
                .Where(s => s.KeyHash == suppliedHash)
                .Take(2)
                .ToList();

            if (matches.Count == 0)
                return SessionCredentialResolution.Unknown;

            if (matches.Count != 1)
            {
                // The unique index makes this unreachable through the registry's own writes. If it is ever
                // reached the table has been edited underneath us, and "which of these two sessions is the
                // caller" has no correct answer - so there is no answer, not a guess.
                FileLog.Write("[SessionKeyRegistry] ResolveCredential FAILED: duplicate session key hashes in the registry");
                return SessionCredentialResolution.Unavailable;
            }

            var row = matches[0];
            var storedHash = DecodeHash(row.KeyHash);
            if (storedHash is null
                || storedHash.Length != suppliedBytes.Length
                || !CryptographicOperations.FixedTimeEquals(storedHash, suppliedBytes))
            {
                FileLog.Write("[SessionKeyRegistry] ResolveCredential FAILED: malformed session key hash in the registry");
                return SessionCredentialResolution.Unavailable;
            }

            if (!Guid.TryParse(row.SessionId, out var sessionId))
            {
                FileLog.Write($"[SessionKeyRegistry] ResolveCredential FAILED: the matched row's session id is not a GUID");
                return SessionCredentialResolution.Unavailable;
            }

            var tenant = new TenantId(row.TenantId);
            if (!tenant.IsValid || tenant.IsSystem)
            {
                // A key with no usable tenant is not a caller we can scope, so it is not a caller. Deny by
                // default rather than resolving it to anything.
                FileLog.Write($"[SessionKeyRegistry] ResolveCredential: session={row.SessionId} has no usable tenant binding - denying");
                return new SessionCredentialResolution(SessionCredentialResolutionKind.Revoked, null);
            }

            var identity = new SessionCredentialIdentity(sessionId, tenant, row.DirectorId);

            if (row.RevokedAtUtc is not null || row.ExpiresAtUtc <= _clock())
                return new SessionCredentialResolution(SessionCredentialResolutionKind.Revoked, identity);

            return new SessionCredentialResolution(SessionCredentialResolutionKind.Active, identity);
        }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        {
            FileLog.Write($"[SessionKeyRegistry] ResolveCredential FAILED: registry unavailable ({ex.GetType().Name})");
            return SessionCredentialResolution.Unavailable;
        }
    }

    /// <summary>
    /// End one session's key. Called when the session is reaped. The row is kept as a tombstone rather than
    /// deleted, so a re-registration that races the reap cannot revive the credential.
    /// </summary>
    /// <returns>True when a live row was revoked; false when there was none (already revoked, or never registered).</returns>
    public bool Revoke(TenantId tenant, string sessionId, string reason)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required", nameof(reason));

        var id = sessionId.Trim();
        var when = _clock();
        try
        {
            using var ctx = _db.CreateUnscopedContext();
            // Scoped to the TENANT as well as the session id: a Director may only end its own account's
            // session keys, so a session id learned or guessed across the account boundary revokes nothing.
            var changed = ctx.SessionKeys
                .Where(s => s.SessionId == id && s.TenantId == tenant.Value && s.RevokedAtUtc == null)
                .ExecuteUpdate(setters => setters
                    .SetProperty(s => s.RevokedAtUtc, when)
                    .SetProperty(s => s.RevokedReason, reason.Trim()));
            FileLog.Write($"[SessionKeyRegistry] Revoke: session={id}, reason={reason.Trim()}, found={changed == 1}");
            return changed == 1;
        }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        {
            FileLog.Write($"[SessionKeyRegistry] Revoke FAILED: session={id}, {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Tombstone every key whose expiry has passed. The expiry is already enforced on every resolution, so
    /// this changes no authentication answer - it is housekeeping, so the table does not accumulate rows
    /// that read as live and an operator listing it sees the truth.
    /// </summary>
    /// <returns>How many rows were tombstoned.</returns>
    public int SweepExpired()
    {
        var now = _clock();
        try
        {
            using var ctx = _db.CreateUnscopedContext();
            var changed = ctx.SessionKeys
                .Where(s => s.RevokedAtUtc == null && s.ExpiresAtUtc <= now)
                .ExecuteUpdate(setters => setters
                    .SetProperty(s => s.RevokedAtUtc, now)
                    .SetProperty(s => s.RevokedReason, ReasonExpired));
            if (changed > 0)
                FileLog.Write($"[SessionKeyRegistry] SweepExpired: tombstoned={changed}");
            return changed;
        }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        {
            FileLog.Write($"[SessionKeyRegistry] SweepExpired FAILED: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>How many rows the registry holds. Diagnostics and tests.</summary>
    public int Count
    {
        get
        {
            using var ctx = _db.CreateUnscopedContext();
            return ctx.SessionKeys.Count();
        }
    }

    private static byte[]? DecodeHash(string hex)
    {
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsDatabaseFailure(Exception ex)
        => ex is DbException or DbUpdateException or InvalidOperationException or ObjectDisposedException;
}

/// <summary>What one session-key lookup turned out to be.</summary>
public enum SessionCredentialResolutionKind
{
    /// <summary>No row holds this key's hash.</summary>
    Unknown,

    /// <summary>A live, unexpired session key.</summary>
    Active,

    /// <summary>A key we know and no longer accept - the session was reaped, or the expiry passed.</summary>
    Revoked,

    /// <summary>The registry could not be read. NOT a grant and NOT an unknown key.</summary>
    Unavailable,
}

/// <summary>An authenticated session identity: which session is calling, and whose account it belongs to.
/// Carries no raw key and no stored hash.</summary>
public sealed record SessionCredentialIdentity(Guid SessionId, TenantId Tenant, string DirectorId);

/// <summary>The typed result of one session-key lookup.</summary>
public readonly record struct SessionCredentialResolution(
    SessionCredentialResolutionKind Kind,
    SessionCredentialIdentity? Identity)
{
    public static SessionCredentialResolution Unknown { get; } =
        new(SessionCredentialResolutionKind.Unknown, null);

    public static SessionCredentialResolution Unavailable { get; } =
        new(SessionCredentialResolutionKind.Unavailable, null);
}
