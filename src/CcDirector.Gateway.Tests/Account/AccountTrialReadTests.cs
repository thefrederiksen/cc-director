using System;
using System.Linq;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// The free Pro trial READ (issue #1243) - the path that tells a member their trial is running.
///
/// The trial itself was never broken. It is granted at hosted enrolment, stored in <c>account_trials</c>, and
/// read for the entitlement decision; a live row sat in the production database with fourteen days on it
/// while every screen stayed silent, because nothing could ask. These tests cover the read that was missing
/// and, above all, what it says when it CANNOT ANSWER.
///
/// THE ONE THAT MATTERS MOST is <see cref="An_UNREADABLE_ledger_is_UNKNOWN_and_is_never_described_as_no_trial"/>.
/// Collapsing "I could not find out" into "you have no trial" is the failure this whole shape exists to
/// prevent: it tells a member with twelve days left that they have nothing, and it does it silently, on a
/// billing page, at the moment they are deciding whether to trust the product. Every other test here is about
/// saying something TRUE; that one is about refusing to say something comfortable.
///
/// Four states, three partitions. Expired and never-granted are split because a SENTENCE cannot fold them -
/// "your Pro trial ended on 17 August" is false for someone who never had one. The safety partition is still
/// three: running / known-not-running / unknown, and only the last may never be folded into the others.
///
/// Revert-proved, one production line at a time, each mutation confirmed present by diff before the run:
///  - In <c>TrialRegistry.ReadStatus</c>, return <c>NeverGranted</c> where it returns <c>Expired</c>:
///    <see cref="An_ended_trial_is_EXPIRED_and_still_names_the_day_it_ended"/> and
///    <see cref="A_member_whose_trial_ended_is_never_told_they_never_had_one"/> go RED.
///  - In <c>TrialRegistry.ReadStatus</c>, return <c>NeverGranted</c> where it returns <c>Unreadable</c> - the
///    exact collapse under proof: <see cref="An_UNREADABLE_ledger_is_UNKNOWN_and_is_never_described_as_no_trial"/>
///    goes RED.
///  - In <c>TrialRegistry.ReadStatus</c>, weaken <c>nowUtc &lt; row.ExpiresAtUtc</c> to <c>&lt;=</c>:
///    <see cref="At_the_exact_expiry_instant_the_trial_has_ENDED_not_one_tick_later"/> goes RED.
///  - In <c>AccountTrialEndpoint.DaysRemaining</c>, swap <c>Math.Ceiling</c> for <c>Math.Floor</c>:
///    <see cref="A_part_day_still_counts_as_a_day_so_the_last_day_never_reads_zero"/> goes RED.
/// </summary>
public sealed class AccountTrialReadTests : IDisposable
{
    private const string Subject = "sub-trial-read";

    /// <summary>A fixed instant, so the fourteen-day window is judged exactly rather than in whichever
    /// direction the clock happens to be pointing when the suite runs.</summary>
    private static readonly DateTime Granted = new(2026, 8, 3, 3, 42, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    // -------------------------------------------------------------------------------------------------
    // The ledger read: four states kept apart.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_running_trial_is_ACTIVE_and_carries_both_of_its_instants()
    {
        // The state the production row is in right now. A surface needs the END to name a date, and the START
        // is what lets it say how long the trial has been running without inventing one.
        var db = _harness.Open();
        var trials = new TrialRegistry(db);
        trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, Granted);

        var status = trials.ReadStatus(Subject, Granted.AddDays(2));

        Assert.Equal(TrialStatusKind.Active, status.Kind);
        Assert.Equal(Granted, status.StartedAtUtc);
        Assert.Equal(Granted.AddDays(14), status.ExpiresAtUtc);
    }

    [Fact]
    public void An_ended_trial_is_EXPIRED_and_still_names_the_day_it_ended()
    {
        // EXPIRED, not "none". The row is deliberately kept after the window closes - it is what stops a
        // second trial - so the date it ended is knowledge we hold and can state.
        var db = _harness.Open();
        var trials = new TrialRegistry(db);
        trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, Granted);

        var status = trials.ReadStatus(Subject, Granted.AddDays(20));

        Assert.Equal(TrialStatusKind.Expired, status.Kind);
        Assert.Equal(Granted.AddDays(14), status.ExpiresAtUtc);
    }

    [Fact]
    public void An_account_that_never_had_a_trial_is_NEVER_GRANTED_and_not_expired()
    {
        // The control that keeps the split honest in the other direction. A paying member who never took a
        // trial is in this state, and telling them theirs "ended" would be a plain falsehood.
        var db = _harness.Open();
        var trials = new TrialRegistry(db);

        var status = trials.ReadStatus("sub-never-had-one", Granted);

        Assert.Equal(TrialStatusKind.NeverGranted, status.Kind);
        Assert.Null(status.ExpiresAtUtc);
    }

    [Fact]
    public void At_the_exact_expiry_instant_the_trial_has_ENDED_not_one_tick_later()
    {
        // Boundaries are where a policy is decided. This mirrors the assertion the entitlement side already
        // pins, and it is here as well because the two now share ONE comparison - so if that comparison is
        // ever weakened, the display and the access decision are wrong together and this catches it.
        var db = _harness.Open();
        var trials = new TrialRegistry(db);
        trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, Granted);

        var oneTickBefore = trials.ReadStatus(Subject, Granted.AddDays(14).AddTicks(-1));
        var atExpiry = trials.ReadStatus(Subject, Granted.AddDays(14));

        Assert.Equal(TrialStatusKind.Active, oneTickBefore.Kind);
        Assert.Equal(TrialStatusKind.Expired, atExpiry.Kind);
    }

    [Fact]
    public void The_access_decision_and_the_displayed_state_can_never_disagree()
    {
        // Evaluate is now a FOLD over ReadStatus rather than a second read, so there is one row load and one
        // expiry comparison in the type. This asserts the fold at every state: what the member is told and
        // what the paywall does are answers to the same read, not two reads that happen to agree today.
        var db = _harness.Open();
        var trials = new TrialRegistry(db);
        trials.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, Granted);

        foreach (var moment in new[] { Granted, Granted.AddDays(13), Granted.AddDays(14), Granted.AddDays(99) })
        {
            var shown = trials.ReadStatus(Subject, moment);
            var access = trials.Evaluate(Subject, moment);

            var runningPerDisplay = shown.Kind == TrialStatusKind.Active;
            var runningPerAccess = access.Outcome == TrialOutcome.Active;

            Assert.True(runningPerDisplay == runningPerAccess,
                $"at {moment:O} the display says running={runningPerDisplay} and the access decision says " +
                $"running={runningPerAccess}. These must come from one read - a member told their trial is " +
                $"live while the paywall refuses them (or the reverse) is the worst outcome of the two paths " +
                $"drifting.");
        }
    }

    // -------------------------------------------------------------------------------------------------
    // The description: what a member is actually shown.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void An_active_trial_is_described_with_its_day_count_and_its_end_date()
    {
        // The sentence the issue asks for, folded on the Gateway so every surface renders the same one.
        var status = new TrialStatus(TrialStatusKind.Active, Granted, Granted.AddDays(14));

        var dto = AccountTrialEndpoint.Describe(status, Granted.AddDays(2));

        Assert.Equal(AccountTrialDto.StateActive, dto.State);
        Assert.Equal(12, dto.DaysRemaining);
        Assert.Equal(Granted.AddDays(14), dto.EndsAtUtc);
        Assert.Contains("12 days left", dto.Message, StringComparison.Ordinal);
        Assert.Contains("17 August 2026", dto.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_day_never_reads_zero()
    {
        // Pins the FLOOR OF ONE specifically. With hours left, a plain round-down would print "0 days left"
        // beside access that still works, which reads as an expiry that has not happened - the product
        // contradicting itself on the last day of the window, precisely the day a member is deciding whether
        // to pay. This case alone does NOT prove the rounding direction: the clamp rescues a round-down here,
        // which is why the test below exists and why this one no longer claims to cover it.
        var status = new TrialStatus(TrialStatusKind.Active, Granted, Granted.AddDays(14));

        var lastHours = AccountTrialEndpoint.Describe(status, Granted.AddDays(13).AddHours(20));

        Assert.Equal(1, lastHours.DaysRemaining);
        Assert.Contains("1 day left", lastHours.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1 days left", lastHours.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_part_day_still_counts_as_a_day_so_time_left_is_never_rounded_away()
    {
        // THE ROUNDING DIRECTION, pinned where the clamp cannot rescue it: twelve days and six hours remain.
        // Rounding DOWN reports 12 and quietly deletes a quarter of a day of Pro from what the member is told
        // they have - always in the direction that under-states what they were promised.
        //
        // Added because the first version of this suite did not have it: the round-down mutation passed every
        // test, so the ceiling was unproven while appearing covered. A revert-proof that fails to redden is
        // the finding, not the inconvenience.
        var status = new TrialStatus(TrialStatusKind.Active, Granted, Granted.AddDays(14));

        var partDay = AccountTrialEndpoint.Describe(status, Granted.AddDays(1).AddHours(18));

        Assert.Equal(13, partDay.DaysRemaining);
        Assert.Contains("13 days left", partDay.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_whose_trial_ended_is_never_told_they_never_had_one()
    {
        var status = new TrialStatus(TrialStatusKind.Expired, Granted, Granted.AddDays(14));

        var dto = AccountTrialEndpoint.Describe(status, Granted.AddDays(20));

        Assert.Equal(AccountTrialDto.StateExpired, dto.State);
        Assert.Equal(Granted.AddDays(14), dto.EndsAtUtc);
        Assert.Contains("ended on 17 August 2026", dto.Message, StringComparison.Ordinal);
        Assert.Null(dto.DaysRemaining);
    }

    [Fact]
    public void A_member_who_never_had_a_trial_is_never_told_theirs_ended()
    {
        // The other direction of the same split, and the one a paying member meets.
        var dto = AccountTrialEndpoint.Describe(new TrialStatus(TrialStatusKind.NeverGranted), Granted);

        Assert.Equal(AccountTrialDto.StateNone, dto.State);
        Assert.DoesNotContain("ended", dto.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expired", dto.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(dto.EndsAtUtc);
        Assert.Null(dto.DaysRemaining);
    }

    // -------------------------------------------------------------------------------------------------
    // The state this whole shape exists for.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void An_UNREADABLE_ledger_is_UNKNOWN_and_is_never_described_as_no_trial()
    {
        // THE ONE THAT MATTERS. A failed read is ignorance, not absence. Answered as "none" it would tell a
        // member on day two of their trial that they have nothing - confidently, on a page about what they
        // are entitled to, with no way for them to tell it was a database error.
        var dto = AccountTrialEndpoint.Describe(new TrialStatus(TrialStatusKind.Unreadable), Granted);

        Assert.Equal(AccountTrialDto.StateUnknown, dto.State);
        Assert.NotEqual(AccountTrialDto.StateNone, dto.State);
        Assert.NotEqual(AccountTrialDto.StateExpired, dto.State);

        // And it must SAY so, not merely be flagged. A surface renders this verbatim, so the correction to
        // the reader's natural assumption has to be in the sentence itself.
        Assert.Contains("does not mean you have no trial", dto.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_answer_carries_no_date_and_no_day_count()
    {
        // A number beside "we could not tell" invites exactly the reading this state prevents. Absent, never
        // zero: a zero day count is a statement, and it is one we have no evidence for.
        var dto = AccountTrialEndpoint.Describe(new TrialStatus(TrialStatusKind.Unreadable), Granted);

        Assert.Null(dto.DaysRemaining);
        Assert.Null(dto.EndsAtUtc);
        Assert.Null(dto.StartedAtUtc);
    }

    [Fact]
    public void An_active_read_with_no_end_instant_is_answered_UNKNOWN_rather_than_inventing_a_date()
    {
        // A contradiction, not a state to render. The alternative to refusing here is printing a date derived
        // from nothing on a page telling somebody what they are entitled to - and it would look completely
        // ordinary, which is what makes it worth a test.
        var dto = AccountTrialEndpoint.Describe(new TrialStatus(TrialStatusKind.Active, Granted), Granted);

        Assert.Equal(AccountTrialDto.StateUnknown, dto.State);
        Assert.Null(dto.EndsAtUtc);
        Assert.Null(dto.DaysRemaining);
    }

    [Fact]
    public void The_contract_offers_no_boolean_that_could_absorb_the_unknown_state()
    {
        // A STRUCTURAL guard, not a behavioural one. Every test above can pass while somebody adds a helpful
        // `TrialActive` boolean to the contract - and from that moment every consumer has a comfortable field
        // to read instead of the state, and each one silently answers "is a trial running?" with "no" in the
        // one case where the honest answer is "I could not find out". The absence of that field is what forces
        // a reader to look at State and decide what to do about unknown.
        var offenders = typeof(AccountTrialDto).GetProperties()
            .Where(p => p.PropertyType == typeof(bool) || p.PropertyType == typeof(bool?))
            .Select(p => p.Name)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "AccountTrialDto must expose no boolean: a two-valued field makes the unknown/none choice for " +
            "every consumer, at every site, silently. Found: " + string.Join(", ", offenders) +
            ". Read State instead and handle StateUnknown explicitly.");
    }
}
