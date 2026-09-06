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

        var rows = new List<Row>();
        foreach (var line in text[(begin + Begin.Length)..end].Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '|') continue;

            var cells = trimmed.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length != 3) continue;
            if (cells[0] == "Resolved role") continue;                          // the header
            if (cells[0].StartsWith("---", StringComparison.Ordinal)) continue; // the separator

            var verdict = cells[2];
            Assert.True(verdict is "SUPERVISED" or "HUMAN-FACING",
                $"The supervision table in {DocRelativePath} has the verdict \"{verdict}\" for " +
                $"{cells[0]}/{cells[1]}. The only two answers are SUPERVISED and HUMAN-FACING - a third " +
                "word means the document is saying something this rule cannot express.");

            rows.Add(new Row(cells[0], cells[1], verdict == "SUPERVISED"));
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    /// <summary>The repository root, located from this source file's own path - the tests always run from a
    /// checkout, and bin-relative paths would break under different runners.</summary>
    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // this file: <repo>/src/CcDirector.Gateway.UnitTests/SupervisionRuleMatchesTheDesignDocumentTests.cs
        var dir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }
}
