using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Running;

/// <summary>The outcome of resolving a cron job's target machine to a runnable Director (#503).</summary>
/// <param name="DirectorId">The chosen Director's id, or null when none could be resolved/launched. In
/// tunnel-only mode a resolved DirectorId is the WHOLE result: the Director is reached over its tunnel
/// by id, not by dialing a control endpoint. There used to be an Endpoint field here carrying the
/// Director's ControlEndpoint; it was always blank in tunnel-only mode and a stale guard rejected the
/// spawn on it, so it has been retired (issue #1727).</param>
/// <param name="Error">A human-readable reason when no Director could be resolved/launched, else null.</param>
public sealed record DirectorTargetResult(string? DirectorId, string? Error);

/// <summary>
/// Resolves a cron job's target MACHINE to a runnable Director (epic #479, #503): picks the first
/// reachable Director on that machine, and if none is running asks the launcher to start one and
/// waits (bounded) for it to register. Behind an interface so the firing engine is unit-testable
/// without a live registry/launcher. Production is <see cref="RegistryDirectorTargetResolver"/>.
/// </summary>
public interface IDirectorTargetResolver
{
    Task<DirectorTargetResult> ResolveAsync(string machine, CancellationToken ct);
}

/// <summary>
/// Production resolver. Reads the live Director list (the <see cref="DirectorRegistry"/>) and uses an
/// <see cref="IDirectorLauncher"/> to start a Director on demand. Uses wall-clock waits (small in
/// tests) for the launch poll, so it never depends on the engine's injected clock.
///
/// Hosted Multi-Tenancy (audit H1, gap audit-e): the resolve is confined to the CALLER'S tenant. The
/// <paramref name="listDirectors"/> reader takes the tenant to list (production: the registry's
/// tenant-scoped <c>ListDirectors(TenantId)</c> overload), and the tenant is resolved per-resolve from
/// <paramref name="resolveTenant"/> - the tenant of the current cron fire (<c>() =&gt; _tenantPass.Current</c>
/// in production), which flows across the launch poll's awaits with the ambient scope. A bare machine-name
/// match against the FLEET-GLOBAL list could pick another tenant's Director on the same machine and persist
/// that cross-tenant DirectorId in this tenant's CronRunRecord; scoping to the caller's partition makes that
/// structurally impossible. On self-host the tenant is always Local - one partition, unchanged.
/// </summary>
public sealed class RegistryDirectorTargetResolver : IDirectorTargetResolver
{
    private readonly Func<TenantId, IEnumerable<DirectorDto>> _listDirectors;
    private readonly Func<TenantId?> _resolveTenant;
    private readonly IDirectorLauncher _launcher;
    private readonly TimeSpan _launchTimeout;
    private readonly TimeSpan _pollInterval;

    /// <param name="listDirectors">
    /// The live Director list for ONE tenant (production: the registry's <c>ListDirectors(TenantId)</c>
    /// overload). It is only ever called with the tenant <paramref name="resolveTenant"/> yields.</param>
    /// <param name="resolveTenant">
    /// The tenant of the current unit of work - the cron fire's scope (production: <c>() =&gt; _tenantPass.Current</c>),
    /// always <see cref="TenantId.Local"/> on self-host. A fire is only ever reached inside a resolved scope, so a
    /// null here (hosted, no scope) is a boundary bug and DENIES (throws) rather than defaulting to a partition.</param>
    /// <param name="launcher">Starts a Director on a machine when none is running.</param>
    public RegistryDirectorTargetResolver(
        Func<TenantId, IEnumerable<DirectorDto>> listDirectors,
        Func<TenantId?> resolveTenant,
        IDirectorLauncher launcher,
        TimeSpan? launchTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _listDirectors = listDirectors ?? throw new ArgumentNullException(nameof(listDirectors));
        _resolveTenant = resolveTenant ?? throw new ArgumentNullException(nameof(resolveTenant));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _launchTimeout = launchTimeout ?? TimeSpan.FromSeconds(90);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(3);
    }

    public async Task<DirectorTargetResult> ResolveAsync(string machine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(machine))
            return new DirectorTargetResult(null, "no target machine");

        var d = PickReachable(machine);
        if (d is not null)
            return new DirectorTargetResult(d.DirectorId, null);

        // No Director running on the machine: ask its launcher to start one, then wait for it to register.
        FileLog.Write($"[RegistryDirectorTargetResolver] no Director on machine={machine}; asking launcher to start one");
        if (!await _launcher.StartAsync(machine, ct))
            return new DirectorTargetResult(null, $"no Director on '{machine}' and the launcher could not start one");

        var deadline = DateTime.UtcNow + _launchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(_pollInterval, ct);
            d = PickReachable(machine);
            if (d is not null)
            {
                FileLog.Write($"[RegistryDirectorTargetResolver] launched Director registered on machine={machine}: {d.DirectorId}");
                return new DirectorTargetResult(d.DirectorId, null);
            }
        }
        return new DirectorTargetResult(null, $"launched a Director on '{machine}' but none registered within {_launchTimeout.TotalSeconds:0}s");
    }

    /// <summary>
    /// First registered Director on the machine. Gateway Cleanup mission (tunnel-only): a registered Director
    /// IS reachable - it is reached over its tunnel by id, not by dialing a control endpoint - so this no
    /// longer requires a non-empty ControlEndpoint or an advertised-endpoint reachability state (both are
    /// artifacts of the deleted HTTP-dial path).
    /// </summary>
    private DirectorDto? PickReachable(string machine)
    {
        // Confine the machine-name match to the caller's OWN tenant partition (audit H1, gap audit-e): a
        // fleet-global scan could return another tenant's Director that happens to run on the same machine.
        var tenant = _resolveTenant()
            ?? throw new InvalidOperationException(
                "A cron target resolution ran with no tenant scope in effect. The Director list is " +
                "partitioned by tenant; the caller path was not bound at its tenant boundary.");
        return _listDirectors(tenant).FirstOrDefault(x =>
            string.Equals(x.MachineName, machine, StringComparison.OrdinalIgnoreCase));
    }
}
