using System;
using System.Security.Cryptography;
using System.Text;

namespace CcDirector.Core.Tenancy;

/// <summary>
/// THE canonical internal key for any retained Gateway state that is addressed by a session identifier.
///
/// WHY THIS TYPE EXISTS. Fourteen retained Gateway collections are keyed by a bare session identifier that
/// a Director chose - the needs-you clock, both transcribing markers, the governance state emitter, the
/// turn-end watcher, five session-keyed statistics indexes, the current-session concurrency set, and both
/// turn-brief caches. On a hosted Gateway two accounts can present the SAME raw session identifier, so a
/// bare-identifier key lets one account read, overwrite, suppress, delete, or contend with the other's
/// entry. Keying by (tenant, session) instead makes naming another tenant's entry structurally impossible
/// rather than merely refused - the same shape this codebase already adopted for
/// <c>DirectorRegistry.DirectorKey</c> and for the composite tenant primary keys in the database.
///
/// WHERE THE TENANT COMES FROM. Always from authenticated request state or bound-connection state, and
/// NEVER from the payload: <c>DirectorHub.RequireBoundTenant()</c> for anything arriving on a Director
/// stream, and <c>GatewayEndpoints.ResolveReadTenant(ctx, boundary)</c> for anything arriving on an HTTP
/// request. This type cannot be built from a raw string, so a caller cannot conjure a key without first
/// holding a <see cref="TenantId"/> that one of those resolvers produced.
///
/// THE RAW IDENTIFIER SURVIVES. Only internal storage receives the namespaced key. Session identifiers
/// appear in route parameters, browser links, tunnel commands, deletion, dictation progress, governance
/// history and brief file paths, and every one of those keeps the raw value - which is why
/// <see cref="SessionId"/> is carried alongside and is what any external protocol must emit.
///
/// PRIVACY. The raw account tenant identifier is never rendered. <see cref="Value"/> carries a one-way
/// Secure Hash Algorithm 256 of the tenant, exactly as <c>HostedEnrollmentEndpoint.Enroll</c> namespaces a
/// caller device identifier before it reaches <c>DeviceRegistry</c>. The tenant hash is an INTERNAL
/// partition key only: it must never reach a user-facing surface, and <see cref="ToString"/> is the
/// log-safe rendering (short tag) rather than the storage key.
///
/// NOTE on <c>default(TenantSessionKey)</c>: like any struct, a default value bypasses construction and is
/// NOT a valid key. Check <see cref="IsValid"/> when a value's provenance is uncertain; the collections in
/// <see cref="TenantSessionMap{TValue}"/> reject it outright rather than storing under a null partition.
/// </summary>
public readonly record struct TenantSessionKey
{
    /// <summary>The domain tag placed between the tenant hash and the raw identifier, so a session key can
    /// never collide with a differently-scoped key (an upload key, a machine key) that happens to share a
    /// tenant and a string.</summary>
    public const string Domain = "session";

    /// <summary>The separator between the three parts, matching the enrollment template's
    /// <c>tenant hash | caller identifier</c> shape.</summary>
    public const char Separator = '|';

    private TenantSessionKey(TenantId tenant, string sessionId, string value)
    {
        Tenant = tenant;
        SessionId = sessionId;
        Value = value;
    }

    /// <summary>The owning tenant. Present so a partitioned store can route by tenant without re-parsing
    /// <see cref="Value"/>. Never render this on a user-facing surface.</summary>
    public TenantId Tenant { get; }

    /// <summary>The RAW session identifier, unchanged. This - never <see cref="Value"/> - is what goes into
    /// route parameters, links, tunnel commands, data-transfer objects, and file paths.</summary>
    public string SessionId { get; }

    /// <summary>
    /// The domain-tagged internal key, <c>tenant hash | session | raw identifier</c>, for stores whose key
    /// must be a single flat string. In-memory dictionaries should prefer the typed key itself (see
    /// <see cref="TenantSessionMap{TValue}"/>), which is partitioned and therefore cannot be addressed
    /// across tenants at all.
    ///
    /// This is a KEY, not a path segment. <see cref="For"/> rejects a session identifier containing a
    /// separator, a path separator, a relative-path element, or a control character, so a later use as a
    /// directory name cannot be made to escape its parent - but the intended use remains a dictionary key.
    /// </summary>
    public string Value { get; }

    /// <summary>True when this key came through <see cref="For"/> and carries a partition. A
    /// <c>default(TenantSessionKey)</c> is false and must never be stored under.</summary>
    public bool IsValid => Tenant.IsValid && !string.IsNullOrEmpty(Value);

    /// <summary>
    /// Derive the canonical key for one tenant's session. Fails loud on an invalid tenant or an unusable
    /// session identifier rather than carrying a half-scoped key forward - an unresolved tenant is denied,
    /// never defaulted.
    /// </summary>
    /// <param name="tenant">The tenant from authenticated request state or bound-connection state. Never
    /// from the payload.</param>
    /// <param name="sessionId">The raw session identifier as the Director or the route supplied it.</param>
    public static TenantSessionKey For(TenantId tenant, string sessionId)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A TenantSessionKey needs a valid tenant; an unresolved tenant is denied, not defaulted.", nameof(tenant));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("A TenantSessionKey needs a non-empty session identifier.", nameof(sessionId));

        var raw = sessionId.Trim();
        var rejected = FirstUnusableReason(raw);
        if (rejected is not null)
            throw new ArgumentException($"This session identifier cannot be namespaced: {rejected}.", nameof(sessionId));

        var value = string.Concat(NamespaceHash(tenant.Value), Separator, Domain, Separator, raw);
        return new TenantSessionKey(tenant, raw, value);
    }

    /// <summary>
    /// The non-throwing form, for the observation paths that today silently ignore an empty session
    /// identifier and must keep doing so. Returns false and yields an invalid key rather than throwing.
    /// </summary>
    public static bool TryFor(TenantId tenant, string? sessionId, out TenantSessionKey key)
    {
        key = default;
        if (!tenant.IsValid || string.IsNullOrWhiteSpace(sessionId))
            return false;

        var raw = sessionId.Trim();
        if (FirstUnusableReason(raw) is not null)
            return false;

        key = new TenantSessionKey(tenant, raw, string.Concat(NamespaceHash(tenant.Value), Separator, Domain, Separator, raw));
        return true;
    }

    /// <summary>
    /// A LOG-SAFE rendering. It reuses <see cref="TenantId.ToLogString"/>, so a real account tenant becomes
    /// a short one-way tag and neither the raw tenant identifier nor the full namespace hash reaches a log.
    /// Never write <see cref="Value"/> to a log, and never render either on a user-facing surface.
    /// </summary>
    public override string ToString() =>
        IsValid ? string.Concat(Tenant.ToLogString(), Separator, Domain, Separator, SessionId) : "<invalid-session-key>";

    /// <summary>
    /// Why this identifier cannot be namespaced, or null when it is usable. Rejecting rather than escaping
    /// keeps one canonical form: two identifiers can never be sanitized into the same key, and a key that is
    /// later used as a directory name cannot climb out of its parent.
    /// </summary>
    private static string? FirstUnusableReason(string raw)
    {
        if (raw is "." or "..")
            return "it is a relative-path element";
        foreach (var c in raw)
        {
            if (c == Separator) return "it contains the key separator";
            if (c is '/' or '\\') return "it contains a path separator";
            if (char.IsControl(c)) return "it contains a control character";
        }
        return null;
    }

    /// <summary>A one-way Secure Hash Algorithm 256 hex hash, so the raw tenant identifier never becomes
    /// part of a stored key. Identical in construction to the hash
    /// <c>HostedEnrollmentEndpoint.Enroll</c> uses to namespace a caller device identifier.</summary>
    private static string NamespaceHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
