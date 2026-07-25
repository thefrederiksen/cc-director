using System;
using System.Collections.Generic;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// The BACKGROUND-LOOP tenant seam (Hosted Multi-Tenancy, session-serving PR2) - the loop-side twin of
/// <see cref="HostedTenantBoundary.ResolveRequestTenant"/>.
///
/// A background loop (a sweep, an aggregator, a reconcile poll) is not on a request and not on a tunnel
/// connection, so unlike every other caller it has NO tenant of its own to resolve. Before this seam the
/// Gateway's loops papered over that by hard-coding <see cref="TenantId.Local"/> - which is correct on
/// self-host but on the hosted Gateway means every loop runs one pass against a single implicit tenant
/// that owns nothing. This seam replaces that single implicit pass with ONE PASS PER TENANT, each inside
/// that tenant's own scope, so a sweep sees exactly one tenant's fleet at a time and never reaches across.
///
/// SELF-HOST IS UNCHANGED BY CONSTRUCTION: there is exactly one tenant (Local), so
/// <see cref="ForEachTenant"/> runs exactly one pass and <see cref="Current"/> is always Local - the same
/// single unscoped pass against the same partition the loops run today.
///
/// DENY-BY-DEFAULT: on hosted, <see cref="Current"/> is NULL when no scope has been entered. A caller that
/// gets null must do nothing (an empty read, an undelivered command) - it must never substitute
/// <see cref="TenantId.Local"/> or <see cref="TenantId.System"/>, which is precisely the fallback that
/// would turn one tenant's loop into a read of another tenant's partition.
/// </summary>
public interface ITenantPass
{
    /// <summary>
    /// Run <paramref name="pass"/> once per tenant, inside that tenant's scope. Self-host runs it exactly
    /// once (Local). Hosted runs it once per live tenant; with no live tenants it runs zero times, which is
    /// the correct answer - there is no fleet to sweep - and never a Local pass.
    /// </summary>
    void ForEachTenant(Action pass);

    /// <summary>
    /// The AWAITABLE per-tenant pass: run <paramref name="pass"/> once per tenant and AWAIT it inside that
    /// tenant's scope, so work that continues after an await is still scoped to the tenant it belongs to.
    /// Same tenant set and same self-host behaviour as <see cref="ForEachTenant"/>.
    ///
    /// This exists because <see cref="ForEachTenant"/> takes a synchronous <see cref="Action"/>, and a sweep
    /// that needs to await something cannot express that inside the pass. The one way out is to collect a
    /// plan inside the pass and act on it after the pass returns - and that hands the caller a trap, because
    /// the scope is gone by then and every scope-reading call it makes is silently DENIED. That is not
    /// hypothetical: the voice-mode sweep did exactly this and, on hosted, dropped every command it tried to
    /// send for its whole lifetime while logging them as an unreachable Director. Self-host never showed it,
    /// because there the ambient tenant is always Local whether a scope was entered or not.
    ///
    /// So: a per-tenant sweep that awaits anything scope-reading MUST use this overload rather than planning
    /// inside <see cref="ForEachTenant"/> and acting outside it.
    /// </summary>
    /// <param name="pass">The per-tenant work, awaited inside that tenant's scope.</param>
    /// <param name="ct">Checked between tenants so a shutdown does not have to wait out the whole census.</param>
    Task ForEachTenantAsync(Func<Task> pass, CancellationToken ct = default);

    /// <summary>
    /// The tenant of the unit of work currently running: the pass <see cref="ForEachTenant"/> entered, or
    /// the request / tunnel-connection scope the caller is already inside. Null ONLY on hosted with no scope
    /// in effect, and that null is a DENY - never Local, never System.
    /// </summary>
    TenantId? Current { get; }
}

/// <summary>
/// The single-tenant <see cref="ITenantPass"/>: exactly one pass, always <see cref="TenantId.Local"/>. This
/// is the self-host shape and the default a component falls back to when no seam is supplied (the unit
/// tests), so omitting the seam can never accidentally produce hosted behavior.
/// </summary>
public sealed class SingleTenantPass : ITenantPass
{
    public static readonly SingleTenantPass Instance = new();

    /// <inheritdoc />
    public TenantId? Current => TenantId.Local;

    /// <inheritdoc />
    public void ForEachTenant(Action pass)
    {
        if (pass is null) throw new ArgumentNullException(nameof(pass));
        pass();
    }

    /// <inheritdoc />
    public async Task ForEachTenantAsync(Func<Task> pass, CancellationToken ct = default)
    {
        if (pass is null) throw new ArgumentNullException(nameof(pass));
        await pass().ConfigureAwait(false);
    }
}

/// <summary>
/// The production <see cref="ITenantPass"/>. It reads its tenant list from the live push store
/// (<c>PushedSessionStore.KnownTenants</c>), which is exactly the set of tenants with a Director bound to
/// the tunnel - the only tenants whose fleet a push-store-driven sweep could act on - and so costs no
/// per-tick database scan. A tenant that appears or disappears between ticks is picked up on the next
/// sweep, the same eventual consistency the sweeps already rely on.
/// </summary>
public sealed class TenantPass : ITenantPass
{
    private readonly HostedTenantBoundary _boundary;
    private readonly AsyncLocalTenantContext? _ambient;
    private readonly Func<IReadOnlyCollection<TenantId>> _knownTenants;

    /// <param name="boundary">The auth-boundary binder; its <c>IsHosted</c> decides single-pass vs per-tenant.</param>
    /// <param name="ambient">The hosted ambient context (null on self-host), read for <see cref="Current"/>.</param>
    /// <param name="knownTenants">The live tenant list - <c>PushedSessionStore.KnownTenants</c> in production.</param>
    public TenantPass(
        HostedTenantBoundary boundary,
        AsyncLocalTenantContext? ambient,
        Func<IReadOnlyCollection<TenantId>> knownTenants)
    {
        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        _ambient = ambient;
        _knownTenants = knownTenants ?? throw new ArgumentNullException(nameof(knownTenants));
    }

    /// <inheritdoc />
    public TenantId? Current => _ambient is null ? TenantId.Local : _ambient.CurrentOrNull;

    /// <inheritdoc />
    public void ForEachTenant(Action pass)
    {
        if (pass is null) throw new ArgumentNullException(nameof(pass));

        // Self-host: exactly one pass, no scope to enter - byte-identical to the single pass today.
        if (!_boundary.IsHosted)
        {
            pass();
            return;
        }

        foreach (var tenant in _knownTenants())
        {
            if (!tenant.IsValid) continue;
            using (_boundary.EnterScope(tenant))
                pass();
        }
    }

    /// <inheritdoc />
    public async Task ForEachTenantAsync(Func<Task> pass, CancellationToken ct = default)
    {
        if (pass is null) throw new ArgumentNullException(nameof(pass));

        // Self-host: exactly one pass. EnterScope is inert and Current is already Local, so awaiting here is
        // byte-identical to the single unscoped pass - there is no second tenant for the scope to protect.
        if (!_boundary.IsHosted)
        {
            await pass().ConfigureAwait(false);
            return;
        }

        foreach (var tenant in _knownTenants())
        {
            ct.ThrowIfCancellationRequested();
            if (!tenant.IsValid) continue;
            // The scope is held ACROSS the await, not just up to it. The scope is ambient (AsyncLocal), so a
            // continuation that resumes after an await still resolves this tenant - which is the whole reason
            // this overload exists.
            using (_boundary.EnterScope(tenant))
                await pass().ConfigureAwait(false);
        }
    }
}
