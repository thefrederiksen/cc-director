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
    string Outcome);

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

    /// <summary>Every rule was considered and passed over, each with its reason.</summary>
    public const string NoCandidates = "no-candidates";

    /// <summary>The reply named something it was not offered, or was not an answer. Recorded as a refusal.</summary>
    public const string Refused = "refused";

    /// <summary>The agent read the screen against the instruction and declined. Recorded.</summary>
    public const string Declined = "declined";

    /// <summary>The act was given up before the keystroke. Recorded.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>The rule is in dry run: it decided to act, and typed nothing.</summary>
    public const string DryRun = "dry-run";

    /// <summary>The rule is live and the text was typed into the session.</summary>
    public const string Acted = "acted";

    /// <summary>The rule is live, the text was sent, and the send did not land.</summary>
    public const string SendFailed = "send-failed";
}

/// <summary>What one evaluation pass did, and the firings it wrote.</summary>
public sealed record RulePass(string What, string Detail, IReadOnlyList<RuleFiringDraft> Recorded);

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
    Task<bool> TypeIntoSessionAsync(
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
                    Array.Empty<RulePrimitiveRun>(), "", "nothing was typed.")))
                .ToList();
            return new RulePass(RulePassOutcomes.Refused, reading.Refusal, written);
        }

        var reply = reading.Reply!;
        var chosen = candidates.Chosen.First(r => r.Id == reply.RuleId);

        var runtime = new RuleRuntime(screen, facts.RepositoryPath, _env.NowUtc, FirstFailureUtc: null);
        var checks = RuleCheckRunner.Run(reply.Checks, runtime, _registry);

        if (reply.Decision == RuleDecisions.Decline)
        {
            var declined = Record(tenant, new RuleFiringDraft(
                chosen.Id, sessionId, screen, reply.Understanding, RuleDecisions.Decline, reply.Reason,
                checks.Runs, "", "declined - nothing was typed."));
            return new RulePass(RulePassOutcomes.Declined, reply.Reason, new[] { declined });
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
                "dry run: nothing was typed. It would have typed: " + reply.TextToType));
            return new RulePass(RulePassOutcomes.DryRun, reply.TextToType, new[] { wouldHave });
        }

        var landed = await _env.TypeIntoSessionAsync(tenant, directorId, sessionId, reply.TextToType, ct)
            .ConfigureAwait(false);

        var acted = Record(tenant, new RuleFiringDraft(
            chosen.Id, sessionId, screen, reply.Understanding, RuleDecisions.Act, reply.Reason,
            checks.Runs, reply.TextToType,
            landed
                ? "typed into the session: " + reply.TextToType
                : "the send did not land, so the session was never reached."));

        return new RulePass(
            landed ? RulePassOutcomes.Acted : RulePassOutcomes.SendFailed, reply.TextToType, new[] { acted });
    }

    private RulePass Abandon(
        TenantId tenant, SessionRule rule, string sessionId, string screen,
        RuleAgentReply reply, IReadOnlyList<RulePrimitiveRun> runs, string why)
    {
        var firing = Record(tenant, new RuleFiringDraft(
            rule.Id, sessionId, screen, reply.Understanding, RuleDecisions.Abandoned, why,
            runs, "", "abandoned - nothing was typed."));
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
