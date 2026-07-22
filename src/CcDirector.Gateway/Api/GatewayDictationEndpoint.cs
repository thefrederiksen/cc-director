using System.Collections.Concurrent;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Durable, server-owned dictation upload (issue #1006).
///
/// The mobile app persists the raw recorded audio locally (IndexedDB) the instant Send is pressed,
/// then streams it here in SHA-checked chunks. Once the clip is fully uploaded the GATEWAY assembles
/// it, transcribes it, and injects the resulting text into the owning session ITSELF. So once the
/// audio reaches the server a dead tab, a page refresh, or a dropped connection can no longer lose a
/// recorded utterance: the client only has to get the bytes up (resumable, retry-per-chunk from the
/// durable local copy), and the server finishes the turn.
///
///   POST /dictation/upload               { sessionId, baselineBufferBytes } + Idempotency-Key -> { upload_id }
///   PUT  /dictation/{uploadId}/chunk/{i}  octet-stream + X-Chunk-Sha256                        -> { ok }
///   POST /dictation/{uploadId}/complete   { sessionId,totalChunks,mime,ext,before,after,baselineBufferBytes,resumed }
///                                          -> 200 { submitted, movedOn, transcript } | 200 { dropped, reason } | 409 { missing } | 402 | 5xx
///   POST /dictation/{uploadId}/ack        -> 200 { ok, retired }
///
/// A retried complete is single-flighted per uploadId (so the turn is submitted at most once WHILE this
/// instance holds it), and de-duplicated durably by the per-upload-id delivery record on disk (issue
/// #1183): once an upload id is DELIVERED or ABANDONED, its terminal tombstone makes every later
/// register/complete return the cached outcome and NEVER inject a second turn - past any age and across a
/// Gateway restart. A PENDING upload's chunks are retained (never age-swept) until it becomes terminal;
/// the tombstone is retired only by the client ack. Every route is token-gated via
/// <see cref="AuthMiddleware.HasValidToken"/> so it holds even when the production tray Gateway runs with
/// the global auth middleware off.
///
/// DENIED IN WHOLE ON HOSTED (issue #1884). This is the SIBLING of the utterance upload family denied in
/// <see cref="GatewayWingmanVoiceEndpoint"/> under issue #1896 - a different route family standing on the
/// same <see cref="VoiceUploadStore"/> shape, with the same defect: one on-disk root shared across every
/// account, every directory and record and chunk keyed SOLELY by the caller-supplied upload id, a
/// <c>DictationDeliveryRecord</c> that carries no tenant, and a static <c>_completes</c> cache keyed solely
/// by that same id.
///
/// The disclosure is live and needs no session: after account A completes upload id X, account B posts
/// /dictation/upload with <c>Idempotency-Key: X</c>, the terminal-register short-circuit reads A's record,
/// and B is handed A's TRANSCRIPT. That leg never looks a session up, so the fact that /complete's session
/// lookup already fails on hosted does not contain it. Before A reaches a terminal state, B can also
/// overwrite A's chunks, or ack or abandon A's record.
///
/// A caller-supplied identifier is not a tenant boundary. Secrecy of an identifier is not authorization,
/// and upload ids travel through client logs, retries and store-and-forward queues.
///
/// The whole family, not only the leg that hands text back: ack and abandon destroy another account's
/// in-flight recording, which is the same missing boundary with a different consequence. It is a deny
/// rather than a partition because the records carry no tenant to partition BY - partitioning the store is
/// issue #1884's job, and un-denying is gated on it. It refuses rather than reporting an empty or dropped
/// upload, because "your dictation was dropped" is a FALSE statement where a refusal is merely absent.
///
/// Self-host is COMPLETELY unchanged, and that is the control. Self-host has one tenant, so the shared root
/// is the owner's own root and the phone's durable store-and-forward dictation lane behaves exactly as it
/// always has.
/// </summary>
internal static class GatewayDictationEndpoint
{
    // How much the session's terminal output may grow after a RESUMED clip was recorded before we treat
    // it as "moved on" and refuse to inject the now-stale dictation (issue #1006 guard). Immediate
    // (non-resumed) sends always inject; the 1-hour staging sweep is the hard backstop for staleness.
    private const long MovedOnBufferGrowthBytes = 512;

    // In-memory single-flight for complete, keyed by uploadId: concurrent or retried completes await the
    // SAME in-flight work so the turn is submitted at most once WHILE this instance holds the entry. The
    // entry is dropped as soon as the work settles - the DURABLE de-dupe (a delivered/abandoned upload id
    // never re-injecting, past any age and across a restart) is owned by the on-disk delivery record
    // (issue #1183), not by this cache, so there is no age-swept idempotency window to reopen the hole.
    private sealed record CompleteEntry(Lazy<Task<DictationOutcome>> Task);
    private static readonly ConcurrentDictionary<string, CompleteEntry> _completes = new();

    // uploadId -> sessionId, captured at register so the chunk handler (which only has the uploadId)
    // can refresh the session's orange "Transcribing..." heartbeat as chunks stream in (issue #1126).
    // Pruned when the upload reaches a terminal completion; a leaked entry for a never-completed upload
    // is a single guid-pair and is bounded by real abandoned-upload volume.
    private static readonly ConcurrentDictionary<string, string> _uploadSids = new();

    /// <summary>The exclusive prefix the dictation upload route group owns outright on hosted.</summary>
    internal const string Prefix = "/dictation";

    /// <summary>The single error string the hosted refusal serves. Held here so a test can assert against the
    /// exact string that is served rather than a copy that could drift.</summary>
    internal const string RefusalMessage = "dictation upload is not available on the hosted gateway";

    /// <summary>
    /// The hosted refusal payload for the whole /dictation upload family (issue #1884). Validated on
    /// construction, so a blank field fails the Gateway at startup rather than serving a refusal a caller
    /// cannot act on. 404 rather than 403: on hosted this upload family does not exist as a concept - an
    /// upload id is meaningless without a tenant to scope it to, and the store has none - so "not here" is
    /// the truthful answer; 403 would imply the right credential could reach it, and none can. Driven off
    /// <see cref="GatewayHostedMode.IsHosted"/> inside the primitive - the INDEPENDENT deployment signal, not
    /// an optional argument a caller can omit and thereby fail OPEN.
    ///
    /// UN-DENY CONDITION. Two SEPARATE questions, because answering only the first is how a deny gets
    /// mistaken for a clean slate.
    ///
    /// (a) DOES ANYTHING STILL WRITE IT? No, and NO OFF-ROUTE WRITER NEEDS A HOST-GATE HERE. Checked by
    /// sweeping the COMPLETE MUTATING SURFACE of the state rather than the routes that touch it: every
    /// <c>VoiceUploadStore</c> construction in the repository, then every production caller of every mutating
    /// method on the type, and finally any raw file writer into the root that bypasses the type. The only
    /// production instance on the dictation-uploads root is the one <c>GatewayHost</c> holds as
    /// <c>_dictationUploads</c>, which reaches this endpoint plus one READ-only status query
    /// (<c>DictationStatusFor</c>); Core's <c>DictationLockReader</c> also only reads. Every mutation -
    /// register, mark pending, store chunk, assemble, delete, mark delivered, mark failed, clear failed,
    /// record baseline, acknowledge, mark abandoned - is inside this refused group or inside a private helper
    /// (<c>RunCompleteCoreAsync</c>, <c>MapNonOkTranscription</c>) whose only caller is the completion leg,
    /// which is itself refused. The static <c>_completes</c> cache is filled only by that same leg.
    /// <c>SweepAbandoned</c> has no production caller. Nothing writes the root outside the store. The only
    /// residual write is the constructor ensuring the directory exists: an empty directory, never content. So
    /// with the routes refused, no writer to this store reaches it on hosted - there is no OFF-route writer
    /// to gate (unlike the transcription-telemetry and wingman-training stores, whose writers fire from
    /// undenied paths and are host-gated at the writer).
    ///
    /// (b) WHAT ALREADY EXISTS? A SEPARATE QUESTION, and it is not answered by (a). NO NEW WRITES IS A
    /// STATEMENT ABOUT THE FUTURE, NOT EVIDENCE ABOUT THE PAST. The shared dictation-uploads root may hold
    /// pre-deny cross-tenant staged audio, delivery records and transcripts. SO THE UN-DENY STILL REQUIRES
    /// PURGING OR QUARANTINING THE LEGACY ROOT, on top of issue #1884's tenant-keying of the store, the
    /// record and the <c>_completes</c> cache.
    ///
    /// NOTE THE BOUNDARY OF THE (a) CLAIM: it is about THIS store. A completed dictation also writes a turn
    /// into the shared transcription telemetry log - but that writer is now host-gated in
    /// <c>GatewayTranscriptionService.RecordTelemetry</c>, so it too stops on hosted (see the un-deny
    /// condition on <c>TranscriptionAnalysisEndpoint</c>).
    ///
    /// HOW THE DENY IS EXPRESSED - THE SHARED REFUSAL PRIMITIVE, NOT A BESPOKE FILTER. This group is denied
    /// through <see cref="HostedRouteDeny.ExclusiveGroup"/>, the ONE hosted-refusal boundary every deny
    /// family on this Gateway adopts (reference implementation: the key-vault deny in pull request #1904). An
    /// earlier revision rolled its own <c>AddEndpointFilter</c> deny before the primitive existed; it has
    /// been replaced. The family owns the <c>/dictation</c> prefix OUTRIGHT - nothing else serves beneath it
    /// - so on hosted the five handlers are NEVER MAPPED and ONE verb-less catch-all refuses everything under
    /// the prefix plus a root refusal at the prefix itself, covering every verb, every request shape, and
    /// every future sub-path for free. The exclusivity claim is CHECKED at startup by
    /// <see cref="HostedRefusalRouteSpace.ValidateBeforeStart"/>. Off hosted the primitive maps the five real
    /// handlers exactly as an unguarded builder would - self-host unchanged.
    /// </summary>
    private static HostedDenial Denial() => new(
        family: "dictation-upload",
        message: RefusalMessage,
        reason: "the dictation upload store keys every directory, record and chunk SOLELY by the caller-supplied " +
                "upload id under one on-disk root shared across every account, and a re-register of another " +
                "account's upload id short-circuits on its terminal tombstone and hands back that account's " +
                "transcript with no session lookup in the way - so a caller-supplied id is not a tenant boundary",
        unDenyInstruction: "do NOT simply remove this deny: tenant-key the store, the delivery record and the " +
                "_completes cache, THEN purge or quarantine the pre-existing shared dictation-uploads root " +
                "(pre-deny cross-tenant staged audio, records and transcripts are already in it and this change " +
                "never touched them), and only then restore a tenant-scoped route",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Maps the dictation routes and RETURNS the denied group they were mapped through. The routes are mapped
    /// through the group HANDLE (<see cref="HostedDenyGroup"/>), never through the ungrouped builder: the
    /// handle is obtainable only from <see cref="HostedRouteDeny"/>, so a route mapped around the refusal is
    /// not expressible in <see cref="MapRoutes"/> without changing its signature - the bypass count is reduced
    /// by design, not by care. On hosted the exclusive catch-all refuses the whole group and each handler is
    /// DISCARDED; off hosted the handle maps each handler as an unguarded builder would. The return value
    /// exists so a test can map a brand-new route through the returned handle and show the refusal already
    /// covers routes nobody has written yet.
    /// </summary>
    public static HostedDenyGroup Map(IEndpointRouteBuilder outer, DirectorRegistry registry,
        SessionOwnerCache? owners, string token, GatewayTranscriptionService transcription,
        TranscribingSessions transcribingSessions, VoiceUploadStore uploads, Pairing.DeviceRegistry devices,
        Streaming.PushedSessionStore? pushedSessions = null,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand = null,
        TimeSpan? streamStale = null)
    {
        FileLog.Write($"[GatewayDictation] mapping {Prefix}; hosted={GatewayHostedMode.IsHosted} - on hosted the whole group is refused via the shared refusal primitive (issue #1884)");

        var app = HostedRouteDeny.ExclusiveGroup(outer, Prefix, Denial());

        // Gateway Cleanup mission, Phase 2 (PR E-B): resolve the owning Director push-store-first and inject
        // the dictation through the tunnel-first SessionVerbClient (the delivery marker rides the PromptRequest
        // DeliveryUploadId field, not an HTTP header), so this path no longer HTTP-dials the Director.
        var stale = streamStale ?? TimeSpan.FromSeconds(Core.Configuration.GatewayConfig.DefaultStreamStaleAfterSeconds);

        MapRoutes(app, registry, owners, token, transcription, transcribingSessions, uploads, devices,
            pushedSessions, sendCommand, stale);
        return app;
    }

    /// <summary>
    /// The five dictation upload routes, mapped relative to the <see cref="Prefix"/> so the full paths are
    /// <c>/dictation/upload</c> and <c>/dictation/{uploadId}/...</c> exactly as before. Takes the denied GROUP
    /// HANDLE and nothing else: the ungrouped route builder is deliberately out of scope here, so no route in
    /// this family can be mapped around the hosted refusal.
    /// </summary>
    private static void MapRoutes(HostedDenyGroup app, DirectorRegistry registry,
        SessionOwnerCache? owners, string token, GatewayTranscriptionService transcription,
        TranscribingSessions transcribingSessions, VoiceUploadStore uploads, Pairing.DeviceRegistry devices,
        Streaming.PushedSessionStore? pushedSessions,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand,
        TimeSpan stale)
    {
        app.MapPost("/upload", (DictationUploadRequest? body, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            var sid = body?.SessionId ?? "";
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "sessionId (guid) is required" }, statusCode: StatusCodes.Status400BadRequest);

            // The client's locally-generated id (its IndexedDB record id) is the Idempotency-Key AND the
            // upload id, so a resumed upload after a tab death maps back to the same staging dir.
            var key = ctx.Request.Headers["Idempotency-Key"].ToString();

            // Durable de-dupe at register (issue #1183): if this upload id already reached a terminal record
            // (delivered or abandoned), do NOT re-open it as a fresh PENDING upload - return the cached
            // outcome so a re-registering client (whose earlier response was lost) drops its on-device copy
            // and acknowledges instead of re-uploading and re-injecting. Survives a restart (on disk).
            var existing = string.IsNullOrWhiteSpace(key) ? null : uploads.ReadRecord(key);
            if (existing is { State: DictationDeliveryState.Delivered or DictationDeliveryState.Abandoned })
            {
                FileLog.Write($"[GatewayDictation] upload re-register of terminal uploadId={key} state={existing.State}");
                return TerminalRegisterResult(uploads.Register(key), existing);
            }
            // A PENDING or FAILED record (or none) is (re-)opened as a fresh PENDING upload. Register
            // (re-)opens the staging dir; MarkPending writes the explicit durable PENDING marker carrying the
            // sessionId, which BOTH persists the owning session on disk for the enforced session lock (issue
            // #1188, so the lock survives a Gateway restart) AND, for a FAILED id, IS the retry re-entry back
            // to PENDING - overwriting the FAILED marker while keeping the staged chunks (issue #1185).
            if (existing is { State: DictationDeliveryState.Failed })
                FileLog.Write($"[GatewayDictation] upload re-register clears FAILED uploadId={key}, retrying");
            var uploadId = uploads.Register(string.IsNullOrWhiteSpace(key) ? null : key);
            uploads.MarkPending(uploadId, sid);
            _uploadSids[uploadId] = sid;
            try { transcribingSessions.Begin(sid); } catch { /* the orange mark is a nicety */ }
            FileLog.Write($"[GatewayDictation] upload registered sid={sid} uploadId={uploadId}");
            return Results.Json(new { upload_id = uploadId });
        });

        app.MapPut("/{uploadId}/chunk/{index:int}", async (string uploadId, int index, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            if (!uploads.Exists(uploadId))
                return Results.Json(new { error = "unknown upload id (register it first)" }, statusCode: StatusCodes.Status404NotFound);

            var sha = ctx.Request.Headers["X-Chunk-Sha256"].ToString();
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            try
            {
                await uploads.StoreChunkAsync(uploadId, index, ms.ToArray(), string.IsNullOrEmpty(sha) ? null : sha, ctx.RequestAborted);
                // Heartbeat: a stored chunk is progress, so keep the orange mark alive past its idle
                // backstop for a slow upload that streams over more than the idle window (issue #1126).
                if (_uploadSids.TryGetValue(uploadId, out var chunkSid))
                    transcribingSessions.Refresh(chunkSid);
                return Results.Json(new { ok = true, index });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayDictation] chunk uploadId={uploadId} index={index} FAILED: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/{uploadId}/complete", async (string uploadId, DictationCompleteRequest? req, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            if (req is null || req.TotalChunks <= 0 || !Guid.TryParse(req.SessionId ?? "", out _))
                return Results.Json(new { error = "sessionId (guid) and totalChunks (>0) are required" },
                    statusCode: StatusCodes.Status400BadRequest);

            // A completion attempt is progress - keep the orange mark alive across the server-side
            // transcribe so a slow transcribe cannot let it age out mid-flight (issue #1126).
            transcribingSessions.Refresh(req.SessionId!);

            // DevThrottle Stats: this dictation is a VOICE turn; resolve WHICH surface recorded it from the
            // verified device key that authenticated this complete (the phone that recorded it, or the
            // cockpit browser for a cockpit Speak) so the tally does not mislabel cockpit voice as phone.
            // Captured here (in request context) and threaded into the cached single-flight run; all completes
            // for one upload id come from the same device, so the value is stable across retries.
            var deliverySurface = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;

            // Durable de-dupe (issue #1183): a DELIVERED or ABANDONED upload id has a terminal tombstone on
            // disk. Return its cached outcome and NEVER inject a second turn - even past the old one-hour
            // window and even after a Gateway restart (the record and this check both live on disk). This
            // handles the SEQUENTIAL/after-restart retry; the in-memory single-flight below handles two
            // CONCURRENT completes racing before the first tombstone is written (they share one run).
            var settled = uploads.ReadRecord(uploadId);
            if (settled is { State: DictationDeliveryState.Delivered or DictationDeliveryState.Abandoned })
            {
                EndTranscribing(transcribingSessions, req.SessionId!);
                _uploadSids.TryRemove(uploadId, out _);
                FileLog.Write($"[GatewayDictation] complete uploadId={uploadId}: cached terminal outcome " +
                    $"state={settled.State} (no re-injection)");
                return TerminalOutcome(settled).ToResult();
            }
            // A FAILED (parked) record is user-retryable, NOT a terminal short-circuit (issue #1185): this
            // complete IS the explicit retry, so clear the FAILED marker back to PENDING (keeping the staged
            // chunks) and re-drive the real work below.
            if (settled is { State: DictationDeliveryState.Failed })
            {
                uploads.ClearFailed(uploadId);
                FileLog.Write($"[GatewayDictation] complete uploadId={uploadId}: cleared FAILED, retrying");
            }

            // Per-uploadId single-flight: concurrent or retried completes await the SAME in-flight run, so a
            // still-PENDING id is assembled + transcribed + injected at most once even under a concurrent
            // race. The entry is dropped once the run settles (below); the durable tombstone owns de-dupe
            // from then on, so there is no age-swept cache window.
            var entry = _completes.GetOrAdd(uploadId, id => new CompleteEntry(
                new Lazy<Task<DictationOutcome>>(() => RunCompleteCoreAsync(
                    id, req, uploads, registry, owners, transcription, transcribingSessions, deliverySurface,
                    pushedSessions, sendCommand, stale))));

            DictationOutcome outcome;
            try
            {
                outcome = await entry.Task.Value;
            }
            catch (Exception ex)
            {
                _completes.TryRemove(uploadId, out _); // transient: let a retry re-run
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }

            // The Gateway OWNS the orange "Transcribing..." mark, so it clears it here - OUTSIDE the
            // single-flight cache - on EVERY terminal outcome (issue #1048, extended by #1126). Doing the
            // clear here, not inside the cached RunCompleteCoreAsync, means a RESENT completion for an
            // already-finished upload id (which returns the cached outcome WITHOUT re-running the core, so
            // the old in-core clear never fired again) still clears the mark. The ONLY outcome we keep the
            // mark for is an incomplete upload: more chunks are still coming and the client completes again
            // on the same upload id, so the session is genuinely still transcribing.
            if (!outcome.IsIncomplete)
            {
                EndTranscribing(transcribingSessions, req.SessionId!);
                _uploadSids.TryRemove(uploadId, out _);
            }
            // Drop the in-memory single-flight entry once the run settles. A terminal run has already
            // written the durable tombstone (inside the core), so a later retry short-circuits on the
            // ReadRecord check above; a non-terminal run (transient error, incomplete upload, out-of-credits)
            // leaves no tombstone, so the next complete re-runs the real work (issue #1183).
            _completes.TryRemove(uploadId, out _);
            return outcome.ToResult();
        });

        // Client acknowledgment (issue #1183): once the client has received a terminal (delivered or
        // abandoned) outcome, dropped its on-device copy, and will not re-drive this upload id, it calls
        // this to retire the durable tombstone. Idempotent: acking an already-retired (or never-created) id
        // is a no-op returning retired=false. If the ack is lost the tombstone simply persists and a later
        // re-complete returns the same outcome and the client re-acks - so the tombstone is retired ONLY on
        // a real client ack, never by age.
        app.MapPost("/{uploadId}/ack", (string uploadId, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            var retired = uploads.Acknowledge(uploadId);
            FileLog.Write($"[GatewayDictation] ack uploadId={uploadId} retired={retired}");
            return Results.Json(new { ok = true, retired });
        });

        // User-initiated ABANDON (issue #1181, Task 5): the user gives up on this dictation. Marks the
        // durable record ABANDONED - a terminal tombstone that DISCARDS the staged audio and clears the
        // session lock (the PENDING marker is gone, so IsSessionLocked is false and the session un-oranges).
        // Idempotent and safe against a race with delivery: if the turn already DELIVERED we do NOT abandon
        // (it landed) and say so; otherwise the id becomes ABANDONED. The client that still holds the audio
        // reconciles on its next contact - a re-register / re-complete of an abandoned id returns dropped, so
        // it drops its on-device copy with no resurrection and no duplicate. Abandon may target ANY surface's
        // dictation because it addresses the durable upload id, not the caller.
        app.MapPost("/{uploadId}/abandon", (string uploadId, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);

            var existing = uploads.ReadRecord(uploadId);
            if (existing is { State: DictationDeliveryState.Delivered })
            {
                FileLog.Write($"[GatewayDictation] abandon uploadId={uploadId}: already DELIVERED, not abandoning");
                return Results.Json(new { ok = true, upload_id = uploadId, abandoned = false, already_delivered = true });
            }

            uploads.MarkAbandoned(uploadId, "user_abandoned");
            // Clear the in-memory transcribing marks so the roster un-oranges at once (the durable PENDING
            // marker - the "Uploading from phone" source - is already gone via MarkAbandoned).
            var sid = existing?.SessionId;
            if (!string.IsNullOrEmpty(sid))
            {
                EndTranscribing(transcribingSessions, sid);
                transcribingSessions.ClearActivelyTranscribing(sid);
            }
            FileLog.Write($"[GatewayDictation] abandon uploadId={uploadId} sid={sid}: marked ABANDONED, staging discarded");
            return Results.Json(new { ok = true, upload_id = uploadId, abandoned = true });
        });
    }

    // Map a NON-Ok transcription result to the dictation outcome, or null when the result is Ok and the
    // caller should continue to inject (issue #1185).
    //
    // TWO SEPARATE QUESTIONS, and conflating them is what wedged the orange (defect 19):
    //
    //   1. What does the CLIENT get told? Only PermanentError gets the 422 "stop forever" contract.
    //      Out-of-credits stays 402 and every other non-Ok outcome stays a retryable 502, so the durable
    //      queue keeps re-driving a failure that might yet succeed. That classification is CORRECT and is
    //      deliberately unchanged here.
    //
    //   2. What state is the RECORD left in? EVERY non-Ok outcome now parks the record FAILED. This is the
    //      defect-19 fix. Parking is NOT "giving up": FAILED keeps the staged chunk bytes, and the next
    //      register/complete clears it back to PENDING and re-drives (see the FAILED re-entry at register
    //      and complete). What it changes is that a record which is going nowhere RIGHT NOW stops claiming
    //      the session is "Uploading from phone", because IsPending is false while FAILED.
    //
    // OBSERVED, not theorised (14 July 2026 log correlation): upload f13cb4b6d9d0 on 12 July stood PENDING
    // for 1 hour 30 minutes - painting its session orange across four Gateway restarts - while complete
    // returned 502 roughly fifteen times from THIS retryable arm, which returned without any terminal write.
    // It then transcribed and delivered 362 characters at 07:40. Both halves matter: the durable record was
    // right to keep the words (they landed), and the colour was lying for ninety minutes. Parking FAILED
    // here keeps the words AND ends the lie - that upload would still have delivered at 07:40.
    //
    // The retryable arm used to return silently, which is why the log could say WHEN it wedged but never
    // WHY. It logs now.
    internal static DictationOutcome? MapNonOkTranscription(
        GatewayTranscriptionResult result, string uploadId, VoiceUploadStore uploads)
    {
        if (result.Outcome == TranscriptionOutcome.Ok) return null;
        if (result.Outcome == TranscriptionOutcome.OutOfCredits)
        {
            // Park the record so the session stops reading "Uploading from phone" while there is no credit
            // to transcribe it with; the client still gets 402, and adding credit + retrying re-enters
            // PENDING and delivers. NOTE: never once observed to fire - zero OutOfCredits in any log on this
            // machine, ever, across 846 terminal outcomes. The mechanism is real; the cause is not this.
            uploads.MarkFailed(uploadId, "out_of_credits");
            FileLog.Write($"[GatewayDictation] complete uploadId={uploadId}: out of credits " +
                $"code={result.Code}; parked FAILED (chunks retained, retryable)");
            return DictationOutcome.OutOfCredits(HostedAiErrorMapper.MapCode(result.Code));
        }
        // A genuinely-permanent failure (unsupported/undecodable format, or too large to reduce - issue
        // #1139) can NEVER transcribe, so returning the generic retryable 502 makes the durable queue
        // re-drive it forever. Instead park the record FAILED with the reason code (KEEPING the chunks so an
        // explicit user retry can re-complete) and return the client's stop contract.
        if (result.Outcome == TranscriptionOutcome.PermanentError)
        {
            uploads.MarkFailed(uploadId, result.Code ?? "");
            FileLog.Write($"[GatewayDictation] complete uploadId={uploadId}: permanent failure " +
                $"code={result.Code}; parked FAILED");
            return DictationOutcome.Permanent(TranslatePermanentReason(result.Code));
        }
        // The retryable arm - THE ONE THAT ACTUALLY WEDGED (see f13cb4b6d9d0 above). Still a 502 the client
        // re-drives; now it parks the record so the colour tells the truth between attempts.
        uploads.MarkFailed(uploadId, result.Code ?? "transcription_error");
        FileLog.Write($"[GatewayDictation] complete uploadId={uploadId}: retryable transcription failure " +
            $"code={result.Code} error={result.Error}; parked FAILED (chunks retained, retryable)");
        return DictationOutcome.Error(StatusCodes.Status502BadGateway, result.Error ?? "transcription failed");
    }

    // Translate the transcription lane's machine-readable permanent-failure code into the client-facing
    // reason at THIS boundary (issue #1185): audio_too_large -> audio-too-large; unsupported_format and
    // non_decodable both -> unsupported-format. Any unrecognized permanent code is still a permanent stop,
    // so it defaults to the generic unsupported-format rather than being reclassified as retryable.
    internal static string TranslatePermanentReason(string? code) => code switch
    {
        "audio_too_large" => "audio-too-large",
        "unsupported_format" => "unsupported-format",
        "non_decodable" => "unsupported-format",
        _ => "unsupported-format",
    };

    // Map a terminal delivery record to the outcome a re-complete returns: the cached submitted result for
    // DELIVERED (so the turn is never injected twice), or a clear dropped result for ABANDONED.
    private static DictationOutcome TerminalOutcome(DictationDeliveryRecord record)
        => record.State == DictationDeliveryState.Abandoned
            ? DictationOutcome.Dropped(record.Reason ?? "")
            : DictationOutcome.Submitted(record.Submitted, record.MovedOn, record.Transcript);

    // The register-time response for an upload id that is already terminal: echoes the id plus the cached
    // outcome so a re-registering client drops its copy and acknowledges instead of re-uploading.
    private static IResult TerminalRegisterResult(string uploadId, DictationDeliveryRecord record)
        => record.State == DictationDeliveryState.Abandoned
            ? Results.Json(new { upload_id = uploadId, terminal = true, submitted = false, movedOn = false, dropped = true, reason = record.Reason ?? "", transcript = "" })
            : Results.Json(new { upload_id = uploadId, terminal = true, submitted = record.Submitted, movedOn = record.MovedOn, dropped = false, transcript = record.Transcript });

    private static async Task<DictationOutcome> RunCompleteCoreAsync(
        string uploadId, DictationCompleteRequest req, VoiceUploadStore uploads, DirectorRegistry registry,
        SessionOwnerCache? owners, GatewayTranscriptionService transcription,
        TranscribingSessions transcribingSessions, string? deliverySurface,
        Streaming.PushedSessionStore? pushedSessions, DirectorCommandRouter.SendDirectorCommandAsync? sendCommand,
        TimeSpan streamStale)
    {
        var sid = req.SessionId!;
        // Issue #1181, Task 4: this run assembles + transcribes + delivers, so mark the session ACTIVELY
        // transcribing for its duration. The aggregator reads this to show "Transcribing" (vs the durable
        // PENDING marker's "Uploading from phone"). Cleared in the finally so it never outlives the run.
        transcribingSessions.MarkActivelyTranscribing(sid);
        try
        {
            // The configured mode's key must be present before we pay the reassembly + transcribe cost.
            var routing = transcription.Resolve();
            if (routing.Key is null)
                return DictationOutcome.Error(StatusCodes.Status503ServiceUnavailable,
                    $"no key configured for transcription mode {routing.Mode}");

            var assembled = await uploads.AssembleAsync(uploadId, req.TotalChunks);
            if (assembled.Status == "unknown_upload")
                return DictationOutcome.Error(StatusCodes.Status404NotFound, "unknown upload id");
            if (assembled.Status == "incomplete")
                return DictationOutcome.Incomplete(assembled.Missing);
            var audio = assembled.Audio;
            if (audio is null || audio.Length == 0)
            {
                uploads.Delete(uploadId);
                return DictationOutcome.Error(StatusCodes.Status502BadGateway, "assembled recording was empty");
            }

            var result = await transcription.TranscribeAsync(
                audio, "audio." + (req.Ext ?? "wav"), req.Mime ?? "audio/wav", applyCorrection: true, CancellationToken.None);
            if (MapNonOkTranscription(result, uploadId, uploads) is { } nonOk)
                return nonOk;

            var transcript = (result.Text ?? "").Trim();
            // Compose the final message: any typed text the caret split the dictation around (before /
            // after), any earlier paused dictation segments already turned to text (prefix), and this
            // clip's transcript, space-joined skipping empties. The common voice case is transcript alone.
            var parts = new[] { req.Before, req.Prefix, transcript, req.After }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!.Trim());
            var message = string.Join(" ", parts).Trim();
            if (message.Length == 0)
            {
                // Silent/empty clip with no typed text: nothing to submit, but the turn is genuinely done -
                // record it as a durable DELIVERED tombstone so a re-complete returns the same no-op outcome
                // instead of re-running (issue #1183). Discards the retained chunks, keeps the marker.
                uploads.MarkDelivered(uploadId, submitted: false, movedOn: false, transcript);
                return DictationOutcome.Submitted(false, false, transcript);
            }

            // Gateway Cleanup mission, Phase 2: resolve the owner push-store-first (no HTTP fan-out) and gate
            // on an exited session, exactly as the old LocateAsync did, then reach it through the tunnel.
            var (director, session) = await GatewayEndpoints.LocateSessionAsync(
                registry, sid, pushedSessions, streamStale, TenantId.Local, owners);
            if (director is null || session is null)
                return DictationOutcome.Error(StatusCodes.Status404NotFound, "session not found");
            if (IsExited(session))
                return DictationOutcome.Error(StatusCodes.Status410Gone, "session has exited");
            var route = new SessionVerbClient(director, sendCommand);

            // Moved-on guard (issue #1006): for a RESUMED clip, if the session's terminal output grew
            // materially since the clip was recorded, other turns happened - drop the stale dictation
            // rather than inject it into a session that has moved on. Immediate sends skip this.
            //
            // The baseline is the LARGER of what the phone recorded and what a previous FAILED delivery
            // attempt of THIS upload id re-baselined to (Lost Dictations mission, issue #1593). The phone's
            // baseline is stamped once, when the clip was recorded, and never moves - so after one of our own
            // failed attempts typed the text and cleared it again, the phone's baseline describes a terminal
            // that no longer exists, and the retry gets dropped as "moved on" by OUR OWN noise. Taking the
            // larger of the two costs nothing when no attempt failed (the stored value is absent) and is what
            // stops the observed drop when one did.
            var effectiveBaseline = Math.Max(
                req.BaselineBufferBytes, uploads.ReadRecord(uploadId)?.RebaselineBufferBytes ?? 0);
            if (req.Resumed && session is not null && req.BaselineBufferBytes > 0 &&
                session.TotalBufferBytes > effectiveBaseline + MovedOnBufferGrowthBytes)
            {
                // The turn is resolved (deliberately dropped as stale): a durable DELIVERED tombstone with
                // movedOn set, so a re-complete returns the same moved-on outcome and never injects the stale
                // clip (issue #1183). Discards the retained chunks, keeps the marker.
                uploads.MarkDelivered(uploadId, submitted: false, movedOn: true, transcript);
                FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: session moved on " +
                    $"(buffer {req.BaselineBufferBytes}/effective {effectiveBaseline}->{session.TotalBufferBytes}); dropped");
                return DictationOutcome.Submitted(false, true, transcript);
            }

            // The Gateway injects the dictation by calling the owning Director's control API DIRECTLY, which
            // BYPASSES the Gateway's own /sessions/{sid}/prompt front door (issue #1188). That front door
            // blocks OTHER surfaces from typing into the PENDING session. The Director now ALSO enforces the
            // lock on its own control API (issue #1181, Task 3b), so this delivery is no longer implicitly
            // exempt there: it names its upload id via the X-Dictation-Delivery header (deliveryUploadId), and
            // the Director exempts exactly this send - the dictation's own arrival, which is what the lock is
            // held for. The header rides the fleet-authenticated call, so it cannot be forged from outside.
            // DevThrottle Stats: Surface tags which surface recorded this voice turn (phone / cockpit);
            // deliveryUploadId marks it a voice Delivery at the Director. Together the Director counts it as
            // one voice turn from the resolved surface. A dictation is always a real operator turn, so when
            // the device key did not resolve we stamp "unknown" (never null) - it is counted into the honest
            // "unknown" surface bucket, never silently dropped (decision 9).
            var (ok, _, err) = await route.PostPromptAsync(sid, new PromptRequest { Text = message, AppendEnter = true, Surface = deliverySurface ?? "unknown", DeliveryUploadId = uploadId });
            if (!ok)
            {
                // THE ATTEMPT WE JUST MADE INVALIDATED THE PHONE'S BASELINE (Lost Dictations mission, #1593).
                // A failed submit is not a no-op on the terminal: the observed failure (05:31 on 2026-07-15)
                // typed the text twice and cleared it twice, writing ~8,700 bytes of our own noise into the
                // buffer. The phone's baseline was stamped when the clip was RECORDED and never moves, so the
                // retry that follows this 502 would be judged against a number our own failure just made a
                // lie, and the moved-on guard would drop the user's words as stale. Re-baseline the upload id
                // to the freshest buffer position we can see, so the retry is judged honestly.
                //
                // Re-READ the session rather than reusing the `session` snapshot from before the attempt:
                // that snapshot predates the noise by definition and would re-baseline to the same number the
                // phone already sent, which is no re-baseline at all.
                //
                // NOT PROVEN TO CLOSE THE WINDOW COMPLETELY, and deliberately not dressed up as if it does:
                // this reads the pushed store, so it sees only what the owning Director has pushed BY NOW. If
                // the push stream lags the failure, some of the attempt's noise is not in this number yet and
                // a retry could still be judged against a baseline that is too low. It is strictly better than
                // today (today re-baselines by exactly zero) and it is monotonic, so a later failed attempt
                // can only improve it - but the residual lag window is real and is not closed here.
                var (_, freshSession) = await GatewayEndpoints.LocateSessionAsync(
                    registry, sid, pushedSessions, streamStale, TenantId.Local, owners);
                if (freshSession is not null)
                    uploads.RecordFailedDeliveryBaseline(uploadId, freshSession.TotalBufferBytes);
                FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: submit FAILED ({err}); " +
                    $"re-baselined to {freshSession?.TotalBufferBytes.ToString() ?? "unavailable"} " +
                    $"(was {req.BaselineBufferBytes}) so the retry is not dropped as stale");
                return DictationOutcome.Error(StatusCodes.Status502BadGateway, err ?? "submit to session failed");
            }

            // Write the durable DELIVERED tombstone as the IMMEDIATE next step after the session accepted the
            // prompt, before anything else, to minimize the window in which a re-complete could re-inject
            // (issue #1183). Known, deliberately unfixed residual limitation: a Gateway crash in the few
            // milliseconds between PostPromptAsync returning and this marker landing on disk would let a
            // later re-complete inject the turn a second time. We minimize and document it rather than paper
            // over it with a fallback. MarkDelivered discards the retained chunks and keeps the marker.
            uploads.MarkDelivered(uploadId, submitted: true, movedOn: false, transcript);
            FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: submitted chars={message.Length}");
            return DictationOutcome.Submitted(true, false, transcript);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId} FAILED: {ex.Message}");
            return DictationOutcome.Error(StatusCodes.Status502BadGateway, ex.Message);
        }
        finally
        {
            // Issue #1181, Task 4: the transcription run is over (delivered, failed, or threw), so drop the
            // "Transcribing" mark. The durable PENDING/DELIVERED marker now owns the session's state.
            transcribingSessions.ClearActivelyTranscribing(sid);
        }
    }

    private static void EndTranscribing(TranscribingSessions t, string sid)
    {
        try { t.End(sid); } catch { /* the Gateway's stale-mark backstop clears it if this throws */ }
    }

    private static bool IsExited(SessionDto session)
        => string.Equals(session.Status, "Exited", StringComparison.OrdinalIgnoreCase)
        || string.Equals(session.Status, "Failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(session.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Register-time body: the session and the client's record-time terminal-byte baseline.</summary>
public sealed class DictationUploadRequest
{
    public string? SessionId { get; set; }
    public long BaselineBufferBytes { get; set; }
}

/// <summary>Complete-time body.</summary>
public sealed class DictationCompleteRequest
{
    public string? SessionId { get; set; }
    public int TotalChunks { get; set; }
    public string? Mime { get; set; }
    public string? Ext { get; set; }
    /// <summary>Typed text before the caret (prepended to the transcript). Empty for the voice case.</summary>
    public string? Before { get; set; }
    /// <summary>Typed text after the caret (appended to the transcript). Empty for the voice case.</summary>
    public string? After { get; set; }
    /// <summary>Earlier paused dictation segments already turned to text, joined ahead of this clip.</summary>
    public string? Prefix { get; set; }
    /// <summary>The session's TotalBufferBytes when the clip was recorded (for the moved-on guard).</summary>
    public long BaselineBufferBytes { get; set; }
    /// <summary>True when this complete is a resume after a reload/relaunch (applies the moved-on guard).</summary>
    public bool Resumed { get; set; }
}

/// <summary>Terminal or retryable outcome of a dictation complete, mapped to an HTTP result.</summary>
internal sealed class DictationOutcome
{
    private enum Kind { Submitted, Error, Incomplete, OutOfCredits, Dropped, Permanent }
    private readonly Kind _kind;
    private readonly bool _submitted;
    private readonly bool _movedOn;
    private readonly string _transcript;
    private readonly int _status;
    private readonly string? _error;
    private readonly HostedAiState _creditsState;
    private readonly IReadOnlyList<int> _missing;

    private DictationOutcome(Kind kind, bool submitted = false, bool movedOn = false, string transcript = "",
        int status = 0, string? error = null, HostedAiState creditsState = default, IReadOnlyList<int>? missing = null)
    {
        _kind = kind;
        _submitted = submitted;
        _movedOn = movedOn;
        _transcript = transcript;
        _status = status;
        _error = error;
        _creditsState = creditsState;
        _missing = missing ?? Array.Empty<int>();
    }

    /// <summary>Terminal: the server resolved the clip (submitted, moved-on, empty, or an abandoned
    /// upload id that was dropped). Do not retry.</summary>
    public bool Terminal => _kind == Kind.Submitted || _kind == Kind.Dropped;

    /// <summary>The upload is missing chunks and the client will complete again on the same upload id, so
    /// the session is still genuinely transcribing - the ONE outcome that must NOT clear the orange mark
    /// (issue #1048).</summary>
    public bool IsIncomplete => _kind == Kind.Incomplete;

    public static DictationOutcome Submitted(bool submitted, bool movedOn, string transcript)
        => new(Kind.Submitted, submitted: submitted, movedOn: movedOn, transcript: transcript);
    public static DictationOutcome Error(int status, string error) => new(Kind.Error, status: status, error: error);
    public static DictationOutcome Incomplete(IReadOnlyList<int> missing) => new(Kind.Incomplete, missing: missing);
    public static DictationOutcome OutOfCredits(HostedAiState state) => new(Kind.OutOfCredits, creditsState: state);
    /// <summary>An ABANDONED upload id: the dictation was given up, so a re-complete returns a clear dropped
    /// outcome and never injects (issue #1183). Terminal - the client drops its copy and does not re-drive.</summary>
    public static DictationOutcome Dropped(string reason) => new(Kind.Dropped, error: reason);
    /// <summary>A PERMANENT transcription failure (issue #1185): this attempt is over (clears the orange
    /// mark, HTTP 422 { permanent, reason }), but the record is parked FAILED and is user-retryable - so it
    /// is NOT Terminal (a retry re-runs, it is not cached in _completes) and NOT Incomplete.</summary>
    public static DictationOutcome Permanent(string reason) => new(Kind.Permanent, error: reason);

    public IResult ToResult() => _kind switch
    {
        Kind.Submitted => Results.Json(new { submitted = _submitted, movedOn = _movedOn, transcript = _transcript }),
        Kind.Dropped => Results.Json(new { submitted = false, movedOn = false, dropped = true, reason = _error ?? "" }),
        Kind.Permanent => Results.Json(new { permanent = true, reason = _error ?? "" }, statusCode: StatusCodes.Status422UnprocessableEntity),
        Kind.Incomplete => Results.Json(new { status = "incomplete", missing = _missing }, statusCode: StatusCodes.Status409Conflict),
        Kind.OutOfCredits => HostedAiHttp.PaymentRequiredResult(_creditsState),
        _ => Results.Json(new { error = _error }, statusCode: _status),
    };
}
