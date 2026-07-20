#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace CcDirector.TestInfrastructure;

/// <summary>
/// Proves the mutation-proof pin guard CAN FIRE, not merely that it is present.
///
/// This is the point of the whole file, so it is worth being blunt about why. A guard that never fires
/// looks exactly like a guard that works: both are silent, and both let every run through. An instrument
/// that cannot observe its subject reports the subject's absence, and absence reads as success. So each
/// refusal below is paired with a CONTROL that must be admitted, because a guard which refused everything
/// would sail through the refusal tests on its own.
///
/// Four properties are pinned, and the last two are the controls:
///
///   1. A baseline on a tree carrying an uncommitted modification is REFUSED, and the refusal names the
///      file. The case is built by REPLAYING the event of 2026-07-19 against a real git repository: a
///      security guard block deleted from a source file and left uncommitted. The deletion leaves a
///      brace-balanced file, which is asserted here, because a detector that could only see a
///      syntactically broken tree would not have caught the real event and would be worthless.
///   2. A baseline on a tree whose head has moved off the pinned head is REFUSED, naming both heads.
///   3. CONTROL: a clean tree at the pinned head is ADMITTED.
///   4. CONTROL FOR SCOPE: a mid-rework run with NO pin is ADMITTED however dirty the tree is. This is what
///      proves the guard the Architect ruled for was built - gated to baselines and arms - rather than a
///      blanket refusal on any dirty tree. A blanket guard would be switched off within a day, and a guard
///      that gets switched off protects nothing.
///
/// The refusal cases and the admission cases are driven through the SAME repository, differing only by the
/// contamination under test, so nothing here can pass because the two arms were set up differently.
/// </summary>
public sealed class MutationProofPinGuardTests
{
    // -------------------------------------------------------------------------------------------------
    // The guard on the guard.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A module initializer that silently does not run leaves no trace whatsoever - the suite would go
    /// straight back to admitting contaminated baselines and every test here would still pass, because
    /// every test here drives the decision directly. So the fact that the mechanism actually executed in
    /// this process is asserted separately from the decision it makes.
    /// </summary>
    [Fact]
    public void TheGuardRanInThisProcess()
    {
        Assert.True(
            MutationProofPinGuard.HasRun,
            "The mutation-proof pin guard's module initializer did not run in this test process, so "
            + "nothing checked whether this run's working tree is the tree a proof pinned. See "
            + "MutationProofPinGuard.");

        Assert.NotEqual("(the guard has not run)", MutationProofPinGuard.LastVerdictSummary);
    }

    // -------------------------------------------------------------------------------------------------
    // 1. The event of 2026-07-19, replayed against a real git repository.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The source file as it is committed: a security guard block inside a request handler.
    /// </summary>
    private const string FileWithTheGuardBlock =
        """
        namespace Example;

        public static class RequestHandler
        {
            public static string Handle(string tenantId, string callerTenantId)
            {
                if (tenantId != callerTenantId)
                {
                    throw new UnauthorizedAccessException("Cross-tenant read refused.");
                }

                return Read(tenantId);
            }

            private static string Read(string tenantId) => tenantId;
        }
        """;

    /// <summary>
    /// The same file with the guard block deleted, which is precisely what a killed mutation-arm run leaves
    /// behind when it never gets to restore what it removed. It COMPILES. That is the whole danger: the
    /// baseline taken over this file is green, complete, and measures nothing.
    /// </summary>
    private const string FileWithTheGuardBlockDeleted =
        """
        namespace Example;

        public static class RequestHandler
        {
            public static string Handle(string tenantId, string callerTenantId)
            {
                return Read(tenantId);
            }

            private static string Read(string tenantId) => tenantId;
        }
        """;

    [Fact]
    public void ABaselineOverAnUncommittedlyDeletedGuardBlockIsRefused_AndTheRefusalNamesTheFile()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var pin = BaselinePinAt(repository.Head());

        // THE OTHER ARM, taken first and from the same repository: before the contamination, this exact
        // baseline is admitted. Without this, a guard that refused unconditionally would pass the assertion
        // below and nobody would learn anything.
        var beforeContamination = MutationProofPinGuard.Decide(pin, MutationProofPinGuard.ReadTree(repository.Root));
        Assert.True(
            beforeContamination.Admitted,
            "A clean tree at the pinned head must be admitted, or the refusal below proves nothing. "
            + beforeContamination.Message);

        // The event: the guard block is deleted and left uncommitted. Nothing else changes.
        repository.Write("src/RequestHandler.cs", FileWithTheGuardBlockDeleted);

        var afterContamination = MutationProofPinGuard.Decide(pin, MutationProofPinGuard.ReadTree(repository.Root));

        Assert.False(
            afterContamination.Admitted,
            "A baseline was admitted over a working tree in which a security guard block had been deleted "
            + "and left uncommitted. That baseline would look green and complete and would reconcile "
            + "perfectly against its own mutation arm while measuring nothing. " + afterContamination.Message);

        Assert.Contains("src/RequestHandler.cs", afterContamination.Message, StringComparison.Ordinal);
        Assert.Contains("NO TESTS WILL RUN", afterContamination.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The property that makes the case above a real reconstruction rather than a convenient one: the
    /// mutation leaves a file the compiler still accepts. If the only trees this guard could detect were
    /// syntactically broken ones, it would not have caught the event it was built for - the broken tree
    /// announces itself with a build failure and needs no guard at all.
    /// </summary>
    [Fact]
    public void TheReplayedMutationLeavesAFileThatStillCompiles_OtherwiseItWouldNotBeTheRealEvent()
    {
        Assert.NotEqual(FileWithTheGuardBlock, FileWithTheGuardBlockDeleted);

        Assert.Equal(
            FileWithTheGuardBlock.Count(c => c == '{'),
            FileWithTheGuardBlock.Count(c => c == '}'));

        Assert.Equal(
            FileWithTheGuardBlockDeleted.Count(c => c == '{'),
            FileWithTheGuardBlockDeleted.Count(c => c == '}'));

        // The security decision is gone; everything the compiler needs is still there.
        Assert.Contains("UnauthorizedAccessException", FileWithTheGuardBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("UnauthorizedAccessException", FileWithTheGuardBlockDeleted, StringComparison.Ordinal);
        Assert.Contains("return Read(tenantId);", FileWithTheGuardBlockDeleted, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // 2. The head has moved.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void ABaselineOnATreeWhoseHeadHasMovedIsRefused_AndNamesBothHeads()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var pinnedHead = repository.Head();
        var pin = BaselinePinAt(pinnedHead);

        // Clean tree, different commit - the arm this test exists to separate from a dirty tree. A baseline
        // and its mutation arm taken at different commits compare two different programs, and the
        // reconciliation arithmetic silently means nothing.
        repository.WriteAndCommit("src/Other.cs", "namespace Example; public static class Other { }", "unrelated work");
        var movedHead = repository.Head();

        Assert.NotEqual(pinnedHead, movedHead);
        Assert.Empty(MutationProofPinGuard.ReadTree(repository.Root).Changes);

        var verdict = MutationProofPinGuard.Decide(pin, MutationProofPinGuard.ReadTree(repository.Root));

        Assert.False(verdict.Admitted, verdict.Message);
        Assert.Contains(pinnedHead, verdict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(movedHead, verdict.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------------------------------
    // 3. CONTROL: the guard does not refuse everything.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void CONTROL_ACleanTreeAtThePinnedHeadIsAdmitted()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var verdict = MutationProofPinGuard.Decide(
            BaselinePinAt(repository.Head()),
            MutationProofPinGuard.ReadTree(repository.Root));

        Assert.True(
            verdict.Admitted,
            "A clean tree at the pinned head was refused. A guard that refuses everything would pass every "
            + "other test in this file while stopping all proof work, and would be removed within the day. "
            + verdict.Message);
    }

    // -------------------------------------------------------------------------------------------------
    // 4. CONTROL FOR SCOPE: an unpinned run is never this guard's business.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void CONTROL_AMidReworkRunWithNoPinIsAdmittedHoweverDirtyTheTreeIs()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        // As dirty as a working tree gets mid-rework: a modified file, a deleted file, and a new one.
        repository.Write("src/RequestHandler.cs", FileWithTheGuardBlockDeleted);
        repository.WriteAndCommit("src/Doomed.cs", "namespace Example; public static class Doomed { }", "add");
        repository.Delete("src/Doomed.cs");
        repository.Write("src/Scratch.cs", "namespace Example; public static class Scratch { }");

        var tree = MutationProofPinGuard.ReadTree(repository.Root);
        Assert.True(tree.Changes.Count >= 3, "the tree under test must actually be dirty for this control to mean anything");

        var noPin = new MutationProofPinGuard.PinReading(false, null, null);
        var verdict = MutationProofPinGuard.Decide(noPin, tree);

        Assert.True(
            verdict.Admitted,
            "A run with no mutation-proof pin was refused because the tree was dirty. That is a BLANKET "
            + "guard, which the Architect ruled against: a worker mid-rework is legitimately dirty, and a "
            + "guard that blocks ordinary work is switched off within a day, after which it protects "
            + "nothing. " + verdict.Message);
    }

    // -------------------------------------------------------------------------------------------------
    // The mutation arm. An arm is SUPPOSED to be dirty, so it is checked against a tighter rule than a
    // baseline: exactly the declared change, no more and no less.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void AnArmCarryingExactlyItsDeclaredMutationIsAdmitted()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");
        var pin = ArmPinAt(repository.Head(), "src/RequestHandler.cs");

        repository.Write("src/RequestHandler.cs", FileWithTheGuardBlockDeleted);

        var verdict = MutationProofPinGuard.Decide(pin, MutationProofPinGuard.ReadTree(repository.Root));

        Assert.True(
            verdict.Admitted,
            "A mutation arm carrying exactly the mutation it declared was refused, which would make the "
            + "guard impossible to use for the arm half of every proof. " + verdict.Message);
    }

    [Fact]
    public void AnArmCarryingAnUndeclaredChangeAsWellIsRefused_AndNamesOnlyTheUndeclaredFile()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");
        repository.WriteAndCommit("src/OtherGuard.cs", FileWithTheGuardBlock.Replace("RequestHandler", "OtherGuard"), "add another guard");

        var pin = ArmPinAt(repository.Head(), "src/RequestHandler.cs");

        repository.Write("src/RequestHandler.cs", FileWithTheGuardBlockDeleted);

        // The contaminated-arm case: the intended mutation PLUS a leftover somebody else's killed run left
        // behind. The counts would still reconcile, because both runs carry the leftover.
        repository.Write("src/OtherGuard.cs", FileWithTheGuardBlockDeleted.Replace("RequestHandler", "OtherGuard"));

        var verdict = MutationProofPinGuard.Decide(pin, MutationProofPinGuard.ReadTree(repository.Root));

        Assert.False(verdict.Admitted, verdict.Message);
        Assert.Contains("src/OtherGuard.cs", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("THIS PROOF DID NOT DECLARE", verdict.Message, StringComparison.Ordinal);

        // Only the undeclared one. Naming the declared mutation here would train a worker to ignore the
        // list, which is the same as not printing it.
        Assert.DoesNotContain("    src/RequestHandler.cs", verdict.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mirror image, and the one most likely to be dismissed as unnecessary: an arm run on a tree where
    /// the mutation was never applied, or was already restored, is a SECOND BASELINE wearing an arm's name.
    /// It reconciles perfectly against the first baseline - passed plus zero failed equals passed - and the
    /// proof reads as a clean pass.
    /// </summary>
    [Fact]
    public void AnArmWhoseDeclaredMutationIsAbsentIsRefused_BecauseItIsASecondBaselineInDisguise()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var pin = ArmPinAt(repository.Head(), "src/RequestHandler.cs");

        // No mutation applied at all.
        var verdict = MutationProofPinGuard.Decide(pin, MutationProofPinGuard.ReadTree(repository.Root));

        Assert.False(verdict.Admitted, verdict.Message);
        Assert.Contains("src/RequestHandler.cs", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("SECOND BASELINE", verdict.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // A pin that cannot be understood must REFUSE, never quietly become "no pin".
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("pinnedHead=0123456789abcdef0123456789abcdef01234567", "declares no phase")]
    [InlineData("phase=baseline", "not a full forty-character commit id")]
    [InlineData("phase=warmup\npinnedHead=0123456789abcdef0123456789abcdef01234567", "is neither")]
    [InlineData("phase=arm\npinnedHead=0123456789abcdef0123456789abcdef01234567", "declares no mutation")]
    [InlineData("phase=baseline\npinnedHead=0123456789abcdef0123456789abcdef01234567\nmutates=src/A.cs", "yet declares mutations")]
    [InlineData("phase=baseline\npinnedHead=0123456789abcdef0123456789abcdef01234567\nmutate=src/A.cs", "is not one this guard understands")]
    public void APinThatCannotBeUnderstoodRefuses_ItDoesNotDegradeIntoNoPin(string text, string expectedDetail)
    {
        var reading = MutationProofPinGuard.ParsePin(text, "C:/pins/example.pin");

        Assert.True(reading.Found, "a pin file that exists must never be reported as absent");
        Assert.Null(reading.Pin);
        Assert.NotNull(reading.Problem);
        Assert.Contains(expectedDetail, reading.Problem!, StringComparison.Ordinal);

        var verdict = MutationProofPinGuard.Decide(
            reading,
            new MutationProofPinGuard.TreeReading(true, "0123456789abcdef0123456789abcdef01234567", Array.Empty<MutationProofPinGuard.ChangedPath>(), null));

        Assert.False(
            verdict.Admitted,
            "A pin file that exists but cannot be read was treated as no pin at all, which disarms the "
            + "guard for exactly the proof that most needs it. " + verdict.Message);
    }

    /// <summary>
    /// A typo of "mutates" is worth its own case because it is the failure that would look like the
    /// guard working: the arm would be refused for carrying an undeclared change, the worker would edit the
    /// pin, and nobody would learn that the pin format had silently dropped a key.
    /// </summary>
    [Fact]
    public void AGoodPinParsesEverythingItWasGiven()
    {
        var reading = MutationProofPinGuard.ParsePin(
            "# a comment\n"
            + "proofId=7f3c\n"
            + "phase=arm\n"
            + "pinnedHead=0123456789ABCDEF0123456789abcdef01234567\n"
            + "pinnedUtc=2026-07-20T00:00:00.0000000Z\n"
            + "tree=D:/ReposFred/_wt/example\n"
            + "mutates=src/A.cs\n"
            + "mutates=src/B.cs\n"
            + "note=removing the tenant comparison\n",
            "C:/pins/example.pin");

        Assert.Null(reading.Problem);
        Assert.NotNull(reading.Pin);
        Assert.Equal("7f3c", reading.Pin!.ProofId);
        Assert.Equal(MutationProofPinGuard.ArmPhase, reading.Pin.Phase);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", reading.Pin.PinnedHead);
        Assert.Equal(new[] { "src/A.cs", "src/B.cs" }, reading.Pin.DeclaredMutations);
        Assert.Equal("removing the tenant comparison", reading.Pin.Note);
    }

    // -------------------------------------------------------------------------------------------------
    // A pinned run that cannot read its own tree must stop, not proceed.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void APinnedRunThatCannotReadTheTreeIsRefused()
    {
        var pin = BaselinePinAt("0123456789abcdef0123456789abcdef01234567");
        var unreadable = new MutationProofPinGuard.TreeReading(
            false, "", Array.Empty<MutationProofPinGuard.ChangedPath>(), "git is not on the path");

        var verdict = MutationProofPinGuard.Decide(pin, unreadable);

        Assert.False(verdict.Admitted, verdict.Message);
        Assert.Contains("git is not on the path", verdict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CONTROL_AnUnpinnedRunThatCannotReadTheTreeIsStillAdmitted()
    {
        var noPin = new MutationProofPinGuard.PinReading(false, null, null);
        var unreadable = new MutationProofPinGuard.TreeReading(
            false, "", Array.Empty<MutationProofPinGuard.ChangedPath>(), "git is not on the path");

        Assert.True(
            MutationProofPinGuard.Decide(noPin, unreadable).Admitted,
            "An ordinary run was blocked because git could not be invoked. The guard must be inert for "
            + "unpinned runs even when its own inputs are unavailable.");
    }

    // -------------------------------------------------------------------------------------------------
    // Parsing what git actually emits.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A rename entry carries a SECOND path in its own field. Consuming it is not cosmetic: miss it and
    /// every subsequent entry is shifted by one, so the guard names the wrong file in its refusal - which
    /// is worse than not refusing, because somebody acts on the name.
    /// </summary>
    [Fact]
    public void RenameEntriesDoNotShiftEverySubsequentPath()
    {
        var output = "R  src/New.cs\0src/Old.cs\0 M src/Changed.cs\0?? src/Untracked.cs\0";

        var changes = MutationProofPinGuard.ParsePorcelainZ(output);

        Assert.Equal(
            new[] { "src/New.cs", "src/Changed.cs", "src/Untracked.cs" },
            changes.Select(c => c.Path));
    }

    [Fact]
    public void PathsWithSpacesSurviveParsing()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/a file with spaces.cs", "namespace Example; public static class A { }", "add");
        repository.Write("src/a file with spaces.cs", "namespace Example; public static class A { public const int X = 1; }");

        var tree = MutationProofPinGuard.ReadTree(repository.Root);

        Assert.Contains("src/a file with spaces.cs", tree.Changes.Select(c => c.Path));
    }

    // -------------------------------------------------------------------------------------------------
    // Where the pin lives.
    // -------------------------------------------------------------------------------------------------

    // -------------------------------------------------------------------------------------------------
    // IS THE GUARD ACTUALLY ARMED ON THE PLATFORM IT IS RUNNING ON?
    //
    // A reviewer found that it was not, on Linux and macOS. The pin's location was DERIVED twice - once in
    // PowerShell to write it, once in C# to read it - and the two derivations disagreed off Windows. The
    // script printed "PINNED" while the guard looked somewhere else, found nothing, and admitted
    // contaminated baselines. Armed according to its own output, inert in fact. Continuous integration runs
    // on Linux, so the platform where it was dead is the platform nobody watches.
    //
    // These run on WHATEVER PLATFORM executes them, so the Linux answer is checked by the Linux run rather
    // than reasoned about from Windows.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The end-to-end arming check: a pin placed where the SCRIPT puts it must be found by the GUARD, here,
    /// on this operating system.
    ///
    /// The script's location is reproduced the way the script gets it - by asking git, in this process, in
    /// a real repository - rather than by restating a path. That is the whole repair: there is no longer a
    /// derivation on either side to disagree about, only one question with one answer.
    /// </summary>
    [Fact]
    public void TheGuardFindsAPinWrittenWhereTheScriptWritesIt_OnThisPlatform()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        // Exactly what scripts/mutation-proof-pin.ps1 does to find the pin's home.
        var asTheScriptResolvesIt = Path.Combine(
            repository.Git("rev-parse --absolute-git-dir").Trim(),
            MutationProofPinGuard.PinFileName);

        var asTheGuardResolvesIt = MutationProofPinGuard.ResolvePinFilePath(repository.Root);

        Assert.NotNull(asTheGuardResolvesIt);
        Assert.Equal(
            Path.GetFullPath(asTheScriptResolvesIt),
            Path.GetFullPath(asTheGuardResolvesIt!));

        // And a pin written there is genuinely readable as a pin, rather than merely landing at a matching
        // path. A path that agrees but content that does not parse would be the same silence.
        File.WriteAllText(
            asTheScriptResolvesIt,
            "proofId=abc123\nphase=baseline\npinnedHead=" + repository.Head() + "\n");

        var reading = MutationProofPinGuard.ParsePin(
            File.ReadAllText(asTheGuardResolvesIt!), asTheGuardResolvesIt!);

        Assert.Null(reading.Problem);
        Assert.Equal("abc123", reading.Pin!.ProofId);
    }

    /// <summary>
    /// The pin must not sit anywhere "git status" can see, or the guard would trip over its own
    /// declaration and need an exemption - and an exemption is a hole.
    /// </summary>
    [Fact]
    public void ThePinDoesNotDirtyTheTreeItGuards()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        File.WriteAllText(
            MutationProofPinGuard.ResolvePinFilePath(repository.Root)!,
            "proofId=abc123\nphase=baseline\npinnedHead=" + repository.Head() + "\n");

        Assert.Empty(MutationProofPinGuard.ReadTree(repository.Root).Changes);
    }

    /// <summary>
    /// Two worktrees of one repository - how every mission here runs - must hold independent pins. A shared
    /// pin would mean one worker's proof declaration silently governing another worker's tree. This is now
    /// a property of git's own answer rather than of a hash we maintain, so it is checked against a REAL
    /// second worktree.
    /// </summary>
    [Fact]
    public void TwoWorktreesOfOneRepositoryGetDifferentPins()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var secondRoot = Path.Combine(repository.ScratchPath, "second-worktree");
        repository.Git("worktree add \"" + secondRoot + "\" -b second");

        var first = MutationProofPinGuard.ResolvePinFilePath(repository.Root);
        var second = MutationProofPinGuard.ResolvePinFilePath(secondRoot);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(Path.GetFullPath(first!), Path.GetFullPath(second!));
    }

    /// <summary>
    /// The one string the script and the guard still have to agree on, checked by reading the script's own
    /// text. The previous version froze a golden file NAME while the two sides disagreed about the
    /// DIRECTORY, which is precisely the gap the reviewer walked through - so this asserts the location
    /// primitive as well, not just the name.
    /// </summary>
    [Fact]
    public void TheScriptAndTheGuardAgreeOnThePinFileName()
    {
        var script = ReadPinScript();
        if (script is null)
            return; // Not run from a checkout that carries the script; the arming test above still applies.

        Assert.Contains(
            "$script:PinFileName = '" + MutationProofPinGuard.PinFileName + "'",
            script,
            StringComparison.Ordinal);

        // Both sides must locate the pin by asking git the same question. If either ever goes back to
        // deriving a path, this is the line that should stop it.
        Assert.Contains("rev-parse --absolute-git-dir", script, StringComparison.Ordinal);
    }

    private static string? ReadPinScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "mutation-proof-pin.ps1");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        return null;
    }

    [Fact]
    public void WithNoLocalApplicationDataFolder_TheLedgerRefusesRatherThanFallingBack()
    {
        Assert.Throws<InvalidOperationException>(
            () => MutationProofPinGuard.ComputeLedgerDirectory(""));
    }

    // -------------------------------------------------------------------------------------------------
    // THE PROPERTY THAT JUSTIFIES THE WHOLE DESIGN: THE PINNED HEAD DOES NOT MOVE.
    //
    // Nothing tested this in the first round, and the reason is worth keeping: the pin being fixed is the
    // PREMISE, so no test was aimed at it. A reviewer then found the supported workflow broke it - every
    // "set", including the baseline-to-arm transition, recomputed HEAD and overwrote the pin, so a head
    // that moved between the two runs was silently re-pinned and the arm ADMITTED. The documented happy
    // path walked into the exact event the guard exists to refuse.
    //
    // The script is fixed. These pin the SECOND mechanism, which holds however the pin file came to say
    // what it says: a hand edit, a copied file, a future script change, a tool nobody has written yet.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void AProofWhoseHeadMovedSinceAnEarlierRunOfTheSameProofIsRefused()
    {
        const string headTheBaselineMeasured = "1111111111111111111111111111111111111111";
        const string headThePinNowClaims = "2222222222222222222222222222222222222222";

        var priorBaselineRun = LedgerLine("proof-42", headTheBaselineMeasured);

        // The arm, at a tree that exactly matches the RE-PINNED head - so every other check in this guard
        // passes. Only the proof's own history shows that its foundation moved.
        var pin = ArmPinAt(headThePinNowClaims, "src/RequestHandler.cs");
        var tree = new MutationProofPinGuard.TreeReading(
            true,
            headThePinNowClaims,
            new[] { new MutationProofPinGuard.ChangedPath(" M", "src/RequestHandler.cs") },
            null);

        var verdict = MutationProofPinGuard.Decide(pin, tree, new[] { priorBaselineRun });

        Assert.False(
            verdict.Admitted,
            "A proof was admitted whose pinned head had moved since its own baseline ran. Every other check "
            + "passes in this state - the tree matches the new pin exactly - so this is the only thing "
            + "standing between a silently re-pinned proof and a clean-looking result. " + verdict.Message);

        Assert.Contains(headTheBaselineMeasured, verdict.Message, StringComparison.Ordinal);
        Assert.Contains(headThePinNowClaims, verdict.Message, StringComparison.Ordinal);
        Assert.Contains("PINNED HEAD HAS MOVED", verdict.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// CONTROL. The same proof, run again at the same head, must be admitted - otherwise the check above
    /// would pass by refusing every second run of every proof, and the mechanism would be discarded.
    /// </summary>
    [Fact]
    public void CONTROL_AProofRunAgainAtItsOwnPinnedHeadIsAdmitted()
    {
        const string head = "1111111111111111111111111111111111111111";

        var verdict = MutationProofPinGuard.Decide(
            ArmPinAt(head, "src/RequestHandler.cs"),
            new MutationProofPinGuard.TreeReading(
                true, head,
                new[] { new MutationProofPinGuard.ChangedPath(" M", "src/RequestHandler.cs") }, null),
            new[] { LedgerLine("proof-42", head), LedgerLine("proof-42", head) });

        Assert.True(verdict.Admitted, verdict.Message);
    }

    /// <summary>
    /// CONTROL. A DIFFERENT proof at a different head is somebody else's business. Without this, the check
    /// could pass by refusing any run that followed any earlier proof, which would stop the second proof
    /// ever run on a machine.
    /// </summary>
    [Fact]
    public void CONTROL_AnEarlierUnRELATEDProofAtAnotherHeadDoesNotRefuseThisOne()
    {
        const string head = "1111111111111111111111111111111111111111";

        var verdict = MutationProofPinGuard.Decide(
            BaselinePinAt(head),
            new MutationProofPinGuard.TreeReading(
                true, head, Array.Empty<MutationProofPinGuard.ChangedPath>(), null),
            new[]
            {
                LedgerLine("some-other-proof", "9999999999999999999999999999999999999999"),
                LedgerLine("another-proof", "8888888888888888888888888888888888888888"),
            });

        Assert.True(verdict.Admitted, verdict.Message);
    }

    /// <summary>
    /// A pin with no proof identity cannot be checked against its own history at all, so it is refused
    /// outright rather than admitted with the moved-head check quietly skipped. Minting an identity here
    /// instead would be worse than doing nothing: every run would look like the first run of a brand-new
    /// proof, and the mechanism would report itself in force while never firing.
    /// </summary>
    [Fact]
    public void APinWithNoProofIdentityIsRefused_RatherThanCheckedAgainstNothing()
    {
        var reading = MutationProofPinGuard.ParsePin(
            "phase=baseline\npinnedHead=1111111111111111111111111111111111111111\n",
            "C:/pins/example.pin");

        Assert.NotNull(reading.Problem);
        Assert.Contains("declares no proofId", reading.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A prior run that names this proof but whose head cannot be read is treated as a MISMATCH. Skipping
    /// it would mean the check is skipped on exactly the input that is already wrong.
    /// </summary>
    [Fact]
    public void AProofsHistoryThatCannotBeReadIsTreatedAsAMismatch_NotAsAgreement()
    {
        var problem = MutationProofPinGuard.DetectMovedPin(
            new MutationProofPinGuard.ProofPin(
                "proof-42", MutationProofPinGuard.BaselinePhase,
                "1111111111111111111111111111111111111111", "when",
                Array.Empty<string>(), "", "C:/pins/example.pin"),
            new[] { "when=2026-07-20T00:00:00Z  verdict=admitted  proofId=proof-42  phase=baseline" });

        Assert.NotNull(problem);
    }

    // -------------------------------------------------------------------------------------------------
    // THE PROOFS THAT WERE ONCE DRIVEN BY HAND.
    //
    // The two refusals below were first demonstrated as a sequence typed at a terminal on one machine on
    // one night. That proves the mechanism worked that day and protects nothing afterwards: a script, a
    // path or a parser can regress, and hand-run evidence - being prose in a pull request - cannot notice.
    // Worse, the hand run happened on Windows only, which is the same blind spot that produced the
    // platform divergence a reviewer had to find: I could only ever see the arm that worked.
    //
    // These drive the REAL pin file, the REAL working tree and the REAL ledger, so they re-run on every
    // push. See the pull request for what this does and does not close about the platform question.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The whole ledger-memory refusal, on disk, with no hand-typed steps.
    ///
    /// This is the case the script fix alone cannot catch, and therefore the one worth mechanising most: a
    /// pin that names the wrong head, where the working tree matches that wrong head EXACTLY. Every other
    /// check in the guard passes. Only the proof's own history says the foundation moved.
    /// </summary>
    [Fact]
    public void APinReWrittenToANewHeadIsRefusedFromDisk_EvenThoughTheTreeMatchesItExactly()
    {
        using var repository = TemporaryGitRepository.Create();
        using var ledger = new TemporaryDirectory();

        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");
        var headTheBaselineMeasured = repository.Head();

        var pinPath = MutationProofPinGuard.ResolvePinFilePath(repository.Root)!;
        WritePin(pinPath, "proof-99", MutationProofPinGuard.BaselinePhase, headTheBaselineMeasured);

        // The baseline runs and is admitted, and the ledger remembers the head it measured.
        var baseline = MutationProofPinGuard.Evaluate(
            repository.Root, MutationProofPinGuard.ReadLedger(ledger.Path));

        Assert.True(baseline.Admitted, "the baseline itself must be admitted. " + baseline.Message);
        AppendLedger(ledger.Path, LedgerLine("proof-99", headTheBaselineMeasured));

        // The head moves, and the pin is REWRITTEN to the new head - what the old script did by itself,
        // and what a hand edit or a future script regression would do again.
        repository.WriteAndCommit("src/Other.cs", "namespace Example; public static class Other { }", "later work");
        var headThePinNowClaims = repository.Head();
        Assert.NotEqual(headTheBaselineMeasured, headThePinNowClaims);

        WritePin(pinPath, "proof-99", MutationProofPinGuard.BaselinePhase, headThePinNowClaims);

        // The tree is clean and sits exactly on the re-pinned head, so the head comparison AGREES.
        var tree = MutationProofPinGuard.ReadTree(repository.Root);
        Assert.Empty(tree.Changes);
        Assert.Equal(headThePinNowClaims, tree.Head);

        var verdict = MutationProofPinGuard.Evaluate(
            repository.Root, MutationProofPinGuard.ReadLedger(ledger.Path));

        Assert.False(
            verdict.Admitted,
            "A proof was admitted after its pinned head was rewritten to a later commit. The tree matches "
            + "the new pin exactly, so every other check in this guard passes - the ledger's memory of the "
            + "baseline is the only thing that can catch it. " + verdict.Message);

        Assert.Contains(headTheBaselineMeasured, verdict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(headThePinNowClaims, verdict.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// CONTROL for the test above, and it is not optional: without it, a guard that refused every run whose
    /// ledger was non-empty would pass. Same repository, same ledger, same proof - the pin simply is not
    /// rewritten.
    /// </summary>
    [Fact]
    public void CONTROL_AProofWhosePinWasNotRewrittenIsAdmittedFromDisk()
    {
        using var repository = TemporaryGitRepository.Create();
        using var ledger = new TemporaryDirectory();

        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");
        var head = repository.Head();

        WritePin(
            MutationProofPinGuard.ResolvePinFilePath(repository.Root)!,
            "proof-99", MutationProofPinGuard.BaselinePhase, head);

        AppendLedger(ledger.Path, LedgerLine("proof-99", head));

        var verdict = MutationProofPinGuard.Evaluate(
            repository.Root, MutationProofPinGuard.ReadLedger(ledger.Path));

        Assert.True(verdict.Admitted, verdict.Message);
    }

    /// <summary>
    /// The arm transition, driven through the REAL script.
    ///
    /// This is the bypass a reviewer found: "set -Phase arm" recomputed the head, so a head that moved
    /// between the baseline and the arm was silently re-pinned and the arm admitted. Asserting the script's
    /// behaviour by reading its source would test my belief about PowerShell; running it tests PowerShell.
    ///
    /// Two things are asserted, and the second matters more: the transition is REFUSED, and the pin file
    /// still names the BASELINE's head afterwards. A refusal that had already overwritten the pin would
    /// leave the next run to be measured against the wrong commit.
    /// </summary>
    [Fact]
    public void TheScriptRefusesAnArmTransitionAfterTheHeadMoves_AndLeavesThePinOnTheBaselineHead()
    {
        var shell = RequirePowerShell();
        var script = RequirePinScript();

        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var baselineHead = repository.Head();

        var pinned = RunScript(shell, script, repository.Root, "set -Phase baseline -Note \"transition test\"");
        Assert.True(pinned.ExitCode == 0, "pinning the baseline failed: " + pinned.Output + pinned.Error);

        // The head moves between the baseline run and the arm - the whole condition under test.
        repository.WriteAndCommit("src/Other.cs", "namespace Example; public static class Other { }", "later work");
        var movedHead = repository.Head();
        Assert.NotEqual(baselineHead, movedHead);

        var arm = RunScript(
            shell, script, repository.Root, "set -Phase arm -Mutates src/RequestHandler.cs");

        Assert.False(
            arm.ExitCode == 0,
            "The script accepted an arm transition after the head had moved. That silently re-pins the "
            + "proof to the new head, and the guard then finds an exact match and admits an arm that "
            + "measured a different program from its own baseline. Output: " + arm.Output + arm.Error);

        Assert.Contains("the head has moved", arm.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(baselineHead, arm.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(movedHead, arm.Output, StringComparison.OrdinalIgnoreCase);

        // And the pin was NOT quietly updated on the way out.
        var pinText = File.ReadAllText(MutationProofPinGuard.ResolvePinFilePath(repository.Root)!);
        Assert.Contains("pinnedHead=" + baselineHead, pinText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pinnedHead=" + movedHead, pinText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// CONTROL. The same transition, with the head left alone, must SUCCEED - otherwise the refusal above
    /// would be satisfied by a script that refuses every arm, which would make the tool unusable for the
    /// arm half of every proof and would be discovered only by whoever tried to run one.
    /// </summary>
    [Fact]
    public void CONTROL_TheScriptAcceptsAnArmTransitionAtTheBaselineHead()
    {
        var shell = RequirePowerShell();
        var script = RequirePinScript();

        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");
        var head = repository.Head();

        RunScript(shell, script, repository.Root, "set -Phase baseline");

        // Apply the mutation, exactly as a real arm would, then declare it.
        repository.Write("src/RequestHandler.cs", FileWithTheGuardBlockDeleted);

        var arm = RunScript(shell, script, repository.Root, "set -Phase arm -Mutates src/RequestHandler.cs");

        Assert.True(
            arm.ExitCode == 0,
            "A legitimate arm transition at the pinned head was refused. Output: " + arm.Output + arm.Error);

        var pinText = File.ReadAllText(MutationProofPinGuard.ResolvePinFilePath(repository.Root)!);
        Assert.Contains("phase=arm", pinText, StringComparison.Ordinal);
        Assert.Contains("pinnedHead=" + head, pinText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mutates=src/RequestHandler.cs", pinText, StringComparison.Ordinal);

        // And the guard admits the run that follows, which is the point of the whole transition.
        using var ledger = new TemporaryDirectory();
        var verdict = MutationProofPinGuard.Evaluate(
            repository.Root, MutationProofPinGuard.ReadLedger(ledger.Path));

        Assert.True(verdict.Admitted, verdict.Message);
    }

    /// <summary>
    /// The arming check, driven through the real script rather than by reproducing its algorithm: a pin the
    /// SCRIPT wrote must be found and understood by the GUARD. This is the property that was false off
    /// Windows, and running the actual writer is the only way to test the actual writer.
    /// </summary>
    [Fact]
    public void APinWrittenByTheRealScriptIsFoundAndUnderstoodByTheGuard()
    {
        var shell = RequirePowerShell();
        var script = RequirePinScript();

        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var result = RunScript(shell, script, repository.Root, "set -Phase baseline -Note \"arming check\"");
        Assert.True(result.ExitCode == 0, result.Output + result.Error);

        var reading = MutationProofPinGuard.ReadPinFor(repository.Root);

        Assert.True(
            reading.Found,
            "The script reported success but the guard cannot find the pin it wrote. That is the exact "
            + "shape of the platform divergence a reviewer found: armed according to its own output, inert "
            + "in fact.");
        Assert.Null(reading.Problem);
        Assert.Equal(repository.Head(), reading.Pin!.PinnedHead, ignoreCase: true);
    }

    private static void WritePin(string path, string proofId, string phase, string head, params string[] mutations)
    {
        var text = "proofId=" + proofId + "\nphase=" + phase + "\npinnedHead=" + head + "\n"
            + string.Concat(mutations.Select(m => "mutates=" + m + "\n"));
        File.WriteAllText(path, text);
    }

    private static void AppendLedger(string ledgerDirectory, string line) =>
        File.AppendAllText(
            Path.Combine(ledgerDirectory, MutationProofPinGuard.ProofLedgerFileName),
            line + Environment.NewLine);

    /// <summary>
    /// The script host, or a LOUD FAILURE.
    ///
    /// Deliberately not a silent skip. A skipped test is invisible in a summary line and looks exactly like
    /// a test that ran - which is the failure mode this entire unit exists to end, reproduced in its own
    /// test suite. The tests run on Windows, which always has a PowerShell host, and the hosted Linux
    /// images carry pwsh; a machine with neither cannot check the writer at all, and should be told so
    /// rather than quietly reassured.
    /// </summary>
    private static string RequirePowerShell() =>
        FindPowerShell()
        ?? throw new InvalidOperationException(
            "No PowerShell host (pwsh or powershell) could be executed on this machine, so the pin script "
            + "cannot be run and the writer half of this mechanism cannot be checked here. This is reported "
            + "as a failure rather than skipped on purpose: a silent skip would leave the suite looking "
            + "green while the script that WRITES pins went unchecked.");

    private static string RequirePinScript() =>
        FindPinScriptPath()
        ?? throw new InvalidOperationException(
            "scripts/mutation-proof-pin.ps1 was not found above " + AppContext.BaseDirectory
            + ", so the writer half of this mechanism cannot be checked. Reported rather than skipped for "
            + "the same reason as the missing-host case above.");

    private static string? FindPinScriptPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "mutation-proof-pin.ps1");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// pwsh first, because it is the cross-platform host and is present on the hosted Linux images;
    /// powershell second, because Windows always has it. Resolved by running it, not by probing a path -
    /// a name on the path that cannot execute is not a host.
    /// </summary>
    private static string? FindPowerShell()
    {
        foreach (var candidate in new[] { "pwsh", "powershell" })
        {
            try
            {
                using var probe = new Process();
                probe.StartInfo = new ProcessStartInfo(candidate)
                {
                    Arguments = "-NoProfile -Command \"exit 0\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                probe.Start();
                probe.StandardOutput.ReadToEnd();
                probe.StandardError.ReadToEnd();

                if (probe.WaitForExit(30_000) && probe.ExitCode == 0)
                    return candidate;
            }
            catch (Exception)
            {
                // Not present on this machine; try the next.
            }
        }

        return null;
    }

    private readonly record struct ScriptResult(int ExitCode, string Output, string Error);

    private static ScriptResult RunScript(string shell, string scriptPath, string workingDirectory, string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(shell)
        {
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" " + arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);

        return new ScriptResult(process.ExitCode, output, error);
    }

    private static string LedgerLine(string proofId, string pinnedHead) =>
        "when=2026-07-20T00:00:00.0000000Z  verdict=admitted  proofId=" + proofId
        + "  phase=baseline  pinnedHead=" + pinnedHead + "  observedHead=" + pinnedHead
        + "  headVerified=yes  tree=D:/ReposFred/_wt/w-example";

    [Fact]
    public void LedgerFieldsAreReadableEvenWhenAValueContainsSpaces()
    {
        const string line = "when=2026-07-20T00:00:00Z  proofId=proof-42  observedChanges= M src/a file.cs  tree=D:/x";

        Assert.Equal("proof-42", MutationProofPinGuard.ReadLedgerField(line, "proofId"));
        Assert.Equal("M src/a file.cs", MutationProofPinGuard.ReadLedgerField(line, "observedChanges"));
        Assert.Null(MutationProofPinGuard.ReadLedgerField(line, "absent"));
    }

    // -------------------------------------------------------------------------------------------------
    // Finding the working tree root.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// In a git worktree - how every mission in this repository is run - ".git" is a FILE, not a directory.
    /// A root-finder that only accepted a directory would find nothing in exactly the trees this guard
    /// exists to protect, and would then admit every proof run silently.
    /// </summary>
    [Fact]
    public void TheWorkingTreeRootIsFoundWhenDotGitIsAFile()
    {
        using var scratch = new TemporaryDirectory();
        var root = Path.Combine(scratch.Path, "tree");
        var deep = Path.Combine(root, "src", "Project", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../.git/worktrees/tree");

        Assert.Equal(root, MutationProofPinGuard.FindWorkingTreeRoot(deep));
    }

    [Fact]
    public void OutsideAnyRepositoryThereIsNoWorkingTreeRoot()
    {
        using var scratch = new TemporaryDirectory();
        var deep = Path.Combine(scratch.Path, "a", "b");
        Directory.CreateDirectory(deep);

        // A temporary directory is not inside this repository, so the walk reaches the drive root and
        // stops. If some ancestor of the temporary directory ever is a repository this would find it, so
        // the assertion is on the shape of the answer rather than on a specific null.
        var found = MutationProofPinGuard.FindWorkingTreeRoot(deep);
        Assert.True(
            found is null || !found.StartsWith(scratch.Path, StringComparison.OrdinalIgnoreCase),
            "no repository was created under the scratch directory, so none may be reported from inside it");
    }

    // -------------------------------------------------------------------------------------------------
    // The ledger: the tree's state at run time, recorded as a fact that outlives the tree.
    //
    // Four already-merged security proofs cannot now be shown to have been taken on clean trees, because
    // their worktrees were removed after merging and that removal destroyed the only evidence. These pin
    // the properties that stop the fifth one joining them.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// An ADMITTED baseline must leave a POSITIVE statement behind - which head was verified, and that the
    /// tree matched it. This is the property the Manager's finding turns on: a ledger that only writes on
    /// refusal cannot afterwards distinguish "verified clean" from "the guard never ran", because both are
    /// silence, and silence reads as success.
    /// </summary>
    [Fact]
    public void AnAdmittedBaselineIsRecordedAsAPositiveVerifiedFact_NotAsAnAbsenceOfComplaint()
    {
        const string head = "0123456789abcdef0123456789abcdef01234567";

        var pin = BaselinePinAt(head);
        var tree = new MutationProofPinGuard.TreeReading(
            true, head, Array.Empty<MutationProofPinGuard.ChangedPath>(), null);
        var verdict = MutationProofPinGuard.Decide(pin, tree);

        Assert.True(verdict.Admitted, verdict.Message);

        var line = MutationProofPinGuard.FormatRunRecord(new MutationProofPinGuard.RunRecord(
            "2026-07-20T01:02:03.0000000Z", 4242, "net10.0", "D:/ReposFred/_wt/w-example",
            "cj_example", pin, tree, verdict));

        Assert.Contains("verdict=admitted", line, StringComparison.Ordinal);
        Assert.Contains("headVerified=yes", line, StringComparison.Ordinal);
        Assert.Contains("pinnedHead=" + head, line, StringComparison.Ordinal);
        Assert.Contains("VERIFIED", line, StringComparison.Ordinal);
        Assert.Contains("MATCHED it", line, StringComparison.Ordinal);

        // The identity that ties the record to the run it admitted, so the record still means something
        // once the tree it describes has been removed.
        Assert.Contains("tree=D:/ReposFred/_wt/w-example", line, StringComparison.Ordinal);
        Assert.Contains("session=cj_example", line, StringComparison.Ordinal);
        Assert.Contains("pid=4242", line, StringComparison.Ordinal);

        // One line per run: a partially written record from an interrupted process must not be able to
        // corrupt its neighbours, and the file must stay greppable.
        Assert.DoesNotContain('\n', line);
    }

    /// <summary>
    /// The refusal arm of the same record, so the ledger distinguishes the three outcomes rather than two:
    /// verified, refused, and never checked. A refused run's line must say that no tests ran, because
    /// somebody reading a proof's numbers later needs to know they did not come from this process.
    /// </summary>
    [Fact]
    public void ARefusedRunIsRecordedAsRefused_AndSaysItsNumbersAreNotItsOwn()
    {
        const string pinnedHead = "0123456789abcdef0123456789abcdef01234567";

        var pin = BaselinePinAt(pinnedHead);
        var tree = new MutationProofPinGuard.TreeReading(
            true,
            pinnedHead,
            new[] { new MutationProofPinGuard.ChangedPath(" M", "src/RequestHandler.cs") },
            null);
        var verdict = MutationProofPinGuard.Decide(pin, tree);

        Assert.False(verdict.Admitted, verdict.Message);

        var line = MutationProofPinGuard.FormatRunRecord(new MutationProofPinGuard.RunRecord(
            "2026-07-20T01:02:03.0000000Z", 4242, "net10.0", "D:/ReposFred/_wt/w-example",
            "cj_example", pin, tree, verdict));

        Assert.Contains("verdict=refused", line, StringComparison.Ordinal);
        Assert.Contains("NO TESTS RAN", line, StringComparison.Ordinal);
        Assert.Contains("src/RequestHandler.cs", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The third outcome, and the one that must never be confused with the first: a run nobody declared
    /// part of a proof verified NOTHING. Recording it as a bland success would manufacture exactly the
    /// false reassurance this ledger exists to prevent.
    /// </summary>
    [Fact]
    public void AnUnpinnedRunIsRecordedAsHavingVerifiedNothing_NotAsVerified()
    {
        var noPin = new MutationProofPinGuard.PinReading(false, null, null);
        var tree = new MutationProofPinGuard.TreeReading(
            true,
            "0123456789abcdef0123456789abcdef01234567",
            new[] { new MutationProofPinGuard.ChangedPath(" M", "src/InProgress.cs") },
            null);
        var verdict = MutationProofPinGuard.Decide(noPin, tree);

        Assert.True(verdict.Admitted, verdict.Message);

        var line = MutationProofPinGuard.FormatRunRecord(new MutationProofPinGuard.RunRecord(
            "2026-07-20T01:02:03.0000000Z", 4242, "net10.0", "D:/ReposFred/_wt/w-example",
            "cj_example", noPin, tree, verdict));

        Assert.Contains("headVerified=no", line, StringComparison.Ordinal);
        Assert.Contains("NOT PART OF A PROOF", line, StringComparison.Ordinal);
        Assert.DoesNotContain("VERIFIED - ", line, StringComparison.Ordinal);

        // The observed state is still kept, which is what makes a FORGOTTEN pin answerable after the fact.
        Assert.Contains("src/InProgress.cs", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The record must not live where the evidence died last time. A worktree is removed after its work
    /// merges - correct hygiene - and anything stored inside it goes with it.
    /// </summary>
    [Fact]
    public void TheLedgerLivesOutsideEveryWorkingTree_SoWorktreeRemovalCannotDestroyIt()
    {
        const string workingTree = "D:/ReposFred/_wt/w-example";

        var directory = MutationProofPinGuard.ComputeLedgerDirectory("C:/Users/example/AppData/Local");
        var ledger = Path.Combine(directory, MutationProofPinGuard.ProofLedgerFileName);
        var allRuns = Path.Combine(directory, MutationProofPinGuard.AllRunsFileName);

        foreach (var path in new[] { ledger, allRuns })
        {
            Assert.DoesNotContain(
                workingTree,
                path.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_wt", path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

            // And not in the git directory either, which is where the PIN lives. The pin is per-worktree
            // and dies with it, correctly; the ledger has the opposite requirement and must not.
            Assert.DoesNotContain(".git", path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        // And it is one file for the whole machine, so "every proof run ever taken here" is one read
        // rather than a hunt through directories that no longer exist. Compared with separators
        // normalized, because the directory is spelled with the separator the caller supplied and the
        // combine uses the platform's.
        Assert.Equal(
            directory.Replace('\\', '/'),
            Path.GetDirectoryName(ledger)!.Replace('\\', '/'));
    }

    /// <summary>
    /// The ledger's identity must not move with whoever launched the run, for the same reason the per-user
    /// suite lock's did not: a baseline and its arm writing to two different ledgers would each look like
    /// the only run that ever happened.
    /// </summary>
    [Fact]
    public void TheLedgerDoesNotMoveWithTheWorkingTreeThatWroteToIt()
    {
        // One rule on every platform, and no branch on the operating system - the branch is what diverged
        // from the writer last time.
        Assert.Equal(
            MutationProofPinGuard.ComputeLedgerDirectory("C:/Users/example/AppData/Local"),
            MutationProofPinGuard.ComputeLedgerDirectory("C:/Users/example/AppData/Local"));

        Assert.Contains(
            "cc-director",
            MutationProofPinGuard.ComputeLedgerDirectory("/home/example/.local/share").Replace('\\', '/'),
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------------------------------

    private static MutationProofPinGuard.PinReading BaselinePinAt(string head) =>
        new(
            true,
            new MutationProofPinGuard.ProofPin(
                "proof-42", MutationProofPinGuard.BaselinePhase, head, "2026-07-20T00:00:00Z",
                Array.Empty<string>(), "test", "C:/pins/example.pin"),
            null);

    private static MutationProofPinGuard.PinReading ArmPinAt(string head, params string[] mutations) =>
        new(
            true,
            new MutationProofPinGuard.ProofPin(
                "proof-42", MutationProofPinGuard.ArmPhase, head, "2026-07-20T00:00:00Z",
                mutations, "test", "C:/pins/example.pin"),
            null);

    /// <summary>A scratch directory that removes itself.</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cc-mutation-pin-tests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => DeleteTree(Path);

        /// <summary>
        /// Git marks its object files read-only, which makes a plain recursive delete throw on Windows, so
        /// the attributes are cleared first. Failure to clean up is swallowed: a leftover directory under
        /// the temporary path is untidy, whereas a test that fails during cleanup reports a defect that is
        /// not there.
        /// </summary>
        internal static void DeleteTree(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    return;

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);

                Directory.Delete(path, recursive: true);
            }
            catch (Exception)
            {
                // Cleanup only. See the remarks above.
            }
        }
    }

    /// <summary>
    /// A real git repository in a scratch directory.
    ///
    /// Real, rather than a hand-written status string, because the properties under test are properties of
    /// what GIT actually reports - the exact status codes, the exact path spelling, the difference between
    /// a modification and a move of the head. A fixture built from what the author believed git emits would
    /// test the author's belief.
    /// </summary>
    private sealed class TemporaryGitRepository : IDisposable
    {
        private readonly TemporaryDirectory _scratch;

        public string Root { get; }

        /// <summary>The directory holding the tree, so a second worktree can be created beside it.</summary>
        public string ScratchPath => _scratch.Path;

        private TemporaryGitRepository(TemporaryDirectory scratch)
        {
            _scratch = scratch;
            Root = Path.Combine(scratch.Path, "tree");
            Directory.CreateDirectory(Root);
        }

        public static TemporaryGitRepository Create()
        {
            var repository = new TemporaryGitRepository(new TemporaryDirectory());
            repository.Git("init --initial-branch=main");
            repository.Git("config user.email tests@example.invalid");
            repository.Git("config user.name Tests");
            repository.Git("config commit.gpgsign false");
            return repository;
        }

        public void Write(string relativePath, string content)
        {
            var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Delete(string relativePath) =>
            File.Delete(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        /// <summary>
        /// Stages ONLY the named path. An earlier draft used "git add --all", which quietly committed
        /// whatever else the test had already left in the tree - so the mid-rework control ended up
        /// asserting against a tree far cleaner than the one it meant to build. A fixture that tidies up
        /// behind the test destroys the condition under test.
        /// </summary>
        public void WriteAndCommit(string relativePath, string content, string message)
        {
            Write(relativePath, content);
            Git("add -- \"" + relativePath + "\"");
            Git("commit -m \"" + message + "\"");
        }

        public string Head() => Git("rev-parse HEAD").Trim().ToLowerInvariant();

        public string Git(string arguments)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("git")
            {
                Arguments = "-C \"" + Root + "\" " + arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "git " + arguments + " failed in the test repository with exit code "
                    + process.ExitCode + ": " + error);
            }

            return output;
        }

        public void Dispose() => _scratch.Dispose();
    }
}
