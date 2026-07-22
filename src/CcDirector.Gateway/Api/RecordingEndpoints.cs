using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Network;
using CcDirector.Core.Recording;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Maps the <c>/ingest</c> REST surface the phone recorder uploads to. The phone records offline; when it
/// has connectivity it registers a recording, PUTs each finalized audio segment (idempotent by index +
/// hash), then POSTs <c>complete</c>, which queues the recording and returns 202 immediately. A background
/// worker transcribes every segment through the existing dictation pipeline and assembles + cleans the
/// transcript into the local transcripts area, retrying flaky segments and re-queueing a failed job for a
/// later attempt. The phone polls <c>status</c> to watch progress. Transcripts stay local (transient) until
/// the user promotes one into the vault. A shared dictation glossary lives under the same prefix.
///
/// Routes (all JSON except the raw-bytes chunk PUT), all under <c>/ingest</c>:
///   POST   /ingest/recording                      register, body RecordingRegisterRequest
///   PUT    /ingest/recording/{id}/chunk/{index}   raw audio bytes, header X-Chunk-Sha256
///   POST   /ingest/recording/{id}/complete        body RecordingManifest
///   GET    /ingest/recording/{id}/status          RecordingStatusDto
///   GET    /ingest/recordings                     list EVERY recording on this gateway
///   GET    /ingest/recording/{id}/transcript      the cleaned transcript as plain text
///   GET    /ingest/recording/{id}/audio/{index}   one raw audio segment
///   POST   /ingest/recording/{id}/promote         copy transcript + audio into the vault
///   PATCH  /ingest/recording/{id}/meta            set human-readable title/subtitle/summary
///   DELETE /ingest/recording/{id}                 delete the transient local transcript
///   GET    /ingest/dictionary                     the shared dictation glossary
///   PUT    /ingest/dictionary                     replace the shared dictation glossary
///   POST   /ingest/dictionary/terms               add terms to the shared dictation glossary
///   GET    /ingest/agent-info                     copy-paste API guide for an external agent
///
/// Auth is the Gateway's existing token middleware (applied host-wide when enabled), so these routes
/// inherit it without extra checks here.
///
/// DENIED IN WHOLE ON HOSTED. Every route under <c>/ingest</c> is refused on the hosted Gateway, because
/// nothing in this surface carries a tenant. The durable directory for a recording is built from the
/// CALLER-SUPPLIED recording id alone, so on shared hosted infrastructure any authenticated device can name
/// another account's recording and be served it: list every record on the box, read its raw audio and its
/// raw and cleaned transcript, overwrite its title, subtitle, summary and chunks, promote it into the vault,
/// or delete it outright. The id sanitiser only replaces invalid characters, so two distinct caller ids can
/// alias onto the same directory as well. The dictionary routes have the same shape: one shared glossary
/// file, no tenant, read and written by anyone. It is a deny of the WHOLE GROUP, not a guard on the worst
/// route, because the read, the write and the destruction are all equally wrong here, and because a
/// route-by-route fix rots: the next ingest route added would be open again by default.
///
/// It is a DENY rather than a per-tenant partition because partitioning this store is real work - the
/// on-disk layout, the promote target and the shared glossary all have to grow an owner - and a
/// half-partition is worse than an honest refusal. (Contrast the cached wingman voice READ surface, which
/// WAS partitioned per tenant in #1973 and is therefore served, not denied - the two surfaces looked alike
/// but had different work done to them.)
///
/// HOW THE DENY IS EXPRESSED - THE SHARED REFUSAL PRIMITIVE, NOT A BESPOKE CHECK. This group is denied
/// through <see cref="HostedRouteDeny.ExclusiveGroup"/>, the ONE hosted-refusal boundary every deny family
/// on this Gateway adopts (primitive at <c>src/CcDirector.Gateway/Tenancy/HostedRouteDeny.cs</c>; the
/// key-vault group in <see cref="VaultEndpoints"/> is the reference adoption). On hosted the handlers are
/// NEVER MAPPED. In their place the exclusive prefix maps ONE verb-less catch-all refusal over everything
/// under <c>/ingest</c> plus a root refusal at the prefix itself. There is no binding step to get ahead of,
/// no body parameter, no media-type constraint and no method constraint, so EVERY request shape - a valid
/// body, a malformed body, a wrong media type, a verb the group never mapped, and a route added LATER -
/// meets the refusal. The exclusivity claim is CHECKED at startup by
/// <see cref="HostedRefusalRouteSpace.ValidateBeforeStart"/>: the Gateway refuses to start if any live route
/// serves beneath <c>/ingest</c>. Nothing else on this Gateway serves under <c>/ingest</c>, so the whole
/// prefix is this group's to claim.
///
/// NO OFF-ROUTE WRITER TO HOST-GATE. Unlike the key-vault group (which had a startup seed, a provisioner and
/// a revocation firing off-route), NOTHING writes this surface's state except the routes themselves.
/// <see cref="RecordingIngestService"/> is constructed in exactly one place - <see cref="BuildService"/>,
/// reached only through the per-request Lazy below - and on hosted the catch-all answers before any handler
/// runs, so the Lazy is never forced, the service is never built and its transcription worker never starts.
/// The shared glossary is written only by the two denied dictionary routes. (DictionaryResolver's cache
/// write is a Director-side consumer of a Gateway glossary, not a Gateway writer of this store.) So there is
/// no defence-in-depth writer to gate; the deny is the whole mechanism.
///
/// UN-DENY DEBT: a deny closes the ROUTE door only. Before it is lifted: (1) give recordings and the shared
/// glossary a per-tenant layout (keyed by tenant, not by caller-supplied id alone, which also closes the
/// id-aliasing hazard), (2) quarantine, purge or migrate the PRE-DENY root rather than adopting un-owned
/// material into the new layout, and ONLY THEN (3) lift this refusal. This is recorded on the payload's
/// unDenyInstruction so it travels with the deny.
///
/// Self-host is COMPLETELY unchanged - the owner records, transcribes, edits and deletes exactly as before -
/// and that is the control. Off hosted the primitive maps the real handlers on the group exactly as an
/// unguarded builder would and creates no refusal at all.
/// </summary>
internal static class RecordingEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The exclusive prefix the recording-ingest group owns outright on hosted.</summary>
    internal const string Prefix = "/ingest";

    /// <summary>The single error string the hosted refusal serves. Held here so a test can assert against the
    /// exact string that is served rather than a copy that could drift.</summary>
    internal const string RefusalMessage = "recordings are not available on the hosted gateway";

    /// <summary>
    /// The hosted refusal payload for the whole recording-ingest group. Validated on construction, so a blank
    /// field fails the Gateway at startup rather than serving a refusal a caller cannot act on. 404 rather
    /// than 403: on hosted this surface does not exist as a concept, so "not here" is the truthful answer;
    /// 403 would imply some credential could reach it, and none can.
    /// </summary>
    private static HostedDenial Denial() => new(
        family: "recording-ingest",
        message: RefusalMessage,
        reason: "recordings and the shared dictation glossary carry no tenant - the recording directory is keyed on " +
                "a caller-supplied id alone and the glossary is one global file - and the host-wide auth gate admits " +
                "any enrolled device key from any account, so one subscriber could list, read, overwrite, promote or " +
                "delete every other subscriber's recorded conversations",
        unDenyInstruction: "do NOT simply remove this deny: give recordings and the shared glossary a per-tenant " +
                "layout (keyed by tenant, not by caller-supplied id alone), THEN quarantine, purge or migrate the " +
                "pre-existing global recording root (material predating this deny is already on disk and this change " +
                "never touched it), and only then restore tenant-scoped routes",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Maps the recording-ingest routes and RETURNS the denied group they were mapped through.
    ///
    /// The routes are mapped through the group HANDLE (<see cref="HostedDenyGroup"/>), never through the
    /// ungrouped builder: the handle is obtainable only from <see cref="HostedRouteDeny.ExclusiveGroup"/>, so
    /// a route mapped around the refusal is not expressible in <see cref="MapRoutes"/> without changing its
    /// signature. On hosted the handle DISCARDS each handler (the exclusive catch-all already refuses the
    /// path); off hosted it maps each handler as an unguarded builder would.
    ///
    /// The return value exists so the future-route property is statable from outside this file: a test can map
    /// a brand-new route through the returned handle and show the refusal already covers routes nobody has
    /// written yet - the one property that distinguishes an exclusive-prefix deny from a guard repeated in
    /// each handler.
    /// </summary>
    public static HostedDenyGroup Map(
        IEndpointRouteBuilder outer,
        KeyVault? keyVault = null,
        TranscriptionTelemetryLog? telemetry = null,
        TranscriptionAudioArchive? audioArchive = null)
    {
        FileLog.Write($"[RecordingEndpoints] mapping {Prefix} recording + dictionary routes; hosted={GatewayHostedMode.IsHosted} - on hosted the whole group is refused via the shared refusal primitive");

        var group = HostedRouteDeny.ExclusiveGroup(outer, Prefix, Denial());
        MapRoutes(group, keyVault, telemetry, audioArchive);
        return group;
    }

    /// <summary>
    /// Every /ingest route, mapped relative to the <see cref="Prefix"/> so the full paths are
    /// <c>/ingest/recording</c>, <c>/ingest/dictionary</c> and so on exactly as before. Takes the denied
    /// GROUP HANDLE and nothing else: the ungrouped route builder is deliberately out of scope here so no
    /// route can be mapped around the hosted refusal.
    /// </summary>
    private static void MapRoutes(
        HostedDenyGroup app,
        KeyVault? keyVault,
        TranscriptionTelemetryLog? telemetry,
        TranscriptionAudioArchive? audioArchive)
    {
        // Lazily built on FIRST USE, not at host startup: constructing the service resolves
        // the OpenAI API key (the transcriber needs it), and the Gateway must boot on machines
        // without that key. A missing key then fails the individual recording request loudly
        // (500 with an explicit hosted-AI setup message) instead of preventing
        // the entire Gateway host from starting. On hosted the group discards every handler, so this
        // Lazy is never forced and the service is never built.
        // In production the host owns the key vault + telemetry + audio archive and passes them, so the
        // recording transcriber shares the host's single instances rather than newing its own copies.
        var lazyService = new Lazy<RecordingIngestService>(() => BuildService(keyVault, telemetry, audioArchive));

        app.MapPost("/recording", async (HttpContext ctx) =>
        {
            try
            {
                var req = await JsonSerializer.DeserializeAsync<RecordingRegisterRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (req is null || string.IsNullOrWhiteSpace(req.RecordingId))
                    return Results.BadRequest(new { error = "RecordingId is required" });
                var status = lazyService.Value.Register(req);
                return Results.Json(status);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] register bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        app.MapPut("/recording/{id}/chunk/{index:int}", async (string id, int index, HttpContext ctx) =>
        {
            try
            {
                var sha = ctx.Request.Headers["X-Chunk-Sha256"].ToString();
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
                var bytes = ms.ToArray();
                if (bytes.Length == 0)
                    return Results.BadRequest(new { error = "empty chunk body" });

                await lazyService.Value.StoreChunkAsync(id, index, bytes, sha, ctx.RequestAborted);
                return Results.Json(new { ok = true, index, bytes = bytes.Length });
            }
            catch (InvalidOperationException ex)
            {
                FileLog.Write($"[RecordingEndpoints] chunk store failed: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapPost("/recording/{id}/complete", async (string id, HttpContext ctx) =>
        {
            try
            {
                var manifest = await JsonSerializer.DeserializeAsync<RecordingManifest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (manifest is null)
                    return Results.BadRequest(new { error = "manifest body required" });
                // The audio completeness gate (issue #586) runs inside CompleteAsync.
                // Three outcomes:
                //   - "incomplete": a segment is missing or its SHA256/byte count
                //     does not match the manifest. NOTHING is transcribed; the
                //     response names MissingOrBadIndices so the phone re-sends
                //     exactly those, then calls complete again. Returned as 409
                //     Conflict (the upload conflicts with the declared manifest)
                //     so the phone treats it as "resend", never "done".
                //   - all-pass: enqueue and return 202. Transcription runs in the
                //     background worker, so the phone never holds the request open
                //     for the length of a transcription - a long recording can no
                //     longer be killed by a request/proxy timeout. The phone polls
                //     GET .../status to watch progress.
                //   - empty capture (zero segments): CompleteAsync throws; surfaced
                //     below as an explicit error, never a silent empty transcript.
                var status = await lazyService.Value.CompleteAsync(id, manifest, ctx.RequestAborted);
                if (status.State == "incomplete")
                    return Results.Json(status, statusCode: StatusCodes.Status409Conflict);
                return Results.Json(status, statusCode: StatusCodes.Status202Accepted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] complete bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            catch (InvalidOperationException ex)
            {
                // An empty capture (zero segments) fails loud here rather than
                // ever producing an empty transcript (issue #586). Surface a clean,
                // named error to the phone.
                FileLog.Write($"[RecordingEndpoints] complete refused: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (Exception ex)
            {
                // The recording's status.json is already marked "error" by the
                // service; surface a clean message to the phone for retry.
                FileLog.Write($"[RecordingEndpoints] complete failed: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/recording/{id}/status", (string id) =>
        {
            try
            {
                return Results.Json(lazyService.Value.GetStatus(id));
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound(new { error = "unknown recording" });
            }
        });

        // The Transcripts and Dictionary PAGES are served by the Cockpit now (one-URL plan);
        // /voice, /transcripts, /dictionary fall through the proxy to it. Only the data API
        // below stays here.

        // ===== Dictionary data API ==========================================
        // The glossary is a single shared YAML file used by both phone-recording
        // transcription and desktop dictation. The page sends the whole document
        // on save (no partial merge) so the file stays the single source of truth.

        app.MapGet("/dictionary", () =>
        {
            var dict = DictionaryLoader.LoadFromDisk(DictionaryFilePath());
            return Results.Json(ToDto(dict));
        });

        app.MapPut("/dictionary", async (HttpContext ctx) =>
        {
            try
            {
                var dto = await JsonSerializer.DeserializeAsync<DictionaryDto>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (dto is null)
                    return Results.BadRequest(new { error = "dictionary body required" });

                DictionaryLoader.WriteToDisk(DictionaryFilePath(), FromDto(dto));
                var reread = DictionaryLoader.LoadFromDisk(DictionaryFilePath());
                return Results.Json(ToDto(reread));
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] dictionary bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // Additive convenience endpoint so an agent in a session can add a term
        // (and optional mistranscription spellings) without round-tripping the
        // whole document. Existing entries are preserved; duplicates are ignored.
        app.MapPost("/dictionary/terms", async (HttpContext ctx) =>
        {
            try
            {
                var add = await JsonSerializer.DeserializeAsync<DictionaryAddRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                var hasTerms = add?.Terms is { Count: > 0 };
                var hasPatterns = add?.Mistranscriptions is { Count: > 0 };
                if (add is null || (!hasTerms && !hasPatterns))
                    return Results.BadRequest(new { error = "provide 'terms' and/or 'mistranscriptions'" });

                var path = DictionaryFilePath();
                var current = ToDto(DictionaryLoader.LoadFromDisk(path));

                foreach (var term in add.Terms ?? new())
                {
                    var t = term?.Trim();
                    if (!string.IsNullOrWhiteSpace(t) && !current.Vocabulary.Contains(t))
                        current.Vocabulary.Add(t);
                }

                foreach (var kv in add.Mistranscriptions ?? new())
                {
                    var term = kv.Key?.Trim();
                    if (string.IsNullOrWhiteSpace(term) || kv.Value is null)
                        continue;
                    if (!current.CommonMistranscriptions.TryGetValue(term, out var variants))
                    {
                        variants = new List<string>();
                        current.CommonMistranscriptions[term] = variants;
                    }
                    foreach (var v in kv.Value)
                    {
                        var vv = v?.Trim();
                        if (!string.IsNullOrWhiteSpace(vv) && !variants.Contains(vv))
                            variants.Add(vv);
                    }
                }

                DictionaryLoader.WriteToDisk(path, FromDto(current));
                return Results.Json(ToDto(DictionaryLoader.LoadFromDisk(path)));
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] dictionary add bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        app.MapGet("/recordings", () => Results.Json(lazyService.Value.ListAll()));

        app.MapGet("/recording/{id}/transcript", (string id) =>
        {
            var text = lazyService.Value.GetTranscript(id);
            return text is null
                ? Results.NotFound(new { error = "no transcript" })
                : Results.Text(text, "text/plain; charset=utf-8");
        });

        app.MapGet("/recording/{id}/audio/{index:int}", (string id, int index) =>
        {
            var audio = lazyService.Value.GetAudioFile(id, index);
            return audio is null
                ? Results.NotFound(new { error = "no such segment" })
                : Results.File(audio.Value.path, audio.Value.contentType, enableRangeProcessing: true);
        });

        app.MapDelete("/recording/{id}", (string id) =>
        {
            try
            {
                lazyService.Value.DeleteRecording(id);
                return Results.Json(new { ok = true, id });
            }
            catch (InvalidOperationException ex)
            {
                FileLog.Write($"[RecordingEndpoints] delete not found: {ex.Message}");
                return Results.NotFound(new { error = ex.Message });
            }
        });

        app.MapPost("/recording/{id}/promote", async (string id) =>
        {
            try
            {
                var status = await lazyService.Value.PromoteToVaultAsync(id);
                return Results.Json(status);
            }
            catch (InvalidOperationException ex)
            {
                FileLog.Write($"[RecordingEndpoints] promote rejected: {ex.Message}");
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[RecordingEndpoints] promote failed: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // Update human-readable metadata (title, subtitle, summary). Accepts both
        // PATCH (partial update) and POST so simple clients can use either.
        app.MapMethods("/recording/{id}/meta", new[] { "PATCH", "POST" }, async (string id, HttpContext ctx) =>
        {
            try
            {
                var update = await JsonSerializer.DeserializeAsync<RecordingMetaUpdate>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (update is null)
                    return Results.BadRequest(new { error = "meta body required" });
                var item = lazyService.Value.UpdateMeta(id, update);
                return Results.Json(item);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RecordingEndpoints] meta bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            catch (InvalidOperationException ex)
            {
                FileLog.Write($"[RecordingEndpoints] meta not found: {ex.Message}");
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // A copy-paste guide for an external agent/LLM. The base URL is the
        // tailnet front door (resolved via Tailscale, independent of how this
        // page was reached) so an agent on any tailnet machine gets a URL that
        // actually works. Access is API-only; the guide does not expose the disk.
        app.MapGet("/agent-info", () =>
        {
            var baseUrl = TailscaleIdentity.TryGetFrontDoorBaseUrl();
            var guide = BuildAgentInfo(baseUrl);
            return Results.Text(guide, "text/plain; charset=utf-8");
        });
    }

    private static string BuildAgentInfo(string? baseUrl)
    {
        // When Tailscale is not available there is no remotely reachable URL.
        // Say so truthfully rather than emitting a localhost URL that only works
        // on this one machine.
        var url = baseUrl ?? "(unavailable - Tailscale was not detected on this machine, so the API has no remote URL)";
        return $$"""
        # DevThrottle Transcripts - Agent API

        Base URL: {{url}}

        This API is reachable from any machine on the tailnet over HTTPS. Do
        everything through the REST API below. Do NOT read or write transcript
        files on disk - the files live on one machine and are not portable; the
        API is the only supported access path.

        ## REST API

        GET    {base}/ingest/recordings
               List all transcripts (JSON). Fields: recordingId, title, subtitle,
               summary, startedAt, state, segments, durationMs, hasTranscript,
               inVault.

        GET    {base}/ingest/recording/{id}/transcript
               The cleaned transcript as plain text.

        GET    {base}/ingest/recording/{id}/audio/{index}
               One audio segment (index starts at 0).

        PATCH  {base}/ingest/recording/{id}/meta
               Set human-readable metadata. JSON body, any subset of:
                 { "title": "...", "subtitle": "...", "summary": "..." }
               A null/omitted field is left unchanged. Returns the updated record.

        POST   {base}/ingest/recording/{id}/promote
               Copy this transcript + audio into the vault (permanent).

        DELETE {base}/ingest/recording/{id}
               Delete the transient local transcript. A promoted vault copy is kept.

        ## Dictionary (speech-to-text glossary)

        The shared glossary that biases transcription toward the user's terms.
        Editing it affects both phone-recording transcription and desktop
        dictation. Changes apply on the next recording (no restart).

        GET    {base}/ingest/dictionary
               The glossary as JSON:
                 { "vocabulary": ["acmeflow", ...],
                   "commonMistranscriptions": { "ConPTY": ["Conty", ...] },
                   "profiles": { "default": { "cleanupEnabled": true, "stylePrompt": null } } }

        POST   {base}/ingest/dictionary/terms
               Add term(s) and/or mistranscription spellings. Additive: existing
               entries are kept and duplicates ignored. JSON body, either field
               optional:
                 { "terms": ["NewTerm"],
                   "mistranscriptions": { "ConPTY": ["Conty"] } }
               Returns the updated dictionary. Use this for "add this word".

        PUT    {base}/ingest/dictionary
               Replace the ENTIRE glossary. Body is the shape GET returns. Use
               only when rewriting the whole thing; prefer POST .../terms to add.

        (Replace {base} with the Base URL above.)

        ## Typical workflow for an agent

        1. GET /ingest/recordings and find transcripts with an empty or auto
           generated title and no summary.
        2. GET .../transcript to read the text.
        3. PATCH .../meta with a clear title, a short subtitle, and a summary.
        """;
    }

    private static RecordingIngestService BuildService(
        KeyVault? keyVault,
        TranscriptionTelemetryLog? telemetry,
        TranscriptionAudioArchive? audioArchive)
    {
        // Local transient store for transcripts (audio + markdown). Transcripts
        // are NOT auto-filed into the vault; the user promotes the keepers.
        var root = CcStorage.Transcripts();
        // Promotion target: the vault transcripts collection (permanent copy). Via CcStorage.Vault()
        // so it honors CC_VAULT_PATH - the old hardcoded path would have promoted into the wrong
        // place for anyone who relocated their vault.
        var collectionDir = CcStorage.VaultTranscripts();

        FileLog.Write($"[RecordingEndpoints] BuildService: root={root}, collection={collectionDir}");

        var filer = new CcVaultFiler(collectionDir);

        // The transcriber is built LAZILY, only when the background worker actually transcribes -
        // never during register/chunk/complete. This is what guarantees audio + notes always
        // land on the server regardless of transcription: a missing key no longer fails ingest, it
        // just fails (and reschedules) the downstream transcription job.
        //
        // It routes through the ONE Gateway transcription owner (issue #839): the single
        // GatewayTranscriptionService resolves the configured mode and the key (from the Gateway
        // vault) and picks the provider - in-process Whisper for on-device mode, or the resolved
        // provider-compatible batch endpoint for hosted mode. So switching the mode in the Cockpit
        // changes how the recording is transcribed with no Gateway restart, and on-device mode now
        // works for recordings too - the same single audio-to-text path every other batch caller uses.
        return new RecordingIngestService(
            root,
            transcriberFactory: () => new GatewayServiceRecordingTranscriber(
                new GatewayTranscriptionService(keyVault ?? new KeyVault(), telemetry: telemetry, audioArchive: audioArchive)),
            filer,
            collectionDir);
    }

    /// <summary>
    /// The single shared dictation glossary file. Used by both the recording transcriber and the
    /// dictionary editor endpoints. Defined once, in
    /// <see cref="GatewayTranscriptionService.DictionaryPath"/>.
    /// </summary>
    private static string DictionaryFilePath() => GatewayTranscriptionService.DictionaryPath();

    private static DictionaryDto ToDto(DictationDictionary dict) => new(
        Vocabulary: dict.Vocabulary.ToList(),
        CommonMistranscriptions: dict.CommonMistranscriptions
            .ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
        Profiles: dict.Profiles.ToDictionary(
            kv => kv.Key,
            kv => new DictionaryProfileDto(kv.Value.CleanupEnabled)));

    private static DictationDictionary FromDto(DictionaryDto dto)
    {
        var vocab = (dto.Vocabulary ?? new List<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();

        var patterns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var kv in dto.CommonMistranscriptions ?? new())
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null)
                continue;
            var variants = kv.Value
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToList();
            if (variants.Count > 0)
                patterns[kv.Key.Trim()] = variants;
        }

        var profiles = new Dictionary<string, DictationProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in dto.Profiles ?? new())
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null)
                continue;
            var name = kv.Key.Trim();
            profiles[name] = new DictationProfile(
                Name: name,
                CleanupEnabled: kv.Value.CleanupEnabled);
        }

        return new DictationDictionary(vocab, patterns, profiles);
    }
}

/// <summary>JSON shape for the dictionary editor (GET/PUT /ingest/dictionary).</summary>
internal sealed record DictionaryDto(
    List<string> Vocabulary,
    Dictionary<string, List<string>> CommonMistranscriptions,
    Dictionary<string, DictionaryProfileDto> Profiles);

internal sealed record DictionaryProfileDto(bool CleanupEnabled);

/// <summary>Additive request for POST /ingest/dictionary/terms.</summary>
internal sealed record DictionaryAddRequest(
    List<string>? Terms,
    Dictionary<string, List<string>>? Mistranscriptions);
