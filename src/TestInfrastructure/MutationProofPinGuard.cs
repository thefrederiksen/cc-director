#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CcDirector.TestInfrastructure;

/// <summary>
/// Refuses to START a test run that has been declared part of a mutation proof - a BASELINE run or a
/// MUTATION ARM run - when the working tree is not exactly what that proof pinned. Every other run is
/// admitted untouched.
///
/// WHY THIS EXISTS - READ THIS BEFORE YOU DELETE IT OR WIDEN IT.
///
/// We prove a security guard is real by mutating the one primitive it depends on, running the whole suite
/// again, and reconciling the arithmetic: the tests that pass plus the tests that fail under the mutation
/// must add up to the tests that passed under the restored run. If they add up, the suite is held to have
/// observed the primitive.
///
/// That reconciliation has one failure it cannot see by itself, and it is the failure that matters most.
/// Suppose the BASELINE is taken on a tree where a security guard block has ALREADY been silently deleted
/// and left uncommitted. The tree still compiles. The suite still runs. The baseline still looks green and
/// complete. Then the mutation arm runs on that same contaminated tree, and the two runs reconcile
/// PERFECTLY - because they agree with each other, not because either one measured anything. The cheapest
/// detector we own passes while measuring nothing, and every conclusion drawn from it is worthless with
/// nothing anywhere in the process saying so.
///
/// This is not hypothetical. On 2026-07-19 a worker found exactly that on disk: a security guard block that
/// a KILLED run had deleted and never restored, because a process that is killed skips its cleanup. A sweep
/// of the mission's working trees that night found four dirty trees.
///
/// AND IT IS AN INSTRUCTION THAT CANNOT BIND. Telling every worker "check your tree is clean before you
/// take a baseline" does not work, and the reason is the whole design argument for this file: the worker
/// who INHERITS a contaminated tree is, by definition, the one who does not know there is a mutation in it.
/// It looks clean to them because it compiles and the tests pass. This is the same shape as the problem the
/// per-user suite lock in this repository was built for: a rule that requires global knowledge cannot be
/// obeyed by an actor holding only local knowledge. Four workers were each told not to run two suites at
/// once, each complied with everything it could observe, and four suites ran concurrently anyway. That one
/// was solved by a mechanism rather than a briefing, and so is this.
///
/// WHAT IT IS DELIBERATELY NOT: A BLANKET REFUSAL ON ANY DIRTY TREE.
///
/// A worker halfway through a rework is legitimately dirty and must be able to run whatever it likes,
/// whenever it likes. Only a baseline and a mutation arm carry a MEANING that depends on the tree being
/// exactly the pinned head. A blanket guard would be switched off inside a day, and a guard that gets
/// switched off protects nothing at all. So the guard is inert - it does not even invoke git - unless the
/// tree has an active pin.
///
/// HOW A RUN DECLARES ITSELF PART OF A PROOF, AND WHY THAT IS HARD TO FORGET.
///
/// The declaration is made ONCE PER PROOF, not once per run, by writing a pin for the working tree
/// (scripts/mutation-proof-pin.ps1). From that moment until the pin is released, EVERY test run in that
/// tree is checked - the baseline, the arm, and any re-run of either. So the only moment a worker can
/// forget is the moment before the proof starts, and at that moment forgetting means something visible:
/// the pin is the only place the pinned head is written down, and a mutation proof with no recorded pinned
/// head is not a proof anybody can write up. The un-guarded path is not "skip a flag on the command line",
/// it is "have no pinned head at all".
///
/// It is not, and cannot honestly be claimed to be, impossible to forget - see the RUN RECORD below, which
/// is what closes the remainder.
///
/// THE ARM IS CHECKED TOO, AND AGAINST A TIGHTER RULE THAN THE BASELINE.
///
/// A mutation arm is SUPPOSED to be dirty - that is what a mutation is. So the arm's pin declares which
/// paths the mutation touches, and the guard requires the working tree to carry EXACTLY those changes:
///
///   - a change to a path the pin did not declare is refused, which is the contaminated-arm case, and
///   - a declared path that is NOT modified is refused, which is the case where the mutation was never
///     applied or was already restored. That one matters as much as the first: an arm run with no mutation
///     in it is a second baseline wearing an arm's name, and it reconciles perfectly against the first.
///
/// A baseline is simply an arm that declares no mutations, so both phases run one rule.
///
/// THE SECOND JOB: MAKE THE TREE'S STATE AT RUN TIME OUTLIVE THE TREE.
///
/// Refusing a contaminated run is only half of what this mechanism is for. On 2026-07-19 somebody was asked
/// whether four ALREADY-MERGED security proofs had been taken on contaminated trees. The answer could not be
/// given - not because the evidence was hard to find, but because it no longer existed. Those proofs were
/// run in worktrees, and the worktrees were removed after merging, which is correct hygiene and also
/// destroyed the only artifact that could have answered the question. Those four are recorded as unknown
/// and will stay unknown forever.
///
/// So every run appends a line to a LEDGER that lives outside every working tree, in the same per-user
/// neighbourhood as the suite lock, and therefore survives "git worktree remove", "git clean -xdf", and the
/// deletion of the whole checkout. Six months from now the question "was that proof taken on a clean tree at
/// the pinned head" is answered by reading a file, not by reconstructing a directory that is gone.
///
/// THE LEDGER STATES A POSITIVE FACT, NOT THE ABSENCE OF A COMPLAINT. It writes on ADMISSION as well as on
/// refusal, and an admission line says which head was verified and that the tree matched it. A log written
/// only when something goes wrong cannot afterwards distinguish "verified clean" from "the guard never
/// ran" - both are silence, and silence reads as success. That is the same principle the firing tests are
/// built on: an instrument that cannot observe its subject reports the subject's absence.
///
/// UNPINNED RUNS ARE OBSERVED, NEVER REFUSED. They are recorded too - head, and the tree's changed paths -
/// which is what makes a FORGOTTEN pin recoverable: when a proof's numbers are later questioned, the record
/// says whether the tree was dirty at the moment the baseline ran, even though no pin was ever written.
/// Recording NEVER refuses and NEVER fails a run; a diagnostic that can break a run is worse than no
/// diagnostic.
///
/// WHERE THE PIN LIVES: OUTSIDE THE WORKING TREE.
///
/// Two reasons, both load-bearing. A pin file inside the tree would itself be a working-tree change, so the
/// guard would trip over its own declaration or would need an exemption - and an exemption is a hole. And a
/// pin inside the tree is removed by "git clean -xdf", which is exactly the sort of housekeeping a worker
/// runs in the middle of a proof. So pins live under the per-user local application data directory, keyed
/// by the working tree's path, alongside the suite lock's home.
///
/// WHY A MODULE INITIALIZER. It runs on assembly load, before any test, under every runner, and there is no
/// call site for anybody to forget. It is the same form the per-user suite lock and the storage-root
/// redirect already use in this repository. This file is linked into EVERY project whose name ends in
/// ".Tests" by Directory.Build.props, so a test project added next year is covered without anybody
/// remembering - keying on the project name rather than on the IsTestProject flag was deliberate, because
/// one of the seven existing test projects does not set that flag.
///
/// It is pinned by MutationProofPinGuardTests, because a guard that silently fails to run leaves no trace
/// and looks exactly like a guard that works.
/// </summary>
internal static class MutationProofPinGuard
{
    /// <summary>Exit code used when this run refuses to start because the tree is not what the proof pinned.</summary>
    internal const int ContaminatedTreeExitCode = 97;

    /// <summary>The phase name for a run whose tree must carry no changes at all.</summary>
    internal const string BaselinePhase = "baseline";

    /// <summary>The phase name for a run whose tree must carry exactly the declared mutation and nothing else.</summary>
    internal const string ArmPhase = "arm";

    /// <summary>How long to let a git invocation run before treating it as unusable.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Trim the run record back to this many lines once it passes <see cref="RunRecordMaxBytes"/>.</summary>
    private const int RunRecordKeepLines = 500;

    /// <summary>Size at which the run record is trimmed, so a long-lived tree cannot grow it without bound.</summary>
    private const long RunRecordMaxBytes = 1024 * 1024;

    /// <summary>True once the module initializer has run in this process. Pinned by the tests, because a
    /// module initializer that silently does not run is invisible.</summary>
    internal static bool HasRun { get; private set; }

    /// <summary>What the guard decided in this process, for the tests to read.</summary>
    internal static string LastVerdictSummary { get; private set; } = "(the guard has not run)";

    // ---------------------------------------------------------------------------------------------------
    // The decision. A pure function of its arguments so it can be driven directly, including down the paths
    // that only occur on a contaminated tree - which is the whole point, since those are the paths that
    // must be shown to FIRE rather than merely to exist.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>One entry from the working tree's change list: the two-letter git status code and the path.</summary>
    internal readonly record struct ChangedPath(string StatusCode, string Path)
    {
        public override string ToString() => StatusCode + " " + Path;
    }

    /// <summary>A proof's declaration for one working tree.</summary>
    internal sealed record ProofPin(
        string Phase,
        string PinnedHead,
        string PinnedUtc,
        IReadOnlyList<string> DeclaredMutations,
        string Note,
        string PinFilePath);

    /// <summary>
    /// The result of looking for a pin. A pin that EXISTS but cannot be understood is not the same as no
    /// pin, and must never be treated as one - a typo in a pin file would otherwise silently disarm the
    /// guard for the whole proof.
    /// </summary>
    internal sealed record PinReading(bool Found, ProofPin? Pin, string? Problem);

    /// <summary>
    /// What git says about the working tree. Reading can fail - git missing from the path, a corrupt
    /// repository - and when a pin is active that failure REFUSES, because a proof that cannot verify its
    /// own tree has not verified anything.
    /// </summary>
    internal sealed record TreeReading(bool Read, string Head, IReadOnlyList<ChangedPath> Changes, string? Problem);

    /// <summary>Admitted, or refused with the reason a human needs in order to fix it.</summary>
    internal sealed record Verdict(bool Admitted, string Message);

    /// <summary>
    /// The rule, in one place. Collects EVERY problem rather than stopping at the first, so a worker fixes
    /// the tree in one pass instead of discovering the second fault after another nine-minute run.
    /// </summary>
    internal static Verdict Decide(PinReading pin, TreeReading tree)
    {
        if (pin.Problem is not null)
        {
            return new Verdict(
                false,
                "A mutation-proof pin file exists for this working tree but cannot be understood: "
                + pin.Problem
                + " A pin that cannot be read is NOT the same as no pin, and is not treated as one - "
                + "a proof whose declaration is unreadable has declared nothing. Fix or release the pin "
                + "(scripts/mutation-proof-pin.ps1 release) and start the proof again.");
        }

        if (!pin.Found || pin.Pin is null)
        {
            // The ordinary case, and the scope limit the whole design turns on: no pin, no opinion. A
            // worker mid-rework is legitimately dirty and is not this guard's business.
            return new Verdict(true, "No mutation-proof pin is active for this working tree.");
        }

        var active = pin.Pin;

        if (!tree.Read)
        {
            return new Verdict(
                false,
                "This run is declared part of a mutation proof (" + Describe(active) + "), but the state of "
                + "the working tree could not be read, so there is no way to know whether the tree is the "
                + "one the proof pinned: " + (tree.Problem ?? "(no detail)")
                + " A proof run that cannot verify its own tree has verified nothing, so it stops rather "
                + "than producing numbers nobody can trust.");
        }

        var problems = new List<string>();

        if (!string.Equals(active.PinnedHead, tree.Head, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(
                "THE HEAD HAS MOVED. This proof pinned " + active.PinnedHead + ", and the working tree is "
                + "now at " + tree.Head + ". A baseline and its mutation arm must be taken at the same "
                + "commit or the arithmetic compares two different programs. Either return the tree to the "
                + "pinned commit, or release the pin and start the proof again from the head you mean.");
        }

        var declared = active.DeclaredMutations
            .Select(NormalizePath)
            .Where(p => p.Length > 0)
            .ToList();

        var changedPaths = tree.Changes.Select(c => NormalizePath(c.Path)).ToList();

        var undeclared = tree.Changes
            .Where(c => !declared.Contains(NormalizePath(c.Path), StringComparer.Ordinal))
            .ToList();

        if (undeclared.Count > 0)
        {
            problems.Add(
                "THE WORKING TREE CARRIES " + undeclared.Count.ToString(CultureInfo.InvariantCulture)
                + " CHANGE(S) THIS PROOF DID NOT DECLARE:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, undeclared.Select(c => "    " + c.StatusCode + "  " + c.Path))
                + Environment.NewLine
                + "    An uncommitted change that nobody declared is how a contaminated baseline happens: "
                + "a security guard deleted by a killed run still compiles, still runs green, and then "
                + "reconciles perfectly against its own mutation arm while measuring nothing. Commit these "
                + "changes, revert them, or declare them - but do not run a proof over them.");
        }

        var missing = declared
            .Where(d => !changedPaths.Contains(d, StringComparer.Ordinal))
            .ToList();

        if (missing.Count > 0)
        {
            problems.Add(
                "THE DECLARED MUTATION IS NOT IN THE WORKING TREE:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, missing.Select(m => "    " + m))
                + Environment.NewLine
                + "    This run was declared a mutation arm, so those paths were expected to be modified. "
                + "An arm with no mutation in it is a SECOND BASELINE wearing an arm's name, and it "
                + "reconciles perfectly against the first while proving nothing. Apply the mutation, or "
                + "correct the declared path - the tree currently carries: "
                + (changedPaths.Count == 0
                    ? "(no changes at all)"
                    : string.Join(", ", changedPaths))
                + ".");
        }

        if (problems.Count == 0)
        {
            return new Verdict(
                true,
                "This run is declared part of a mutation proof (" + Describe(active)
                + ") and the working tree matches what the proof pinned.");
        }

        return new Verdict(
            false,
            "*** REFUSING TO RUN. NO TESTS WILL RUN. ***" + Environment.NewLine
            + "This run is declared part of a mutation proof (" + Describe(active) + "), and the working "
            + "tree is not the tree that proof pinned. A mutation proof compares a baseline against a "
            + "mutation arm and reconciles the counts; if the tree carries changes nobody declared, both "
            + "runs agree with each other rather than measuring anything, and the proof passes while "
            + "proving nothing." + Environment.NewLine
            + string.Join(Environment.NewLine + Environment.NewLine, problems.Select(p => "  - " + p))
            + Environment.NewLine + Environment.NewLine
            + "  Pin file: " + active.PinFilePath + Environment.NewLine
            + "  If this run is NOT part of the proof, release the pin: "
            + "powershell -File scripts/mutation-proof-pin.ps1 release");
    }

    private static string Describe(ProofPin pin)
    {
        var mutations = pin.DeclaredMutations.Count == 0
            ? "no declared mutations"
            : "declared mutations: " + string.Join(", ", pin.DeclaredMutations);

        return "phase " + pin.Phase + ", pinned head " + pin.PinnedHead + ", pinned at " + pin.PinnedUtc
            + ", " + mutations
            + (string.IsNullOrWhiteSpace(pin.Note) ? "" : ", note: " + pin.Note);
    }

    /// <summary>Git reports forward-slash relative paths; a hand-written pin may not. Compare like for like.</summary>
    internal static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('.', '/').Trim();

    // ---------------------------------------------------------------------------------------------------
    // Reading the pin. Every rejection here REFUSES rather than falling through to "no pin", because the
    // fall-through is silent and this file exists to stop silent failures.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Parses a pin file's text. Separated from the file system so the malformed cases can be driven
    /// directly - they are the cases that must not degrade into "no pin found".
    /// </summary>
    internal static PinReading ParsePin(string text, string pinFilePath)
    {
        string? phase = null;
        string? head = null;
        string? pinnedUtc = null;
        var note = "";
        var mutations = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var split = line.IndexOf('=');
            if (split <= 0)
                return Malformed(pinFilePath, "the line '" + line + "' is not a key=value pair.");

            var key = line[..split].Trim();
            var value = line[(split + 1)..].Trim();

            switch (key)
            {
                case "phase":
                    phase = value;
                    break;
                case "pinnedHead":
                    head = value;
                    break;
                case "pinnedUtc":
                    pinnedUtc = value;
                    break;
                case "mutates":
                    if (value.Length > 0)
                        mutations.Add(value);
                    break;
                case "note":
                    note = value;
                    break;
                case "tree":
                    break; // diagnostic only
                default:
                    // Refuse rather than ignore. An ignored key is how "mutate=" instead of "mutates="
                    // would quietly drop a declaration - and the guard would then blame the worker for a
                    // change the worker did declare.
                    return Malformed(pinFilePath, "the key '" + key + "' is not one this guard understands.");
            }
        }

        if (phase is null)
            return Malformed(pinFilePath, "it declares no phase.");

        if (phase != BaselinePhase && phase != ArmPhase)
        {
            return Malformed(
                pinFilePath,
                "the phase '" + phase + "' is neither '" + BaselinePhase + "' nor '" + ArmPhase + "'.");
        }

        if (head is null || !LooksLikeCommitId(head))
        {
            return Malformed(
                pinFilePath,
                "the pinned head '" + (head ?? "(absent)") + "' is not a full forty-character commit id.");
        }

        if (phase == BaselinePhase && mutations.Count > 0)
        {
            return Malformed(
                pinFilePath,
                "it is a baseline yet declares mutations (" + string.Join(", ", mutations)
                + "). A baseline is taken on an unmodified tree by definition; a run that carries a "
                + "mutation is an arm.");
        }

        if (phase == ArmPhase && mutations.Count == 0)
        {
            return Malformed(
                pinFilePath,
                "it is a mutation arm yet declares no mutation. An arm that declares nothing would admit "
                + "any tree at all, which is the opposite of what this guard is for.");
        }

        return new PinReading(
            true,
            new ProofPin(phase, head.ToLowerInvariant(), pinnedUtc ?? "(not recorded)", mutations, note, pinFilePath),
            null);
    }

    private static PinReading Malformed(string pinFilePath, string detail) =>
        new(true, null, "the pin file '" + pinFilePath + "' is not usable - " + detail);

    private static bool LooksLikeCommitId(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

    // ---------------------------------------------------------------------------------------------------
    // Locating things.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Walks up from a starting directory to the working tree root - the directory holding ".git". In a git
    /// worktree ".git" is a FILE rather than a directory, which is the normal case for this repository's
    /// mission work, so both are accepted.
    ///
    /// Returns null when there is no repository above the starting point. That is the honest answer for a
    /// test assembly copied somewhere else entirely, and it means the guard has nothing to key on; it is
    /// stated here rather than papered over because it is the one way a proof run could sidestep the guard.
    /// </summary>
    internal static string? FindWorkingTreeRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// The pin file for one working tree. The key is a hash of the tree's path so that two working trees of
    /// the same repository - which is how every mission here is run - hold independent pins.
    ///
    /// SHA-256 rather than a runtime hash code, because this name is written to disk and must mean the same
    /// thing in the next process. The leaf directory name is kept in front of the hash so a human reading
    /// the pins directory can tell at a glance which tree each pin belongs to.
    /// </summary>
    internal static string ComputePinFilePath(string pinsDirectory, string workingTreeRoot)
    {
        var normalized = workingTreeRoot.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();

        var leaf = new string(Path.GetFileName(normalized.TrimEnd('/'))
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray());

        if (leaf.Length > 40)
            leaf = leaf[..40];

        return Path.Combine(pinsDirectory, leaf + "-" + digest[..16] + ".pin");
    }

    /// <summary>
    /// Where pins live: beside the per-user suite lock, under the per-user local application data
    /// directory, and NOT anywhere the process environment can move - for the same reason the suite lock
    /// is derived that way. A pin the launching environment can relocate is a pin two runs of the same
    /// proof would not share.
    /// </summary>
    internal static string ComputePinsDirectory(bool isWindows, string localApplicationDataFolder, string userName)
    {
        if (!isWindows)
        {
            // Built by hand: the separator belongs to the target platform, not the running one.
            return "/tmp/cc-director-" + userName + "-mutation-proof-pins";
        }

        if (string.IsNullOrWhiteSpace(localApplicationDataFolder))
        {
            throw new InvalidOperationException(
                "Cannot locate the per-user local application data directory, so mutation-proof pins have "
                + "no environment-independent home. Every alternative location is settable by whoever "
                + "launched the run, which would let the baseline and the mutation arm read different "
                + "pins. See MutationProofPinGuard.");
        }

        return Path.Combine(localApplicationDataFolder, "cc-director", "mutation-proof-pins");
    }

    private static string PinsDirectory => ComputePinsDirectory(
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        Environment.UserName);

    // ---------------------------------------------------------------------------------------------------
    // Reading the tree, via git.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Asks git for the head commit and the working tree's changes.
    ///
    /// "--no-optional-locks" because several agents run in this repository at once and a status call has no
    /// business contending for the index lock. The porcelain format is requested explicitly with a version
    /// number, because the default format is documented as subject to change and this parser is not.
    /// </summary>
    internal static TreeReading ReadTree(string workingTreeRoot)
    {
        var head = RunGit(workingTreeRoot, "--no-optional-locks rev-parse HEAD");
        if (head.ExitCode != 0)
        {
            return new TreeReading(
                false, "", Array.Empty<ChangedPath>(),
                "'git rev-parse HEAD' failed in '" + workingTreeRoot + "': " + Describe(head));
        }

        var status = RunGit(
            workingTreeRoot, "--no-optional-locks status --porcelain=v1 --untracked-files=normal -z");
        if (status.ExitCode != 0)
        {
            return new TreeReading(
                false, "", Array.Empty<ChangedPath>(),
                "'git status' failed in '" + workingTreeRoot + "': " + Describe(status));
        }

        return new TreeReading(
            true,
            head.Output.Trim().ToLowerInvariant(),
            ParsePorcelainZ(status.Output),
            null);
    }

    private static string Describe(GitResult result) =>
        "exit code " + result.ExitCode.ToString(CultureInfo.InvariantCulture)
        + (string.IsNullOrWhiteSpace(result.Error) ? "" : ", " + result.Error.Trim());

    /// <summary>
    /// Parses the NUL-separated porcelain v1 format.
    ///
    /// NUL-separated rather than line-separated on purpose: in the line format git QUOTES and escapes paths
    /// containing unusual characters, so a path with a space or a non-ASCII character comes back in a shape
    /// this parser would mis-read - and mis-reading a path means naming the wrong file in a refusal, or
    /// failing to match a declared mutation. In the NUL format paths are always literal.
    ///
    /// Rename and copy entries carry a SECOND path (the origin) as its own field, which must be consumed or
    /// every subsequent entry is shifted by one.
    /// </summary>
    internal static IReadOnlyList<ChangedPath> ParsePorcelainZ(string output)
    {
        var fields = output.Split('\0');
        var changes = new List<ChangedPath>();

        for (var i = 0; i < fields.Length; i++)
        {
            var entry = fields[i];
            if (entry.Length < 4)
                continue; // the trailing empty field, and anything too short to be an entry

            var code = entry[..2];
            var path = entry[3..];

            changes.Add(new ChangedPath(code, path));

            // 'R' is a rename, 'C' a copy; either way the origin path follows in its own field.
            if (code[0] is 'R' or 'C' || code[1] is 'R' or 'C')
                i++;
        }

        return changes;
    }

    private readonly record struct GitResult(int ExitCode, string Output, string Error);

    private static GitResult RunGit(string workingTreeRoot, string arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("git")
            {
                Arguments = "-C \"" + workingTreeRoot + "\" " + arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit((int)GitTimeout.TotalMilliseconds))
            {
                return new GitResult(
                    -1, "", "git did not finish within " + GitTimeout.TotalSeconds + " seconds.");
            }

            return new GitResult(process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            // Deliberately reported rather than swallowed. When a pin is active this becomes a refusal,
            // because a proof that cannot read its own tree has proved nothing; when no pin is active the
            // caller ignores it, and the run proceeds untouched.
            return new GitResult(-1, "", ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // The entry point.
    // ---------------------------------------------------------------------------------------------------

    [ModuleInitializer]
    internal static void Check()
    {
        HasRun = true;

        string? root;
        try
        {
            root = FindWorkingTreeRoot(AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            // Walking to the root touched the file system and it refused. There is no pin this run can be
            // measured against, so it is admitted - but it is admitted LOUDLY, because a guard that goes
            // quiet on an unexpected input is how a guard stops guarding without anybody noticing.
            LastVerdictSummary = "admitted: the working tree root could not be located (" + ex.Message + ")";
            Say("[mutation-proof-pin] Could not locate the working tree root, so no mutation-proof pin "
                + "could be checked for this run: " + ex.Message);
            return;
        }

        if (root is null)
        {
            LastVerdictSummary = "admitted: this run is not inside a git working tree";
            return;
        }

        var pin = ReadPinFor(root);
        var tree = ReadTree(root);
        var verdict = Decide(pin, tree);

        LastVerdictSummary = (verdict.Admitted ? "admitted" : "refused") + ": " + verdict.Message;

        // Recorded for EVERY run, admitted or refused, pinned or not - see the remarks on the ledger. An
        // admission is the fact worth keeping; a log that only writes on refusal cannot afterwards tell
        // "verified clean" from "the guard never ran".
        Record(root, pin, tree, verdict);

        if (verdict.Admitted)
        {
            // An unpinned run says nothing at all. There are thousands of them and they are not this
            // guard's business; a line of output on each one is how a mechanism trains people to ignore it.
            if (pin.Found)
                Say("[mutation-proof-pin] " + verdict.Message);

            return;
        }

        Say("[mutation-proof-pin] " + verdict.Message);

        // Environment.Exit rather than an exception, for the same reason the suite lock does it: an
        // exception thrown from a module initializer surfaces as a type-initialization failure on whichever
        // test happened to touch this type first, and reads as an unrelated test failure. Stopping the
        // process is unambiguous - no test ran, and the reason is the last thing on the console.
        Console.Out.Flush();
        Console.Error.Flush();
        Environment.Exit(ContaminatedTreeExitCode);
    }

    private static PinReading ReadPinFor(string workingTreeRoot)
    {
        string path;
        try
        {
            path = ComputePinFilePath(PinsDirectory, workingTreeRoot);
        }
        catch (Exception ex)
        {
            return new PinReading(true, null, "the pins directory could not be located: " + ex.Message);
        }

        try
        {
            if (!File.Exists(path))
                return new PinReading(false, null, null);

            return ParsePin(File.ReadAllText(path), path);
        }
        catch (Exception ex)
        {
            // A pin file that EXISTS but cannot be read is a refusal, not an absence. Treating it as an
            // absence would disarm the guard for exactly the proof that most needs it.
            return new PinReading(true, null, "the pin file '" + path + "' could not be read: " + ex.Message);
        }
    }

    /// <summary>The name of the ledger every pinned run in every working tree appends to.</summary>
    internal const string ProofLedgerFileName = "mutation-proof-ledger.log";

    /// <summary>
    /// Everything about one run that a person reading the ledger in six months needs, gathered as VALUES so
    /// the line can be produced and asserted without a repository, a clock, or a file system.
    /// </summary>
    internal readonly record struct RunRecord(
        string WhenUtc,
        int ProcessId,
        string Assembly,
        string WorkingTreeRoot,
        string SessionIdentifier,
        PinReading Pin,
        TreeReading Tree,
        Verdict Verdict);

    /// <summary>
    /// Renders one ledger line.
    ///
    /// It states a POSITIVE FACT on admission - which head was verified, and that the tree matched it -
    /// rather than merely failing to complain. That distinction is the whole value of the ledger: a record
    /// written only on refusal cannot afterwards distinguish a proof that was verified clean from a proof
    /// where this guard never ran at all. Both would be silence.
    ///
    /// One line per run, key=value, so it can be read by eye and grepped by anybody, and so a partially
    /// written line from an interrupted process cannot corrupt its neighbours.
    /// </summary>
    internal static string FormatRunRecord(RunRecord record)
    {
        var pin = record.Pin.Pin;

        var phase = record.Pin.Problem is not null
            ? "unreadable-pin"
            : pin is null ? "unpinned" : pin.Phase;

        var pinnedHead = pin?.PinnedHead ?? "(none)";
        var observedHead = record.Tree.Read ? record.Tree.Head : "(unreadable)";

        var headVerified = pin is not null
            && record.Tree.Read
            && string.Equals(pin.PinnedHead, record.Tree.Head, StringComparison.OrdinalIgnoreCase);

        var changes = !record.Tree.Read
            ? "(unreadable: " + (record.Tree.Problem ?? "no detail") + ")"
            : record.Tree.Changes.Count == 0
                ? "none"
                : string.Join(" | ", record.Tree.Changes.Select(c => c.StatusCode + " " + c.Path));

        // The positive statement, spelled out in words a person can read without knowing this format. It is
        // deliberately a sentence rather than a flag, because whoever reads this in six months will be
        // deciding whether to trust a merged proof, and a bare "ok=1" is not an answer to that question.
        var finding = record.Verdict.Admitted
            ? pin is null
                ? "NOT PART OF A PROOF - this run was not declared a baseline or a mutation arm, so nothing "
                  + "was verified about its tree; the observed state is recorded above for later reference."
                : "VERIFIED - the working tree was checked against pinned head " + pinnedHead
                  + " and MATCHED it, carrying exactly the declared changes for phase '" + phase
                  + "'. This run's results were produced on that tree."
            : "REFUSED - the working tree did NOT match what the proof pinned, and NO TESTS RAN in this "
              + "process. Any numbers attributed to this run are not from this run.";

        return string.Join("  ", new[]
        {
            record.WhenUtc,
            "verdict=" + (record.Verdict.Admitted ? "admitted" : "refused"),
            "phase=" + phase,
            "pinnedHead=" + pinnedHead,
            "observedHead=" + observedHead,
            "headVerified=" + (headVerified ? "yes" : "no"),
            "declaredMutations=" + (pin is null || pin.DeclaredMutations.Count == 0
                ? "(none)"
                : string.Join(",", pin.DeclaredMutations)),
            "observedChanges=" + changes,
            "tree=" + record.WorkingTreeRoot,
            "assembly=" + record.Assembly,
            "pid=" + record.ProcessId.ToString(CultureInfo.InvariantCulture),
            "session=" + record.SessionIdentifier,
            "finding=" + finding,
        });
    }

    /// <summary>
    /// Appends this run to the ledger, and to a per-tree record beside it.
    ///
    /// TWO FILES ON PURPOSE. The per-tree record carries the high-volume traffic - every ordinary
    /// unpinned run - and is where you look when a FORGOTTEN pin has to be reconstructed for one tree. The
    /// ledger carries only runs that declared themselves part of a proof, so it stays small enough that the
    /// question "show me every proof run ever taken on this machine, and whether each one was verified" is
    /// answered by reading one file top to bottom. Mixing them would bury the second in the first.
    ///
    /// BOTH LIVE OUTSIDE EVERY WORKING TREE, which is the point: "git worktree remove" is correct hygiene
    /// and it is also what destroyed the evidence for four already-merged proofs. Nothing here is stored
    /// anywhere that removal can reach.
    ///
    /// It can never fail a run. Every failure is swallowed, because a channel whose only job is to explain
    /// a problem must never be able to cause one.
    /// </summary>
    private static void Record(string workingTreeRoot, PinReading pin, TreeReading tree, Verdict verdict)
    {
        try
        {
            var line = FormatRunRecord(new RunRecord(
                WhenUtc: DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ProcessId: Environment.ProcessId,
                Assembly: Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)),
                WorkingTreeRoot: workingTreeRoot,
                SessionIdentifier: SessionIdentifier(),
                Pin: pin,
                Tree: tree,
                Verdict: verdict));

            var directory = PinsDirectory;
            Directory.CreateDirectory(directory);

            var perTree = ComputePinFilePath(directory, workingTreeRoot) + ".runs.log";
            TrimIfLarge(perTree);
            Append(perTree, line);

            if (pin.Found)
            {
                // Never trimmed. It only grows by one line per proof run, and a ledger that discards its
                // oldest entries is exactly useless for the question it exists to answer, which is always
                // about something that happened a while ago.
                Append(Path.Combine(directory, ProofLedgerFileName), line);
            }
        }
        catch (Exception)
        {
            // Diagnostics only. See the remarks above: this must never be able to break a run.
        }
    }

    /// <summary>
    /// Appends one line, retrying briefly around the sharing conflicts that happen when several test
    /// assemblies start at once. A lost ledger line is a lost fact, so it is worth a few attempts - but
    /// never worth failing the run, so the attempts are bounded and the last failure is swallowed by the
    /// caller.
    /// </summary>
    private static void Append(string path, string line)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.AppendAllText(path, line + Environment.NewLine);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>
    /// Identifies the run for whoever reads the ledger later. Every fleet-launched session carries
    /// CC_SESSION_ID, which is the case that matters - it names the session whose proof this was. A run
    /// started outside the fleet says so rather than inventing an identity.
    /// </summary>
    private static string SessionIdentifier()
    {
        var id = Environment.GetEnvironmentVariable("CC_SESSION_ID");
        return string.IsNullOrWhiteSpace(id) ? "(none)" : id;
    }

    private static void TrimIfLarge(string recordPath)
    {
        var info = new FileInfo(recordPath);
        if (!info.Exists || info.Length <= RunRecordMaxBytes)
            return;

        var lines = File.ReadAllLines(recordPath);
        File.WriteAllLines(recordPath, lines.Skip(Math.Max(0, lines.Length - RunRecordKeepLines)));
    }

    /// <summary>
    /// Says it on standard output, standard error, and the attached terminal.
    ///
    /// Three channels for the reason the suite lock documents: "dotnet test" launches the test host as a
    /// child with its standard streams redirected and, at the default console verbosity, relays none of
    /// that output. A refusal nobody sees is indistinguishable from a crash, and somebody will go looking
    /// for the wrong fault.
    /// </summary>
    private static void Say(string message)
    {
        var stamped = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            + " pid " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ": " + message;

        Console.Out.WriteLine(stamped);
        Console.Out.Flush();
        Console.Error.WriteLine(stamped);
        Console.Error.Flush();

        var device = OperatingSystem.IsWindows() ? "CONOUT$" : "/dev/tty";
        try
        {
            using var terminal = new StreamWriter(
                new FileStream(device, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            terminal.WriteLine(stamped);
            terminal.Flush();
        }
        catch (Exception)
        {
            // No terminal is attached (a scheduled run, a continuous-integration agent, a redirected pipe
            // with no console). The standard streams above already carry the message.
        }
    }
}
