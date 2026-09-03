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
/// THE AUTHORING CONVERSATION IS THE DRAFT ROUTE, and it is deliberately not a write. A person says a
/// sentence, a model turns it into a rule, and the rule comes BACK to be confirmed; storing it is a
/// separate call the person makes, and making it live is a third. Every write goes through
/// <see cref="SessionRuleStore"/>, which is the gate: a rule naming a check we do not ship, or supplying
/// the wrong arguments to one we do, is refused here with the store's own stated reason - and the draft
/// route runs a proposal through that same gate before offering it, so nobody is ever asked to agree to a
/// rule the writing route would then refuse.
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
    public static void Map(
        IEndpointRouteBuilder app,
        SessionRuleStore store,
        RuleAuthor author,
        Func<CcDirector.Core.Tenancy.TenantId> currentTenant)
    {
        if (author is null) throw new ArgumentNullException(nameof(author));
        if (currentTenant is null) throw new ArgumentNullException(nameof(currentTenant));

        // MAKE A RULE BY TALKING. Say what you want in ordinary words; a model works out what the product
        // has to hold and hands it BACK to be confirmed. THIS ROUTE STORES NOTHING - it answers with a
        // rule to look at, or with the one question it needs answered, or with a stated refusal. What
        // comes back under "rule" is exactly the body the writing route above takes, so confirming a
        // drafted rule is posting it, and the rule that is then stored is in dry run like every other one.
        // The person is still between the sentence and the first real act, twice: once to store it and
        // once to promote it.
        app.MapPost("/gateway/rules/draft", async (JsonElement body, CancellationToken ct) =>
        {
            var turns = SessionRuleWire.Turns(body);
            if (turns.Count == 0)
                return Results.Json(
                    new { error = "say what you want the rule to do - there is nothing here to turn into one." },
                    statusCode: StatusCodes.Status400BadRequest);

            // The screen they captured, when they captured one. It is what turns "guess what the screen
            // probably says" into "read the words off this". With it comes WHICH SESSION it came from -
            // the agent and the machine - because the same trouble prints different words on different
            // agents, and because a rule written against a session is for that session's agent by default.
            // "allAgents" is the star: the account saying this one is for every agent.
            var origin = new RuleSessionOrigin(
                RuleCallJson.Text(body, "sessionAgent") ?? "",
                RuleCallJson.Text(body, "sessionMachine") ?? "");
            var allAgents = body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("allAgents", out var star)
                && star.ValueKind == JsonValueKind.True;
            var reading = await author.DraftAsync(
                currentTenant(), turns, ct, RuleCallJson.Text(body, "screen") ?? "", origin, allAgents);

            if (reading.Proposal is not null)
            {
                FileLog.Write("[SessionRuleEndpoints] POST /gateway/rules/draft: drafted a rule to confirm");
                return Results.Json(SessionRuleWire.Project(reading.Proposal));
            }

            if (reading.Question is not null)
            {
                FileLog.Write("[SessionRuleEndpoints] POST /gateway/rules/draft: asked a question back");
                return Results.Json(new { question = reading.Question });
            }

            // A DRAFT THAT COULD NOT BE MADE SAYS SO. It never degrades into a rule assembled out of the
            // parts that could be read - a rule nobody meant is worse than no rule.
            FileLog.Write($"[SessionRuleEndpoints] POST /gateway/rules/draft REFUSED: {reading.Refusal}");
            return Results.Json(new { error = reading.Refusal }, statusCode: StatusCodes.Status400BadRequest);
        });

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
