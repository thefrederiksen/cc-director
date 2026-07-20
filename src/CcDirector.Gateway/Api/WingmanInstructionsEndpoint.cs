using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The editable, versioned wingman-instructions surface for the Cockpit settings page (issue #537).
/// The wingman uses the ACTIVE instructions (the user's custom version, else the deployed default);
/// this exposes viewing, editing (new version), version history + revert, and the managed-default
/// flow (see the dev team's changes, switch to the latest default). Backed by
/// <see cref="WingmanInstructionsStore"/>.
///
/// DENIED IN WHOLE ON HOSTED (issue #1853, read side). Every route in this file is refused on the hosted
/// Gateway, for two independent reasons that both land on the same answer.
///
/// FIRST, THE CONTENT. GET /gateway/wingman/instructions/records pages the wingman training capture, and
/// each of those records holds up to <see cref="WingmanTrainingStore.MaxTerminalChars"/> characters of raw
/// session TERMINAL output plus the agent's reply, written to one shared directory with no tenant in it.
/// A record is addressed by a POSITIONAL identifier - "&lt;filename&gt;#&lt;lineindex&gt;" - so a caller does not
/// even have to guess: it can walk the index. POST /gateway/wingman/instructions/test then hands those same
/// records back in full (<c>reply</c> and <c>oldSpoken</c> per record) to whoever asked. Issue #1853 set out
/// to deny the training WRITE on hosted, but a write deny does nothing about records already on disk, and
/// nothing about the read. This closes the read.
///
/// SECOND, THE OWNERSHIP. The instructions themselves are a single-owner control over the whole box, the
/// same class as the /gateway settings group: a Gateway has ONE active wingman prompt, and it is the prompt
/// every account's wingman speaks through. A tenant that saves a version, reverts a version or switches to
/// default is not editing its own copy - there is no own copy - it is rewriting what the machine says to
/// everybody. There is nothing to partition by, so a per-tenant answer would mean inventing state the
/// schema does not have.
///
/// The A/B test route is a third strike on its own: it spends up to <see cref="MaxTestRecords"/> real brain
/// calls per request on shared infrastructure, driven by attacker-chosen draft text.
///
/// It is a DENY OF THE WHOLE GROUP because the read and the write are equally damaging here and a
/// route-by-route guard rots - the next route added to this file would be open by default.
///
/// It REFUSES rather than returning an empty record list. "You have no training records" is a FALSE
/// statement on a box that has them; a refusal is merely an absent one.
///
/// Self-host is COMPLETELY unchanged, and that is the control. Self-host has one tenant and one owner, so
/// the records are the owner's own terminal output and the prompt is the owner's own prompt.
/// </summary>
internal static class WingmanInstructionsEndpoint
{
    /// <summary>Cap on records re-run per A/B test - each one is a (serial, sometimes slow) brain call.</summary>
    private const int MaxTestRecords = 5;

    /// <summary>
    /// The hosted refusal for the whole wingman-instructions group (issue #1853, read side), or null on
    /// self-host where nothing changes.
    ///
    /// Gated on <see cref="GatewayHostedMode.IsHosted"/> - the INDEPENDENT deployment signal - and NOT on a
    /// boundary or tenant argument being passed in. A security branch that depends on an optional argument
    /// fails OPEN when a caller omits it, which is exactly how the hosted account-status fix nearly shipped
    /// a hole: omit the argument and a hosted Gateway silently takes the self-host path. Asking hosted mode
    /// directly means this group cannot serve captured terminal output on hosted however the host is wired.
    ///
    /// 404 rather than 403: on hosted these routes do not exist as a concept - there is no per-tenant
    /// training pool and no per-tenant wingman prompt - so "not here" is the truthful answer. 403 would
    /// imply the right credential could reach them, and none can.
    ///
    /// UN-DENY CONDITION - REMOVING THIS DENY REQUIRES ALSO PURGING OR PARTITIONING WHAT ACCUMULATED BEHIND
    /// IT. Two SEPARATE questions, and here the first one already fails.
    ///
    /// (a) DOES ANYTHING STILL WRITE IT? YES, for the training records - and the writer is worse than a
    /// route. The prompt half IS contained: every route that can rewrite the wingman instructions (save,
    /// revert, switch-to-default) is in this group and refused with the reads. The RECORDS are written by
    /// <c>WingmanVoiceService.GenerateOnceAsync</c> through <c>WingmanTrainingStore.CaptureAsync</c>, and
    /// that path is reached by the Gateway's OWN voice sweep timer (<c>GatewayHost.SweepVoiceSessionsAsync</c>
    /// pre-builds voice for idle sessions on an interval). So raw session terminal output keeps landing in
    /// one untenanted store on hosted WITH NO REQUEST FROM ANYBODY - an unattended background writer, not
    /// merely a route this deny happens not to cover. Issue #1853's separate interim write deny has not
    /// landed. Looking only at the denied routes would have missed this entirely.
    ///
    /// (b) WHAT ALREADY EXISTS? Records from before the deny, plus everything the timer adds while it
    /// stands. They carry no tenant, so they cannot be attributed after the fact: the choice is deletion or
    /// quarantine, never a later migration.
    /// </summary>
    private static IResult? DenyOnHosted()
    {
        if (!GatewayHostedMode.IsHosted) return null;

        FileLog.Write("[WingmanInstructionsEndpoint] DENIED on hosted: the training records hold raw session terminal output in one shared store, and the wingman prompt is a single-owner control with no per-tenant answer");
        return Results.Json(
            new { error = "the wingman instructions surface is not available on the hosted gateway" },
            statusCode: StatusCodes.Status404NotFound);
    }

    /// Returns the guarded route group. That return value exists SOLELY so a test can map a brand-new
    /// route onto the same group and prove it is refused on hosted with no deny written for it - the
    /// property that distinguishes a group filter from a per-route guard, and which is otherwise
    /// invisible to any test that only drives the routes existing today.
    public static RouteGroupBuilder Map(IEndpointRouteBuilder outer, WingmanInstructionsStore store,
        WingmanTrainingStore training, Func<WingmanModelRole, CancellationToken, Task<IAgentBrain>> brainProvider)
    {
        var translator = new WingmanTranslator(brainProvider);

        FileLog.Write($"[WingmanInstructionsEndpoint] mapping /gateway/wingman/instructions; hosted={GatewayHostedMode.IsHosted} - on hosted EVERY route in this group is refused (issue #1853)");

        // The whole group behind ONE filter, rather than a guard line repeated in every handler.
        // A repeated guard is a thing to forget: the route added next year would be open by default and
        // nobody would notice. A group filter runs before EVERY route mapped below, including routes that
        // do not exist yet, so the refusal cannot rot as the group grows. The empty prefix keeps the route
        // paths written out in full, exactly as before, so the self-host surface is byte-identical.
        var app = outer.MapGroup("");
        app.AddEndpointFilter(async (ctx, next) =>
        {
            if (DenyOnHosted() is { } denied) return denied;
            return await next(ctx);
        });

        // THE ROUTES ARE MAPPED WHERE `outer` IS NOT IN SCOPE - deliberately, and that is the only reason
        // MapRoutes exists as a separate method. Copied from the key-vault deny (pull request #1904), which
        // is the reviewed instance of this pattern.
        //
        // Written inline here, beside both builders, each of these NINE routes could INDIVIDUALLY be mapped
        // onto `outer` instead of onto `app` - a one-word edit that compiles, passes every existing test,
        // and opens exactly that route on hosted while the other eight stay correctly denied. That is nine
        // independently bypassable primitives, and under the bypassability rule each would owe its own
        // full-suite security run. Handing the guarded group to a method that never receives the ungrouped
        // builder makes the mistake INEXPRESSIBLE rather than merely unlikely: inside MapRoutes there is
        // nothing to map onto except the guarded group. The count falls by DESIGN, not by an argument about
        // how careful the next author will be.
        MapRoutes(app, store, training, translator);
        return app;
    }

    /// <summary>
    /// The nine wingman-instructions routes. Takes the GUARDED group and nothing else - see the note at the
    /// call site: the ungrouped route builder is deliberately out of scope here, so no route in this family
    /// can be mapped around the hosted filter.
    /// </summary>
    private static void MapRoutes(RouteGroupBuilder app, WingmanInstructionsStore store,
        WingmanTrainingStore training, WingmanTranslator translator)
    {
        // Current state: the active instructions, whether they are customized, whether the dev team
        // has shipped a newer default, and the deployed-default identity.
        app.MapGet("/gateway/wingman/instructions", () =>
        {
            var active = store.Active();
            return Results.Json(new
            {
                active = Project(active),
                isCustomized = store.IsCustomized,
                updateAvailable = store.UpdateAvailable,
                defaultVersion = store.DefaultVersion,
                defaultHash = store.DefaultHash,
            });
        });

        // Save edited instructions as a new version and make them active.
        app.MapPut("/gateway/wingman/instructions", (WingmanInstructionsBody? req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Content))
                return Results.Json(new { error = "content is required" }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var v = store.Save(req.Content, req.Label);
                FileLog.Write($"[WingmanInstructionsEndpoint] saved version {v.Id}");
                return Results.Json(new { active = Project(v), isCustomized = store.IsCustomized });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // Version history, newest first.
        app.MapGet("/gateway/wingman/instructions/versions", () =>
            Results.Json(new { versions = store.Versions().Select(Project).ToList() }));

        // Make an existing version active again.
        app.MapPost("/gateway/wingman/instructions/revert", (RevertBody? req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Id))
                return Results.Json(new { error = "id is required" }, statusCode: StatusCodes.Status400BadRequest);
            if (!store.Revert(req.Id))
                return Results.Json(new { error = "unknown version id" }, statusCode: StatusCodes.Status404NotFound);
            return Results.Json(new { active = Project(store.Active()), isCustomized = store.IsCustomized });
        });

        // The deployed default (the DevThrottle dev team's shipped instructions).
        app.MapGet("/gateway/wingman/instructions/default", () =>
        {
            var d = store.DefaultAsVersion();
            return Results.Json(new { version = store.DefaultVersion, hash = store.DefaultHash, content = d.Content });
        });

        // The managed-default review: is a newer default available, and what did the dev team change
        // (the acknowledged/based-on default -> the new default), so the page can show the diff.
        app.MapGet("/gateway/wingman/instructions/update", () =>
        {
            var (ackVersion, ackContent) = store.AcknowledgedDefault();
            return Results.Json(new
            {
                updateAvailable = store.UpdateAvailable,
                isCustomized = store.IsCustomized,
                acknowledgedDefaultVersion = ackVersion,
                acknowledgedDefaultContent = ackContent,
                newDefaultVersion = store.DefaultVersion,
                newDefaultContent = store.DefaultContent,
            });
        });

        // Adopt the deployed default (drop the custom version, acknowledge the latest default).
        app.MapPost("/gateway/wingman/instructions/switch-to-default", () =>
        {
            store.SwitchToDefault();
            return Results.Json(new { active = Project(store.Active()), isCustomized = store.IsCustomized, updateAvailable = store.UpdateAvailable });
        });

        // Recent captured training sessions (issue #537): the pool the user picks from to A/B-test a
        // draft prompt. Empty until the wingman_training_capture setting has been on for some turns.
        app.MapGet("/gateway/wingman/instructions/records", (int? limit) =>
        {
            var n = Math.Clamp(limit ?? 20, 1, 100);
            var records = training.ListRecords(n).Select(r => new
            {
                id = r.Id, source = r.Source, atUtc = r.AtUtc, sessionId = r.SessionId,
                replyPreview = r.ReplyPreview, spokenPreview = r.SpokenPreview,
            }).ToList();
            return Results.Json(new { records, captureEnabled = training.Enabled });
        });

        // A/B test (issue #537): re-run the DRAFT instructions over the chosen saved sessions and
        // return, per record, the agent reply, the wingman's ORIGINAL spoken output, and the NEW one
        // the draft produces - so the user sees the effect before saving. Does NOT change the live
        // instructions. Each record is a brain call, so the count is capped.
        app.MapPost("/gateway/wingman/instructions/test", async (InstructionsTestBody? req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Content))
                return Results.Json(new { error = "content (the draft instructions) is required" }, statusCode: StatusCodes.Status400BadRequest);
            if (req.RecordIds is null || req.RecordIds.Length == 0)
                return Results.Json(new { error = "pick at least one saved session" }, statusCode: StatusCodes.Status400BadRequest);

            var ids = req.RecordIds.Take(MaxTestRecords).ToList();
            var results = new List<object>();
            foreach (var id in ids)
            {
                var rec = training.GetRecord(id);
                if (rec is null) { results.Add(new { id, error = "record not found" }); continue; }
                if (string.IsNullOrWhiteSpace(rec.Reply)) { results.Add(new { id, error = "record has no agent reply to translate" }); continue; }
                try
                {
                    // No session title: a training record stores only the session ID, and the session it
                    // was captured from may be long gone, so there is no name to resolve. Null makes the
                    // OPEN WITH THE SESSION TITLE rule no-op for this comparison (passing the ID instead
                    // would make the wingman read out an identifier, which the same prompt forbids). The
                    // draft-vs-live diff this endpoint shows is therefore title-less on both sides, which
                    // is the honest comparison - both were produced without one.
                    var t = await translator.TranslateWithAsync(req.Content, rec.RecentContext, rec.Reply, sessionTitle: null, ct);
                    results.Add(new { id, source = rec.Source, reply = rec.Reply, oldSpoken = rec.Spoken, newSpoken = t.Spoken, replySeconds = t.ReplySeconds });
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[WingmanInstructionsEndpoint] test record {id} FAILED: {ex.Message}");
                    results.Add(new { id, source = rec.Source, reply = rec.Reply, oldSpoken = rec.Spoken, error = ex.Message });
                }
            }
            return Results.Json(new { results, ranCount = results.Count, capped = req.RecordIds.Length > MaxTestRecords });
        });
    }

    private static object Project(WingmanInstructionsStore.InstructionVersion v) => new
    {
        id = v.Id,
        label = v.Label,
        source = v.Source,
        createdAt = v.CreatedAtUtc,
        hash = v.Hash,
        contentLength = v.Content.Length,
        content = v.Content,
    };
}

/// <summary>Body of the save route: the edited instructions and an optional label.</summary>
public sealed class WingmanInstructionsBody
{
    public string Content { get; set; } = "";
    public string? Label { get; set; }
}

/// <summary>Body of the revert route: the version id to make active.</summary>
public sealed class RevertBody
{
    public string Id { get; set; } = "";
}

/// <summary>Body of the A/B test route: the draft instructions and the saved-session ids to re-run.</summary>
public sealed class InstructionsTestBody
{
    public string Content { get; set; } = "";
    public string[] RecordIds { get; set; } = Array.Empty<string>();
}
