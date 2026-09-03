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
///
/// TWO THINGS HAVE TO BE SAID OUT LOUD ON THESE ROUTES, and both used to be inferred. A write says which
/// sessions the rule may act on - "all-sessions" is a choice a caller can make, but an absent scope is no
/// longer read as meaning all of them. And a promotion says who is asking and what they are agreeing to;
/// an empty POST to the promote route now promotes nothing.
/// </summary>
[Rules.RuleFeature]
internal static class SessionRuleEndpoints
{
    public static void Map(IEndpointRouteBuilder app, SessionRuleStore store)
    {
        // Every rule this account has, newest first - the account's own sentences.
        app.MapGet("/gateway/rules", () => Results.Json(new { rules = store.All().Select(SessionRuleWire.Project).ToList() }));

        // One rule.
        app.MapGet("/gateway/rules/{id:guid}", (Guid id) =>
        {
            var rule = store.Get(id);
            return rule is null
                ? Results.Json(new { error = $"there is no rule with the id {id}." }, statusCode: StatusCodes.Status404NotFound)
                : Results.Json(new { rule = SessionRuleWire.Project(rule) });
        });

        // Write a rule. It is ALWAYS created in dry run - the store takes no state parameter at all.
        app.MapPost("/gateway/rules", (JsonElement body) =>
        {
            try
            {
                var rule = store.Create(
                    RuleCallJson.Text(body, "instruction") ?? "",
                    RuleCallJson.Text(body, "screenDescription") ?? "",
                    SessionRuleWire.Strings(body, "triggerWords"),
                    SessionRuleWire.Calls(body),
                    SessionRuleWire.ReadScope(body),
                    SessionRuleWire.Number(body, "cooldownSeconds"),
                    SessionRuleWire.Number(body, "dailyCap"),
                    DateTime.UtcNow);
                FileLog.Write($"[SessionRuleEndpoints] POST /gateway/rules: stored {rule.Id} in dry run");
                return Results.Json(new { rule = SessionRuleWire.Project(rule) });
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleEndpoints] POST /gateway/rules REFUSED: {ex.Reason}");
                return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // Move a rule out of dry run. ONLY A PERSON DOES THIS, and this route is the only place in the
        // whole Gateway that can obtain the evidence: the grant's factory is internal, takes THE REQUEST
        // rather than an identity somebody typed, and a structural test over the built assembly asserts
        // that no other type reaches it or reaches Promote. The evaluator has no inbound request, so there
        // is nothing it could pass - see RulePromotionGrant, which states exactly what that does and does
        // not enforce.
        app.MapPost("/gateway/rules/{id:guid}/promote", (Guid id, HttpContext http, JsonElement body) =>
        {
            try
            {
                var grant = RulePromotionGrant.FromAuthenticatedRequest(
                    id, http, RuleCallJson.Text(body, "acknowledgement"), DateTime.UtcNow);
                return Results.Json(new { rule = SessionRuleWire.Project(store.Promote(id, grant, DateTime.UtcNow)) });
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleEndpoints] promote {id} REFUSED: {ex.Reason}");
                return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // Delete a rule. Its firings are left alone - the record outlives the rule.
        app.MapDelete("/gateway/rules/{id:guid}", (Guid id) =>
            Results.Json(new { deleted = store.Delete(id) }));

        // THE RECORD, which is the product: every firing of one rule, newest first.
        app.MapGet("/gateway/rules/{id:guid}/firings", (Guid id) =>
            Results.Json(new { firings = store.FiringsFor(id).Select(SessionRuleWire.Project).ToList() }));
    }

}
