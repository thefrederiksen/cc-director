using CcDirector.Gateway.Contracts;
using CcDirector.StateAgreementCheck;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// THE ARITHMETIC BEHIND THE SENTENCE PEOPLE QUOTE.
///
/// "Zero disagreements over sixteen sessions" is the whole output of this mission that anyone will ever
/// repeat. It was WRONG TWICE, and both times it was found by a human reading console output and doing
/// the sums in their head - the least reliable way to find anything, and it only worked because someone
/// bothered.
///
/// It was wrong twice because it was unreachable. The counting lived inline in Program.Report, printing
/// straight to the console, so no test could bind it, so it drifted out of step with Compare twice
/// without one test going red. This mission's own law, for the third time: AN UNBINDABLE SEAM IS AN
/// UNTESTABLE ONE, and the rule is the same whether the consumer is a rail or a reader.
///
/// The two wrong versions, kept here because the shape is the lesson:
///
///   1. It subtracted unreadable rows from the denominator, counted their CERTAIN findings in the
///      numerator anyway, and then printed "the number above says nothing about them". Three statements
///      that cannot all be true. Nothing caught it; the inspector reasoned it out from the code.
///   2. Before that, it printed "the desktop's fold agrees with the Gateway's" unqualified on a run that
///      had just admitted it could not read a row. The inspector caught that one by RUNNING the tool and
///      reading what it printed rather than what it meant.
///
/// THE RULE: every live session IS checked. Only ONE of the five checks - the desktop comparison - can be
/// defeated by an unreadable row, and only on that row. So the denominator is every live session, and an
/// unreadable row's certain findings count like anyone else's.
/// </summary>
public sealed class AgreementSummaryTests
{
    private static SessionDto Row(string id) => new() { SessionId = id, Name = id };

    private static AgreementCheck.Finding F(string id, string kind) =>
        new(id, id, kind, "detail");

    [Fact]
    public void ACleanFleet_IsZeroOverEveryone()
    {
        var sum = AgreementCheck.Summarize(
            new[] { Row("a"), Row("b"), Row("c") },
            Array.Empty<AgreementCheck.Finding>());

        Assert.Equal(0, sum.Disagreements);
        Assert.Equal(3, sum.LiveSessions);
        Assert.Equal(0, sum.DesktopNotGraded);
    }

    /// <summary>
    /// THE SHAPE THAT EXPOSED THE BUG, and the reason this file exists: ONE row that is both unreadable
    /// AND has a certain defect. The old arithmetic reported "1 disagreement over 0 graded sessions" and
    /// then said the number said nothing about the unreadable row - while that row's stamp-not-fold was
    /// the entire numerator.
    ///
    /// The honest answer: the row was checked. Four of the five checks ran on it and one of them found
    /// something. It counts, and it counts over a denominator that includes it.
    /// </summary>
    [Fact]
    public void AnUnreadableRowWithACertainDefect_CountsInBoth_AndTheDenominatorKeepsIt()
    {
        var sum = AgreementCheck.Summarize(
            new[] { Row("ambiguous") },
            new[] { F("ambiguous", "indeterminate"), F("ambiguous", "stamp-not-fold") });

        // The certain finding is a real disagreement and is counted.
        Assert.Equal(1, sum.Disagreements);
        // The row was checked - four of five checks ran on it - so it stays in the denominator. The old
        // version said "over 0 graded session(s)" here, which invited a divide-by-nothing reading of a
        // fleet that plainly had one session in it.
        Assert.Equal(1, sum.LiveSessions);
        // And the ONE check that could not run is reported separately, as itself.
        Assert.Equal(1, sum.DesktopNotGraded);
    }

    /// <summary>
    /// An unreadable row with NOTHING else wrong contributes no disagreement - the refusal is not itself
    /// a defect, it is the absence of a verdict. If this counted as a disagreement the tool would report
    /// a fleet as broken every time the Gateway prepared a voice summary.
    /// </summary>
    [Fact]
    public void AnUnreadableRowAlone_IsNotADisagreement()
    {
        var sum = AgreementCheck.Summarize(
            new[] { Row("a"), Row("quiet-ambiguous") },
            new[] { F("quiet-ambiguous", "indeterminate") });

        Assert.Equal(0, sum.Disagreements);
        Assert.Equal(2, sum.LiveSessions);
        Assert.Equal(1, sum.DesktopNotGraded);
    }

    /// <summary>
    /// Several findings on ONE row are several disagreements - the numerator counts findings, not rows.
    /// Stated because the denominator counts ROWS, and mixing the two units is how the first bug got in.
    /// </summary>
    [Fact]
    public void TheNumeratorCountsFindings_TheDenominatorCountsSessions()
    {
        var sum = AgreementCheck.Summarize(
            new[] { Row("bad"), Row("fine") },
            new[] { F("bad", "stamp-not-fold"), F("bad", "law-broken"), F("bad", "two-different-pixels") });

        Assert.Equal(3, sum.Disagreements);
        Assert.Equal(2, sum.LiveSessions);
        Assert.Equal(0, sum.DesktopNotGraded);
    }
}
