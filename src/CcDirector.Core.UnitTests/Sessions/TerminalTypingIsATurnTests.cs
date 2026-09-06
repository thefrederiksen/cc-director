using System.Text.RegularExpressions;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The guard for defect one of the "Clean up Your Throttle" mission (2026-09-05): a turn typed at the
/// desktop terminal is a SUBMITTED TURN and is counted as one.
///
/// THE SYMPTOM THESE TESTS REPRODUCE. <see cref="Session.SendInput"/> stamped the submission event and
/// recorded the characters, and never recorded the turn. Over the owner's week of 2026-W35 that left 594
/// of his 771 typed submissions - 77 per cent of his typing - out of the denominator of the ratio Your
/// Throttle publishes, while the submission ledger written in the SAME METHOD, eight lines away, had
/// every one of them. The page said 92 per cent spoken; the ledger said 56.8. Excluding those 594 is 28.3
/// of the 34 points of the gap on its own. Measured in
/// docs/missions/clean-up-your-throttle-2026-09-05/reconciliation.md.
///
/// WHY IT IS THE WHOLE SHAPE THAT IS GUARDED, NOT JUST THE COUNT. Any test that only asserts "SendInput
/// counts a turn" is satisfied by adding a second tally call next to the first, which is precisely the
/// arrangement that drifted. So the last two tests here pin the STRUCTURE: every submission the session
/// stamps carries exactly one counted turn (behaviour), and the turn tally has exactly one call site and
/// it is the method that stamps the submission (source). Remove either and a future edit can reopen the
/// same 28-point hole with a green suite.
///
/// PROVED ABLE TO FAIL. With the fix reverted (the RecordTurn call removed from StampSubmission) the
/// first, third and last two tests go red with the reported symptom - zero turns counted for terminal
/// typing - and the composing controls stay green.
///
/// IT LIVES IN THE PARALLEL HALF ON PURPOSE. CcDirector.Core.Tests, where the other Session tests are,
/// is PARKED and does not run in the default gate. A guard against a defect that shipped for months and
/// was found by an audit rather than by a test is worth nothing in a suite nobody runs; this one is fast
/// and free of wall-clock dependence, so it belongs where the gate can see it.
/// </summary>
public sealed class TerminalTypingIsATurnTests
{
    /// <summary>A backend that records what it was given and starts no process.</summary>
    private sealed class RecordingBackend : ISessionBackend
    {
        public List<byte[]> Writes { get; } = new();
        public List<string> SentTexts { get; } = new();

        public int ProcessId => 1234;
        public string Status => "Recording";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) => Writes.Add(data);
        public Task SendTextAsync(string text) { SentTexts.Add(text); return Task.CompletedTask; }
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static Session NewSession(RecordingBackend backend)
    {
        var s = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: "claude-test",
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: null,
            customColor: null);
        s.MarkRunning();
        return s;
    }

    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    /// <summary>One bucket of the tally by its wire tokens, zeroes when the bucket does not exist.</summary>
    private static InputStatBucketDto Bucket(Session s, string modality, string surface) =>
        s.InputStats.Snapshot().Buckets.FirstOrDefault(b =>
            string.Equals(b.Modality, modality, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(b.Surface, surface, StringComparison.OrdinalIgnoreCase))
        ?? new InputStatBucketDto { Modality = modality, Surface = surface };

    private static void Type(Session s, string line)
    {
        // One keystroke at a time, which is exactly how the terminal delivers it.
        foreach (var ch in line)
            s.SendInput(Bytes(ch.ToString()), InputOrigin.DesktopTyped);
    }

    private static void Submit(Session s) => s.SendInput(new byte[] { 0x0D }, InputOrigin.DesktopTyped);

    // ---- the symptom ----

    [Fact]
    public void TypingALineAndPressingEnter_CountsExactlyOneTypedDesktopTurn()
    {
        using var s = NewSession(new RecordingBackend());

        Type(s, "commit this");
        Submit(s);

        var typed = Bucket(s, "typed", "desktop");
        Assert.Equal(1, typed.Turns);
        Assert.Equal(11, typed.Characters);
    }

    [Fact]
    public void ThreeSubmissions_CountThreeTurns_EachCarryingItsOwnLine()
    {
        using var s = NewSession(new RecordingBackend());

        Type(s, "go"); Submit(s);          // 2 characters
        Type(s, "keep going"); Submit(s);  // 10
        Type(s, "merge it"); Submit(s);    // 8

        var typed = Bucket(s, "typed", "desktop");
        Assert.Equal(3, typed.Turns);
        Assert.Equal(20, typed.Characters);
    }

    [Fact]
    public void ALineRecalledFromHistory_SubmittedWithNoNewKeystrokes_IsStillOneTurn()
    {
        // The submission ledger counts it, so the tally must too, or the two disagree by exactly the
        // turns one of them dropped. Characters are honestly zero: nothing new was typed.
        using var s = NewSession(new RecordingBackend());

        Submit(s);

        var typed = Bucket(s, "typed", "desktop");
        Assert.Equal(1, typed.Turns);
        Assert.Equal(0, typed.Characters);
    }

    // ---- the controls: what must still NOT be counted ----

    [Fact]
    public void ComposingWithoutPressingEnter_CountsNothingAtAll()
    {
        // A bare keystroke is the user composing. The rule that made the defect look reasonable is
        // correct about THIS case and is unchanged.
        using var s = NewSession(new RecordingBackend());

        Type(s, "half a thought");

        Assert.True(s.InputStats.IsEmpty);
    }

    [Fact]
    public void RawBytesWithNoOrigin_CountNothing_EvenWithAnEnter()
    {
        // An agent's AppendEnter=false prompt reaches SendInput with no origin. It is not the person's
        // turn and it is not on the agent lane either - SendInput carries no SendSource to say so.
        using var s = NewSession(new RecordingBackend());

        s.SendInput(Bytes("a prompt from another agent\r"));

        Assert.True(s.InputStats.IsEmpty);
    }

    // ---- the structural guards: the two tallies cannot drift apart again ----

    [Fact]
    public async Task EverySubmissionTheSessionStamps_CarriesExactlyOneCountedTurn()
    {
        // THE INVARIANT. The submission ledger (OnTurnSubmitted) and the Your Throttle tally are the same
        // fact recorded twice; phase one found them 28 points apart. Drive a realistic mixture through
        // BOTH send paths and assert the two agree turn for turn.
        using var s = NewSession(new RecordingBackend());

        var stampedWithOrigin = 0;
        var stampedByAgent = 0;
        s.OnTurnSubmitted += (source, origin, _) =>
        {
            if (origin is not null) stampedWithOrigin++;
            else if (source == SendSource.Agent) stampedByAgent++;
        };

        Type(s, "fix all ten"); Submit(s);                                    // terminal typing
        Type(s, "now b1"); Submit(s);                                         // terminal typing
        Submit(s);                                                            // history recall, no new characters
        Type(s, "abandoned");                                                 // composing: NOT a submission
        await s.SendTextAsync("through the composer", origin: InputOrigin.DesktopTyped);
        await s.SendTextAsync("a dictated utterance", origin: InputOrigin.DesktopVoice);
        await s.SendTextAsync("a fleet message", SendSource.Agent);
        await s.SendTextAsync("/handover", SendSource.Framework);             // framework: nobody's turn
        s.SendInput(Bytes("an agent's raw prompt\r"));                        // no origin: nobody's turn

        var snapshot = s.InputStats.Snapshot();
        var countedHuman = snapshot.Buckets.Sum(b => b.Turns);

        Assert.Equal(5, stampedWithOrigin);
        Assert.Equal(countedHuman, stampedWithOrigin);
        Assert.Equal(1, stampedByAgent);
        Assert.Equal(stampedByAgent, snapshot.AgentDrivenTurns);
    }

    [Fact]
    public void TheTurnTally_HasExactlyOneCallSite_AndItIsTheMethodThatStampsTheSubmission()
    {
        // The defect was not a missing call - it was that "stamp the submission" and "count the turn"
        // were two separate writes a caller could do one of. This pins them to one write. If this test
        // is ever failing because a second call site was added, the fix is to route that caller through
        // StampSubmission, not to raise the expected count.
        var session = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CcDirector.Core", "Sessions", "Session.cs"));

        Assert.Single(Regex.Matches(session, @"InputStats\.RecordTurn\("));
        Assert.Single(Regex.Matches(session, @"InputStats\.RecordAgentTurn\("));

        // Both live inside StampSubmission, which is also the only place LastSubmissionAtUtc is stamped
        // and the only place OnTurnSubmitted is raised.
        var body = MethodBody(session, "private void StampSubmission(");
        Assert.Contains("InputStats.RecordTurn(o, characters);", body);
        Assert.Contains("InputStats.RecordAgentTurn(characters);", body);
        // The ledger observers are raised HERE and only here - each subscriber on its own, so one that
        // throws cannot keep the activity producer from hearing the turn (final inspection finding F-06).
        Assert.Contains("OnTurnSubmitted", body);
        Assert.Contains("GetInvocationList()", body);
        Assert.DoesNotContain("OnTurnSubmitted?.Invoke(", session);
        Assert.Single(Regex.Matches(session, @"OnTurnSubmitted\.GetInvocationList\(\)|observers\.GetInvocationList\(\)"));

        // And the method that recorded characters WITHOUT a turn - the only caller of which was terminal
        // typing, and the mechanism of the defect - is gone rather than merely unused.
        var stats = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CcDirector.Core", "Sessions", "SessionInputStats.cs"));
        Assert.DoesNotContain("public void RecordCharacters(", stats);
    }

    /// <summary>The source text of one method, from its signature to its matching closing brace.</summary>
    private static string MethodBody(string text, string signature)
    {
        var i = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, "Session.cs no longer contains '" + signature + "'.");
        var open = text.IndexOf('{', i);
        Assert.True(open >= 0, "'" + signature + "' has no body.");
        var depth = 0;
        for (var j = open; j < text.Length; j++)
        {
            if (text[j] == '{') depth++;
            else if (text[j] == '}' && --depth == 0) return text[i..(j + 1)];
        }

        Assert.Fail("'" + signature + "' has no matching closing brace.");
        return "";
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from " + AppContext.BaseDirectory);
    }
}
