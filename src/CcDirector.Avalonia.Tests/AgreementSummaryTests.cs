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
    public void ACleanFleet_IsZeroOverEveryone_AndEveryCheckPasses()
    {
        var sum = AgreementCheck.Summarize(
            new[] { Row("a"), Row("b"), Row("c") },
            Array.Empty<AgreementCheck.Finding>());

        Assert.Equal(0, sum.Disagreements);
        Assert.Equal(3, sum.LiveSessions);
        Assert.Equal(0, sum.IndeterminateRows);

        Assert.All(sum.AllChecks, c => Assert.True(c.PassedEverywhere));
    }

    /// <summary>
    /// A CHECK CANNOT PASS WHILE A FINDING SAYS IT FAILED - which the report used to claim, in prose,
    /// four lines under the finding itself.
    ///
    /// The verdict paragraph was a fixed block asserting all five checks passed, printed unconditionally
    /// AFTER the findings. So a run could describe a broken law in detail and then announce that the law
    /// holds over every live session. The arithmetic above it had already been extracted and pinned; the
    /// prose had not, so it stayed wrong in precisely the way the numbers had been - one level out.
    ///
    /// PASS now means "this check found nothing" and cannot mean anything else. Each of these is a shape
    /// the fault-injection suite already produces, so the two files cannot drift apart.
    /// </summary>
    [Theory]
    [InlineData("stamp-not-fold")]
    [InlineData("law-broken")]
    [InlineData("desktop-vs-gateway")]
    [InlineData("two-different-pixels")]
    [InlineData("palette-missing")]
    public void AFindingOfAnyKind_FailsExactlyItsOwnCheck_AndNoOther(string kind)
    {
        var sum = AgreementCheck.Summarize(new[] { Row("x") }, new[] { F("x", kind) });

        Assert.Equal(1, sum.Disagreements);

        Assert.Equal(kind != "stamp-not-fold", sum.StampIsFold.Passed);
        Assert.Equal(kind != "law-broken", sum.Law.Passed);
        Assert.Equal(kind != "desktop-vs-gateway", sum.DesktopAgreed.Passed);
        // One line covers both pixel faults: a colour the client cannot paint and a colour it paints
        // differently are the same promise broken - "every device shows the same thing".
        Assert.Equal(kind is not ("two-different-pixels" or "palette-missing"), sum.SamePixels.Passed);

        // None of these is terminal, so every check still RAN on every row.
        Assert.All(sum.AllChecks, c => Assert.Equal(0, c.NotGraded));
    }

    /// <summary>
    /// AN UNSTAMPED ROW IS TERMINAL, AND THE CHECKS AFTER IT DID NOT PASS - THEY DID NOT RUN.
    ///
    /// Compare stops on an unstamped row because there is nothing to compare: no stamped answer to test
    /// against the fold, no colour to test the law on, no colour to look up in a palette. So no findings
    /// come back from those four checks - and the first version of this summary read that silence as PASS
    /// and printed it. Absence of evidence, published as evidence, on the row where the tool KNOWS it was
    /// blind.
    ///
    /// The theory above USED TO ASSERT THIS BUG. It had an [InlineData("unstamped")] case demanding that
    /// every other check still read as passed - the defect, written down as the expected behaviour, by me,
    /// in the file whose whole job is to stop exactly that. The third time in this mission that a test I
    /// wrote defended the defect it was meant to catch. It is removed and replaced with this.
    ///
    /// Found by the ninth inspection pass of pull request 1606 - the same pattern as the fourth, fifth,
    /// seventh and eighth, one check earlier each time.
    /// </summary>
    [Fact]
    public void AnUnstampedRow_LeavesTheLaterChecksNOTGRADED_NeverPassed()
    {
        var sum = AgreementCheck.Summarize(new[] { Row("no-stamp") }, new[] { F("no-stamp", "unstamped") });

        // Check 1 ran and failed. It is the only one that got to run at all.
        Assert.False(sum.StampPresent.Passed);
        Assert.Equal(0, sum.StampPresent.NotGraded);

        // The other four found nothing - because they never looked.
        foreach (var check in new[] { sum.StampIsFold, sum.Law, sum.SamePixels, sum.DesktopAgreed })
        {
            Assert.Equal(1, check.NotGraded);
            Assert.Equal(0, check.Graded);
            // The distinction the whole type exists for: it found nothing where it ran (vacuously true,
            // it ran nowhere) and it emphatically did NOT pass over the fleet.
            Assert.True(check.Passed);
            Assert.False(check.PassedEverywhere);
            Assert.Contains("NOT GRADED", check.Line);
            Assert.DoesNotContain("PASS", check.Line); // never the unqualified word
        }
    }

    /// <summary>
    /// FAILING SOMEWHERE AND NEVER LOOKING ELSEWHERE ARE BOTH TRUE AT ONCE, AND BOTH GET SAID.
    ///
    /// The gap that let the tenth bug live: every test here covered failure-only or not-graded-only, never
    /// both. So Line could return early on a failure and silently drop NotGraded, and the whole suite
    /// stayed green while a check that ran on one row of two printed a confident bare "FAIL (1)" under a
    /// header saying "over 2 live session(s)".
    ///
    /// Not the bare PASS of the ninth bug - the same claim-without-evidence one step sideways. A partial
    /// truth IS the failure mode here, not an acceptable summary of a complicated one. Every version of
    /// this defect, in all ten passes, has been some true half printed without its qualifier.
    ///
    /// Found by the tenth inspection pass of pull request 1606.
    /// </summary>
    [Fact]
    public void ACheckThatBothFailedAndDidNotRunEverywhere_SaysBoth()
    {
        // Row A has no stamp, so it stops every later check. Row B breaks the law. The law check therefore
        // FAILED on B and NEVER RAN on A - and the reader must be told both, or "FAIL (1)" reads as a
        // complete verdict over two sessions.
        var sum = AgreementCheck.Summarize(
            new[] { Row("a-no-stamp"), Row("b-law") },
            new[] { F("a-no-stamp", "unstamped"), F("b-law", "law-broken") });

        var law = sum.Law;
        Assert.Equal(1, law.Failures);
        Assert.Equal(1, law.NotGraded);
        Assert.Equal(1, law.Graded);
        Assert.False(law.Passed);
        Assert.False(law.PassedEverywhere);

        // BOTH halves, in the one line the report prints.
        Assert.Contains("FAIL (1)", law.Line);
        Assert.Contains("NOT GRADED on 1", law.Line);
        Assert.Contains("1 of 2", law.Line);
    }

    /// <summary>
    /// TWO CAUSES, ONE CHECK - the shape that had no test, which is why the eleventh bug lived.
    ///
    /// The desktop comparison is stopped by TWO different things: an unstamped row (no answer arrived, so
    /// nothing downstream can be checked) and an indeterminate row (the Gateway overwrote the fact it
    /// needs). IndeterminateRows counts only the second. DesktopAgreed.NotGraded counts both.
    ///
    /// They sat side by side under names close enough to swap, and the report duly printed the narrow
    /// count under the broad meaning: "the desktop comparison was not graded on 1 row" on a fleet where it
    /// had not been graded on two - and then added that every other check ran on them, which was true of
    /// the indeterminate row and false of the unstamped one. Two true sentences about different rows,
    /// welded into one false one.
    ///
    /// Every test here covered one cause or the other. None covered a roster carrying both, so nothing
    /// could ever see the two numbers disagree. Same gap, eleventh instance.
    /// </summary>
    [Fact]
    public void TheDesktopCheckCountsBOTHReasonsItDidNotRun_NotJustTheInterestingOne()
    {
        var sum = AgreementCheck.Summarize(
            new[] { Row("a-no-stamp"), Row("b-ambiguous"), Row("c-fine") },
            new[] { F("a-no-stamp", "unstamped"), F("b-ambiguous", "indeterminate") });

        // The CAUSE counts are each about their own cause, and neither is the check's scope.
        Assert.Equal(1, sum.Unstamped);
        Assert.Equal(1, sum.IndeterminateRows);

        // The CHECK knows it was blocked twice, for two different reasons, and says so.
        Assert.Equal(2, sum.DesktopAgreed.NotGraded);
        Assert.Equal(1, sum.DesktopAgreed.Graded);
        Assert.False(sum.DesktopAgreed.PassedEverywhere);
        Assert.Contains("NOT GRADED on 2", sum.DesktopAgreed.Line);

        // And the other checks are blocked ONLY by the unstamped row - the indeterminate one does not
        // touch them. That asymmetry is exactly what the old sentence flattened.
        Assert.Equal(1, sum.Law.NotGraded);
        Assert.Equal(1, sum.StampIsFold.NotGraded);
        Assert.Equal(1, sum.SamePixels.NotGraded);
    }

    /// <summary>
    /// ONE REASON IS NOT TWO REASONS - the twelfth finding, and the smallest of them all.
    ///
    /// The headline hard-coded "for two different reasons" and then listed the causes conditionally. So a
    /// live run with a single indeterminate row printed: "NOT GRADED on 1 of 16 row(s), for two different
    /// reasons", followed by exactly one reason. The count was right. The cause list was right. The
    /// CONNECTIVE between them overstated - there is one reason on that run, drawn from a set of two
    /// possible reasons, and those are different sentences.
    ///
    /// The inspector found it by RUNNING the tool, because no test could reach the sentence: it was a
    /// Console.WriteLine, like every other prose defect on this pull request. That is why this is now a
    /// function returning lines instead of a print, and why these tests exist at all.
    /// </summary>
    [Fact]
    public void OneCauseIsNotTwoReasons()
    {
        var sum = AgreementCheck.Summarize(
            new[] { Row("a"), Row("b-ambiguous") },
            new[] { F("b-ambiguous", "indeterminate") });

        var lines = sum.DesktopNotGradedLines();

        Assert.Equal(2, lines.Count); // the headline plus exactly one cause
        Assert.Contains("NOT GRADED on 1 of 2", lines[0]);
        Assert.DoesNotContain("different reasons", lines[0]);
        Assert.Contains("[indeterminate]", lines[1]);
        Assert.DoesNotContain(lines, l => l.Contains("[unstamped]"));
    }

    [Fact]
    public void TwoCausesEarnThePlural_AndNameThemseparately()
    {
        var sum = AgreementCheck.Summarize(
            new[] { Row("a-no-stamp"), Row("b-ambiguous"), Row("c-fine") },
            new[] { F("a-no-stamp", "unstamped"), F("b-ambiguous", "indeterminate") });

        var lines = sum.DesktopNotGradedLines();

        Assert.Equal(3, lines.Count); // headline plus BOTH causes
        Assert.Contains("NOT GRADED on 2 of 3", lines[0]);
        Assert.Contains("for 2 different reasons", lines[0]);
        Assert.Contains(lines, l => l.Contains("[unstamped]"));
        Assert.Contains(lines, l => l.Contains("[indeterminate]"));
    }

    /// <summary>
    /// The control that stops "never overstate" being satisfied by never speaking: a check that ran on
    /// everything says NOTHING about not being graded, because there is nothing to say.
    /// </summary>
    [Fact]
    public void AFullyGradedDesktopCheck_SaysNothingAboutNotBeingGraded()
    {
        var sum = AgreementCheck.Summarize(new[] { Row("a"), Row("b") }, Array.Empty<AgreementCheck.Finding>());

        Assert.Empty(sum.DesktopNotGradedLines());
    }

    /// <summary>
    /// THE EXIT CODE - the only claim here a MACHINE reads, and therefore the one that could do the most
    /// damage, because a script cannot read the caveat on the next line.
    ///
    /// Main returned 1 whenever ANY finding existed. The contract says 1 means disagreements. The Summary
    /// says an indeterminate row is NOT a disagreement. So an indeterminate-only run printed "AGREEMENT
    /// NUMBER: 0 disagreement(s)" and exited 1 - the report table had learned the distinction and the
    /// process contract had not.
    ///
    /// And 0 would have been no better: it claims a clean fleet the instrument never fully read. Both
    /// available answers were false, which is the tell that a third was needed. 3 is "no disagreements AND
    /// I could not grade everything" - the machine-readable version of the same honesty the prose spent
    /// four passes learning.
    ///
    /// Thirteenth instance of one defect, and the first in an interface with no human in it.
    /// </summary>
    [Fact]
    public void ACleanFullyGradedFleet_ExitsZero()
    {
        var sum = AgreementCheck.Summarize(new[] { Row("a"), Row("b") }, Array.Empty<AgreementCheck.Finding>());
        Assert.Equal(0, sum.ExitCode);
    }

    [Fact]
    public void RealDisagreements_ExitOne()
    {
        var sum = AgreementCheck.Summarize(new[] { Row("a") }, new[] { F("a", "law-broken") });
        Assert.Equal(1, sum.ExitCode);
    }

    [Fact]
    public void AnIndeterminateOnlyRun_ExitsThree_NeitherCleanNorADisagreement()
    {
        var sum = AgreementCheck.Summarize(
            new[] { Row("a"), Row("b-ambiguous") },
            new[] { F("b-ambiguous", "indeterminate") });

        // The headline is honest...
        Assert.Equal(0, sum.Disagreements);
        // ...and the exit code must be too. NOT 1 (there is no disagreement) and NOT 0 (the check did not
        // grade the whole fleet). Both of those are the false half this mission is made of.
        Assert.Equal(3, sum.ExitCode);
    }

    [Fact]
    public void AnUnstampedRow_AlsoExitsOne_BecauseItIsARealDefect_NotMerelyUngradeable()
    {
        // An unstamped row is a genuine finding in its own right - the Gateway sent no answer - AND it
        // blocks the four checks below it. The disagreement wins: 1, not 3.
        var sum = AgreementCheck.Summarize(new[] { Row("a") }, new[] { F("a", "unstamped") });

        Assert.Equal(1, sum.Disagreements);
        Assert.Equal(1, sum.ExitCode);
    }

    /// <summary>
    /// The control: a genuinely clean fleet is the ONLY thing that may print the unqualified word.
    /// Without this, "never say PASS" would be trivially satisfiable by never saying it.
    /// </summary>
    [Fact]
    public void OnlyACleanFleetEarnsTheUnqualifiedWord()
    {
        var sum = AgreementCheck.Summarize(new[] { Row("a"), Row("b") }, Array.Empty<AgreementCheck.Finding>());

        Assert.All(sum.AllChecks, c =>
        {
            Assert.True(c.PassedEverywhere);
            Assert.Equal("PASS", c.Line);
        });
    }

    /// <summary>
    /// The one the eighth inspection named specifically: a definite desktop disagreement with ZERO
    /// unreadable rows. The old prose gated its "the desktop agrees on every one of them" line on
    /// DesktopNotGraded == 0 - which is true here - and so printed it directly beneath the disagreement
    /// it had just reported.
    /// </summary>
    [Fact]
    public void ADesktopDisagreement_WithNothingUnreadable_StillFailsTheDesktopCheck()
    {
        var sum = AgreementCheck.Summarize(
            new[] { Row("phone-dictation") },
            new[] { F("phone-dictation", "desktop-vs-gateway") });

        Assert.Equal(0, sum.IndeterminateRows);
        Assert.False(sum.DesktopAgreed.Passed);
        Assert.Equal(1, sum.Disagreements);
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
        Assert.Equal(1, sum.IndeterminateRows);
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
        Assert.Equal(1, sum.IndeterminateRows);
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
        Assert.Equal(0, sum.IndeterminateRows);
    }
}
