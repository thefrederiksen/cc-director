using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CcDirector.Core.Tenancy;

/// <summary>
/// THE key-accepting collection for retained state addressed by a session identifier: a drop-in
/// replacement for the <c>ConcurrentDictionary&lt;string, TValue&gt;</c> that the fourteen unsafe
/// session-keyed Gateway collections declare today, which accepts ONLY a <see cref="TenantSessionKey"/>.
///
/// WHY A TYPE AND NOT A CONVENTION. A prefixing convention is bypassable one call site at a time, and the
/// bypass is invisible: a writer that forgets the prefix compiles, runs, and silently shares a partition
/// with another tenant. Here there is no un-namespaced key to express - every member takes the typed key,
/// there is no string overload and no implicit conversion, so a call site that has not resolved a tenant
/// does not compile. That is the difference between a partition that is enforced and one that is merely
/// intended.
///
/// THE PARTITION IS PHYSICAL. Entries live in a per-tenant inner dictionary, exactly as
/// <c>PushedSessionStore._byTenant</c> does (census row 16, clean). A lookup routes by
/// <see cref="TenantSessionKey.Tenant"/> before the session identifier is ever compared, so one tenant's
/// raw session identifier cannot reach another tenant's entry even if the two strings are identical. It
/// also gives the per-tenant enumeration that the expiry passes, roster folds and statistics reads need,
/// which a single flat namespaced dictionary could only do by scanning every tenant's keys.
///
/// DENY BY DEFAULT. An invalid key - a <c>default(TenantSessionKey)</c>, which is the only way to hold one
/// without having resolved a tenant - throws on EVERY member, read and write alike. It is never quietly
/// treated as a miss, because a quiet miss is how an unpartitioned access survives review.
///
/// CONCURRENCY. Individual operations are thread-safe. <see cref="RemoveAllFor"/> is a tenant TEARDOWN
/// operation: a write racing a teardown of its OWN tenant may land in the discarded partition and be lost.
/// It can never land in another tenant's partition, which is the property under proof here.
/// </summary>
/// <typeparam name="TValue">The stored value - a timestamp, a marker, a counter, a cached brief.</typeparam>
public sealed class TenantSessionMap<TValue>
{
    private readonly ConcurrentDictionary<TenantId, ConcurrentDictionary<string, TValue>> _byTenant = new();

    /// <summary>Store or replace this session's value.</summary>
    public void Set(TenantSessionKey key, TValue value) => Writable(Require(key))[key.SessionId] = value;

    /// <summary>Store this session's value only if it has none yet. True when this call stored it.</summary>
    public bool TryAdd(TenantSessionKey key, TValue value) => Writable(Require(key)).TryAdd(key.SessionId, value);

    /// <summary>Read this session's value.</summary>
    public bool TryGetValue(TenantSessionKey key, out TValue value)
    {
        if (Readable(Require(key)) is { } partition)
            return partition.TryGetValue(key.SessionId, out value!);
        value = default!;
        return false;
    }

    /// <summary>Read this session's value, or the default when it has none.</summary>
    public TValue? GetValueOrDefault(TenantSessionKey key) =>
        TryGetValue(key, out var value) ? value : default;

    /// <summary>Whether this session has a value.</summary>
    public bool ContainsKey(TenantSessionKey key) =>
        Readable(Require(key)) is { } partition && partition.ContainsKey(key.SessionId);

    /// <summary>
    /// Read this session's value, creating it from <paramref name="factory"/> when absent.
    /// <paramref name="added"/> reports whether THIS call created it - the entry/hold distinction the
    /// needs-you clock and the statistics seed markers both turn on.
    /// </summary>
    public TValue GetOrAdd(TenantSessionKey key, Func<TenantSessionKey, TValue> factory, out bool added)
    {
        if (factory is null) throw new ArgumentNullException(nameof(factory));
        var created = false;
        var value = Writable(Require(key)).GetOrAdd(key.SessionId, _ =>
        {
            created = true;
            return factory(key);
        });
        added = created;
        return value;
    }

    /// <summary>
    /// Replace this session's value ONLY if it already has one. False when the session is absent. This is
    /// the "keep a live mark alive, never resurrect a cleared one" operation - an unconditional write in
    /// its place lets a progress update race a clear and re-create what the clear just removed.
    /// </summary>
    public bool TryUpdateExisting(TenantSessionKey key, TValue value)
    {
        if (Readable(Require(key)) is not { } partition) return false;
        if (!partition.ContainsKey(key.SessionId)) return false;
        partition[key.SessionId] = value;
        return true;
    }

    /// <summary>Remove this session's value, yielding what was removed.</summary>
    public bool TryRemove(TenantSessionKey key, out TValue value)
    {
        if (Readable(Require(key)) is { } partition)
            return partition.TryRemove(key.SessionId, out value!);
        value = default!;
        return false;
    }

    /// <summary>Remove this session's value.</summary>
    public bool Remove(TenantSessionKey key) => TryRemove(key, out _);

    /// <summary>How many sessions this tenant holds. The per-tenant count a concurrency or statistics read
    /// must report - never a process-wide total.</summary>
    public int CountFor(TenantId tenant) =>
        Readable(RequireTenant(tenant)) is { } partition ? partition.Count : 0;

    /// <summary>This tenant's keys, as a point-in-time copy safe to enumerate while others write. The input
    /// to an expiry pass, a roster fold, or a per-tenant statistics read.</summary>
    public IReadOnlyList<TenantSessionKey> KeysFor(TenantId tenant)
    {
        var t = RequireTenant(tenant);
        if (Readable(t) is not { } partition) return Array.Empty<TenantSessionKey>();
        return partition.Keys.Select(sid => TenantSessionKey.For(t, sid)).ToList();
    }

    /// <summary>This tenant's entries, as a point-in-time copy safe to enumerate while others write.</summary>
    public IReadOnlyList<KeyValuePair<TenantSessionKey, TValue>> SnapshotFor(TenantId tenant)
    {
        var t = RequireTenant(tenant);
        if (Readable(t) is not { } partition) return Array.Empty<KeyValuePair<TenantSessionKey, TValue>>();
        return partition
            .Select(pair => new KeyValuePair<TenantSessionKey, TValue>(TenantSessionKey.For(t, pair.Key), pair.Value))
            .ToList();
    }

    /// <summary>Drop this tenant's whole partition, returning how many entries went. Tenant teardown only -
    /// no ordinary path removes another tenant's state.</summary>
    public int RemoveAllFor(TenantId tenant) =>
        _byTenant.TryRemove(RequireTenant(tenant), out var partition) ? partition.Count : 0;

    /// <summary>How many tenants currently hold state. Diagnostics only - it is not an answer to a
    /// client, and it must never surface a tenant identity.</summary>
    public int TenantCount => _byTenant.Count;

    private static TenantSessionKey Require(TenantSessionKey key) =>
        key.IsValid ? key : throw new ArgumentException(
            "This key was never derived from a resolved tenant, so it addresses no partition. Derive it with " +
            "TenantSessionKey.For from the tenant bound to the connection or the request.", nameof(key));

    private static TenantId RequireTenant(TenantId tenant) =>
        tenant.IsValid ? tenant : throw new ArgumentException(
            "An unresolved tenant addresses no partition; it is denied, not defaulted.", nameof(tenant));

    private ConcurrentDictionary<string, TValue>? Readable(TenantSessionKey key) =>
        _byTenant.TryGetValue(key.Tenant, out var partition) ? partition : null;

    private ConcurrentDictionary<string, TValue>? Readable(TenantId tenant) =>
        _byTenant.TryGetValue(tenant, out var partition) ? partition : null;

    private ConcurrentDictionary<string, TValue> Writable(TenantSessionKey key) =>
        _byTenant.GetOrAdd(key.Tenant, _ => new ConcurrentDictionary<string, TValue>(StringComparer.Ordinal));
}
