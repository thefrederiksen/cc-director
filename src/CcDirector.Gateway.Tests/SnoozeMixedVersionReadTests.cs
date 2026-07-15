using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE VERSION-SKEW SEAM: a Gateway newer than the Director it serves.
///
/// This is not a corner case. A Gateway is routinely updated ahead of the Directors across the fleet, so
/// "the Director does not send this field yet" is an ORDINARY running state, and every new wire field has
/// to answer what its absence means. Defect 12 was the Gateway losing the difference between "not held"
/// and "held, not landed yet"; the tri-state fixed the wire, and then a DEFAULT VALUE on the receiving DTO
/// quietly reintroduced it for exactly these Directors - an absent field deserialized to None, the sweep
/// read None as "the Director says it is genuinely not held", and cleared a live deferred snooze about
/// fifteen seconds after the user asked for it.
///
/// The rule these tests pin: ABSENCE IS NOT EVIDENCE. An old Director reports onHold=false for BOTH "not
/// held" AND "deferred", so false cannot disprove a hold - it can only ever prove one (true). The honest
/// read of silence is null, "I do not know", and the sweep changes nothing on null.
///
/// Written after an independent review of pull request 1585 caught this in the diff; the mission's own
/// tests proved the old boolean still landed for held/not-held and never asked what happened to a
/// DEFERRED hold on an old Director.
/// </summary>
public sealed class SnoozeMixedVersionReadTests
{
    /// <summary>
    /// The whole chain the sweep actually runs, with nothing hand-set: an old Director's real payload ->
    /// the real deserializer -> the real SnoozeSweepDirectorClient.ReadHoldStateAsync. It must answer
    /// null (unknown), never HoldState.None - the sweep clears on None, and clearing here is the defect.
    /// </summary>
    [Fact]
    public async Task AnOldDirectorsBodyWithoutHoldState_ReadsAsUnknown_NotAsNotHeld()
    {
        // Exactly what a Director that predates the tri-state puts on the wire for a DEFERRED hold: the
        // boolean only, and it says false, because a deferral has not landed.
        const string oldDirectorBody = """{"sessionId":"s1","onHold":false}""";

        var holdState = await ReadHoldStateFromDirectorBodyAsync(oldDirectorBody);

        Assert.Null(holdState);
        Assert.NotEqual(HoldState.None, holdState);
    }

    /// <summary>
    /// The control, and the reason this is a real fix rather than a blanket "never trust the boolean":
    /// true is CONCLUSIVE. Only a landed hold reports it, so an old Director saying so is believed.
    /// </summary>
    [Fact]
    public async Task AnOldDirectorSayingOnHoldTrue_IsStillBelieved_BecauseTrueIsConclusive()
    {
        const string oldDirectorBody = """{"sessionId":"s1","onHold":true}""";

        var holdState = await ReadHoldStateFromDirectorBodyAsync(oldDirectorBody);

        Assert.Equal(HoldState.Held, holdState);
    }

    /// <summary>
    /// The control for a CURRENT Director: it names the state explicitly, so there is no ambiguity to
    /// resolve and the sweep gets the fact it needs. This is the case that must keep working - the fix
    /// must not turn every read into "unknown".
    /// </summary>
    [Theory]
    [InlineData(HoldStates.DeferredHold, HoldState.DeferredHold)]
    [InlineData(HoldStates.Held, HoldState.Held)]
    [InlineData(HoldStates.None, HoldState.None)]
    public async Task ACurrentDirectorThatNamesTheState_IsReadVerbatim(string wire, HoldState expected)
    {
        var body = $$"""{"sessionId":"s1","onHold":false,"holdState":"{{wire}}"}""";

        var holdState = await ReadHoldStateFromDirectorBodyAsync(body);

        Assert.Equal(expected, holdState);
    }

    /// <summary>
    /// Drive the REAL client with a stubbed transport that returns the given body verbatim. Only the wire
    /// is faked; the deserialization and the normalize/map the sweep depends on are the production ones.
    /// </summary>
    private static async Task<HoldState?> ReadHoldStateFromDirectorBodyAsync(string bodyJson)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cc-snooze-mixed-version-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            using var registry = new DirectorRegistry(tempDir);
            var client = new SnoozeSweepDirectorClient(
                registry,
                pushedSessions: null,
                sendCommand: (directorId, command, ct) =>
                    Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(bodyJson)));

            return await client.ReadHoldStateAsync("dir-1", "s1", CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
