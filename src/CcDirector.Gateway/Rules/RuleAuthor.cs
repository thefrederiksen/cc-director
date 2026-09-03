using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// MAKING A RULE BY TALKING. The account says what it wants in ordinary words, a model works out what the
/// product needs to hold - what the screen looks like, the cheap words that make it worth a closer look,
/// any check the act depends on, which sessions, and the two ceilings - and the result is handed BACK to
/// the person to confirm.
///
/// IT READS, AND IT NEVER WRITES. This type has no store and no database. It has two seams: one asks a
/// model a question and returns what it said, the other reads a session's screen out of the Gateway
/// (fix round D, ruling D2). The proposal it returns is exactly the body the writing route takes, so the
/// person confirming a rule is the person posting it, and the rule that is stored is stored in dry run
/// like every other rule. A person then promotes it. Both of the confirmations that already existed are
/// untouched, and this adds no third way into the table.
///
/// THE SCREEN IS THE GATEWAY'S OWN READING, NOT A CLAIM FROM OUTSIDE. The draft route used to accept a
/// screen as a string, with the agent and machine it supposedly came from beside it, and an independent
/// inspection found that made the headline safety claim optional, caller-asserted, checked against a
/// different text than the model saw, and defeatable by whitespace. Now a caller names a SESSION and
/// nothing else about the screen: the session is located in the caller's own account, its screen is read
/// through the same read the evaluator uses, and the agent and machine come from the roster. A caller
/// that names no session is REFUSED - authoring from memory is no longer a mode.
///
/// THE SAME CHECK RUNS AT THE WRITE GATE, AND IT IS THE ONLY THING THAT CAN MINT THE EVIDENCE THE STORE
/// DEMANDS. <see cref="GroundAsync"/> is what the writing route calls before it stores anything: it reads
/// the session's screen again, freshly, runs the same grounding function over the same normalised words,
/// holds the agent scope to the session's agent or the account's stated star, and - when every word is
/// on the screen - mints a <see cref="RuleGroundingEvidence"/> naming exactly those words.
/// <see cref="SessionRuleStore.Create"/> refuses to persist trigger words without one, and the database
/// gate refuses a write that carries none (fix round E, ruling E1). A check that ran only on the draft
/// route was a check a caller could walk around by posting to create; a check that ran only on the
/// create route was one a caller could walk around by calling the store. Now the store cannot be called
/// without the check having run.
///
/// A PROPOSAL IS CHECKED AGAINST THE SAME GATE THE STORE USES BEFORE IT IS SHOWN. It would be worse than
/// useless to put a rule in front of somebody, have them agree to it, and only then discover the writing
/// route will not take it - they would have confirmed something that never existed. So the assembled rule
/// is run through <see cref="SessionRuleRecordRules.CheckRule"/>, the one implementation the store and the
/// database write gate both call, and a rule that would be refused there is refused HERE, in the same
/// words, before anybody is asked to agree to it.
///
/// A MODEL THAT CANNOT BE ASKED PRODUCES A REFUSAL, NEVER A RULE. There is no default rule, no partial
/// rule and no rule assembled out of what could be read; a draft that could not be made says so.
///
/// AND IT SAYS WHICH KIND OF "COULD NOT". Running out of time is not the same event as answering
/// nothing, and collapsing them costs the person the one thing they need to know: whether trying again
/// is worth it. Measured on the hosted model on 3 September 2026, the same sentence asked five times
/// ran out of the sixty-second limit three times and answered twice - so a person who is told "the
/// model gave no answer at all" after waiting a minute is being told something true about the call and
/// misleading about their situation. The timeout is reported as a timeout, and it says to try again.
/// </summary>
/// <summary>What grounding a rule at the write gate produced: the evidence the store demands, or the
/// reason there is none. Exactly one is set.</summary>
public sealed record RuleWriteGrounding(RuleGroundingEvidence? Evidence, string? Refusal)
{
    /// <summary>Every word was on the screen that was just read; here is the evidence.</summary>
    public static RuleWriteGrounding Grounded(RuleGroundingEvidence evidence) => new(evidence, null);

    /// <summary>It is not grounded, and this is why, in words the account reads.</summary>
    public static RuleWriteGrounding Refused(string reason) => new(null, reason);
}

public sealed class RuleAuthor
{
    private readonly Func<TenantId, string, CancellationToken, Task<string?>> _ask;
    private readonly RuleScreenReader _readScreen;
    private readonly RulePrimitiveRegistry _registry;

    /// <param name="ask">Asks the model a question and returns what it said, or null when it could not be
    /// asked. The same narrow seam the evaluator uses - deliberately not the whole environment, which can
    /// type into a session.</param>
    /// <param name="readScreen">Reads a session's screen out of the Gateway, in the caller's tenant, with
    /// the agent and machine the roster holds for it. See <see cref="RuleScreenReader"/>.</param>
    /// <param name="registry">The verified checks a rule may name. Defaults to what the product ships.</param>
    /// <exception cref="ArgumentNullException">A seam is null.</exception>
    public RuleAuthor(
        Func<TenantId, string, CancellationToken, Task<string?>> ask,
        RuleScreenReader readScreen,
        RulePrimitiveRegistry? registry = null)
    {
        _ask = ask ?? throw new ArgumentNullException(nameof(ask));
        _readScreen = readScreen ?? throw new ArgumentNullException(nameof(readScreen));
        _registry = registry ?? RulePrimitiveRegistry.Default;
    }

    /// <summary>
    /// Turn what has been said so far into a rule to confirm, a question to answer, or a stated refusal.
    /// </summary>
    /// <param name="tenant">The account the rule would belong to.</param>
    /// <param name="turns">The conversation so far. It has to contain something the person said.</param>
    /// <param name="sessionId">The session the rule is about. REQUIRED: its screen is read by the Gateway
    /// and the model reads the trigger words off it, and a word that is not on it is refused. An empty
    /// session id is refused rather than being read as "write it from memory".</param>
    /// <param name="allAgents">The account said this rule is for every agent (the star).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<RuleDraftReading> DraftAsync(
        TenantId tenant,
        IReadOnlyList<RuleDraftTurn> turns,
        string? sessionId,
        bool allAgents,
        CancellationToken ct)
    {
        // THE INSTRUCTION IS THE PERSON'S OWN WORDS AND NOT THE MODEL'S. The store treats the instruction
        // as the authority, so it is assembled here from what the person actually said rather than read
        // out of the reply - a model asked to restate a sentence will eventually improve it, and an
        // improved authority is a different authority.
        var said = (turns ?? Array.Empty<RuleDraftTurn>())
            .Where(t => t is not null && !string.Equals(t.Who, RuleDraftSpeakers.DevThrottle, StringComparison.Ordinal))
            .Select(t => (t.Text ?? "").Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (said.Count == 0)
            return RuleDraftReading.Refused(
                "there is nothing here that you said, and a rule is the sentence you said - so there is " +
                "nothing to turn into one.");

        var instruction = string.Join(" ", said);

        FileLog.Write($"[RuleAuthor] DraftAsync: turns={turns!.Count}, instruction length={instruction.Length}, session={sessionId}");

        var (screen, noScreen) = await ReadScreenAsync(tenant, sessionId, ct).ConfigureAwait(false);
        if (screen is null)
        {
            FileLog.Write($"[RuleAuthor] DraftAsync REFUSED, no screen: {noScreen}");
            return RuleDraftReading.Refused(noScreen! + " Nothing was drafted.");
        }

        var prompt = RuleDraftContract.BuildDraftPrompt(turns, _registry, screen, allAgents);

        string? raw;
        try
        {
            raw = await _ask(tenant, prompt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The CALLER gave up, which is not a failure to report back to them.
            throw;
        }
        catch (TimeoutException ex)
        {
            FileLog.Write($"[RuleAuthor] DraftAsync: the model ran out of time: {ex.Message}");
            return RuleDraftReading.Refused(
                "working out this rule took longer than the model is given, so nothing was drafted and " +
                "nothing was stored. This one is worth trying again - the same sentence often works on " +
                "the next attempt.");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RuleAuthor] DraftAsync: the model could not be asked: {ex.Message}");
            return RuleDraftReading.Refused(
                "the model could not be asked, so no rule was drafted and nothing was stored (" +
                ex.Message + ").");
        }

        var reading = RuleDraftContract.Read(raw, instruction, _registry, screen, allAgents);
        if (reading.Proposal is null)
        {
            FileLog.Write($"[RuleAuthor] DraftAsync: no proposal (question={reading.Question is not null})");
            return reading;
        }

        var refusal = WhyTheStoreWouldRefuse(reading.Proposal);
        if (refusal is not null)
        {
            FileLog.Write($"[RuleAuthor] DraftAsync REFUSED by the write gate: {refusal}");
            return RuleDraftReading.Refused(
                "the rule that was drafted is not one that could be stored, so it is not being offered to " +
                "you: " + refusal);
        }

        FileLog.Write($"[RuleAuthor] DraftAsync: proposed a rule with {reading.Proposal.TriggerWords.Count} trigger words");
        return reading;
    }

    /// <summary>
    /// THE WRITE GATE'S HALF OF GROUNDING (fix round D, ruling D2, item 5; fix round E, ruling E1). Reads
    /// the named session's screen again, freshly, and either mints the evidence the store demands - every
    /// trigger word on the screen right now, the agent scope the session's agent or lifted by the star -
    /// or answers why not. The draft route ran the same function over the same normalised words; running
    /// it here again, and being the only thing that can mint evidence, is what makes the one door the
    /// one door.
    /// </summary>
    /// <param name="tenant">The account the rule would belong to.</param>
    /// <param name="sessionId">The session the rule is about. Required.</param>
    /// <param name="triggerWords">The words the rule would be stored with, in any form.</param>
    /// <param name="scope">The scope the rule would be stored with, or null (the store refuses that with
    /// its own sentence, so it is not this method's to refuse).</param>
    /// <param name="allAgents">The account said this rule is for every agent (the star).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<RuleWriteGrounding> GroundAsync(
        TenantId tenant,
        string? sessionId,
        IEnumerable<string> triggerWords,
        RuleScope? scope,
        bool allAgents,
        CancellationToken ct)
    {
        var refusal = await WhyNotGroundedCoreAsync(tenant, sessionId, triggerWords, scope, allAgents, ct)
            .ConfigureAwait(false);
        if (refusal.Refusal is not null) return RuleWriteGrounding.Refused(refusal.Refusal);

        // EVERY WORD IS ON THE SCREEN THAT WAS JUST READ, so the evidence can be minted - and this is the
        // only place in production code that mints it (a structural test over the built assembly says so).
        return RuleWriteGrounding.Grounded(RuleGroundingEvidence.Minted(refusal.Screen!, triggerWords));
    }

    /// <summary>Why the rule is not grounded, or null when it is - the same reading as
    /// <see cref="GroundAsync"/> without the evidence, for a caller that only wants the sentence.</summary>
    public async Task<string?> WhyNotGroundedAsync(
        TenantId tenant,
        string? sessionId,
        IEnumerable<string> triggerWords,
        RuleScope? scope,
        bool allAgents,
        CancellationToken ct)
    {
        var read = await WhyNotGroundedCoreAsync(tenant, sessionId, triggerWords, scope, allAgents, ct)
            .ConfigureAwait(false);
        return read.Refusal;
    }

    private async Task<(RuleScreenReading? Screen, string? Refusal)> WhyNotGroundedCoreAsync(
        TenantId tenant,
        string? sessionId,
        IEnumerable<string> triggerWords,
        RuleScope? scope,
        bool allAgents,
        CancellationToken ct)
    {
        var (screen, noScreen) = await ReadScreenAsync(tenant, sessionId, ct).ConfigureAwait(false);
        if (screen is null) return (null, noScreen + " Nothing was stored.");

        var notGrounded = RuleTriggerWords.WhyNotGrounded(triggerWords, screen, "that session's screen right now");
        if (notGrounded is not null) return (null, notGrounded + " Nothing was stored.");

        // THE AGENT SCOPE IS THE SESSION'S AGENT OR THE STAR - NEVER SOMETHING WRITTEN BY HAND. The draft
        // pinned it; a body that arrives here with a different agent did not come from the draft unchanged.
        if (scope is not null)
        {
            var agentWritten = (scope.Agent ?? "").Trim();
            if (allAgents && agentWritten.Length > 0)
                return (null, "this rule says it is for every agent and also names the agent " + agentWritten +
                       ". It is one or the other, and it is the account that chooses. Nothing was stored.");
            if (!allAgents && !string.Equals(agentWritten, screen.Origin.Agent, StringComparison.Ordinal))
                return (null, "this rule is written against a session running " + screen.Origin.Agent + ", so it " +
                       "is for " + screen.Origin.Agent + " sessions unless you say every agent" +
                       (agentWritten.Length == 0 ? "" : " - it names " + agentWritten + " instead") +
                       ". The agent is a fact the session holds, not something to write by hand. Nothing was stored.");
        }

        return (screen, null);
    }

    /// <summary>
    /// The session's screen, read by the Gateway, or the reason it could not be. One reader for both
    /// routes, so "no session", "session not on the roster", "screen unreadable", "screen empty" and
    /// "agent unknown" are refused the same way at the draft and at the write gate.
    /// </summary>
    private async Task<(RuleScreenReading? Screen, string? Refusal)> ReadScreenAsync(
        TenantId tenant, string? sessionId, CancellationToken ct)
    {
        var sid = (sessionId ?? "").Trim();
        if (sid.Length == 0)
            return (null,
                "a rule is written against a real session's screen, and this request named no session. " +
                "Say which session it is about; a rule cannot be written from memory.");

        RuleScreenResult read;
        try
        {
            read = await _readScreen(tenant, sid, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RuleAuthor] screen read FAILED sid={sid}: {ex.Message}");
            return (null, $"the screen of session {sid} could not be read ({ex.Message}), and unreadable is not evidence.");
        }

        if (read is null || read.Screen is null)
            return (null, read?.Refusal ?? $"the screen of session {sid} could not be read, and unreadable is not evidence.");

        if (read.Screen.Excerpt.Length == 0)
            return (null, $"session {sid} has an empty screen, so there is nothing to write a rule against. " +
                          "An empty screen is not a capture.");

        if (!read.Screen.Origin.IsKnown)
            return (null, $"session {sid} does not say which agent it runs, so there is no fact to scope a " +
                          "rule to and the model is never allowed to choose that.");

        return (read.Screen, null);
    }

    /// <summary>
    /// What the store would say if this proposal were written, or null when it would take it. The rule is
    /// assembled exactly as <see cref="SessionRuleStore.Create"/> assembles one and handed to the same
    /// check, so there is no second opinion here about what a rule is.
    /// </summary>
    private string? WhyTheStoreWouldRefuse(RuleProposal proposal)
    {
        var candidate = new SessionRuleEntity
        {
            Instruction = proposal.Instruction,
            ScreenDescription = proposal.ScreenDescription,
            TriggerWords = proposal.TriggerWords.ToList(),
            Calls = proposal.Calls.ToList(),
            ScopeAgent = proposal.Scope.Agent,
            ScopeRepository = proposal.Scope.Repository,
            ScopeMachine = proposal.Scope.Machine,
            ScopeMission = proposal.Scope.Mission,
            CooldownSeconds = proposal.CooldownSeconds,
            DailyCap = proposal.DailyCap,
        };

        try
        {
            SessionRuleRecordRules.CheckRule(candidate, _registry);
            return null;
        }
        catch (RuleRejectedException ex)
        {
            return ex.Reason;
        }
    }
}
