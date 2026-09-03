using System.Text.Json;
using CcDirector.Gateway.Rules;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE WIRE SHAPE OF A RULE AND OF A FIRING, AND THE READING OF A WRITE - lifted out of the endpoint so it
/// can be tested without standing a web host up.
///
/// The projections used to live inside the route lambdas. That put the feature's whole public read surface
/// somewhere only a host-bound test could reach, and the host-bound suite is PARKED - so in practice
/// nothing tested it at all, and two accountability fields that had been added on purpose were simply
/// missing from it without anything noticing. The independent inspection of landing B found that.
///
/// So the mapping is a type. The routes stay a thin binding of HTTP to these methods, and what the account
/// actually receives is covered by ordinary unit tests that run in the fast gate.
/// </summary>
[Rules.RuleFeature]
internal static class SessionRuleWire
{
    /// <summary>How a caller says "every session" out loud.</summary>
    internal const string AllSessionsWireValue = "all-sessions";

    /// <summary>One rule, as the account reads it.</summary>
    internal static object Project(SessionRule r) => new
    {
        id = r.Id,
        instruction = r.Instruction,
        screenDescription = r.ScreenDescription,
        triggerWords = r.TriggerWords,
        checks = r.Calls.Select(c => c.Describe()).ToList(),
        scope = new { agent = r.Scope.Agent, repository = r.Scope.Repository, machine = r.Scope.Machine, mission = r.Scope.Mission },
        cooldownSeconds = r.CooldownSeconds,
        dailyCap = r.DailyCap,
        state = RuleWireNames.ToWireName(r.State.ToString()),
        // WHO MADE IT LIVE. Dry run is the bound that puts a person between a standing instruction and its
        // first real use, and this is the only place the account can find out who that person was. It was
        // stored and not delivered, which for a reader is the same as not existing.
        promotedBy = r.PromotedBy,
        createdUtc = r.CreatedUtc,
        updatedUtc = r.UpdatedUtc,
    };

    /// <summary>One firing, as the account reads it. The record is the product, so this is the shape in
    /// which the product is actually delivered.</summary>
    internal static object Project(SessionRuleFiring f) => new
    {
        id = f.Id,
        ruleId = f.RuleId,
        sessionId = f.SessionId,
        occurredUtc = f.OccurredUtc,
        screenText = f.ScreenText,
        understanding = f.Understanding,
        decision = f.Decision,
        reason = f.Reason,
        checksRun = f.PrimitiveRuns.Select(p => new { name = p.Name, arguments = p.Arguments, answer = p.Answer }).ToList(),
        typedText = f.TypedText,
        outcome = f.Outcome,
        // WHAT CHECKING THE STATED REASON AGAINST THE SCREEN FOUND (Architect ruling A12). It is never
        // blank, and delivering it is the whole point of it: a run in which that check never happened must
        // not read the same as one in which it ran and found nothing wrong.
        grounding = f.Grounding,
    };

    /// <summary>
    /// The scope, which has to be SAID. An absent scope used to become "every session" - the widest value
    /// there is - so the wire could not tell a deliberate choice from an omission, and malformed authoring
    /// output would have been read as permission to act on everything. Now: the string "all-sessions" is
    /// the explicit way to say every session, an object naming at least one part is a narrower scope, and
    /// anything else is null, which the store refuses with a reason.
    ///
    /// A PART THAT IS THERE AND EMPTY IS A PART THAT WAS NOT SAID. This is the hole the check below used
    /// to have: a null or an empty string was read as the empty-string VALUE, so an object whose four
    /// parts were all null was not equal to the empty one, came through as a narrow scope of four empty
    /// strings, and was then blanked back to four nulls by the store - which is every session the account
    /// has, produced by a request that chose nothing. It is exactly the shape something filling the fields
    /// in without knowing the answers produces.
    /// </summary>
    internal static RuleScope? ReadScope(JsonElement body)
    {
        if (!body.TryGetProperty("scope", out var scope)) return null;

        if (scope.ValueKind == JsonValueKind.String)
            return string.Equals(scope.GetString()?.Trim(), AllSessionsWireValue, StringComparison.OrdinalIgnoreCase)
                ? RuleScope.AllSessions
                : null;

        if (scope.ValueKind != JsonValueKind.Object) return null;

        var built = new RuleScope(
            Part(scope, "agent"),
            Part(scope, "repository"),
            Part(scope, "machine"),
            Part(scope, "mission"));

        // An object with nothing in it is not a choice of "all sessions"; it is the same omission wearing
        // a pair of braces.
        return built == RuleScope.AllSessions ? null : built;
    }

    /// <summary>One part of a scope: what it names, or null when it names nothing.</summary>
    private static string? Part(JsonElement scope, string name)
    {
        var value = RuleCallJson.Text(scope, name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    internal static IReadOnlyList<string> Strings(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return array.EnumerateArray().Select(RuleCallJson.Scalar).ToList();
    }

    /// <summary>
    /// The checks, read by the SAME reader the agent's reply goes through - one meaning of a check written
    /// as JSON in this feature, and now really one rather than two.
    ///
    /// This used to read them only when the property was an array and to drop any entry that was not an
    /// object, silently, while the route's own comment claimed both paths used the same reader. A check
    /// that disappears takes its refusal with it, and a caller could ask for two checks, have one quietly
    /// removed and receive a rule that runs the other one alone.
    /// </summary>
    /// <exception cref="RuleRejectedException">The checks are not a list of checks; the reason says why.</exception>
    internal static IReadOnlyList<RulePrimitiveCall> Calls(JsonElement body)
    {
        var calls = RuleCallJson.ReadChecks(body, "checks", required: true, out var problem);
        if (calls is null) throw new RuleRejectedException(problem!);
        return calls;
    }

    internal static int Number(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    // ---- authoring by conversation ------------------------------------------------------------------

    /// <summary>
    /// A drafted rule, as the account receives it: what it would actually do in plain words, and the rule
    /// itself.
    ///
    /// THE RULE HERE IS EXACTLY THE BODY THE WRITING ROUTE TAKES, and that is the point rather than a
    /// convenience. Confirming a drafted rule is posting it back unchanged, so the thing the person read
    /// and the thing that gets stored are the same document - there is no second translation step in which
    /// a scope or a check could quietly become something else. A test round-trips it through the readers
    /// above and into the real store.
    /// </summary>
    internal static object Project(RuleProposal proposal) => new
    {
        readBack = proposal.ReadBack,
        rule = WriteBody(proposal),
        // The screen it was made from, returned so the page can show what the rule was checked against.
        // Every trigger word above was verified to appear in THIS text before the rule was offered.
        exampleScreen = proposal.ExampleScreen,
    };

    /// <summary>The drafted rule written the way a caller writes one.</summary>
    internal static object WriteBody(RuleProposal proposal) => new
    {
        instruction = proposal.Instruction,
        screenDescription = proposal.ScreenDescription,
        triggerWords = proposal.TriggerWords,
        checks = proposal.Calls.Select(AsWritten).ToList(),
        scope = ScopeAsWritten(proposal.Scope),
        cooldownSeconds = proposal.CooldownSeconds,
        dailyCap = proposal.DailyCap,
    };

    /// <summary>
    /// One check written the way <see cref="RuleCallJson.ReadCall"/> reads one: a name, and an argument per
    /// parameter whose value is either written out, a list of written-out terms, or a runtime input in
    /// angle brackets. The angle-bracket notation is the same one the question to the model uses and the
    /// same one the firing record prints, so there is one notation in this feature and not three.
    /// </summary>
    private static object AsWritten(RulePrimitiveCall call)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var argument in call.Arguments ?? new List<RuleArgument>())
        {
            arguments[argument.Parameter] = argument.Source == InputWireValue
                ? "<" + string.Join(",", argument.Values) + ">"
                : argument.Values.Count == 1 ? argument.Values[0] : argument.Values.Cast<object?>().ToList();
        }
        return new { name = call.Name, arguments };
    }

    /// <summary>How a runtime-input argument says so.</summary>
    private static readonly string InputWireValue = RuleWireNames.ToWireName(nameof(RuleArgumentSource.Input));

    /// <summary>
    /// The scope written so <see cref="ReadScope"/> reads back the same scope. Every session is the string
    /// that says so; a narrower scope names ONLY the parts that are set, because a part written as null
    /// comes back as an empty string rather than as "any", and an object of empty strings is a scope that
    /// matches nothing.
    /// </summary>
    private static object ScopeAsWritten(RuleScope scope)
    {
        if (scope == RuleScope.AllSessions) return AllSessionsWireValue;

        var parts = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(scope.Agent)) parts["agent"] = scope.Agent;
        if (!string.IsNullOrWhiteSpace(scope.Repository)) parts["repository"] = scope.Repository;
        if (!string.IsNullOrWhiteSpace(scope.Machine)) parts["machine"] = scope.Machine;
        if (!string.IsNullOrWhiteSpace(scope.Mission)) parts["mission"] = scope.Mission;
        return parts;
    }

    /// <summary>
    /// The conversation so far. A turn that does not say who said it is read as the PERSON, because the
    /// person is who the account is - reading an unlabelled turn as the product would put words in the
    /// account's mouth that it never said, and the instruction is assembled from what the person said.
    /// </summary>
    internal static IReadOnlyList<RuleDraftTurn> Turns(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("turns", out var array)
            || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<RuleDraftTurn>();

        var turns = new List<RuleDraftTurn>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var who = (RuleCallJson.Text(entry, "who") ?? "").Trim().ToLowerInvariant();
            turns.Add(new RuleDraftTurn(
                who == RuleDraftSpeakers.DevThrottle ? RuleDraftSpeakers.DevThrottle : RuleDraftSpeakers.Person,
                RuleCallJson.Text(entry, "text") ?? ""));
        }
        return turns;
    }
}
