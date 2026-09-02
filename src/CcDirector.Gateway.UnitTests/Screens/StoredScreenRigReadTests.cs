using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Screens;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Screens;

/// <summary>
/// ROW 4 of the Terminal Rules phase 0 proofs
/// (<c>docs/missions/terminal-rules-2026-09-02/phase-0-proofs.md</c>): a screen captured on one machine is
/// read back from the Gateway WHILE THAT MACHINE IS OFFLINE, and the rows read back are the rows that were
/// on that terminal.
///
/// This is the half of row 4 that has to run inside the Gateway's own code. The rest of the row is driven
/// by <c>scripts\terminal-rules-screen-proof.ps1</c>, which stands up a throwaway Gateway and a throwaway
/// Director, has the Director really end a turn on three lines this run authored, reads the Director's own
/// terminal buffer while the machine is up, then really stops it - and only then runs this, pointed at the
/// rig's own database, to make the READ with the real store over the real MIGRATED schema.
///
/// IT LIVES IN THE UNIT PROJECT, and that is not a filing preference. CcDirector.Gateway.Tests takes a
/// MACHINE-WIDE lock, so running one filtered test from it queues behind whatever other worktree happens
/// to be running that suite - measured: a rig run sat in this step for ten minutes while the turn-push and
/// pull-request-2643 worktrees held the lock, with a throwaway Gateway and Director alive the whole time.
/// A proof step that can be blocked for a quarter of an hour by an unrelated repository is not a proof
/// step anybody will run.
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
///
/// AND IT PROVES THE CONTENT, which inspection 01 found it did not. The old version asserted a grid flag,
/// a nonempty list, one nonblank row, a positive byte mark and a nonblank Director id - every one of which
/// a push path that replaced the terminal's content with any nonblank string would satisfy. The inspector
/// demonstrated exactly that and the row still printed PROVEN. Two comparisons replace it:
///
///  1. The three lines THIS RUN printed are among the stored rows, in the order they were printed. They
///     carry the run's timestamp, so a row left over from an earlier run cannot satisfy them and no
///     constant can be mistaken for them.
///  2. Every nonblank stored row appears in the Director's OWN terminal text, read back over the separate
///     <c>buffer</c> verb while the machine was still up. The capture came from the parser grid and this
///     comes from the raw buffer, so agreeing means the stored screen is made of bytes that were really on
///     that terminal.
///
/// Both sides are written out, quoted, so the row's acceptance is readable rather than taken from a pass
/// line.
/// </summary>
public sealed class StoredScreenRigReadTests
{
    private const string RigDbEnvVar = "CC_SCREEN_RIG_DB";
    private const string RigSessionEnvVar = "CC_SCREEN_RIG_SESSION";
    private const string RigMarkersEnvVar = "CC_SCREEN_RIG_MARKERS";
    private const string RigTerminalEnvVar = "CC_SCREEN_RIG_TERMINAL";

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
        var markerList = Environment.GetEnvironmentVariable(RigMarkersEnvVar);
        var terminalPath = Environment.GetEnvironmentVariable(RigTerminalEnvVar);
        Assert.True(File.Exists(dbPath), $"the rig database does not exist at {dbPath}");
        Assert.False(string.IsNullOrWhiteSpace(sessionId),
            $"{RigSessionEnvVar} must name the session the rig drove; without it this test could pass on any row it found");

        // THE COMPARISON MATERIAL IS REQUIRED, not optional. Without it this test would fall back to the
        // shape-only assertions that inspection 01 showed a mangled push path satisfies - so its absence is
        // a broken instrument and is refused as one, rather than quietly narrowing what the row claims.
        Assert.False(string.IsNullOrWhiteSpace(markerList),
            $"{RigMarkersEnvVar} must carry the marker lines the rig printed on that terminal, separated by |");
        Assert.True(File.Exists(terminalPath),
            $"{RigTerminalEnvVar} must point at the terminal text the rig read from the Director while it was up");

        var markers = markerList!.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, markers.Length);
        var terminalText = File.ReadAllText(terminalPath!);
        Assert.False(string.IsNullOrWhiteSpace(terminalText), "the terminal text the rig captured is empty");

        // The REAL GatewayDatabase, which migrates - so this read is against the migrated schema and not
        // against a model-built stand-in.
        using var db = new GatewayDatabase(new SingleTenantContext(), dbPath);
        var store = new SessionScreenStore(db);

        var stored = store.ReadLatest(sessionId!);

        Assert.True(stored is not null,
            $"the Gateway holds NO screen for session {sessionId} - the capture never reached the store, "
            + "so the push path is broken somewhere between TurnReviewLogger and SessionScreenStore");

        // Shape first, because a screen marked unreadable makes every content check below meaningless.
        Assert.True(stored!.Grid.HasGrid, "the stored screen is marked unreadable - no grid was captured");
        Assert.NotEmpty(stored.Grid.Rows);
        Assert.True(stored.BufferBytes > 0, "the stored screen carries no terminal byte mark");
        Assert.False(string.IsNullOrWhiteSpace(stored.DirectorId),
            "the stored screen does not name the Director that captured it");

        var rows = stored.Grid.Rows.Select(r => r ?? "").ToList();

        // COMPARISON 1. The three lines this run printed, in the order it printed them. Each is located by
        // index so the ORDER is asserted and not just the presence - and each index is reported when it
        // fails, so a run that stored the wrong frame says which markers it did find.
        var at = new int[markers.Length];
        for (var i = 0; i < markers.Length; i++)
        {
            // FindLAST, not FindFirst. The shell echoes the whole compound command back before it runs it,
            // so one line near the top of the scrollback can contain all three markers at once; if that line
            // is still on the grid, three first-matches would all point at it and the order assertion would
            // be comparing a line with itself. The last occurrence of each is the echo output it produced.
            at[i] = rows.FindLastIndex(r => r.Contains(markers[i], StringComparison.Ordinal));
            Assert.True(at[i] >= 0,
                $"the stored screen does NOT contain the line this run printed: '{markers[i]}'. "
                + "The rows that were stored are: " + string.Join(" / ", rows.Where(r => !string.IsNullOrWhiteSpace(r))));
        }
        for (var i = 1; i < at.Length; i++)
            Assert.True(at[i] > at[i - 1],
                $"the stored screen has '{markers[i]}' at row {at[i]}, which is not after '{markers[i - 1]}' at row {at[i - 1]} - "
                + "the rows are not in the order the terminal printed them");

        // COMPARISON 2. Every nonblank stored row was really on that terminal, checked against the text the
        // Director itself reported over a DIFFERENT verb. Rows are trailing-trimmed by the grid snapshot and
        // the buffer text keeps its own line breaks, so each row is looked for as a substring of the whole
        // text rather than as an exact line - which is the strongest comparison the two shapes support, and
        // it is stated here rather than left to be assumed.
        var terminalLines = terminalText.Replace("\r\n", "\n").Split('\n');
        var normalizedTerminal = string.Join("\n", terminalLines.Select(l => l.TrimEnd()));
        var notOnTheTerminal = rows
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Where(r => !normalizedTerminal.Contains(r.TrimEnd(), StringComparison.Ordinal))
            .ToList();
        Assert.True(notOnTheTerminal.Count == 0,
            "these stored rows are NOT anywhere in the terminal text the Director reported, so the stored "
            + "screen is not the screen that was on that terminal: "
            + string.Join(" / ", notOnTheTerminal));

        // The row is WRITTEN OUT, both sides, rather than left on the console. This row's acceptance is
        // "show the stored row and the read", and a pass line shows neither - while capturing a test
        // runner's console needs a verbosity that makes the run minutes slower.
        var lines = new List<string>
        {
            $"session={stored.SessionId} capturedAt={stored.CapturedAtUtc:O} director={stored.DirectorId} "
            + $"agent={stored.Agent} state={stored.ActivityState} bufferBytes={stored.BufferBytes} "
            + $"rows={stored.Grid.Rows.Count} hasGrid={stored.Grid.HasGrid} "
            + $"cursor=({stored.Grid.CursorRow},{stored.Grid.CursorCol}) visible={stored.Grid.CursorVisible} "
            + $"alternateScreen={stored.Grid.IsAlternateScreen}",
            "",
            "STORED ROWS, as read back from the Gateway with the machine offline:",
        };
        lines.AddRange(rows.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => "  stored | " + r));
        lines.Add("");
        lines.Add("THE SAME LINES ON THE TERMINAL, from the Director's own buffer read while it was up:");
        foreach (var row in rows.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var trimmed = row.TrimEnd();
            var match = terminalLines.FirstOrDefault(l => l.TrimEnd().Contains(trimmed, StringComparison.Ordinal));
            lines.Add("  terminal | " + (match?.TrimEnd() ?? "<<NOT FOUND>>"));
        }
        lines.Add("");
        lines.Add("MARKER LINES THIS RUN PRINTED, and the stored row each was found at:");
        for (var i = 0; i < markers.Length; i++) lines.Add($"  {markers[i]} -> stored row {at[i]}");

        foreach (var line in lines) Console.WriteLine("[rig] " + line);

        var outPath = Environment.GetEnvironmentVariable("CC_SCREEN_RIG_OUT");
        if (!string.IsNullOrWhiteSpace(outPath))
            File.WriteAllLines(outPath, lines);
    }
}
