using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="RegistryDirectorTargetResolver"/> (epic #479, #503): resolving a cron
/// job's target MACHINE to a runnable Director. Covers the three paths - a Director is already
/// running (no launch), none is running so the launcher starts one and it registers (launch + poll),
/// and the launcher cannot start one (clean error). A fake launcher and a mutable Director list stand
/// in for the live registry, with tiny wall-clock waits so the poll terminates instantly.
///
/// The resolver's result is a DirectorId (or an error) - not an endpoint. In tunnel-only mode a
/// registered Director is reached over its tunnel by id, so resolving must succeed even when the
/// Director's control endpoint is blank, which every tunnel-only Director's is (issue #1727).
/// </summary>
public sealed class RegistryDirectorTargetResolverTests
{
    private static readonly TimeSpan FastTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    private static DirectorDto Director(string id, string machine, string endpoint) => new()
    {
        DirectorId = id, MachineName = machine, ControlEndpoint = endpoint, Version = "0.9.10",
    };

    private sealed class FakeLauncher : IDirectorLauncher
    {
        private readonly Func<string, bool> _onStart;
        public int StartCount { get; private set; }
        public string? LastMachine { get; private set; }

        /// <summary>The tenant the resolver handed to the launch, so a test can assert that the resolved
        /// tenant reaches the launcher rather than being dropped on the way.</summary>
        public TenantId? LastTenant { get; private set; }

        public FakeLauncher(Func<string, bool> onStart) => _onStart = onStart;

        public Task<bool> StartAsync(TenantId tenant, string machine, CancellationToken ct)
        {
            StartCount++;
            LastMachine = machine;
            LastTenant = tenant;
            return Task.FromResult(_onStart(machine));
        }
    }

    [Fact]
    public async Task Resolve_DirectorAlreadyRunning_WithBlankControlEndpoint_ReturnsItsId_DoesNotLaunch()
    {
        // Tunnel-only shape: the registered Director's control endpoint is blank. Resolving must still
        // succeed and return its id - the id is the whole result (issue #1727).
        var directors = new List<DirectorDto>
        {
            Director("d-7882", "MACHINE_A", ""),
            Director("d-7879", "MACHINE_B", ""),
        };
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch when one is running"));
        var resolver = new RegistryDirectorTargetResolver(
            _ => directors, () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("MACHINE_A", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("d-7882", result.DirectorId);
        Assert.Equal(0, launcher.StartCount);                      // a running Director means no launch
    }

    [Fact]
    public async Task Resolve_NoDirector_LauncherStartsOne_RegistersAfterLaunch_Resolves()
    {
        var directors = new List<DirectorDto> { Director("d-7879", "MACHINE_B", "") };
        // The launcher "starts" a Director on MACHINE_A: it registers in the list and the next poll finds it.
        var launcher = new FakeLauncher(machine =>
        {
            directors.Add(Director("d-new", machine, ""));
            return true;
        });
        var resolver = new RegistryDirectorTargetResolver(
            _ => directors, () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("MACHINE_A", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("d-new", result.DirectorId);
        Assert.Equal(1, launcher.StartCount);
        Assert.Equal("MACHINE_A", launcher.LastMachine);
        // The resolved tenant reaches the launcher. It used to be dropped here: the launch went out as a fresh
        // loopback request to the Gateway's own relay, which carries no device key and so arrived with no
        // tenant at all.
        Assert.Equal(TenantId.Local, launcher.LastTenant);
    }

    [Fact]
    public async Task Resolve_NoDirector_LauncherCannotStart_ReturnsError()
    {
        var directors = new List<DirectorDto>();
        var launcher = new FakeLauncher(_ => false);              // launcher fails to start one
        var resolver = new RegistryDirectorTargetResolver(
            _ => directors, () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("MACHINE_A", CancellationToken.None);

        Assert.Null(result.DirectorId);
        Assert.NotNull(result.Error);
        Assert.Equal(1, launcher.StartCount);
    }

    [Fact]
    public async Task Resolve_LaunchedButNeverRegisters_ReturnsTimeoutError()
    {
        var directors = new List<DirectorDto>();
        var launcher = new FakeLauncher(_ => true);               // claims success but nothing registers
        var resolver = new RegistryDirectorTargetResolver(
            _ => directors, () => TenantId.Local, launcher, TimeSpan.FromMilliseconds(120), FastPoll);

        var result = await resolver.ResolveAsync("MACHINE_A", CancellationToken.None);

        Assert.Null(result.DirectorId);
        Assert.NotNull(result.Error);
        Assert.Contains("registered", result.Error!);
    }

    [Fact]
    public async Task Resolve_EmptyMachine_ReturnsError_DoesNotLaunch()
    {
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch for empty machine"));
        var resolver = new RegistryDirectorTargetResolver(
            _ => new List<DirectorDto>(), () => TenantId.Local, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("   ", CancellationToken.None);

        Assert.Null(result.DirectorId);
        Assert.NotNull(result.Error);
        Assert.Equal(0, launcher.StartCount);
    }

    // ---- Hosted Multi-Tenancy: cross-tenant machine collision (audit H1, gap audit-e) -------------------
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Resolve_scopes_to_the_callers_tenant_never_another_tenants_same_machine_director()
    {
        // Two tenants each own a Director on the SAME machine name (the registry key is (tenant, id), so this
        // is a legitimate hosted state - two accounts, two Directors, one shared machine name). Tenant A's cron
        // fire resolves that machine. It must resolve A's OWN Director; B's Director must never be picked, or
        // A's CronRunRecord would persist a cross-tenant DirectorId that A cannot even address.
        var dirA = Director("d-alice", "SHARED_MACHINE", "");
        var dirB = Director("d-bob", "SHARED_MACHINE", "");
        // The tenant-scoped reader the production registry provides: one tenant's partition per call. B is
        // listed FIRST in the fleet so a tenant-blind (pre-fix, fleet-global) resolve would pick B, making the
        // isolation assertion meaningful rather than order-lucky.
        var byTenant = new Dictionary<TenantId, DirectorDto[]>
        {
            [TenantA] = new[] { dirA },
            [TenantB] = new[] { dirB },
        };
        IEnumerable<DirectorDto> ForTenant(TenantId t) =>
            byTenant.TryGetValue(t, out var list) ? list : Array.Empty<DirectorDto>();
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch when one is running"));

        var asAlice = new RegistryDirectorTargetResolver(ForTenant, () => TenantA, launcher, FastTimeout, FastPoll);
        var resolvedForAlice = await asAlice.ResolveAsync("SHARED_MACHINE", CancellationToken.None);
        Assert.Null(resolvedForAlice.Error);
        Assert.Equal("d-alice", resolvedForAlice.DirectorId);   // A's own Director, never B's

        // Symmetric: B resolving the same machine gets B's Director, never A's.
        var asBob = new RegistryDirectorTargetResolver(ForTenant, () => TenantB, launcher, FastTimeout, FastPoll);
        var resolvedForBob = await asBob.ResolveAsync("SHARED_MACHINE", CancellationToken.None);
        Assert.Equal("d-bob", resolvedForBob.DirectorId);

        // Revert-proof: a tenant-BLIND reader (the pre-fix fleet-global list, B first) resolves B's Director for
        // A's fire - the exact cross-tenant pick the fix removes. This pins that the tenant scoping, not list
        // order, is what protects A: reverting the resolver to ignore the tenant reddens the assertion above.
        var fleetGlobal = new[] { dirB, dirA };
        var blind = new RegistryDirectorTargetResolver(_ => fleetGlobal, () => TenantA, launcher, FastTimeout, FastPoll);
        var blindPick = await blind.ResolveAsync("SHARED_MACHINE", CancellationToken.None);
        Assert.Equal("d-bob", blindPick.DirectorId);            // the leak, demonstrated
    }

    [Fact]
    public async Task Resolve_denies_when_no_tenant_scope_is_in_effect()
    {
        // Deny-by-default: on hosted a resolve reached with no tenant scope (a boundary bug) must fail loud,
        // never fall back to a partition it would then scan cross-tenant.
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch"));
        var resolver = new RegistryDirectorTargetResolver(
            _ => new[] { Director("d-x", "M", "") }, () => (TenantId?)null, launcher, FastTimeout, FastPoll);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("M", CancellationToken.None));
    }
}
