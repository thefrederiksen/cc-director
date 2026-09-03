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
/// THE SCREEN IS READ HERE, NEVER SENT HERE (fix round D, ruling D2). Both the draft route and the write
/// route take a SESSION ID. The Gateway locates that session in the caller's own account, reads its screen
/// itself, and takes the agent and machine from the roster; nothing about the screen is accepted from the
/// caller. A request that names no session is refused, on both routes - authoring from memory is not a
/// mode, and a rule cannot be stored without the write gate reading the screen again and checking every
/// trigger word against it.
///
/// A REFUSAL COMES BACK AS A REFUSAL. The store's reason is returned verbatim with a 400, never flattened
/// into a generic failure - a caller that cannot see why its rule was rejected will guess, and guessing is
/// how a rule ends up subtly different from the sentence that was meant. And a body that cannot be READ -
/// a ceiling written as a decimal, a list of checks that is not a list - is a refusal too, with the reason,
/// never a server error (ruling D7).
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

        // MAKE A RULE BY TALKING. Say what you want in ordinary words and name the session it is about; a
        // model reads that session's screen and works out what the product has to hold, and hands it BACK
        // to be confirmed. THIS ROUTE STORES NOTHING - it answers with a rule to look at, or with the one
        // question it needs answered, or with a stated refusal. What comes back under "rule" is exactly the
        // body the writing route takes, so confirming a drafted rule is posting it, and the rule that is
        // then stored is in dry run like every other one. The person is still between the sentence and
        // the first real act, twice: once to store it and once to promote it.
        //
        // "allAgents" is the star: the account saying this one is for every agent. Otherwise the rule is
        // for the named session's agent, which the Gateway reads off the roster - never off the request.
        app.MapPost("/gateway/rules/draft", async (JsonElement body, CancellationToken ct) =>
        {
            var tenant = currentTenant();
            try
            {
                var turns = SessionRuleWire.Turns(body);
                if (turns.Count == 0)
                    return Results.Json(
                        new { error = "say what you want the rule to do - there is nothing here to turn into one." },
                        statusCode: StatusCodes.Status400BadRequest);

                var reading = await author.DraftAsync(
                    tenant, turns,
                    RuleCallJson.Text(body, "sessionId"),
                    SessionRuleWire.Flag(body, "allAgents"),
                    ct);

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
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleEndpoints] POST /gateway/rules/draft REFUSED: {ex.Reason}");
                return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status400BadRequest);
            }
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
        //
        // THIS IS THE ONE DOOR, SO GROUNDING RUNS HERE TOO (ruling D2, item 5). The body names the session
        // the rule was drafted against; the Gateway reads that session's screen again, freshly, and runs
        // the same check the draft route ran - every trigger word on the screen, the agent scope the
        // session's agent or the account's stated star. A hand-edited proposal cannot smuggle an
        // ungrounded word past this gate, and neither can a caller that skipped the draft route.
        app.MapPost("/gateway/rules", async (JsonElement body, CancellationToken ct) =>
        {
            var tenant = currentTenant();
            try
            {
                var words = SessionRuleWire.Strings(body, "triggerWords");
                var scope = SessionRuleWire.ReadScope(body);
                var notGrounded = await author.WhyNotGroundedAsync(
                    tenant,
                    RuleCallJson.Text(body, "sessionId"),
                    words,
                    scope,
                    SessionRuleWire.Flag(body, "allAgents"),
                    ct);
                if (notGrounded is not null)
                {
                    FileLog.Write($"[SessionRuleEndpoints] POST /gateway/rules REFUSED at the write gate: {notGrounded}");
                    return Results.Json(new { error = notGrounded }, statusCode: StatusCodes.Status400BadRequest);
                }

                var rule = store.Create(
                    RuleCallJson.Text(body, "instruction") ?? "",
                    RuleCallJson.Text(body, "screenDescription") ?? "",
                    words,
                    SessionRuleWire.Calls(body),
                    scope,
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
