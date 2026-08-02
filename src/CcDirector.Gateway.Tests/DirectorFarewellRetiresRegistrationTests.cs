using System;
using System.IO;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A DIRECTOR THAT SAYS GOODBYE MUST STOP BEING EXPECTED.
///
/// The defect: a Director's clean-shutdown farewell reached the work-history recorder and nothing else. The
/// registry entry - the Gateway's record that there is a Director here it should be able to reach - was left
/// exactly as it was. It is deliberately not dropped when the tunnel closes (a reconnect blip must not flap
/// the roster, and the machine's cached sessions have to outlive the gap), and the only thing that finally
/// removes it is the eviction horizon TWENTY-FOUR HOURS later. The graceful
/// <c>DELETE /directors/{id}/registration</c> that used to clear it belongs to the legacy same-machine
/// discovery plane and is refused outright on a hosted Gateway, so on the tunnel-only architecture NOTHING
/// cleared a registration on shutdown. Every orderly stop therefore produced a full day of "unreachable" for
/// a Director that had politely announced it was leaving - and, because the Fleet Map counted those rows as
/// machines, a full day of "1 machine unreachable" about a machine that was fine.
///
/// The fix marks the entry rather than removing it, so everything that depends on the entry surviving is
/// untouched and only the VERDICT about it changes.
///
/// Revert-prove: delete the <c>_registry.MarkStopped(...)</c> line from <c>DirectorHub.DirectorStopping</c>
/// and <see cref="The_farewell_retires_the_registration"/> goes red at the stamp, after its positive control
/// has passed. To redden the clearing rule instead, merge the field in <c>RegisterFromStream</c>'s dto
/// (<c>StoppedAtUtc = existing?.StoppedAtUtc</c>) and
/// <see cref="A_restarted_director_is_running_again_from_its_first_hello"/> goes red - that is the mistake
/// that would leave a restarted Director reading "not running" for the rest of its life.
///
/// These drive the registry directly - no HTTP, no tunnel - so the result depends on nothing a test harness
/// registers on the side. The hub leg (that the farewell actually calls this) is in DirectorHubTests.
/// </summary>
public sealed class DirectorFarewellRetiresRegistrationTests : IDisposable
{
    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly TenantId Bob = new("tenant-bob");

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-farewell-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;

    public DirectorFarewellRetiresRegistrationTests()
    {
        Directory.CreateDirectory(_instancesDir);
        // Constructed but never Start()ed: no watcher, no sweeper, so the only state under test is what
        // these registrations put there.
        _registry = new DirectorRegistry(_instancesDir);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    private void Hello(TenantId tenant, string id, string machine = "SOREN_NORTH") =>
        _registry.RegisterFromStream(id, machine, "soren", "1.9.4", 1234, DateTime.UtcNow, tenant, "Slot 5");

    private CcDirector.Gateway.Contracts.DirectorDto Entry(TenantId tenant, string id) =>
        _registry.ListDirectors(tenant).Single(d => d.DirectorId == id);

    [Fact]
    public void The_farewell_retires_the_registration()
    {
        Hello(Alice, "dir-slot5");
        // Positive control: it is registered and RUNNING first. Without this, "it is marked stopped" would
        // also read as satisfied on an entry that was never there.
        Assert.Null(Entry(Alice, "dir-slot5").StoppedAtUtc);

        Assert.True(_registry.MarkStopped(Alice, "dir-slot5"));

        Assert.NotNull(Entry(Alice, "dir-slot5").StoppedAtUtc);
    }

    /// <summary>
    /// THE REASON IT MARKS RATHER THAN REMOVES. A stopped Director stays in the registry, so its cached
    /// sessions, its snooze rows and its session numbers are all still addressable - none of which would
    /// survive a delete, and none of which this change is entitled to touch.
    /// </summary>
    [Fact]
    public void A_stopped_director_is_still_in_the_registry()
    {
        Hello(Alice, "dir-slot5");
        _registry.MarkStopped(Alice, "dir-slot5");

        Assert.Contains("dir-slot5", _registry.ListDirectors(Alice).Select(d => d.DirectorId));
        Assert.NotNull(_registry.Get(Alice, "dir-slot5"));
    }

    [Fact]
    public void A_restarted_director_is_running_again_from_its_first_hello()
    {
        Hello(Alice, "dir-slot5");
        _registry.MarkStopped(Alice, "dir-slot5");
        Assert.NotNull(Entry(Alice, "dir-slot5").StoppedAtUtc); // positive control: it really was stopped

        Hello(Alice, "dir-slot5"); // the process came back

        Assert.Null(Entry(Alice, "dir-slot5").StoppedAtUtc);
    }

    /// <summary>
    /// A farewell is a write, and every write here is keyed by (tenant, id): one account's goodbye can never
    /// retire another account's Director, however it chose its director id.
    /// </summary>
    [Fact]
    public void One_tenants_farewell_cannot_retire_anothers_director()
    {
        Hello(Alice, "dir-shared", "ALICE-BOX");
        Hello(Bob, "dir-shared", "BOB-BOX");

        Assert.True(_registry.MarkStopped(Bob, "dir-shared"));

        // Positive control on the attack: Bob's own entry really was marked, so the assertion below is not
        // passing merely because the write silently did nothing.
        Assert.NotNull(Entry(Bob, "dir-shared").StoppedAtUtc);
        Assert.Null(Entry(Alice, "dir-shared").StoppedAtUtc);
    }

    [Fact]
    public void A_farewell_for_an_unknown_director_changes_nothing()
    {
        Assert.False(_registry.MarkStopped(Alice, "dir-never-registered"));
        Assert.False(_registry.MarkStopped(Alice, ""));
        Assert.Empty(_registry.ListDirectors(Alice));
    }
}
