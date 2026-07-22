using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
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
    /// (a) DOES ANYTHING STILL WRITE IT? NOT ON HOSTED, in either half. The prompt half was always
    /// contained: every route that can rewrite the wingman instructions (save, revert, switch-to-default) is
    /// in this group and refused with the reads. For the training RECORDS there are three writers, none in
    /// this group: the voice-turn route, the explain route, and <c>WingmanVoiceService.GenerateOnceAsync</c>
    /// (reached by the Gateway's OWN voice sweep timer, so it needs no request from anybody). ALL THREE
    /// funnel through <c>WingmanTrainingStore.CaptureAsync</c>, which was already OPT-IN and DEFAULTS TO
    /// FALSE - but "off by default" is not "cannot be turned on", so THIS PASS HOST-GATES that capture too
    /// (defense in depth, deny-by-default, matching the key-vault deny #1904): <c>CaptureAsync</c> and its
    /// <c>WriteAsync</c> both NO-OP on hosted, so even with the setting on, no raw session terminal output
    /// accumulates in the shared untenanted store while the deny stands. That gate is safe because the
    /// store's ONLY reader is this now-denied group (the /records and /test routes) - verified nothing
    /// billing / metering consumes it. #1853's separate interim write deny is subsumed by this gate.
    ///
    /// (b) WHAT ALREADY EXISTS? A SEPARATE QUESTION, and the one that decides the un-deny. Gating the write
    /// is a statement about the future; it is not evidence about the past. Records may already be on disk
    /// from any period when the setting was on, or carried in from a self-host box. They carry no tenant, so
    /// they cannot be attributed after the fact: the choice is deletion or quarantine, never a later
    /// migration.
    ///
    /// HOW THE DENY IS EXPRESSED - THE SHARED REFUSAL PRIMITIVE, NOT A BESPOKE FILTER. This group is denied
    /// through <see cref="HostedRouteDeny.ExclusiveGroup"/>, the ONE hosted-refusal boundary every deny
    /// family on this Gateway adopts (reference implementation: the key-vault deny in pull request #1904).
    /// An earlier revision rolled its own <c>AddEndpointFilter</c> deny before the primitive existed; it has
    /// been replaced so the release ships ONE refusal boundary. The group owns the <c>/gateway/wingman/instructions</c>
    /// prefix OUTRIGHT - nothing else serves beneath it - so the exclusive shape fits: on hosted the nine
    /// handlers are NEVER MAPPED and ONE verb-less catch-all refuses everything under the prefix plus a root
    /// refusal at the prefix itself, covering every verb, every request shape, and every future sub-path for
    /// free. The exclusivity claim is CHECKED at startup by <see cref="HostedRefusalRouteSpace.ValidateBeforeStart"/>,
    /// which fails the Gateway if any live route serves beneath the prefix. Off hosted the primitive maps the
    /// nine real handlers exactly as an unguarded builder would, with no refusal at all - self-host unchanged.
    /// </summary>
    /// <summary>The exclusive prefix the wingman-instructions route group owns outright on hosted.</summary>
    internal const string Prefix = "/gateway/wingman/instructions";

    /// <summary>The single error string the hosted refusal serves. Held here so a test can assert against the
    /// exact string that is served rather than a copy that could drift.</summary>
    internal const string RefusalMessage = "the wingman instructions surface is not available on the hosted gateway";

    /// <summary>
    /// The hosted refusal payload for the whole wingman-instructions group (issue #1853, read side).
    /// Validated on construction, so a blank field fails the Gateway at startup rather than serving a refusal
    /// a caller cannot act on. 404 rather than 403: on hosted these routes do not exist as a concept - there
    /// is no per-tenant training pool and no per-tenant wingman prompt - so "not here" is the truthful
    /// answer; 403 would imply the right credential could reach them, and none can. Driven off
    /// <see cref="GatewayHostedMode.IsHosted"/> inside the primitive - the INDEPENDENT deployment signal, not
    /// an optional argument a caller can omit and thereby fail OPEN.
    /// </summary>
    private static HostedDenial Denial() => new(
        family: "wingman-instructions",
        message: RefusalMessage,
        reason: "the training records hold raw session terminal output in one shared, untenanted store addressable " +
                "by a positional index, and the wingman prompt is a single-owner control over the whole box with no " +
                "per-tenant version to serve",
        unDenyInstruction: "do NOT simply remove this deny: the training capture is host-gated now (no new " +
                "accumulation on hosted) but a shared, untenanted store predates the gate - so tenant-partition the " +
                "training store, purge or quarantine the pre-existing records (records written with no tenant cannot " +
                "be attributed afterwards - the choice is deletion or quarantine, never a later migration), and give " +
                "the single-owner wingman prompt a per-tenant model before any of these routes come back",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Maps the wingman-instructions routes and RETURNS the denied group they were mapped through.
    ///
    /// The routes are mapped through the group HANDLE (<see cref="HostedDenyGroup"/>), never through the
    /// ungrouped builder: the handle is obtainable only from <see cref="HostedRouteDeny"/>, so a route mapped
    /// around the refusal is not expressible in <see cref="MapRoutes"/> without changing its signature - the
    /// bypass count is reduced by design, not by care. On hosted the exclusive catch-all refuses the whole
    /// group and each handler is DISCARDED; off hosted the handle maps each handler as an unguarded builder
    /// would. The return value exists so the future-route property is statable from outside this file: a test
    /// maps a brand-new route through the returned handle and shows the refusal already covers routes nobody
    /// has written yet.
    /// </summary>
    public static HostedDenyGroup Map(IEndpointRouteBuilder outer, WingmanInstructionsStore store,
        WingmanTrainingStore training, Func<WingmanModelRole, CancellationToken, Task<IAgentBrain>> brainProvider)
    {
        var translator = new WingmanTranslator(brainProvider);

        FileLog.Write($"[WingmanInstructionsEndpoint] mapping {Prefix}; hosted={GatewayHostedMode.IsHosted} - on hosted the whole group is refused via the shared refusal primitive (issue #1853)");

        var group = HostedRouteDeny.ExclusiveGroup(outer, Prefix, Denial());
        MapRoutes(group, store, training, translator);
        return group;
    }

    /// <summary>
    /// The nine wingman-instructions routes, mapped relative to the <see cref="Prefix"/> so the full paths
    /// are <c>/gateway/wingman/instructions</c> and its sub-paths exactly as before. Takes the denied GROUP
    /// HANDLE and nothing else: the ungrouped route builder is deliberately out of scope here so no route can
    /// be mapped around the hosted refusal.
    /// </summary>
    private static void MapRoutes(HostedDenyGroup app, WingmanInstructionsStore store,
        WingmanTrainingStore training, WingmanTranslator translator)
    {
        // Current state: the active instructions, whether they are customized, whether the dev team
        // has shipped a newer default, and the deployed-default identity.
        app.MapGet("", () =>
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
        app.MapPut("", (WingmanInstructionsBody? req) =>
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
        app.MapGet("/versions", () =>
            Results.Json(new { versions = store.Versions().Select(Project).ToList() }));

        // Make an existing version active again.
        app.MapPost("/revert", (RevertBody? req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Id))
                return Results.Json(new { error = "id is required" }, statusCode: StatusCodes.Status400BadRequest);
            if (!store.Revert(req.Id))
                return Results.Json(new { error = "unknown version id" }, statusCode: StatusCodes.Status404NotFound);
            return Results.Json(new { active = Project(store.Active()), isCustomized = store.IsCustomized });
        });

        // The deployed default (the DevThrottle dev team's shipped instructions).
        app.MapGet("/default", () =>
        {
            var d = store.DefaultAsVersion();
            return Results.Json(new { version = store.DefaultVersion, hash = store.DefaultHash, content = d.Content });
        });

        // The managed-default review: is a newer default available, and what did the dev team change
        // (the acknowledged/based-on default -> the new default), so the page can show the diff.
        app.MapGet("/update", () =>
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
        app.MapPost("/switch-to-default", () =>
        {
            store.SwitchToDefault();
            return Results.Json(new { active = Project(store.Active()), isCustomized = store.IsCustomized, updateAvailable = store.UpdateAvailable });
        });

        // Recent captured training sessions (issue #537): the pool the user picks from to A/B-test a
        // draft prompt. Empty until the wingman_training_capture setting has been on for some turns.
        app.MapGet("/records", (int? limit) =>
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
        app.MapPost("/test", async (InstructionsTestBody? req, CancellationToken ct) =>
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
