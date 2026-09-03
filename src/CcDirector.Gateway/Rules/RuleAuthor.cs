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
/// NOTHING HERE WRITES. This type has no store, no database and no session; it asks a model a question and
/// reads the answer. The proposal it returns is exactly the body the writing route already takes, so the
/// person confirming a rule is the person posting it, and the rule that is stored is stored in dry run
/// like every other rule. A person then promotes it. Both of the confirmations that already existed are
/// untouched, and this adds no third way into the table.
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
public sealed class RuleAuthor
{
    private readonly Func<TenantId, string, CancellationToken, Task<string?>> _ask;
    private readonly RulePrimitiveRegistry _registry;

    /// <param name="ask">Asks the model a question and returns what it said, or null when it could not be
    /// asked. The same narrow seam the evaluator uses - deliberately not the whole environment, which can
    /// type into a session.</param>
    /// <param name="registry">The verified checks a rule may name. Defaults to what the product ships.</param>
    /// <exception cref="ArgumentNullException">The asking seam is null.</exception>
    public RuleAuthor(
        Func<TenantId, string, CancellationToken, Task<string?>> ask,
        RulePrimitiveRegistry? registry = null)
    {
        _ask = ask ?? throw new ArgumentNullException(nameof(ask));
        _registry = registry ?? RulePrimitiveRegistry.Default;
    }

    /// <summary>
    /// Turn what has been said so far into a rule to confirm, a question to answer, or a stated refusal.
    /// </summary>
    /// <param name="tenant">The account the rule would belong to.</param>
    /// <param name="turns">The conversation so far. It has to contain something the person said.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="exampleScreen">A REAL screen the account captured from a session, or empty. When it is
    /// present the model reads the trigger words off it instead of imagining them, and a word that is not
    /// on it is refused - see <see cref="RuleDraftContract"/>.</param>
    /// <param name="origin">The session the screen came from, or none. Decides the agent part of the
    /// scope - see <see cref="RuleDraftContract"/>.</param>
    /// <param name="allAgents">The account said this rule is for every agent (the star).</param>
    public async Task<RuleDraftReading> DraftAsync(
        TenantId tenant,
        IReadOnlyList<RuleDraftTurn> turns,
        CancellationToken ct,
        string exampleScreen = "",
        RuleSessionOrigin? origin = null,
        bool allAgents = false)
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

        FileLog.Write($"[RuleAuthor] DraftAsync: turns={turns!.Count}, instruction length={instruction.Length}");

        var prompt = RuleDraftContract.BuildDraftPrompt(turns, _registry, exampleScreen, origin, allAgents);

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

        var reading = RuleDraftContract.Read(raw, instruction, _registry, exampleScreen, origin, allAgents);
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
