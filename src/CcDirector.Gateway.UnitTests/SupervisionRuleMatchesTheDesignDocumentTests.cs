using System.Runtime.CompilerServices;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE WRITTEN RULE AND THE CODE, CHECKED AGAINST EACH OTHER.
///
/// Reads the supervision table out of <c>docs/new_architecture/session-roles-semantics.md</c> and asserts
/// that <see cref="SessionOrdering.IsSupervised"/> answers what the table says, for every seat, in both
/// directions.
///
/// WHY THIS EXISTS, AND WHY A BEHAVIOUR TEST WAS NOT ENOUGH. The owner amended the attention rule on
/// 2026-07-09 - the Architect stops surfacing to him - and that amendment sat in the design document,
/// unimplemented, until 2026-09-03. Nothing was broken. Nothing was flaky. Every test was green, because
/// every test asserted the SHIPPED behaviour in the PRESENT TENSE, which is what tests do. A document
/// saying the opposite could not make any of them go red. The divergence was invisible to the machine and
/// was only ever going to be found by a person happening to read both halves side by side, and for two
/// months nobody did.
///
/// So the answer is not "write the behaviour assertion the other way round" - that has now been done
/// twice, in two directions, and it would not have caught either drift. The answer is a test whose INPUTS
/// come from the DOCUMENT and whose EXPECTED ANSWERS come from the CODE, so that changing one without the
/// other is itself the failure.
///
/// IT CANNOT PASS BY FINDING NOTHING. The table must be present, fenced by its markers, and it must name
/// EVERY combination of the four roles in <see cref="SessionRoles.All"/> and the two origin kinds that
/// matter. A missing marker, an empty table, or a table that quietly stopped covering a seat fails with a
/// message naming what is missing - never a silent pass. That is deliberate: a check whose pass condition
/// is an ABSENCE certifies a run that never happened.
/// </summary>
public sealed class SupervisionRuleMatchesTheDesignDocumentTests
{
    private const string DocRelativePath = "docs/new_architecture/session-roles-semantics.md";
    private const string Begin = "<!-- SUPERVISION-TABLE-BEGIN -->";
    private const string End = "<!-- SUPERVISION-TABLE-END -->";

    /// <summary>The two origin kinds the rule distinguishes. "(none)" is how the table spells an ordinary
    /// session that nothing scheduled; "schedule" is a cron firing or a work-list item.</summary>
    private static readonly string[] OriginKinds = { "(none)", "schedule" };

    private sealed record Row(string Role, string Origin, bool Supervised);

    [Fact]
    public void EverySeatInTheDesignDocument_GetsTheAnswerTheDocumentStates()
    {
        var rows = ReadTable();

        foreach (var row in rows)
        {
            var s = new SessionDto
            {
                SessionId = "s",
                Name = "s",
                ActivityState = "WaitingForInput",
                SessionRole = row.Role,
                OriginKind = row.Origin == "(none)" ? null : row.Origin,
            };

            var actual = SessionOrdering.IsSupervised(s);

            Assert.True(row.Supervised == actual,
                $"{DocRelativePath} says a {row.Role} with origin {row.Origin} is " +
                $"{(row.Supervised ? "SUPERVISED" : "HUMAN-FACING")}, and SessionOrdering.IsSupervised says " +
                $"{(actual ? "SUPERVISED" : "HUMAN-FACING")}. One of the two is wrong and BOTH have to " +
                "change in the same pull request. This exact divergence - a rule written down in one half " +
                "and not the other - went unnoticed for two months in 2026.");
        }
    }

    [Fact]
    public void TheTableCoversEverySeat_soItCannotPassBySayingNothing()
    {
        var rows = ReadTable();

        // THE PRESENCE HALF. Derive what the table MUST cover from SessionRoles itself, so teaching the
        // product a fifth role makes this fail with the missing row named, rather than passing over a table
        // that has silently stopped describing the fleet.
        var expected = SessionRoles.All
            .SelectMany(role => OriginKinds.Select(origin => (role, origin)))
            .ToList();

        var have = rows.Select(r => (r.Role, r.Origin)).ToHashSet();

        var missing = expected.Where(e => !have.Contains(e)).ToList();
        Assert.True(missing.Count == 0,
            $"The supervision table in {DocRelativePath} does not name: " +
            string.Join(", ", missing.Select(m => $"{m.role}/{m.origin}")) +
            ". Every role in SessionRoles.All crossed with every origin kind must appear, so the document " +
            "cannot describe a smaller fleet than the code has.");

        Assert.Equal(expected.Count, rows.Count);

        // And the verdicts are not all one word - a table of eight identical answers would satisfy the count
        // and prove nothing about the rule it claims to state.
        Assert.Contains(rows, r => r.Supervised);
        Assert.Contains(rows, r => !r.Supervised);
    }

    [Fact]
    public void TheScheduledArchitectRow_SaysSupervised_becauseTheOriginOutranksTheSeat()
    {
        // THE EXCEPTION TO THE ROW BELOW, NAMED so it is deliberate rather than a side effect of two arms
        // sharing one predicate. The schedule arm asks "was anyone at a keyboard when this started?", which
        // is a different question from "which seat is this?", and it wins: an Architect a cron fired has
        // nobody it can report to, and the owner's standing rule is that scheduled runs escalate by email
        // rather than sit red on his roster.
        //
        // This is why the code and this document must both say "an Architect A PERSON STARTED is
        // human-facing", and never the unqualified "an Architect is never supervised".
        var rows = ReadTable();

        var scheduled = Assert.Single(rows, r => r.Role == SessionRoles.Architect && r.Origin == "schedule");
        Assert.True(scheduled.Supervised);
    }

    [Fact]
    public void TheArchitectRow_SaysHumanFacing_TheOwnersRulingOf6September2026()
    {
        // The one row this change is about, asserted BY NAME rather than only through the sweep above. The
        // sweep proves the document and the code agree; it would go on passing if somebody moved both of
        // them back together. This says what the owner decided, so changing it is a deliberate act with his
        // ruling in front of you: "parking the architect seat is wrong. the architect is always the session
        // i talk to."
        var rows = ReadTable();

        var architect = Assert.Single(rows, r => r.Role == SessionRoles.Architect && r.Origin == "(none)");
        Assert.False(architect.Supervised);

        // And through the real predicate, on the shape that actually reaches it: an Architect resolved from
        // an explicit role, carrying the controller of whoever spawned it.
        Assert.False(SessionOrdering.IsSupervised(new SessionDto
        {
            SessionId = "arch",
            Name = "arch",
            ActivityState = "WaitingForInput",
            SessionRole = SessionRoles.Architect,
            IsControlled = true,
            ControllerSessionId = "whoever-opened-it",
        }));
    }

    /// <summary>
    /// The rows between the two markers. Fails loudly - naming the file and the marker - when the fence is
    /// gone or the table between it is empty, rather than returning an empty list that would let every
    /// assertion above pass over nothing.
    /// </summary>
    private static IReadOnlyList<Row> ReadTable()
    {
        var path = Path.Combine(RepoRoot(), DocRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"The design document was not found at {path}.");

        var text = File.ReadAllText(path).Replace("\r\n", "\n");

        var begin = text.IndexOf(Begin, StringComparison.Ordinal);
        Assert.True(begin >= 0,
            $"{DocRelativePath} no longer contains the marker {Begin}. The supervision table is the written " +
            "half of SessionOrdering.IsSupervised and this test is what holds the two together - if the " +
            "table has moved, move the marker with it; do not delete it.");

        var end = text.IndexOf(End, begin, StringComparison.Ordinal);
        Assert.True(end > begin, $"{DocRelativePath} contains {Begin} but no closing {End}.");

        var body = text[(begin + Begin.Length)..end];

        // THE FENCE IS NOT ENOUGH ON ITS OWN - IT MUST BE A REAL MARKDOWN TABLE. Eight pipe-prefixed lines
        // inside a fenced code block would parse here perfectly and render as a code sample: the document a
        // person opens would state NO RULE at all while this test went on passing. So a code fence between
        // the markers is refused outright, and the header and separator rows are REQUIRED, in that order,
        // with nothing above the header counted as a row.
        Assert.DoesNotContain("```", body, StringComparison.Ordinal);

        var lines = body.Split('\n').Select(l => l.Trim()).ToList();
        var headerAt = lines.FindIndex(l => l.StartsWith("| Resolved role", StringComparison.Ordinal));
        Assert.True(headerAt >= 0,
            $"The supervision table in {DocRelativePath} has no \"| Resolved role | Origin kind | Verdict |\" " +
            "header row between its markers. Without a header the rows are not a table, so the document " +
            "would render as nothing while this test carried on reading the raw lines.");
        Assert.True(headerAt + 1 < lines.Count && lines[headerAt + 1].StartsWith("|---", StringComparison.Ordinal),
            $"The supervision table in {DocRelativePath} has a header with no separator row beneath it, so " +
            "markdown will not render it as a table.");

        var rows = new List<Row>();
        foreach (var trimmed in lines.Skip(headerAt + 2))
        {
            if (trimmed.Length == 0 || trimmed[0] != '|') continue;

            var cells = trimmed.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            Assert.True(cells.Length == 3,
                $"A row of the supervision table in {DocRelativePath} has {cells.Length} cells, not 3: " +
                $"\"{trimmed}\". A malformed row FAILS rather than being skipped - skipping is how a seat " +
                "silently stops being covered while everything stays green.");

            var verdict = cells[2];
            Assert.True(verdict is "SUPERVISED" or "HUMAN-FACING",
                $"The supervision table in {DocRelativePath} has the verdict \"{verdict}\" for " +
                $"{cells[0]}/{cells[1]}. The only two answers are SUPERVISED and HUMAN-FACING - a third " +
                "word means the document is saying something this rule cannot express.");

            rows.Add(new Row(cells[0], cells[1], verdict == "SUPERVISED"));
        }

        Assert.NotEmpty(rows);
        // A seat named twice, with two different verdicts, would let the coverage check pass while the
        // document contradicted itself. One row per seat.
        Assert.Equal(rows.Count, rows.Select(r => (r.Role, r.Origin)).Distinct().Count());
        return rows;
    }

    /// <summary>
    /// The repository root, located from this source file's own path - the pattern the other document guards
    /// in this repository already use (<c>WorkflowStoreTests</c>, <c>SpokenPhraseTests</c>), because the
    /// suites are run with <c>dotnet test</c> from a checkout and a bin-relative path breaks under different
    /// runners.
    ///
    /// WHAT THIS DOES NOT COVER, said plainly rather than left to be discovered. <c>CallerFilePath</c> is
    /// baked in at COMPILE time, so it names the tree this assembly was BUILT from, not one sitting beside
    /// the assembly as it runs. Copy the built binaries to another machine and the path is simply absent -
    /// which fails, loudly, and is the safe direction. The unsafe direction is narrow but real: build here,
    /// then change the checkout underneath, and this reads that checkout rather than the code under test. It
    /// can only pass WRONGLY when the other tree's table happens to agree with this assembly's rule, in
    /// which case there was nothing to report anyway.
    ///
    /// The check below closes the remaining gap - a path that exists but is not this repository at all. It
    /// asserts both files this guard is about are present, so a stale or unrelated directory fails with the
    /// path named instead of being read as though it were the product.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // this file: <repo>/src/CcDirector.Gateway.UnitTests/SupervisionRuleMatchesTheDesignDocumentTests.cs
        var dir = Path.GetDirectoryName(thisFile)!;
        var root = Path.GetFullPath(Path.Combine(dir, "..", ".."));

        foreach (var marker in new[]
                 {
                     Path.Combine("src", "CcDirector.Gateway.Contracts", "SessionOrdering.cs"),
                     Path.Combine("docs", "new_architecture", "session-roles-semantics.md"),
                 })
        {
            Assert.True(File.Exists(Path.Combine(root, marker)),
                $"Resolved the repository root to {root}, but it does not contain {marker}. This guard was " +
                "built from a tree it can no longer find, so it is not reading the product - it is reading " +
                "whatever happens to sit at that path. Run the suites from a checkout.");
        }

        return root;
    }
}
