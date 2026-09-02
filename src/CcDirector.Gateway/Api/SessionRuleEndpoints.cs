using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Rules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The rule surface: read the standing instructions, write one, promote one out of dry run, delete one, and
/// read a rule's firing record. Device-authed client routes under the non-public "/gateway/..." prefix, so
/// the host-wide device-key middleware gates them exactly like every other client data endpoint.
///
/// THIS IS NOT THE AUTHORING CONVERSATION. Phase 3 builds the part where a person says a sentence and a
/// model turns it into a rule. These routes carry an ALREADY-BUILT rule, so that the phase 2 slice can put a
/// real rule into the real store from outside the process and then be watched acting on it. Every write goes
/// through <see cref="SessionRuleStore"/>, which is the gate: a rule naming a check we do not ship, or
/// supplying the wrong arguments to one we do, is refused here with the store's own stated reason.
///
/// A REFUSAL COMES BACK AS A REFUSAL. The store's reason is returned verbatim with a 400, never flattened
/// into a generic failure - a caller that cannot see why its rule was rejected will guess, and guessing is
/// how a rule ends up subtly different from the sentence that was meant.
/// </summary>
internal static class SessionRuleEndpoints
{
    public static void Map(IEndpointRouteBuilder app, SessionRuleStore store)
    {
        // Every rule this account has, newest first - the account's own sentences.
        app.MapGet("/gateway/rules", () => Results.Json(new { rules = store.All().Select(Project).ToList() }));

        // One rule.
        app.MapGet("/gateway/rules/{id:guid}", (Guid id) =>
        {
            var rule = store.Get(id);
            return rule is null
                ? Results.Json(new { error = $"there is no rule with the id {id}." }, statusCode: StatusCodes.Status404NotFound)
                : Results.Json(new { rule = Project(rule) });
        });

        // Write a rule. It is ALWAYS created in dry run - the store takes no state parameter at all.
        app.MapPost("/gateway/rules", (JsonElement body) =>
        {
            try
            {
                var rule = store.Create(
                    RuleCallJson.Text(body, "instruction") ?? "",
                    RuleCallJson.Text(body, "screenDescription") ?? "",
                    Strings(body, "triggerWords"),
                    Calls(body),
                    ReadScope(body),
                    Number(body, "cooldownSeconds"),
                    Number(body, "dailyCap"),
                    DateTime.UtcNow);
                FileLog.Write($"[SessionRuleEndpoints] POST /gateway/rules: stored {rule.Id} in dry run");
                return Results.Json(new { rule = Project(rule) });
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleEndpoints] POST /gateway/rules REFUSED: {ex.Reason}");
                return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // Move a rule out of dry run. Only a person does this; a rule can never promote itself.
        app.MapPost("/gateway/rules/{id:guid}/promote", (Guid id) =>
        {
            try
            {
                return Results.Json(new { rule = Project(store.Promote(id, DateTime.UtcNow)) });
            }
            catch (RuleRejectedException ex)
            {
                return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status404NotFound);
            }
        });

        // Delete a rule. Its firings are left alone - the record outlives the rule.
        app.MapDelete("/gateway/rules/{id:guid}", (Guid id) =>
            Results.Json(new { deleted = store.Delete(id) }));

        // THE RECORD, which is the product: every firing of one rule, newest first.
        app.MapGet("/gateway/rules/{id:guid}/firings", (Guid id) =>
            Results.Json(new { firings = store.FiringsFor(id).Select(Project).ToList() }));
    }

    private static object Project(SessionRule r) => new
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
        createdUtc = r.CreatedUtc,
        updatedUtc = r.UpdatedUtc,
    };

    private static object Project(SessionRuleFiring f) => new
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
    };

    private static RuleScope ReadScope(JsonElement body)
    {
        if (!body.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object)
            return RuleScope.AllSessions;
        return new RuleScope(
            RuleCallJson.Text(scope, "agent"),
            RuleCallJson.Text(scope, "repository"),
            RuleCallJson.Text(scope, "machine"),
            RuleCallJson.Text(scope, "mission"));
    }

    private static IReadOnlyList<string> Strings(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return array.EnumerateArray().Select(RuleCallJson.Scalar).ToList();
    }

    /// <summary>The checks, read by the SAME reader the agent's reply goes through - one meaning of a check
    /// written as JSON in this feature, not two.</summary>
    private static IReadOnlyList<RulePrimitiveCall> Calls(JsonElement body)
    {
        if (!body.TryGetProperty("checks", out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<RulePrimitiveCall>();
        return array.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(RuleCallJson.ReadCall)
            .ToList();
    }

    private static int Number(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
}
