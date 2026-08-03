using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Which live Gateway does the background work (issue #2398, stage 1).
///
/// A deploy runs TWO full Gateways at once for roughly forty-five seconds, and every sweep's overlap
/// guard is per-PROCESS, so both would fire. Rather than make six subsystems cross-process safe, only the
/// production instance acts - there is no concurrency to make safe when only one process is acting.
///
/// A process decides by asking the public address who is answering there and comparing to itself. These
/// drive that decision a reading at a time through the injected reader, so no server is needed and the
/// swap sequence can be played out deterministically.
/// </summary>
public sealed class GatewayInstanceRoleTests
{
    private const string Me = "instance-me";
    private const string Other = "instance-other";
    private const string PublicUrl = "https://gateway.example.com";

    /// <summary>A role whose view of "who is serving production" the test moves.</summary>
    private static (GatewayInstanceRole role, Func<string?> setServing) Build(string? initiallyServing)
    {
        string? serving = initiallyServing;
        var role = new GatewayInstanceRole(Me, PublicUrl, hosted: true,
            readInstanceAt: (_, _) => Task.FromResult<string?>(serving));
        return (role, () => serving);
    }

    private static async Task<GatewayInstanceRole> AfterReadings(Func<int, string?> servingByReading, int readings)
    {
        string? serving = null;
        var role = new GatewayInstanceRole(Me, PublicUrl, hosted: true,
            readInstanceAt: (_, _) => Task.FromResult<string?>(serving));
        for (var i = 0; i < readings; i++)
        {
            serving = servingByReading(i);
            await role.CheckAsync();
        }
        return role;
    }

    [Fact]
    public void SelfHost_IsProductionImmediately_AndNeverPolls()
    {
        // A local install has one Gateway, no slots and no public URL. It must never stop doing its work
        // waiting for a confirmation that has no meaning there.
        using var role = new GatewayInstanceRole(Me, publicBaseUrl: null, hosted: false,
            readInstanceAt: (_, _) => throw new InvalidOperationException("self-host must not poll"));

        Assert.True(role.IsProduction);
    }

    [Fact]
    public void Hosted_StartsPassive_BeforeAnyReading()
    {
        // Passive until proven production: a warming staging slot must not fire a sweep on its first tick.
        using var role = new GatewayInstanceRole(Me, PublicUrl, hosted: true,
            readInstanceAt: (_, _) => Task.FromResult<string?>(null));

        Assert.False(role.IsProduction);
    }

    [Fact]
    public async Task BecomingProduction_NeedsSeveralAgreeingReadings()
    {
        // The front door alternates between the old and new instance for several seconds after a swap, so
        // one agreeing reading during that flap is not evidence.
        using var afterOne = await AfterReadings(_ => Me, readings: 1);
        Assert.False(afterOne.IsProduction);

        using var afterTwo = await AfterReadings(_ => Me, readings: 2);
        Assert.False(afterTwo.IsProduction);

        using var afterThree = await AfterReadings(_ => Me, readings: 3);
        Assert.True(afterThree.IsProduction);
    }

    [Fact]
    public async Task AFlappingFrontDoor_DoesNotMakeItProduction()
    {
        // Alternating answers must never accumulate into a majority: the streak resets on every reading
        // that is not a confirmed "it is me".
        using var role = await AfterReadings(i => i % 2 == 0 ? Me : Other, readings: 9);

        Assert.False(role.IsProduction);
    }

    [Fact]
    public async Task OnceProduction_ASingleContraryReadingStandsItDown()
    {
        // The swap moment. This process was production; the address now answers with the new container.
        // One reading is enough in this direction, because acting while another process is production
        // means duplicate scheduled work against one shared database.
        string? serving = Me;
        using var role = new GatewayInstanceRole(Me, PublicUrl, hosted: true,
            readInstanceAt: (_, _) => Task.FromResult<string?>(serving));
        for (var i = 0; i < 3; i++) await role.CheckAsync();
        Assert.True(role.IsProduction);

        serving = Other;
        await role.CheckAsync();

        Assert.False(role.IsProduction);
    }

    [Fact]
    public async Task AnUnanswerableCheck_StandsItDownRatherThanLeavingItActing()
    {
        // Silence is not confirmation. A Gateway that cannot reach its own public address does not know
        // whether another process took over, and guessing "still me" risks duplicate work.
        string? serving = Me;
        using var role = new GatewayInstanceRole(Me, PublicUrl, hosted: true,
            readInstanceAt: (_, _) => serving is null
                ? throw new HttpRequestException("unreachable")
                : Task.FromResult<string?>(serving));
        for (var i = 0; i < 3; i++) await role.CheckAsync();
        Assert.True(role.IsProduction);

        serving = null; // the reader now throws
        await role.CheckAsync();

        Assert.False(role.IsProduction);
    }

    [Fact]
    public async Task AGatewayTooOldToReportAnInstance_LeavesBothPassive()
    {
        // Rolling compatibility: an older Gateway on the other side of a rollout publishes no instance
        // field, which must read as "not confirmed to be me" rather than "confirmed to be someone else".
        // Both processes then stand down, which is the safe direction - the alternative is both acting.
        using var role = await AfterReadings(_ => null, readings: 5);

        Assert.False(role.IsProduction);
    }

    [Fact]
    public void MissingInstanceField_IsNotReadAsAnInstance()
    {
        Assert.Null(GatewayInstanceIdentity.ReadInstanceFromHealthJson("""{"status":"ok","commit":"abc1234"}"""));
        Assert.Null(GatewayInstanceIdentity.ReadInstanceFromHealthJson("not json at all"));
        Assert.Null(GatewayInstanceIdentity.ReadInstanceFromHealthJson(""));
        Assert.Null(GatewayInstanceIdentity.ReadInstanceFromHealthJson("""{"instance":""}"""));
        Assert.Equal("abcd1234", GatewayInstanceIdentity.ReadInstanceFromHealthJson("""{"instance":"abcd1234"}"""));
    }

    [Fact]
    public void EachBoot_HasItsOwnIdentity()
    {
        // The commit cannot tell two processes apart: a rollback or a retried release puts two containers
        // on the wire carrying identical commit stamps.
        Assert.False(string.IsNullOrWhiteSpace(GatewayInstanceIdentity.Current));
    }
}
