using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Inspection 1, finding 2, third place: the phone's app-icon badge.
///
/// THE DEFECT. The display fold behind that badge read <see cref="PushedSessionStore.SnapshotFresh"/> with
/// a THIRTY SECOND horizon. That is a question about how recently a machine PUSHED, and the badge asks a
/// different one - may the owner be told there is work here - which is answered by whether the machine can
/// be reached. So a Director whose tunnel was up but whose pushes had gone quiet for half a minute dropped
/// out of the fold entirely, and the one nag that persists when the app is CLOSED cleared itself, telling
/// the owner nothing needed him on a machine he could have acted on at once.
///
/// These tests pin the two snapshots against each other on the same store, at the same moment, over the
/// same Director - so they fail if the age test is ever added back to the connection-scoped read, and they
/// fail if it is ever removed from the fresh one. Both directions matter: the auto-dismiss sweeper still
/// depends on the fresh read, because ACTING on a session needs recent data even though TELLING THE OWNER
/// about one does not.
///
/// WHAT IS NOT PINNED HERE, said plainly rather than left to be discovered. That the shipped
/// <c>GatewayHost</c> hands the connection-scoped snapshot to its display-state observer is NOT covered.
/// The discriminating case needs a push that is genuinely old, the store's clock is injected at
/// construction, and GatewayHost builds its own store with the real clock - so a test at that level could
/// only prove it by sleeping past the thirty-second horizon. That gap goes to inspection 2 as a gap.
/// </summary>
public sealed class ConnectionScopedSnapshotTests
{
    private const string DirectorId = "dir-north";

    private static SessionDto Session(string id) => new()
    {
        SessionId = id,
        Name = id,
        ActivityState = "Waiting",
        StatusColor = "red",
        LastActivityAt = DateTime.UtcNow,
    };

    /// <summary>A Director whose last push landed <paramref name="pushAge"/> ago, tunnel up or down.</summary>
    private static PushedSessionStore StoreWithPush(TimeSpan pushAge, bool tunnelUp, params SessionDto[] sessions)
    {
        var pushedAt = DateTime.UtcNow - pushAge;
        var store = new PushedSessionStore(() => pushedAt);
        store.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(store.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, sessions));
        if (!tunnelUp)
            Assert.True(store.UnregisterConnection(TenantId.Local, DirectorId, "conn-1"));
        return store;
    }

    [Fact]
    public void AConnectedButQuietDirector_IsInTheConnectionSnapshot_AndOutOfTheFreshOne()
    {
        // Tunnel UP, last push ninety seconds ago - three times the thirty-second horizon the badge fold
        // used to apply. This is the exact machine the owner was stopped being told about.
        var store = StoreWithPush(TimeSpan.FromSeconds(90), tunnelUp: true, Session("s-1"));

        var connected = store.SnapshotConnected(TenantId.Local);
        var fresh = store.SnapshotFresh(TenantId.Local, TimeSpan.FromSeconds(30));

        Assert.Equal(new[] { "s-1" }, connected.Select(x => x.Session.SessionId).ToArray());
        Assert.Empty(fresh);   // and the fresh read still refuses it, which the auto-dismiss sweeper needs
    }

    /// <summary>
    /// The control. Without it, a connection snapshot that simply returned everything forever would pass the
    /// test above - and "nag about a machine nobody can reach" is the defect on the other side of this rule,
    /// the one that had retained cards promising they could speak.
    /// </summary>
    [Fact]
    public void ADirectorWhoseTunnelIsDown_IsInNeitherSnapshot()
    {
        var store = StoreWithPush(TimeSpan.FromSeconds(5), tunnelUp: false, Session("s-1"));

        Assert.Empty(store.SnapshotConnected(TenantId.Local));
        Assert.Empty(store.SnapshotFresh(TenantId.Local, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void AFreshlyPushingConnectedDirector_IsInBothSnapshots()
    {
        var store = StoreWithPush(TimeSpan.FromSeconds(2), tunnelUp: true, Session("s-1"), Session("s-2"));

        Assert.Equal(2, store.SnapshotConnected(TenantId.Local).Count);
        Assert.Equal(2, store.SnapshotFresh(TenantId.Local, TimeSpan.FromSeconds(30)).Count);
    }

    /// <summary>
    /// The connection-scoped read keeps the one rule that is NOT about age: a Director that has reconnected
    /// but not yet pushed under the new connection contributes nothing. Its cached sessions belong to the
    /// PREVIOUS connection, and serving them as current is how a stale set that omits a live session reaches
    /// a destructive consumer. Dropping the age test must not drop this one with it.
    /// </summary>
    [Fact]
    public void AReconnectedDirectorThatHasNotPushedYet_ContributesNothing()
    {
        var store = StoreWithPush(TimeSpan.FromSeconds(2), tunnelUp: true, Session("s-1"));
        Assert.Single(store.SnapshotConnected(TenantId.Local));

        // The tunnel drops and a NEW connection arrives, which has said nothing yet.
        Assert.True(store.UnregisterConnection(TenantId.Local, DirectorId, "conn-1"));
        store.RegisterConnection(TenantId.Local, DirectorId, "conn-2");

        Assert.Empty(store.SnapshotConnected(TenantId.Local));
    }
}
