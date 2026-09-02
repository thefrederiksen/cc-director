using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Screens;
using Xunit;

namespace CcDirector.Gateway.Tests.Screens;

/// <summary>
/// ROW 4 of the Terminal Rules phase 0 proofs
/// (<c>docs/missions/terminal-rules-2026-09-02/phase-0-proofs.md</c>): a screen captured on one machine is
/// read back from the Gateway WHILE THAT MACHINE IS OFFLINE.
///
/// This is the half of row 4 that has to run inside the Gateway's own code. The rest of the row is driven
/// by <c>scripts\terminal-rules-screen-proof.ps1</c>, which stands up a throwaway Gateway and a throwaway
/// Director, has the Director really end a turn, then really stops it - and only then runs this, pointed at
/// the rig's own database, to make the READ with the real store over the real MIGRATED schema.
///
/// It is gated on <c>CC_SCREEN_RIG_DB</c> and reports SKIPPED without it, so an ordinary test run never
/// looks for a database that is not there. A skipped result is NOT a pass: the rig script asserts this test
/// actually RAN and passed, because a proof that silently skipped would read exactly like a proof that
/// succeeded - the defect this whole mission kept finding.
///
/// WHAT IT PROVES, and it is the whole chain: the row it reads was written by a real
/// <c>TurnReviewLogger</c> capture, sent through the real <c>GatewayScreenSink</c>, over the real
/// <c>PushScreen</c> hub method, into the real <c>SessionScreenStore</c> on a real migrated database - none
/// of which any other phase 0 row exercises.
/// </summary>
public sealed class StoredScreenRigReadTests
{
    private const string RigDbEnvVar = "CC_SCREEN_RIG_DB";
    private const string RigSessionEnvVar = "CC_SCREEN_RIG_SESSION";

    /// <summary>A Fact that skips itself unless the rig has pointed it at a database, so the ordinary run is
    /// unaffected. The rig treats a SKIP as a failure of the row - see the class comment.</summary>
    private sealed class RequiresRigFactAttribute : FactAttribute
    {
        public RequiresRigFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RigDbEnvVar)))
                Skip = $"Set {RigDbEnvVar} to a rig database path; this row is driven by scripts\\terminal-rules-screen-proof.ps1.";
        }
    }

    [RequiresRigFact]
    public void AScreenCapturedByARealDirectorIsReadBackWhileThatMachineIsOffline()
    {
        var dbPath = Environment.GetEnvironmentVariable(RigDbEnvVar)!;
        var sessionId = Environment.GetEnvironmentVariable(RigSessionEnvVar);
        Assert.True(File.Exists(dbPath), $"the rig database does not exist at {dbPath}");
        Assert.False(string.IsNullOrWhiteSpace(sessionId),
            $"{RigSessionEnvVar} must name the session the rig drove; without it this test could pass on any row it found");

        // The REAL GatewayDatabase, which migrates - so this read is against the migrated schema and not
        // against a model-built stand-in. That distinction is the label every other proven row carries.
        using var db = new GatewayDatabase(new SingleTenantContext(), dbPath);
        var store = new SessionScreenStore(db);

        var stored = store.ReadLatest(sessionId!);

        Assert.True(stored is not null,
            $"the Gateway holds NO screen for session {sessionId} - the capture never reached the store, "
            + "so the push path is broken somewhere between TurnReviewLogger and SessionScreenStore");

        // Content, not merely presence. A blank grid would satisfy "a row exists" while proving the screen
        // never arrived; a fixed-height terminal grid is full of rows even when nothing is on it.
        Assert.True(stored!.Grid.HasGrid, "the stored screen is marked unreadable - no grid was captured");
        Assert.NotEmpty(stored.Grid.Rows);
        Assert.Contains(stored.Grid.Rows, r => !string.IsNullOrWhiteSpace(r));
        Assert.True(stored.BufferBytes > 0,
            "the stored screen carries no terminal byte mark, so nothing could ever certify it as current");
        Assert.False(string.IsNullOrWhiteSpace(stored.DirectorId),
            "the stored screen does not name the Director that captured it");

        // Printed so the rig's report can quote the row rather than assert that a method returned.
        Console.WriteLine($"[rig] session={stored.SessionId} capturedAt={stored.CapturedAtUtc:O} "
            + $"director={stored.DirectorId} agent={stored.Agent} state={stored.ActivityState} "
            + $"bufferBytes={stored.BufferBytes} rows={stored.Grid.Rows.Count}");
        foreach (var row in stored.Grid.Rows.Where(r => !string.IsNullOrWhiteSpace(r)).Take(12))
            Console.WriteLine($"[rig]   | {row}");
    }
}
