using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Rules;

/// <summary>One firing, as the evaluator hands it to whatever writes it down.</summary>
public sealed record RuleFiringDraft(
    Guid RuleId,
    string SessionId,
    string ScreenText,
    string Understanding,
    string Decision,
    string Reason,
    IReadOnlyList<RulePrimitiveRun> Runs,
    string TypedText,
    string Outcome,
    string Grounding);

/// <summary>What one evaluation pass did. A closed set, so a caller and a test both name the same thing.</summary>
public static class RulePassOutcomes
{
    /// <summary>This account has no rules at all.</summary>
    public const string NoRules = "no-rules";

    /// <summary>The session's facts could not be read - it is no longer on the roster.</summary>
    public const string SessionNotFound = "session-not-found";

    /// <summary>The screen could not be read. Unreadable is not evidence.</summary>
    public const string ScreenUnreadable = "screen-unreadable";

    /// <summary>The pass stopped before any rule was considered (working, or the screen did not change).</summary>
    public const string StoppedBeforeAnyRule = "stopped-before-any-rule";

    /// <summary>A pass on this same session was already in flight, so this one did nothing. See the
    /// serialisation comment on <see cref="RuleEvaluator"/>: two passes that overlap can both act past the
    /// cooldown and past the daily cap, because both read the firing record before either wrote to it.</summary>
    public const string AlreadyEvaluating = "already-evaluating";

    /// <summary>Every rule was considered and passed over, each with its reason.</summary>
    public const string NoCandidates = "no-candidates";

    /// <summary>The reply named something it was not offered, or was not an answer. Recorded as a refusal.</summary>
    public const string Refused = "refused";

    /// <summary>The agent read the screen against the instruction and declined. Recorded.</summary>
    public const string Declined = "declined";

    /// <summary>The act was given up before the keystroke. Recorded.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>The reply decided to ACT and its stated reason quoted text the screen does not contain, so
    /// the act was refused (Architect ruling A12). Recorded, with what was quoted and where it was not.</summary>
    public const string Ungrounded = "ungrounded";

    /// <summary>The rule is in dry run: it decided to act, and typed nothing.</summary>
    public const string DryRun = "dry-run";

    /// <summary>The rule is live, the text was typed, and the route confirmed it started a turn.</summary>
    public const string Acted = "acted";

    /// <summary>The text was typed and the route would not confirm it started a turn. NOT a failure:
    /// see <see cref="RuleSendResult.NotConfirmed"/>.</summary>
    public const string SendUnconfirmed = "send-unconfirmed";

    /// <summary>Nothing was typed, because the keystroke never left this Gateway.</summary>
    public const string NotSent = "not-sent";
}

/// <summary>What one evaluation pass did, and the firings it wrote.</summary>
public sealed record RulePass(string What, string Detail, IReadOnlyList<RuleFiringDraft> Recorded);

/// <summary>What the send seam can answer. THREE answers, not two, because "it did not work" hides a
/// distinction that matters: a keystroke that never left this Gateway is not the same event as one
/// that went out and whose arrival nobody would confirm.</summary>
public static class RuleSendOutcomes
{
    /// <summary>It went out and the route confirmed a turn started.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>It went out and the route would not confirm a turn started. NOT a failure. The prompt
    /// route answers this for a session whose turn is over in milliseconds - a plain shell, or an
    /// agent answering a picker - while the keystroke has in fact landed. Recorded as unconfirmed,
    /// with the screen named as the evidence, and never as a keystroke that did not happen.</summary>
    public const string NotConfirmed = "not-confirmed";

    /// <summary>It never left this Gateway - the machine is not connected. Nothing was typed.</summary>
    public const string NotSent = "not-sent";
}

/// <summary>What the send seam answered, and the words that go on the record.</summary>
public sealed record RuleSendResult(string What, string Detail)
{
    /// <summary>It went out and the route confirmed a turn started.</summary>
    public static RuleSendResult Confirmed() => new(RuleSendOutcomes.Confirmed, "");

    /// <summary>It went out and nobody would confirm it. <paramref name="detail"/> is what the route
    /// said, verbatim, so the record carries the route's own words rather than a paraphrase.</summary>
    public static RuleSendResult NotConfirmed(string detail) => new(RuleSendOutcomes.NotConfirmed, detail);

    /// <summary>It never left this Gateway. <paramref name="detail"/> says why.</summary>
    public static RuleSendResult NotSent(string detail) => new(RuleSendOutcomes.NotSent, detail);
}

/// <summary>
/// Everything the evaluator needs from the Gateway around it, as one seam - the same shape, and for the
/// same reason, as the supervisor's <c>ISupervisorEnvironment</c>: production wires the real store, the
/// real tunnel read, the real model and the real prompt route, and a test wires a fake with an instrumented
/// send seam, which is what makes "dry run types nothing" a counted fact rather than a missing log line.
/// </summary>
public interface IRuleEnvironment
{
    /// <summary>Every rule this account has.</summary>
    IReadOnlyList<SessionRule> Rules(TenantId tenant);

    /// <summary>Every firing of one rule, so the cooldown and the daily cap can be counted.</summary>
    IReadOnlyList<SessionRuleFiring> FiringsFor(TenantId tenant, Guid ruleId);

    /// <summary>The session's facts from the pushed roster snapshot, or null when it is no longer there.</summary>
    RuleSessionFacts? ReadSessionFacts(TenantId tenant, string sessionId);

    /// <summary>The session's LIVE screen rows, or null when it cannot be read.</summary>
    Task<IReadOnlyList<string>?> ReadScreenRowsAsync(
        TenantId tenant, string directorId, string sessionId, CancellationToken ct);

    /// <summary>Ask the agent the one question for this screen, and return its raw reply.</summary>
    Task<string?> AskAgentAsync(TenantId tenant, string prompt, CancellationToken ct);

    /// <summary>THE SEND SEAM: type text into the session. The only thing in this feature that can.</summary>
    Task<RuleSendResult> TypeIntoSessionAsync(
        TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct);

    /// <summary>Write one firing down.</summary>
    void RecordFiring(TenantId tenant, RuleFiringDraft draft);

    /// <summary>The clock, as a seam.</summary>
    DateTime NowUtc { get; }
}

/// <summary>
/// THE EVALUATOR (Session Rules mission, phase 2) - the thin vertical slice from a session going idle to
/// something being typed into it, with every step written down.
///
/// THE ORDER IS THE SAFETY. Free checks first, so the common case - a session that finished a turn
/// normally - costs one screen read and nothing else and never reaches a model. Then ONE agent call
/// covering every surviving rule (Architect ruling A5), whose reply is validated against what was offered.
/// Then the checks the agent staked its decision on. Then, immediately before the keystroke and not a
/// moment earlier, the screen is READ AGAIN: a session that has moved on since the decision is abandoned.
/// And a rule in dry run never reaches the send at all.
///
/// SILENCE IS NEVER A DECISION. Every outcome that is not "there was nothing to look at" is written down as
/// a firing - the decline, the abandonment, and the refusal of an unreadable reply included. A rule that
/// did nothing because this code threw looks exactly like a rule that considered the screen and declined,
/// unless the decline is on the record. So the record is the proof, and the absence of a keystroke is not.
/// </summary>
public sealed class RuleEvaluator
{
    private readonly IRuleEnvironment _env;
    private readonly RulePrimitiveRegistry _registry;

    // The last screen evaluated per session, so "has the screen changed" costs nothing. Never a source of
    // truth about a session - only about what this Gateway has already looked at.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(TenantId, string), string> _lastScreen = new();

    /// <param name="environment">Everything it reads and writes.</param>
    /// <param name="registry">The verified checks a reply may name. Defaults to what the product ships.</param>
    /// <exception cref="ArgumentNullException">The environment is null.</exception>
    public RuleEvaluator(IRuleEnvironment environment, RulePrimitiveRegistry? registry = null)
    {
        _env = environment ?? throw new ArgumentNullException(nameof(environment));
        _registry = registry ?? RulePrimitiveRegistry.Default;
    }

    /// <summary>
    /// Evaluate one idle transition. Returns what the pass did and the firings it wrote, so a caller and a
    /// test both read the same account of it rather than inferring one from what did not happen.
    /// </summary>
    public async Task<RulePass> EvaluateAsync(
        TenantId tenant, string directorId, string sessionId, CancellationToken ct)
    {
        var rules = _env.Rules(tenant);
        if (rules is null || rules.Count == 0)
            return Nothing(RulePassOutcomes.NoRules, "this account has no rules.");

        var facts = _env.ReadSessionFacts(tenant, sessionId);
        if (facts is null)
            return Nothing(RulePassOutcomes.SessionNotFound,
                $"session {sessionId} is not on the roster, so there is nothing to look at.");

        var rows = await _env.ReadScreenRowsAsync(tenant, directorId, sessionId, ct).ConfigureAwait(false);
        if (rows is null)
            return Nothing(RulePassOutcomes.ScreenUnreadable,
                "the session's screen could not be read, and unreadable is not evidence.");

        var screen = Join(rows);
        var key = (tenant, sessionId);
        _lastScreen.TryGetValue(key, out var previous);

        var candidates = RuleCandidateFilter.Choose(
            rules, facts, screen, previous, id => _env.FiringsFor(tenant, id), _env.NowUtc);

        _lastScreen[key] = screen;

        if (candidates.StoppedBecause is not null)
            return Nothing(RulePassOutcomes.StoppedBeforeAnyRule, candidates.StoppedBecause);

        if (candidates.Chosen.Count == 0)
            return Nothing(RulePassOutcomes.NoCandidates,
                string.Join(" ", candidates.Skipped.Select(s => s.Reason)));

        FileLog.Write($"[RuleEvaluator] sid={sessionId}: {candidates.Chosen.Count} rule(s) worth asking about");

        var prompt = RuleAgentContract.BuildPrompt(candidates.Chosen, rows, _registry);
        var raw = await _env.AskAgentAsync(tenant, prompt, ct).ConfigureAwait(false);
        var reading = RuleAgentContract.Read(raw, candidates.Chosen, _registry);

        if (reading.Refusal is not null)
        {
            // Recorded against EVERY rule that was in play. The pass covered all of them, none of them
            // fired, and each one's record has to show that its evaluation was refused rather than showing
            // nothing at all - which is exactly what a rule that crashed would show.
            var written = candidates.Chosen
                .Select(rule => Record(tenant, new RuleFiringDraft(
                    rule.Id, sessionId, screen, "", RuleDecisions.Refused, reading.Refusal,
                    Array.Empty<RulePrimitiveRun>(), "", "nothing was typed.",
                    RuleReasonGrounding.NotTheAgentsReason)))
                .ToList();
            return new RulePass(RulePassOutcomes.Refused, reading.Refusal, written);
        }

        var reply = reading.Reply!;
        var chosen = candidates.Chosen.First(r => r.Id == reply.RuleId);

        var runtime = new RuleRuntime(screen, facts.RepositoryPath, _env.NowUtc, FirstFailureUtc: null);
        var checks = RuleCheckRunner.Run(reply.Checks, runtime, _registry);

        // RULING A12: IS THE STATED REASON GROUNDED IN THE SCREEN IT WAS GIVEN? Computed before either
        // branch, so the answer is on the record whichever way the decision went, and so a run where this
        // never happened cannot look like a run where it happened and found nothing wrong.
        var grounding = RuleReasonGrounding.Check(reply.Reason, screen);

        if (reply.Decision == RuleDecisions.Decline)
        {
            // A decline stands even when its reason quotes something that is not there - declining is the
            // direction that does nothing, and the record should show what actually happened. But the
            // mismatch is NOTED rather than smoothed over, because it is the same unfaithfulness that
            // would be an act on evidence that was not there.
            var declined = Record(tenant, new RuleFiringDraft(
                chosen.Id, sessionId, screen, reply.Understanding, RuleDecisions.Decline, reply.Reason,
                checks.Runs, "", "declined - nothing was typed.", grounding.Statement));
            return new RulePass(RulePassOutcomes.Declined, reply.Reason, new[] { declined });
        }

        if (!grounding.IsGrounded)
        {
            // AN ACT ON EVIDENCE THAT WAS NOT THERE IS REFUSED. Recorded as a refusal with what was quoted
            // and where it was not, so the reader sees the mismatch rather than an act that never happened.
            var ungrounded = Record(tenant, new RuleFiringDraft(
                chosen.Id, sessionId, screen, reply.Understanding, RuleDecisions.Refused, reply.Reason,
                checks.Runs, "",
                "nothing was typed: the reason for acting quotes text this screen does not contain.",
                grounding.Statement));
            FileLog.Write($"[RuleEvaluator] sid={sessionId}: act REFUSED, {grounding.Statement}");
            return new RulePass(RulePassOutcomes.Ungrounded, grounding.Statement, new[] { ungrounded });
        }

        if (checks.Problem is not null)
            return Abandon(tenant, chosen, sessionId, screen, reply, checks.Runs, checks.Problem);

        // THE RE-READ. Immediately before the keystroke and after every decision has been made, because a
        // screen that moved on in between belongs to a different moment than the one that was judged.
        var rowsNow = await _env.ReadScreenRowsAsync(tenant, directorId, sessionId, ct).ConfigureAwait(false);
        if (rowsNow is null)
            return Abandon(tenant, chosen, sessionId, screen, reply, checks.Runs,
                "the screen could not be read again immediately before typing, so nothing was typed.");

        if (!string.Equals(Join(rowsNow), screen, StringComparison.Ordinal))
            return Abandon(tenant, chosen, sessionId, screen, reply, checks.Runs,
                "the screen changed between the decision and the keystroke, so the decision was about a " +
                "screen that is no longer there and nothing was typed.");

        if (chosen.State == RuleState.DryRun)
        {
            var wouldHave = Record(tenant, new RuleFiringDraft(
                chosen.Id, sessionId, screen, reply.Understanding, RuleDecisions.Act, reply.Reason,
                checks.Runs, "",
                "dry run: nothing was typed. It would have typed: " + reply.TextToType,
                grounding.Statement));
            return new RulePass(RulePassOutcomes.DryRun, reply.TextToType, new[] { wouldHave });
        }

        var sent = await _env.TypeIntoSessionAsync(tenant, directorId, sessionId, reply.TextToType, ct)
            .ConfigureAwait(false);

        // A keystroke that never left this Gateway is the only case in which nothing was typed. The
        // record says so, and the typed text is blank, because claiming text that never went anywhere
        // would be the same lie in the other direction.
        if (sent.What == RuleSendOutcomes.NotSent)
        {
            var notSent = Record(tenant, new RuleFiringDraft(
                chosen.Id, sessionId, screen, reply.Understanding, RuleDecisions.Act, reply.Reason,
                checks.Runs, "",
                "nothing was typed: " + sent.Detail, grounding.Statement));
            return new RulePass(RulePassOutcomes.NotSent, sent.Detail, new[] { notSent });
        }

        var confirmed = sent.What == RuleSendOutcomes.Confirmed;
        var acted = Record(tenant, new RuleFiringDraft(
            chosen.Id, sessionId, screen, reply.Understanding, RuleDecisions.Act, reply.Reason,
            checks.Runs, reply.TextToType,
            confirmed
                ? "typed into the session: " + reply.TextToType
                : "typed into the session: " + reply.TextToType +
                  " - but the prompt route did not confirm it started a turn (" + sent.Detail +
                  "). The session's screen is the only evidence of whether the keystroke landed.",
            grounding.Statement));

        return new RulePass(
            confirmed ? RulePassOutcomes.Acted : RulePassOutcomes.SendUnconfirmed,
            reply.TextToType, new[] { acted });
    }

    private RulePass Abandon(
        TenantId tenant, SessionRule rule, string sessionId, string screen,
        RuleAgentReply reply, IReadOnlyList<RulePrimitiveRun> runs, string why)
    {
        // The reason on an abandonment is this Gateway's own words, not the agent's, so there is nothing of
        // the agent's to check against the screen. Saying that is not the same as saying nothing.
        var firing = Record(tenant, new RuleFiringDraft(
            rule.Id, sessionId, screen, reply.Understanding, RuleDecisions.Abandoned, why,
            runs, "", "abandoned - nothing was typed.", RuleReasonGrounding.NotTheAgentsReason));
        return new RulePass(RulePassOutcomes.Abandoned, why, new[] { firing });
    }

    private RuleFiringDraft Record(TenantId tenant, RuleFiringDraft draft)
    {
        FileLog.Write(
            $"[RuleEvaluator] firing: rule={draft.RuleId} sid={draft.SessionId} " +
            $"decision={draft.Decision} typed={(draft.TypedText.Length > 0 ? "yes" : "no")}");
        _env.RecordFiring(tenant, draft);
        return draft;
    }

    private static RulePass Nothing(string what, string detail)
    {
        FileLog.Write($"[RuleEvaluator] {what}: {detail}");
        return new RulePass(what, detail, Array.Empty<RuleFiringDraft>());
    }

    /// <summary>The screen as one piece of text - what a check reads, what the record keeps, and what the
    /// re-read is compared against.</summary>
    private static string Join(IReadOnlyList<string> rows) =>
        string.Join("\n", rows.Select(r => r.TrimEnd()));
}
