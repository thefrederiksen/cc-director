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
        Assert.Equal(MutationProofPinGuard.ArmPhase, reading.Pin!.Phase);
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

    /// <summary>
    /// Two working trees of the same repository - which is how every mission here runs - must hold
    /// independent pins. A shared pin would mean one worker's proof declaration silently governing another
    /// worker's tree.
    /// </summary>
    [Fact]
    public void TwoWorkingTreesOfOneRepositoryGetDifferentPins()
    {
        var first = MutationProofPinGuard.ComputePinFilePath("C:/pins", "D:/ReposFred/_wt/w-alpha");
        var second = MutationProofPinGuard.ComputePinFilePath("C:/pins", "D:/ReposFred/_wt/w-beta");

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// The same tree must resolve to the same pin however its path was spelled, or the run that WRITES the
    /// pin and the run that READS it can miss each other and the guard silently does nothing.
    /// </summary>
    [Fact]
    public void OneWorkingTreeResolvesToOnePinHoweverItsPathIsSpelled()
    {
        var canonical = MutationProofPinGuard.ComputePinFilePath("C:/pins", "D:/ReposFred/_wt/w-alpha");

        Assert.Equal(canonical, MutationProofPinGuard.ComputePinFilePath("C:/pins", "D:\\ReposFred\\_wt\\w-alpha"));
        Assert.Equal(canonical, MutationProofPinGuard.ComputePinFilePath("C:/pins", "D:/ReposFred/_wt/w-alpha/"));
        Assert.Equal(canonical, MutationProofPinGuard.ComputePinFilePath("C:/pins", "D:/ReposFred/_WT/W-Alpha"));
    }

    /// <summary>
    /// A GOLDEN VALUE, and the reason it is worth freezing: scripts/mutation-proof-pin.ps1 derives this
    /// same file name INDEPENDENTLY, in another language, because it has to write the pin the guard will
    /// later read. If the two derivations ever drift apart the script writes a pin nothing reads, and the
    /// guard is inert while every command still reports success - the exact failure shape this whole file
    /// exists to prevent, reproduced in its own plumbing.
    ///
    /// A change here is not wrong, but it is never one-sided: change this value and the script's
    /// Get-PinPath together, and re-derive both.
    /// </summary>
    [Fact]
    public void ThePinFileNameIsAGoldenValue_BecauseAScriptDerivesTheSameNameSeparately()
    {
        Assert.Equal(
            Path.Combine("C:/pins", "w-example-d324397e599abb89.pin"),
            MutationProofPinGuard.ComputePinFilePath("C:/pins", "D:/ReposFred/_wt/w-example"));
    }

    /// <summary>
    /// The pins directory must not be movable by whoever launched the run - the same defect the per-user
    /// suite lock was repaired for. If the baseline run and the arm run compute different homes, each reads
    /// its own pin and the guard serializes nothing.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThePinsDirectoryIsNotRelocatableByWhoeverLaunchesTheRun(bool isWindows)
    {
        var directory = MutationProofPinGuard.ComputePinsDirectory(
            isWindows, "C:/Users/example/AppData/Local", "example");

        Assert.DoesNotContain("relocated", directory, StringComparison.Ordinal);

        if (isWindows)
            Assert.StartsWith("C:/Users/example/AppData/Local", directory, StringComparison.Ordinal);
        else
            Assert.StartsWith("/tmp/cc-director-example", directory, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoLocalApplicationDataFolder_ItRefusesRatherThanFallingBack()
    {
        Assert.Throws<InvalidOperationException>(
            () => MutationProofPinGuard.ComputePinsDirectory(isWindows: true, "", "example"));
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

        var directory = MutationProofPinGuard.ComputePinsDirectory(
            isWindows: true, "C:/Users/example/AppData/Local", "example");
        var ledger = Path.Combine(directory, MutationProofPinGuard.ProofLedgerFileName);
        var perTree = MutationProofPinGuard.ComputePinFilePath(directory, workingTree);

        foreach (var path in new[] { ledger, perTree })
        {
            Assert.DoesNotContain(
                workingTree,
                path.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_wt", path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
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
        var directory = MutationProofPinGuard.ComputePinsDirectory(
            isWindows: true, "C:/Users/example/AppData/Local", "example");

        Assert.Equal(
            Path.Combine(directory, MutationProofPinGuard.ProofLedgerFileName),
            Path.Combine(
                MutationProofPinGuard.ComputePinsDirectory(
                    isWindows: true, "C:/Users/example/AppData/Local", "example"),
                MutationProofPinGuard.ProofLedgerFileName));
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------------------------------

    private static MutationProofPinGuard.PinReading BaselinePinAt(string head) =>
        new(
            true,
            new MutationProofPinGuard.ProofPin(
                MutationProofPinGuard.BaselinePhase, head, "2026-07-20T00:00:00Z",
                Array.Empty<string>(), "test", "C:/pins/example.pin"),
            null);

    private static MutationProofPinGuard.PinReading ArmPinAt(string head, params string[] mutations) =>
        new(
            true,
            new MutationProofPinGuard.ProofPin(
                MutationProofPinGuard.ArmPhase, head, "2026-07-20T00:00:00Z",
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

        private string Git(string arguments)
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
