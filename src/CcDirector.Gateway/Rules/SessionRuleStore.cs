using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// The Gateway-owned store of standing instructions and the record of every time one fired (the Session
/// Rules mission, phase 1). Rules are durable and shared - the Cockpit, the phone and the evaluator all
/// read the same rules - so they live here, on the Gateway, in the EF data layer, tenant-scoped.
///
/// THIS IS THE GATE. Nothing reaches <c>session_rules</c> without passing <see cref="RuleCallValidator"/>,
/// so a call naming a check we do not ship, or supplying the wrong arguments to one we do, is refused
/// here with a stated reason (Architect ruling A4). That refusal is the whole of ruling 15 in practice:
/// the stored rule holds a name and argument values, and there is no column and no path by which anything
/// executable could arrive.
///
/// DRY RUN IS ENFORCED, NOT DOCUMENTED. <see cref="Create"/> takes no state and always writes a dry-run
/// rule, so no caller can create a live one; a person promotes it with <see cref="Promote"/>. And a
/// firing recorded against a dry-run rule may not claim to have typed anything - the store refuses it -
/// so "dry run types nothing" is a property of the writer rather than a promise about the reader.
///
/// Threading: the Gateway is a single writer. Every operation runs under this store's write lock over a
/// fresh pooled context.
/// </summary>
public sealed class SessionRuleStore : IRuleReading
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly RulePrimitiveRegistry _registry;

    private static readonly string DryRunValue = RuleWireNames.ToWireName(nameof(RuleState.DryRun));
    private static readonly string LiveValue = RuleWireNames.ToWireName(nameof(RuleState.Live));

    /// <param name="db">The Gateway EF database this store reads and writes through.</param>
    /// <param name="registry">The verified checks a rule may name. Defaults to what the product ships.</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    public SessionRuleStore(GatewayDatabase db, RulePrimitiveRegistry? registry = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _registry = registry ?? RulePrimitiveRegistry.Default;
    }

    /// <summary>
    /// Store a new rule. It is ALWAYS created in dry run - there is no parameter that could make it live.
    /// </summary>
    /// <exception cref="RuleRejectedException">The instruction, the bounds or one of the calls is not
    /// something that can be stored; the reason says which and why.</exception>
    public SessionRule Create(
        string instruction,
        string screenDescription,
        IEnumerable<string> triggerWords,
        IEnumerable<RulePrimitiveCall> calls,
        RuleScope scope,
        int cooldownSeconds,
        int dailyCap,
        DateTime nowUtc)
    {
        FileLog.Write($"[SessionRuleStore] Create: instruction length={instruction?.Length ?? 0}");

        var sentence = (instruction ?? "").Trim();
        if (sentence.Length == 0)
            throw new RuleRejectedException(
                "a rule is the sentence you said, so it cannot be empty - the instruction is the authority.");

        var description = (screenDescription ?? "").Trim();
        if (description.Length == 0)
            throw new RuleRejectedException(
                "a rule has to say, in plain words, what it is watching for on the screen.");

        var words = (triggerWords ?? Array.Empty<string>())
            .Select(w => (w ?? "").Trim())
            .Where(w => w.Length > 0)
            .ToList();
        if (words.Count == 0)
            throw new RuleRejectedException(
                "a rule needs at least one word to watch for, or it would cost a model call on every " +
                "screen. The words are worked out from the instruction, not chosen by hand.");

        if (cooldownSeconds <= 0)
            throw new RuleRejectedException(
                "a rule has to say how long to wait before acting on the same session again. " +
                "The ceiling is what makes a rule in a loop finite.");
        if (dailyCap <= 0)
            throw new RuleRejectedException(
                "a rule has to say how many times a day it may act on one session. " +
                "The ceiling is what makes a rule in a loop finite.");

        var theCalls = (calls ?? Array.Empty<RulePrimitiveCall>()).ToList();
        var validation = RuleCallValidator.ValidateAll(theCalls, _registry);
        if (!validation.IsValid)
        {
            FileLog.Write($"[SessionRuleStore] Create REFUSED: {validation.Reason}");
            throw new RuleRejectedException(validation.Reason);
        }

        var theScope = scope ?? RuleScope.AllSessions;
        var created = nowUtc.ToUniversalTime();

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = new SessionRuleEntity
            {
                TenantId = ctx.ActiveTenant!,
                Instruction = sentence,
                ScreenDescription = description,
                TriggerWords = words,
                Calls = theCalls.Select(CopyOf).ToList(),
                ScopeAgent = Blank(theScope.Agent),
                ScopeRepository = Blank(theScope.Repository),
                ScopeMachine = Blank(theScope.Machine),
                ScopeMission = Blank(theScope.Mission),
                CooldownSeconds = cooldownSeconds,
                DailyCap = dailyCap,
                // ALWAYS dry run. There is deliberately no parameter for this.
                State = DryRunValue,
                CreatedUtc = created,
                UpdatedUtc = created,
            };
            ctx.SessionRules.Add(entity);
            ctx.SaveChanges();
            FileLog.Write($"[SessionRuleStore] Create: stored rule {entity.Id} in dry run");
            return ToRecord(entity);
        }
    }

    /// <summary>The rule with this id, or null when there is none.</summary>
    public SessionRule? Get(Guid id)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionRules.AsNoTracking().FirstOrDefault(r => r.Id == id);
            return entity is null ? null : ToRecord(entity);
        }
    }

    /// <summary>Every rule the account has, newest first.</summary>
    public IReadOnlyList<SessionRule> All()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.SessionRules.AsNoTracking()
                .OrderByDescending(r => r.CreatedUtc)
                .ToList()
                .Select(ToRecord)
                .ToList();
        }
    }

    /// <summary>Move a rule out of dry run and into live. A rule that is already live is returned
    /// unchanged. Only a person does this - a rule can never promote itself (owner ruling 14, bound 6) -
    /// so this takes a <see cref="RulePromotionGrant"/>, which can only be minted from an authenticated
    /// inbound request and names the one rule it is evidence for.</summary>
    /// <exception cref="RuleRejectedException">There is no such rule, or the call carried no evidence that
    /// a person asked, or the grant was obtained for a different rule.</exception>
    public SessionRule Promote(Guid id, RulePromotionGrant grant, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionRules.FirstOrDefault(r => r.Id == id)
                ?? throw new RuleRejectedException($"there is no rule with the id {id}.");

            if (string.Equals(entity.State, LiveValue, StringComparison.Ordinal))
                return ToRecord(entity);

            entity.State = LiveValue;
            entity.UpdatedUtc = nowUtc.ToUniversalTime();
            ctx.SaveChanges();
            FileLog.Write($"[SessionRuleStore] Promote: rule {id} is now live");
            return ToRecord(entity);
        }
    }

    /// <summary>Delete a rule. Its firings are left alone - the record outlives the rule.</summary>
    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionRules.FirstOrDefault(r => r.Id == id);
            if (entity is null) return false;
            ctx.SessionRules.Remove(entity);
            ctx.SaveChanges();
            FileLog.Write($"[SessionRuleStore] Delete: rule {id} removed; its firings are kept");
            return true;
        }
    }

    /// <summary>
    /// Record one firing. A firing against a rule in DRY RUN may not claim to have typed anything; that is
    /// refused with a reason rather than silently blanked, because a store that quietly edits what it was
    /// told is a store nobody can read as evidence.
    /// </summary>
    /// <exception cref="RuleRejectedException">There is no such rule, a dry-run rule was recorded as
    /// having typed something, or the record is not a record of anything.</exception>
    public SessionRuleFiring RecordFiring(
        Guid ruleId,
        string sessionId,
        string screenText,
        string understanding,
        string decision,
        string reason,
        IEnumerable<RulePrimitiveRun> primitiveRuns,
        string typedText,
        string outcome,
        string grounding,
        DateTime nowUtc)
    {
        var typed = typedText ?? "";

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var rule = ctx.SessionRules.AsNoTracking().FirstOrDefault(r => r.Id == ruleId)
                ?? throw new RuleRejectedException($"there is no rule with the id {ruleId}.");

            if (typed.Length > 0 && string.Equals(rule.State, DryRunValue, StringComparison.Ordinal))
            {
                var refusal =
                    "this rule is in dry run, so it types nothing - a firing cannot record it having " +
                    "typed '" + typed + "'. Promote the rule first.";
                FileLog.Write($"[SessionRuleStore] RecordFiring REFUSED: rule {ruleId} is in dry run");
                throw new RuleRejectedException(refusal);
            }

            var entity = new SessionRuleFiringEntity
            {
                TenantId = ctx.ActiveTenant!,
                RuleId = ruleId,
                SessionId = sessionId ?? "",
                OccurredUtc = nowUtc.ToUniversalTime(),
                ScreenText = screenText ?? "",
                Understanding = understanding ?? "",
                Decision = decision ?? "",
                Reason = reason ?? "",
                PrimitiveRuns = (primitiveRuns ?? Array.Empty<RulePrimitiveRun>())
                    .Select(r => new RulePrimitiveRunEntity { Name = r.Name, Arguments = r.Arguments, Answer = r.Answer })
                    .ToList(),
                TypedText = typed,
                Outcome = outcome ?? "",
                Grounding = grounding ?? "",
            };
            ctx.SessionRuleFirings.Add(entity);
            ctx.SaveChanges();
            FileLog.Write(
                $"[SessionRuleStore] RecordFiring: rule={ruleId} session={entity.SessionId} " +
                $"decision={entity.Decision} typed={(typed.Length > 0 ? "yes" : "no")}");
            return ToRecord(entity);
        }
    }

    /// <summary>Every firing of one rule, newest first.</summary>
    public IReadOnlyList<SessionRuleFiring> FiringsFor(Guid ruleId)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.SessionRuleFirings.AsNoTracking()
                .Where(f => f.RuleId == ruleId)
                .OrderByDescending(f => f.OccurredUtc)
                .ToList()
                .Select(ToRecord)
                .ToList();
        }
    }

    /// <summary>An empty or all-whitespace scope part means "any", which is stored as nothing at all.</summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A copy of a call, so the stored rule cannot be changed under us by whoever passed it in.</summary>
    private static RulePrimitiveCall CopyOf(RulePrimitiveCall call) => new()
    {
        Name = call.Name,
        Arguments = call.Arguments
            .Select(a => new RuleArgument
            {
                Parameter = a.Parameter,
                Source = a.Source,
                Values = a.Values.ToList(),
            })
            .ToList(),
    };

    private static SessionRule ToRecord(SessionRuleEntity e) => new(
        e.Id,
        e.Instruction,
        e.ScreenDescription,
        e.TriggerWords.ToList(),
        e.Calls.Select(CopyOf).ToList(),
        new RuleScope(e.ScopeAgent, e.ScopeRepository, e.ScopeMachine, e.ScopeMission),
        e.CooldownSeconds,
        e.DailyCap,
        StateOf(e.State),
        e.PromotedBy,
        e.CreatedUtc,
        e.UpdatedUtc);

    private static SessionRuleFiring ToRecord(SessionRuleFiringEntity e) => new(
        e.Id,
        e.RuleId,
        e.SessionId,
        e.OccurredUtc,
        e.ScreenText,
        e.Understanding,
        e.Decision,
        e.Reason,
        e.PrimitiveRuns.Select(r => new RulePrimitiveRun(r.Name, r.Arguments, r.Answer)).ToList(),
        e.TypedText,
        e.Outcome,
        e.Grounding);

    /// <summary>The stored state value as a state. An unrecognised value fails loudly rather than being
    /// treated as dry run - a rule whose state we cannot read must not be quietly assumed harmless, and
    /// must not be quietly assumed live either.</summary>
    private static RuleState StateOf(string stored)
    {
        if (string.Equals(stored, DryRunValue, StringComparison.Ordinal)) return RuleState.DryRun;
        if (string.Equals(stored, LiveValue, StringComparison.Ordinal)) return RuleState.Live;
        throw new InvalidOperationException(
            $"stored rule state '{stored}' is not one this build knows. Known states: {DryRunValue}, {LiveValue}.");
    }
}
