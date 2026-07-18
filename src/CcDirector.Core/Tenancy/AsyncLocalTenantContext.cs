using System;
using System.Threading;

namespace CcDirector.Core.Tenancy;

/// <summary>
/// The hosted, multi-tenant implementation of <see cref="ITenantContext"/> (Hosted Multi-Tenancy increment
/// 1). Unlike <see cref="SingleTenantContext"/> (which always answers <see cref="TenantId.Local"/>), this
/// resolves the tenant of the CURRENT unit of work from an ambient, async-flowed scope that a boundary
/// establishes with <see cref="Enter"/>.
///
/// DENY-BY-DEFAULT / FAIL-CLOSED: when no scope is in effect, <see cref="Current"/> THROWS - it never
/// defaults to a tenant. A hosted operation that touches tenant data outside an explicit scope is a bug (a
/// caller path that was not bound at its auth boundary), and failing closed turns that into a loud error
/// rather than a silent cross-tenant read or a write into the wrong tenant. The per-account boundaries enter
/// the resolved account tenant; the reserved <see cref="TenantId.System"/> scope is entered ONLY by
/// explicitly-system code (startup/built-in seeding) - it is never the answer to "no tenant resolved".
///
/// The ambient value is an <see cref="AsyncLocal{T}"/>, so it flows down an async call chain (a SignalR hub
/// method, an HTTP request, a background operation) to the store calls inside it, and nested scopes restore
/// the previous value on dispose. The value is per-instance (the field is not static), so tests are isolated.
/// </summary>
public sealed class AsyncLocalTenantContext : ITenantContext
{
    private readonly AsyncLocal<TenantId?> _current = new();

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No tenant scope is in effect (deny-by-default).</exception>
    public TenantId Current =>
        _current.Value is { IsValid: true } tenant
            ? tenant
            : throw new InvalidOperationException(
                "No tenant is in scope for this hosted operation. A tenant-scoped read or write ran outside " +
                "any resolved-tenant boundary. Hosted operations must run inside an explicit tenant scope - " +
                "the per-account tenant bound at the auth boundary, or the reserved system scope for system " +
                "operations. This fails closed rather than defaulting to a tenant (deny-by-default).");

    /// <summary>The tenant currently in scope, or null when none is - for a boundary that must DECIDE whether a
    /// scope is already active without triggering the fail-closed throw.</summary>
    public TenantId? CurrentOrNull => _current.Value is { IsValid: true } tenant ? tenant : (TenantId?)null;

    /// <summary>
    /// Enter a tenant scope for the current async flow. Every tenant-scoped store operation inside the
    /// returned scope resolves to <paramref name="tenant"/>. Dispose restores the previously-active scope (so
    /// scopes nest correctly). Fails loud on an invalid tenant - a boundary must resolve a valid tenant before
    /// entering, never enter <c>default</c>.
    /// </summary>
    public IDisposable Enter(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("Cannot enter a scope for an invalid tenant.", nameof(tenant));

        var previous = _current.Value;
        _current.Value = tenant;
        return new Scope(this, previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly AsyncLocalTenantContext _owner;
        private readonly TenantId? _previous;
        private bool _disposed;

        public Scope(AsyncLocalTenantContext owner, TenantId? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner._current.Value = _previous;
        }
    }
}
