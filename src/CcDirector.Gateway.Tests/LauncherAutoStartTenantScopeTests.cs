using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Running;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE AUTO-LAUNCH PATH, WHICH IS WHERE THE TENANT USED TO BE LOST.
///
/// "Start a session on another computer" resolves the target machine to a Director and, when none is running,
/// asks that machine's launcher to start one. That second step used to be an HTTP POST from the Gateway to
/// the Gateway's OWN /machines/{machine}/director/start over loopback, carrying the host-wide shared token.
///
/// Reusing the shipped relay was right. Reusing it through a fresh inbound request was not: a loopback request
/// carries no device key, so the inner call resolved to NO TENANT. On the hosted Gateway that meant the
/// Gateway refused itself with 403 and a hosted tenant could never start a session on a machine whose Director
/// was not already running. It failed closed rather than leaking - but a capability that cannot work is still
/// a defect, and had the inner call resolved to anything at all, it would have resolved to a tenant unrelated
/// to the one that asked.
///
/// The relay now takes the tenant as an ARGUMENT (<see cref="LauncherLifecycleRelay"/>) and the auto-launcher
/// calls it in process. These tests pin both halves of that: the tenant SURVIVES the hand-off, and it is
/// ENFORCED once it arrives.
/// </summary>
public sealed class LauncherAutoStartTenantScopeTests
{
    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly TenantId Bob = new("tenant-bob");
    private const string Machine = "SHARED-NAME-PC";

    /// <summary>A stub for the launcher STREAM - the only path a command can reach a launcher by (phase 6
    /// deleted the REST relay along with the launcher's listener). It records every command it carries and
    /// resolves (tenant, machine) the way the production connection registry does, so "reached" and "not
    /// reached" are both directly observable rather than inferred from a return value.</summary>
    private sealed class StubLauncherStream
    {
        private readonly TenantId _connectedTenant;
        private readonly string _connectedMachine;
        private readonly List<string> _hits = new();

        public StubLauncherStream(TenantId connectedTenant, string connectedMachine)
        {
            _connectedTenant = connectedTenant;
            _connectedMachine = connectedMachine;
        }

        public Task<LauncherCommandResult?> SendAsync(TenantId tenant, string machine, LauncherCommand command, CancellationToken ct)
        {
            // The production hook resolves the connection as (tenant, machine): another tenant naming the
            // same bare machine name finds NO connection, because the connection registry is composite-keyed.
            if (!tenant.Equals(_connectedTenant) || !string.Equals(machine, _connectedMachine, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<LauncherCommandResult?>(null);
            lock (_hits) _hits.Add(command.Verb);
            return Task.FromResult<LauncherCommandResult?>(LauncherCommandResult.Ok());
        }

        public string[] Snapshot()
        {
            lock (_hits) return _hits.ToArray();
        }
    }

    private static void Register(LauncherRegistry registry, TenantId tenant, string machine) =>
        registry.Upsert(tenant, new LauncherRegistrationRequest
        {
            MachineName = machine,
            Pid = 99,
            Version = "1.0.0",
        });

    [Fact]
    public async Task The_auto_launcher_starts_a_director_for_the_tenant_that_owns_the_machine()
    {
        // The capability, at the layer that used to break it: the launch actually reaches the launcher's
        // stream connection.
        var stream = new StubLauncherStream(Alice, Machine);
        var registry = new LauncherRegistry();
        Register(registry, Alice, Machine);

        var launcher = new RelayDirectorLauncher(registry, stream.SendAsync);
        var started = await launcher.StartAsync(Alice, Machine, CancellationToken.None);

        Assert.True(started);
        Assert.Equal(new[] { "director/start" }, stream.Snapshot());
    }

    [Fact]
    public async Task The_auto_launcher_cannot_start_a_director_on_another_tenants_machine()
    {
        // Alice owns the machine. Bob names it and reaches nothing - and, crucially, Alice's launcher
        // never receives anything. A machine name is unique only WITHIN a tenant, so the same bare name in
        // another partition is a different machine, not a permission question.
        var stream = new StubLauncherStream(Alice, Machine);
        var registry = new LauncherRegistry();
        Register(registry, Alice, Machine);

        var launcher = new RelayDirectorLauncher(registry, stream.SendAsync);
        var started = await launcher.StartAsync(Bob, Machine, CancellationToken.None);

        Assert.False(started);
        Assert.Empty(stream.Snapshot());
    }

    [Fact]
    public async Task The_stream_hook_is_scoped_to_the_calling_tenant()
    {
        // The stream hook itself, exercised directly: the tenant has to reach the connection lookup intact,
        // or two callers would disagree about who owns the machine.
        var seen = new List<(TenantId Tenant, string Machine, string Verb)>();
        LauncherCommandRouter.SendLauncherCommandAsync send = (tenant, machine, command, _) =>
        {
            seen.Add((tenant, machine, command.Verb));
            // Mimic the production hook: it resolves (tenant, machine) and returns null when THIS tenant has
            // no joined launcher for that machine - which the relay reports as undeliverable, never dials.
            return Task.FromResult<LauncherCommandResult?>(
                tenant.Equals(Alice) ? LauncherCommandResult.Ok() : null);
        };

        var registry = new LauncherRegistry();          // deliberately empty: an unregistered machine refuses
        var launcher = new RelayDirectorLauncher(registry, send);

        Assert.True(await launcher.StartAsync(Alice, Machine, CancellationToken.None));
        // Bob's stream lookup misses, and his partition holds no registration either - refused.
        Assert.False(await launcher.StartAsync(Bob, Machine, CancellationToken.None));

        Assert.Equal(
            new[] { (Alice, Machine, "director/start"), (Bob, Machine, "director/start") },
            seen);
    }

    [Fact]
    public async Task The_resolved_tenant_reaches_the_launcher_rather_than_being_dropped()
    {
        // The hand-off itself. The target resolver resolves the tenant once, then hands it to the launcher; if
        // that argument were ever dropped or re-derived, the launch would address the wrong partition. This
        // asserts the tenant that arrives at the launcher is the one the resolver resolved.
        TenantId? seenByLauncher = null;
        var launcher = new CapturingLauncher(t => seenByLauncher = t);

        var resolver = new RegistryDirectorTargetResolver(
            listDirectors: _ => Array.Empty<DirectorDto>(),   // nothing running -> the launch path is taken
            resolveTenant: () => Bob,
            launcher: launcher,
            launchTimeout: TimeSpan.FromMilliseconds(50),
            pollInterval: TimeSpan.FromMilliseconds(10));

        await resolver.ResolveAsync(Machine, director: null, CancellationToken.None);

        Assert.Equal(Bob, seenByLauncher);
    }

    [Fact]
    public async Task A_resolve_with_no_tenant_scope_denies_loudly_rather_than_picking_a_partition()
    {
        // Deny-by-default at the resolver. A null tenant means the caller path was not bound at its tenant
        // boundary; defaulting to Local there would silently run one tenant's spawn against another's fleet.
        // It throws instead - loudly, at the boundary bug, rather than quietly at the wrong machine.
        var launcher = new CapturingLauncher(_ => { });
        var resolver = new RegistryDirectorTargetResolver(
            listDirectors: _ => Array.Empty<DirectorDto>(),
            resolveTenant: () => null,
            launcher: launcher,
            launchTimeout: TimeSpan.FromMilliseconds(50),
            pollInterval: TimeSpan.FromMilliseconds(10));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(Machine, director: null, CancellationToken.None));
        Assert.Contains("no tenant scope", ex.Message, StringComparison.OrdinalIgnoreCase);

        // And it denied BEFORE reaching out to any machine.
        Assert.Null(launcher.LastTenant);
    }

    /// <summary>Records the tenant the resolver handed it, and claims the launch succeeded.</summary>
    private sealed class CapturingLauncher : IDirectorLauncher
    {
        private readonly Action<TenantId> _onStart;
        public TenantId? LastTenant { get; private set; }

        public CapturingLauncher(Action<TenantId> onStart) => _onStart = onStart;

        public Task<bool> StartAsync(TenantId tenant, string machine, CancellationToken ct)
        {
            LastTenant = tenant;
            _onStart(tenant);
            return Task.FromResult(true);
        }
    }
}
