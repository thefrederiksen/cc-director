using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.HostedAi;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// Keeps a ready-to-play spoken summary for each "voice session" (issue #531 follow-up). Once the
/// phone uses voice on a session it becomes a voice session; from then on, every time a turn
/// finishes and the session is waiting for the user, the gateway automatically re-runs the wingman
/// translation and hosted text-to-speech and stores the result here - so the phone's session
/// list can show "voice ready" with a play button, play it without entering, and entering is
/// instant (the voice is already made). Since issue #549 the turn-end trigger is the always-running
/// TurnEndWatcher, which calls <see cref="GenerateAsync"/> directly for voice sessions on turn-end
/// (the retired turn-brief pipeline no longer mediates it).
/// </summary>
public sealed class WingmanVoiceService
{
    public sealed record VoiceReady(string Spoken, string Reply, byte[] Audio, DateTime AtUtc, string ContentType = "audio/mpeg");

    /// <summary>The outcome of one text-to-speech synthesis (issue #939): the audio bytes on success,
    /// or the shared <see cref="HostedAiState"/> when hosted AI is unavailable (out of credits, cap
    /// reached, or account setup is incomplete) so the caller can surface it instead of a silent
    /// null. Both null means a generic provider error (logged, no shared state).</summary>
    private sealed record TtsResult(byte[]? Audio, string? ContentType, HostedAiState? Unavailable);

    private readonly WingmanTranslator _translator;
    private readonly KeyVault _vault;
    private readonly WingmanTrainingStore _training;
    private readonly ConcurrentDictionary<string, byte> _voiceSessions = new();   // sid -> marker
    private readonly ConcurrentDictionary<string, VoiceReady> _ready = new();      // sid -> spoken+audio
    private readonly ConcurrentDictionary<string, byte> _generating = new();       // sid -> wingman is running now
    private readonly ConcurrentDictionary<string, HostedAiState> _voiceUnavailable = new();  // sid -> why voice is off (issue #939)
    private readonly ConcurrentDictionary<string, byte> _nothingToNarrate = new();  // sid -> the last turn has no text reply to read aloud (waiting on a prompt)
    private readonly string _persistPath;
    private readonly string _audioDir;
    private readonly HttpClient? _ttsHttp;   // test seam for TtsAsync (issue #939); the shared static when null
    private readonly Func<string, string?>? _sessionTitleResolver;   // sid -> session title, spoken first

    /// <summary>
    /// The one HTTP client for the narration speech leg, used whenever no test client is injected.
    ///
    /// Static and never disposed, matching the hosted-call pattern used across this codebase
    /// (AiModelsEndpoint, CarModeChat, HostedInferenceBrain, ...) and the /wingman/tts endpoint's own
    /// SharedTtsHttp. Safe to share because the credential goes on each REQUEST inside TtsSynthesis,
    /// never on this client's default headers.
    ///
    /// Timeout is INFINITE deliberately: TtsSynthesis owns the deadline (a 15-second per-attempt cap on
    /// a linked CancellationTokenSource, plus one retry). A second, slower client-level timeout racing
    /// it would only make a stall harder to read. One timeout, one owner.
    /// </summary>
    private static readonly HttpClient SharedTtsHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    // THERE IS DELIBERATELY NO FLEET-WIDE GATE OR CONCURRENCY CAP HERE (owner's call, 2026-07-17).
    //
    // Every voice session calls the hosted relay on its own and discovers an outage for itself. There
    // used to be two shared cooldown gates (a _rateGate on the model leg and a _ttsGate on the speech
    // leg): a single 429 or 5xx from one session's call armed a fleet-wide cooldown, and every OTHER
    // session was then SKIPPED for up to 120 seconds while one "probe" call tested recovery. That is the
    // coupling that turned a partial or flaky outage into total fleet silence - one call's bad luck
    // muted every session on the machine. It was removed on 2026-07-17.
    //
    // The principle: a service outage may be FLAKY (some calls fail, some succeed), so the honest thing
    // is to let every session try - the ones that can get through, do; the ones that fail, fail on their
    // own and retry on their own next cadence (turn-end, or the 45s idle sweep). We are a RELAY of the
    // hosted service and do not model its health on its behalf: no breaker, no shared cooldown, no
    // invented ceiling. This matches the "the hosted proxy is a thin pass-through" law. A 429 or a 5xx
    // is recorded as this ONE session's unavailable-state so its UI can say so, and nothing more - it
    // never reaches across to another session. Do NOT reintroduce a shared gate or a constant cap here.
    //
    // _inFlight stays: it is NOT a fleet gate. It coalesces a single session so two generations for the
    // SAME session never run at once (a slow turn overlapping the idle sweep would otherwise double the
    // spend). It never makes one session wait on another.
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();   // sid -> a generation is running now

    /// <summary>On-disk shape of one ready session's metadata (the audio bytes live next to it as
    /// an .mp3). Persisted so the play triangle / playability survives a gateway restart (issue #553).</summary>
    private sealed record PersistedVoice(string Spoken, string Reply, DateTime AtUtc, string? ContentType = null);

    /// <param name="ttsHttpClient">Optional HTTP client for the text-to-speech call (tests inject a stub
    /// over a fake handler, issue #939). A per-call 60-second client is created when null.</param>
    /// <param name="sessionTitleResolver">Resolves a session id to its title, which the wingman speaks
    /// first so a listener knows which session is talking. The host wires this to the pushed-session
    /// store; a null resolver (or one returning null for an unknown session) simply means no title is
    /// spoken, which is the correct degrade - a narration with no title is worth far more than none.</param>
    public WingmanVoiceService(Func<Core.Configuration.WingmanModelRole, CancellationToken, Task<IAgentBrain>> brainProvider, KeyVault vault, string? persistPath = null, WingmanTrainingStore? training = null, Func<string>? instructionsProvider = null, HttpClient? ttsHttpClient = null, Func<string, string?>? sessionTitleResolver = null)
    {
        _translator = new WingmanTranslator(brainProvider, instructionsProvider: instructionsProvider);
        _vault = vault;
        _sessionTitleResolver = sessionTitleResolver;
        // Post-cut: the owning Director is reached through the tunnel-only SessionVerbClient the callers pass
        // into GenerateAsync, so this service holds no Director client.
        _ttsHttp = ttsHttpClient;
        _training = training ?? new WingmanTrainingStore();
        // Which sessions are voice sessions survives a gateway restart. Issue #553: the per-session
        // audio cache is now ALSO durable - it is persisted next to voice-sessions.json under a
        // "voice-audio" folder so the triangle does not vanish-then-reappear-empty across a restart
        // and a tap after restart plays. Tests pass an isolated path so the two never collide.
        _persistPath = persistPath ?? Path.Combine(CcStorage.Root(), "voice-sessions.json");
        var baseDir = Path.GetDirectoryName(_persistPath);
        if (string.IsNullOrWhiteSpace(baseDir)) baseDir = CcStorage.Root();
        _audioDir = Path.Combine(baseDir, "voice-audio");
        LoadVoiceSessions();
        LoadReadyAudio();
    }

    private void LoadVoiceSessions()
    {
        try
        {
            if (!File.Exists(_persistPath)) return;
            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_persistPath));
            if (ids is not null) foreach (var id in ids) if (!string.IsNullOrWhiteSpace(id)) _voiceSessions[id] = 1;
            FileLog.Write($"[WingmanVoiceService] loaded {_voiceSessions.Count} voice session(s) from disk");
        }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] load voice sessions FAILED: {ex.Message}"); }
    }

    private void SaveVoiceSessions()
    {
        try { File.WriteAllText(_persistPath, JsonSerializer.Serialize(_voiceSessions.Keys.ToArray())); }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] save voice sessions FAILED: {ex.Message}"); }
    }

    /// <summary>Restore the per-session ready audio cache from disk on startup (issue #553) so
    /// HasVoice / ReadySessionIds survive a gateway restart. A session is only loaded ready when BOTH
    /// its metadata (.json) and its audio (.mp3, non-empty) are present - the "if anything fails,
    /// remove the triangle" rule extends to a half-written or missing cache.</summary>
    private void LoadReadyAudio()
    {
        try
        {
            if (!Directory.Exists(_audioDir)) return;
            var loaded = 0;
            foreach (var metaPath in Directory.EnumerateFiles(_audioDir, "*.json"))
            {
                var sid = Path.GetFileNameWithoutExtension(metaPath);
                var audioPath = Path.Combine(_audioDir, sid + ".mp3");
                if (!File.Exists(audioPath)) continue;
                var audio = File.ReadAllBytes(audioPath);
                if (audio.Length == 0) continue;
                var meta = JsonSerializer.Deserialize<PersistedVoice>(File.ReadAllText(metaPath));
                if (meta is null) continue;
                var contentType = NormalizeContentType(meta.ContentType) ?? DetectAudioContentType(audio);
                _ready[sid] = new VoiceReady(meta.Spoken, meta.Reply, audio, meta.AtUtc, contentType);
                loaded++;
            }
            FileLog.Write($"[WingmanVoiceService] loaded {loaded} ready voice audio cache(s) from disk");
        }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] load ready audio FAILED: {ex.Message}"); }
    }

    private void SaveReadyAudio(string sid, VoiceReady ready)
    {
        try
        {
            Directory.CreateDirectory(_audioDir);
            // Write the audio first, then the metadata, so a startup load (which requires BOTH the
            // .mp3 and the .json) never sees a session ready before its bytes are on disk.
            File.WriteAllBytes(Path.Combine(_audioDir, sid + ".mp3"), ready.Audio);
            File.WriteAllText(Path.Combine(_audioDir, sid + ".json"),
                JsonSerializer.Serialize(new PersistedVoice(ready.Spoken, ready.Reply, ready.AtUtc, ready.ContentType)));
        }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] save ready audio FAILED sid={sid}: {ex.Message}"); }
    }

    private void DeleteReadyAudio(string sid)
    {
        try
        {
            var meta = Path.Combine(_audioDir, sid + ".json");
            var audio = Path.Combine(_audioDir, sid + ".mp3");
            if (File.Exists(meta)) File.Delete(meta);
            if (File.Exists(audio)) File.Delete(audio);
        }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] delete ready audio FAILED sid={sid}: {ex.Message}"); }
    }

    /// <summary>This session has had voice used on it at least once.</summary>
    public bool IsVoiceSession(string sid) => _voiceSessions.ContainsKey(sid);

    /// <summary>Every session the gateway is keeping voice for (the persisted set).</summary>
    public IReadOnlyCollection<string> VoiceSessionIds() => _voiceSessions.Keys.ToArray();

    /// <summary>True when this session currently has a fresh, playable cached summary.</summary>
    public bool HasVoice(string sid) => _ready.ContainsKey(sid);

    /// <summary>
    /// Whether a turn-end should (re)generate the spoken narration for this session. True when there is
    /// something to say (<paramref name="currentReply"/> non-empty) and it is NOT already the exact
    /// reply we hold cached audio for. This replaces the old bare "does any narration exist" guard
    /// (issue #1322): comparing the reply TEXT means a genuinely new or changed reply always regenerates
    /// even when the Working transition that would have cleared the cache was never observed (a racy
    /// sampled edge, missed on multi-part turns), while a redundant re-hit of the SAME turn still stays
    /// quiet so a client already playing this turn's clip is never disturbed (no re-mint, no yellow flip).
    /// Reply text is compared trimmed + ordinal - the two sources (cache vs the live /turns widget) are
    /// the same JSONL text block, so an unchanged turn matches exactly.
    /// </summary>
    internal bool ShouldRegenerate(string sid, string? currentReply)
    {
        if (string.IsNullOrWhiteSpace(currentReply)) return false;   // nothing to narrate yet
        if (!_ready.TryGetValue(sid, out var cached)) return true;    // never narrated -> make it
        return !string.Equals(cached.Reply?.Trim(), currentReply.Trim(), StringComparison.Ordinal);
    }

    /// <summary>The sessions that currently have a ready, playable spoken summary.</summary>
    public IReadOnlyCollection<string> ReadySessionIds() => _ready.Keys.ToArray();

    /// <summary>
    /// True while the wingman is actively producing this session's spoken summary (issue #531
    /// voice mode). This is the window the session must show YELLOW - "kind of not ready yet" -
    /// before flipping back to red when it needs the user again. The gateway surfaces it through
    /// the "Briefing" yellow path in the /sessions aggregation (see GatewayEndpoints voiceGeneratingFor).
    /// </summary>
    public bool IsGenerating(string sid) => _generating.ContainsKey(sid);

    /// <summary>Mark the wingman as running for this session (turns the session yellow).</summary>
    public void BeginGenerating(string sid) => _generating[sid] = 1;

    /// <summary>The wingman finished running for this session (back to red / its raw color).</summary>
    public void EndGenerating(string sid) => _generating.TryRemove(sid, out _);

    /// <summary>
    /// Why the Gateway could not keep this session's voice, or null when voice is fine (issue #939).
    /// Set when a turn-end generation hit an out-of-credits / cap / no-key condition instead of being
    /// swallowed silently; cleared on the next successful generation and when voice is turned off. The
    /// <c>/sessions</c> aggregation stamps the shared message onto <c>SessionDto.VoiceUnavailable</c>
    /// from this so the owning UI shows the consistent state.
    /// </summary>
    public HostedAiState? VoiceUnavailableFor(string sid)
        => _voiceUnavailable.TryGetValue(sid, out var s) ? s : (HostedAiState?)null;

    /// <summary>
    /// True when this session's latest turn has NO assistant text reply to read aloud - it is waiting for
    /// the user on a prompt / menu, not a text answer. A NON-failure: there is genuinely nothing to
    /// narrate, distinct from "the audio has not been made yet". Recorded when a generation attempt (auto
    /// or on-demand) finds an empty last reply; cleared the moment a text reply exists, on a new turn, or
    /// when voice is turned off. The <c>/sessions</c> aggregation feeds this to <see cref="VoiceDisplayFold"/>
    /// so the screen shows an honest "nothing to read aloud" instead of a Generate button that cannot work.
    /// </summary>
    public bool NothingToNarrateFor(string sid) => _nothingToNarrate.ContainsKey(sid);

    /// <summary>Set or clear the "nothing to narrate" fact from a caller that has already read the turn
    /// (the on-demand explain path): true when the last reply is empty (waiting on a prompt), false the
    /// moment a text reply exists. The auto path sets/clears it inline in <c>GenerateOnceAsync</c>.</summary>
    public void SetNothingToNarrate(string sid, bool nothing)
    {
        if (nothing) _nothingToNarrate[sid] = 1;
        else _nothingToNarrate.TryRemove(sid, out _);
    }

    /// <summary>
    /// Record that a model/translation call did not answer in time (a bounded timeout or a transport
    /// failure), so this session shows the calm "voice on its way" retrying state rather than a silent
    /// failure or a false "this session's computer is offline". The on-demand explain path uses this
    /// when its translation times out; the auto path sets the same state inline. Cleared on the next
    /// successful generation, on the Working transition, and when voice is turned off - exactly like the
    /// speech-leg unavailable state.
    /// </summary>
    public void NoteRetrying(string sid) => _voiceUnavailable[sid] = HostedAiState.Retrying;

    /// <summary>
    /// True when an exception from the model (translation) leg means it DID NOT ANSWER - a bounded
    /// <see cref="TimeoutException"/> from <see cref="HostedInferenceBrain"/>, or an
    /// <see cref="HttpRequestException"/> transport failure - as opposed to an answered failure (402,
    /// 429, 5xx surface as typed/InvalidOperation exceptions and are handled elsewhere). A caller-cancel
    /// (shutdown) is excluded: that is not the model's non-answer, it is us stopping. This is the model
    /// leg's mirror of the "absence of evidence -> Retrying" rule the speech leg already applies.
    /// </summary>
    private static bool IsModelDidNotAnswer(Exception ex, CancellationToken ct)
        => ex is not WingmanModelRateLimitedException
           && (ex is TimeoutException
               || ex is HttpRequestException
               || (ex is OperationCanceledException && !ct.IsCancellationRequested));

    /// <summary>
    /// Capture one wingman summary for the training dataset (no-op unless the setting is on).
    /// Best-effort and fire-and-forget at the call site so it never delays a voice turn; the
    /// store fetches up to 20,000 chars of the session terminal and appends the record itself.
    /// </summary>
    internal Task CaptureTrainingAsync(SessionVerbClient route, string sid, string source, string reply, string recentContext, string spoken, double replySeconds, CancellationToken ct = default)
        => _training.CaptureAsync(route, sid, source, reply, recentContext, spoken, replySeconds, ct);

    public VoiceReady? Get(string sid) => _ready.TryGetValue(sid, out var v) ? v : null;
    public byte[]? GetAudio(string sid) => _ready.TryGetValue(sid, out var v) ? v.Audio : null;
    public string? GetAudioContentType(string sid) => _ready.TryGetValue(sid, out var v) ? v.ContentType : null;

    /// <summary>Mark the session as a voice session (persisted, so the gateway keeps its voice fresh
    /// across restarts via the background sweep + turn-end).</summary>
    public void Mark(string sid) { if (_voiceSessions.TryAdd(sid, 1)) SaveVoiceSessions(); }

    /// <summary>
    /// Stop keeping voice for this session - it is no longer a voice session (issue #859). The user
    /// turned voice off, so the gateway must stop spending the per-turn Opus translation + hosted
    /// text-to-speech on it. Removes it from the persisted voice-session set (so the turn-end watcher
    /// and the background sweep skip it - both gate on <see cref="IsVoiceSession"/> /
    /// <see cref="VoiceSessionIds"/>), drops any cached spoken summary + audio (so the list stops
    /// showing it "voice ready" and nothing stale is served), and clears the transient generating
    /// marker. The removal is persisted, so a gateway restart does not bring the session back as a
    /// voice session. Read-only: this changes only gateway-side voice marking; it sends nothing into
    /// the session. Re-entering voice (the explain path calls <see cref="Mark"/>) starts it again.
    /// </summary>
    public void Unmark(string sid)
    {
        var wasVoice = _voiceSessions.TryRemove(sid, out _);
        if (wasVoice) SaveVoiceSessions();
        _generating.TryRemove(sid, out _);
        _voiceUnavailable.TryRemove(sid, out _);   // voice is off, so its unavailable-state is moot (issue #939)
        _nothingToNarrate.TryRemove(sid, out _);   // voice is off, so "nothing to narrate" is moot too
        if (_ready.TryRemove(sid, out _))
            DeleteReadyAudio(sid);   // keep the durable cache in step so a stale tap can't 404
        if (wasVoice)
            FileLog.Write($"[WingmanVoiceService] voice unmarked (turned off): sid={sid}");
    }

    /// <summary>
    /// A new turn just started on this session, so the cached spoken summary + audio are now stale.
    /// Drop them immediately - the list stops showing it "voice ready", and nothing stale gets
    /// served or played. The session stays a voice session, so when the turn finishes the turn-end
    /// hook regenerates a fresh summary. Called on the Working transition.
    /// </summary>
    public void OnSessionWorking(string sid)
    {
        // A new turn (blue) supersedes any in-flight generation for the old turn, so drop the
        // yellow "wingman running" marker too - raw activity wins while the agent works.
        _generating.TryRemove(sid, out _);
        _voiceUnavailable.TryRemove(sid, out _);   // a fresh turn clears the old unavailable-state (dismissible, issue #939)
        _nothingToNarrate.TryRemove(sid, out _);   // a fresh turn supersedes "nothing to narrate" - re-evaluated on its turn-end
        if (_ready.TryRemove(sid, out _))
        {
            DeleteReadyAudio(sid);   // issue #553: keep the durable cache in step so a stale tap can't 404
            FileLog.Write($"[WingmanVoiceService] voice + text cache cleared (session working): sid={sid}");
        }
    }

    /// <summary>
    /// Store a spoken summary that a caller already produced (the on-demand explain / voice-turn
    /// paths), synthesize its audio, and mark the session as a voice session. Best-effort: if the
    /// audio can't be made (no key / outage) the session is still marked, so turn-end retries.
    /// </summary>
    public async Task StoreSpokenAsync(string sid, string spoken, string reply, CancellationToken ct = default)
    {
        Mark(sid);
        if (string.IsNullOrWhiteSpace(spoken)) return;
        var tts = await TtsAsync(spoken, ct);
        // The "if anything fails, remove the triangle" rule: when synthesis returns null/empty we
        // leave _ready WITHOUT this session, so HasVoice stays false and no triangle shows. Only a
        // real, playable summary becomes ready - and is persisted (issue #553) so it survives a restart.
        if (tts.Audio is { Length: > 0 })
        {
            _voiceUnavailable.TryRemove(sid, out _);   // success clears any prior unavailable-state (dismissible)
            StoreReady(sid, spoken, reply ?? "", tts.Audio, tts.ContentType);
        }
        else if (tts.Unavailable is HostedAiState state)
        {
            // Issue #939: no longer swallowed. Record WHY voice is unavailable so the /sessions
            // aggregation can show the consistent add-credit / add-key state instead of a silently
            // missing triangle. Left as-is until the next successful turn-end generation clears it.
            _voiceUnavailable[sid] = state;
            FileLog.Write($"[WingmanVoiceService] voice unavailable sid={sid}: {state}");
        }
    }

    /// <summary>Mark a session ready with already-synthesized audio: update the in-memory cache and
    /// persist it to disk so it survives a gateway restart (issue #553). The single place the success
    /// branch lives - the test seam (<see cref="StoreReadyAudioForTest"/>) reuses it so persistence is
    /// exercised without a live provider call.</summary>
    private void StoreReady(string sid, string spoken, string reply, byte[] audio, string? contentType)
    {
        var ready = new VoiceReady(spoken, reply, audio, DateTime.UtcNow,
            NormalizeContentType(contentType) ?? DetectAudioContentType(audio));
        _ready[sid] = ready;
        _nothingToNarrate.TryRemove(sid, out _);   // audio exists, so there was something to narrate after all
        SaveReadyAudio(sid, ready);
    }

    /// <summary>Test seam: store ready audio exactly as a successful synthesis would (in-memory +
    /// durable), so the persistence round-trip can be tested without calling a provider.</summary>
    internal void StoreReadyAudioForTest(string sid, string spoken, string reply, byte[] audio, string? contentType = null)
        => StoreReady(sid, spoken, reply, audio, contentType);

    /// <summary>
    /// Regenerate the voice for a session from its latest turn: read the last reply, translate it,
    /// synthesize audio, store. Called on every turn-end for voice sessions (background, best-effort
    /// - it swallows its own failures so a turn is never blocked on voice).
    ///
    /// Issue #1322: a re-narration must never interrupt a client that is already listening to this
    /// turn's narration. Two guards make it "prepare quietly": it does nothing when the current turn
    /// already has a fresh, playable narration (regenerating it would only mint a new clip and pull
    /// the rug on an active listener), and it shows the yellow "wingman reading" window ONLY for a
    /// brand-new turn (<paramref name="showReadingWindow"/>). A background refresh / catch-up of an
    /// already-ended turn stays quiet, because a phone may be playing this turn from its own local
    /// cache and flipping the session yellow would drop it out of the speaking screen mid-play.
    /// </summary>
    /// <param name="showReadingWindow">True for a genuinely new turn (a live Working -> Waiting
    /// boundary): show the yellow "wingman reading" hold until the summary lands. False for a
    /// background refresh, a startup catch-up, or an idle pre-build sweep: generate silently so a
    /// session a client is already listening to is never flipped yellow.</param>
    internal async Task GenerateAsync(string sid, SessionVerbClient route, CancellationToken ct = default, bool showReadingWindow = true)
    {
        Mark(sid);

        // The "already narrated" skip is now IDENTITY-AWARE and lives in GenerateOnceAsync, after the
        // current reply has actually been read (issue #1322 done right). The old guard here was a bare
        // "does ANY narration exist" check (HasVoice), which assumed every genuinely new turn cleared
        // the cache first via OnSessionWorking on the Working transition. That assumption breaks: the
        // Working transition is observed on a racy sampled edge and, on a multi-part turn (an interim
        // reply, then a sub-agent, then the real answer), the intermediate Working can fall between two
        // samples that both read "waiting" - so the cache is never cleared and the bare guard wrongly
        // suppressed narration of the final answer forever (the phone replayed the stale interim clip).
        // Comparing the reply TEXT instead removes the dependency on catching that edge: a changed reply
        // always regenerates, the same reply still stays quiet so a client mid-play is never disturbed.
        // The idle sweep still guards on HasVoice at its own call site, so it never reaches here for a
        // cached session; only the turn-end path does, and it pays one cheap /turns read to compare.

        // NO fleet-wide gate. Every session calls the hosted relay on its own and discovers an outage
        // for itself (see the field comment above). There used to be two shared cooldown gates here that
        // SKIPPED every session but one probe while another session's 429 held a cooldown - the coupling
        // that muted the whole fleet on one bad call. Removed 2026-07-17. The only guard left is
        // per-session coalescing, which never makes one session wait on another.
        //
        // Coalesce: never run two generations for the SAME session at once (a slow turn overlapping the
        // idle sweep would otherwise double the spend). First caller wins.
        if (!_inFlight.TryAdd(sid, 1))
            return;
        try
        {
            await GenerateOnceAsync(sid, route, ct, showReadingWindow);
        }
        catch (WingmanModelRateLimitedException rl)
        {
            // The provider rate limited THIS call. Nothing fleet-wide happens: this session simply has no
            // audio this cycle and retries on its own next turn-end / idle sweep. Record Retrying so its
            // OWN UI says "voice on its way", exactly as the model-leg timeout path does - and it never
            // reaches across to another session (that coupling was the gate we removed).
            _voiceUnavailable[sid] = HostedAiState.Retrying;
            FileLog.Write($"[WingmanVoiceService] GenerateAsync sid={sid} rate limited (429){(rl.RetryAfter is { } ra ? $" (Retry-After {ra.TotalSeconds:F0}s)" : "")}; no audio this cycle, this session retries on its own");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanVoiceService] GenerateAsync sid={sid} FAILED: {ex.Message}");
        }
        finally { _inFlight.TryRemove(sid, out _); }
    }

    /// <summary>
    /// One generation attempt: read the latest reply, translate it, synthesize and store the audio.
    /// Split out of <see cref="GenerateAsync"/> so the per-session coalescing wrapper stays readable and
    /// this stays the pure "make the voice" step. A rate-limit surfaces as
    /// <see cref="WingmanModelRateLimitedException"/> for the wrapper to catch and record on this session.
    ///
    /// Returns TRUE only when the model leg actually RAN - i.e. we reached the provider and it answered;
    /// FALSE means there was nothing to do (no reply yet, or this reply is already narrated). The caller
    /// no longer needs the distinction now that the shared rate-limit gate is gone, but it is kept because
    /// it honestly reports whether the provider was reached.
    /// </summary>
    private async Task<bool> GenerateOnceAsync(string sid, SessionVerbClient route, CancellationToken ct, bool showReadingWindow)
    {
        var turns = await route.GetTurnsAsync(sid, ct);
        var widgets = turns?.Widgets ?? new List<TurnWidgetDto>();
        var lastReply = widgets.LastOrDefault(w => w.Kind == "Text")?.Content;
        if (string.IsNullOrWhiteSpace(lastReply))
        {
            // No text reply to read aloud - the session is waiting on a prompt / menu. Record the honest
            // "nothing to narrate" fact so the Voice screen (via VoiceDisplayFold) says so, instead of
            // offering a Generate button that would re-run this same empty read and never produce audio.
            _nothingToNarrate[sid] = 1;
            return false;  // nothing to say yet - the provider was not called
        }
        _nothingToNarrate.TryRemove(sid, out _);   // there IS a text reply now - clear any stale "nothing to narrate"
        // Identity-aware skip (issue #1322 done right): only skip when the CURRENT last reply is the
        // exact one already narrated. Unlike the old bare HasVoice guard this does not depend on having
        // observed the Working transition, so a genuinely new/changed reply is never suppressed by a
        // stale cache the missed edge left behind - while the same reply stays quiet (no re-mint, no
        // yellow flip), preserving the "never disturb a listener mid-play" guarantee.
        if (!ShouldRegenerate(sid, lastReply))
        {
            FileLog.Write($"[WingmanVoiceService] GenerateOnce skip (same reply already narrated): sid={sid}");
            return false;   // nothing to do - the provider was not called, so we know nothing new about it
        }
        // Recent conversation so the wingman can add context to a short/terse latest reply.
        var recentContext = WingmanTranslator.BuildRecentContext(widgets);
        // The wingman is now running for this session - show it yellow until the summary lands, but
        // only for a brand-new turn. A background refresh / catch-up stays quiet so a session a phone
        // may be listening to is never flipped yellow mid-play (issue #1322).
        if (showReadingWindow) BeginGenerating(sid);
        try
        {
            WingmanTranslation t;
            try
            {
                t = await _translator.TranslateAsync(recentContext, lastReply, _sessionTitleResolver?.Invoke(sid), ct);
            }
            catch (Exception ex) when (IsModelDidNotAnswer(ex, ct))
            {
                // The MODEL leg (the translation) did not answer - a bounded timeout or a transport
                // failure - so we have no evidence about the service. This is the exact story the speech
                // leg already tells (see TtsAsync): a non-answer is Retrying, NOT a silent FAILED and NOT
                // ServiceDown. Recording Retrying makes the phone show the calm "voice on its way" state
                // and the voice sweep retries on its own, instead of the session sitting red with no audio
                // and no reason (the wedge the owner hit: half the fleet stuck, "generate" doing nothing).
                // A rate-limit (WingmanModelRateLimitedException) is NOT caught here - it stays thrown so
                // GenerateAsync's handler arms the model cooldown with the provider's Retry-After.
                _voiceUnavailable[sid] = HostedAiState.Retrying;
                FileLog.Write($"[WingmanVoiceService] model did not answer for sid={sid}: {ex.Message} - Retrying (audio on its way); the session retries on its own");
                return false;   // nothing produced; the provider was not usefully reached, so report no success
            }
            await StoreSpokenAsync(sid, t.Spoken, lastReply, ct);
            // Log the TRUE outcome: StoreSpokenAsync only makes the session playable when the
            // text-to-speech synthesis actually returned audio. Logging "voice ready"
            // unconditionally (the old behavior) hid every failed synthesis behind a success
            // line, which made a text-to-speech outage look like it was working in the log.
            if (HasVoice(sid))
                FileLog.Write($"[WingmanVoiceService] voice ready: sid={sid}, spokenLen={t.Spoken.Length}");
            else
                FileLog.Write($"[WingmanVoiceService] voice NOT ready (text-to-speech produced no audio): sid={sid}, spokenLen={t.Spoken.Length}");
            // Training capture (no-op unless the setting is on); fire-and-forget so it never
            // delays the turn. CancellationToken.None so a captured turn is not lost on shutdown.
            _ = _training.CaptureAsync(route, sid, "generate", lastReply, recentContext, t.Spoken, t.ReplySeconds, CancellationToken.None);
            // The model leg ran and answered - TranslateAsync returned rather than throwing
            // WingmanModelRateLimitedException. THIS is the only thing that entitles the caller to tell
            // the rate-limit gate the provider is well.
            return true;
        }
        finally { if (showReadingWindow) EndGenerating(sid); }
    }

    /// <summary>
    /// Synthesize the spoken summary to audio through the SAME provider seam the <c>/wingman/tts</c>
    /// endpoint uses (issue #939): the configured mode's base URL + key + model + the user's chosen
    /// voice (<see cref="TtsVoiceConfig"/> / <see cref="TtsModelConfig"/>). This replaced a hardcoded
    /// legacy hardcoded speech call - so a DevThrottle-mode user now hears their configured
    /// voice and hosted narration works from their DevThrottle account. An out-of-credits / cap / setup
    /// condition is returned as a typed <see cref="HostedAiState"/> instead of a silent null, so the
    /// caller can surface the consistent unavailable state.
    /// </summary>
    private async Task<TtsResult> TtsAsync(string text, CancellationToken ct)
    {
        var mode = TranscriptionModeConfig.Get();
        var tts = TranscriptionEndpointResolver.ResolveTts(mode);
        var key = _vault.Get(tts.KeyName);
        if (string.IsNullOrWhiteSpace(key))
            return new TtsResult(null, null, HostedAiState.NeedsKey);

        // The runaway guard, and it announces itself when it fires (issue #1612). This used to be a
        // bare `text[..4000]`: a silent mid-word cut enforcing OpenAI's 4096 limit on a provider that
        // has none, months after we stopped calling OpenAI. The real length control is now the
        // wingman's own instructions (a ~30-second spoken budget) - a summary is short because we
        // asked for a summary, not because we cut an essay in half.
        var input = NarrationText.LimitForSpeech(text, out var wasCut);
        if (wasCut)
            FileLog.Write($"[WingmanVoiceService] narration EXCEEDED {NarrationText.MaxChars} chars " +
                          $"({text.Length}) - spoken text cut and the listener told. The wingman is not summarising.");
        var voice = TtsVoiceConfig.Resolve(mode);
        var model = TtsModelConfig.Resolve(mode);
        var url = tts.BaseUrl.TrimEnd('/') + "/audio/speech";
        // The injected client (tests) or the shared static - never a per-call one. Auth goes on the
        // REQUEST, not the client's default headers, so one shared client is safe under concurrent
        // turn-ends. The effective timeout is the per-attempt deadline inside TtsSynthesis - derived
        // from the text length, since synthesis scales linearly with it - which also retries once when
        // a stalled upstream worker never answers, so a flaky voice backend no longer freezes the turn.
        //
        // This was `_ttsHttp ?? new HttpClient()`: in production _ttsHttp is null, so EVERY turn-end
        // built and dropped a client, leaving a socket in TIME_WAIT and forfeiting the warm TLS
        // connection to the proxy. The narration leg fires on a sweep, so that was the hottest speech
        // path in the product paying the reconnect cost every time.
        var http = _ttsHttp ?? SharedTtsHttp;
        try
        {
            using var resp = await TtsSynthesis.PostAsync(http, url, key, new { model, voice, input, response_format = "mp3" }, input.Length, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                FileLog.Write($"[WingmanVoiceService] tts {mode.ToConfigString()} {(int)resp.StatusCode}");
                // Out of credits / monthly cap (402): map by code to the shared state so the caller
                // records the consistent unavailable state instead of a silent null (issue #939).
                // 402 is the account, not the service: out of credits or over the cap. It is the user's
                // to fix, so it must NOT arm the speech cooldown - backing off would only delay the
                // moment they see the message telling them what to do.
                if ((int)resp.StatusCode == HostedAiHttp.PaymentRequired)
                    return new TtsResult(null, null, HostedAiErrorMapper.Map402(body));

                // A 429 is the provider saying "stop calling" for THIS call. There is no shared gate any
                // more (removed 2026-07-17): this session simply gets no audio this cycle and retries on
                // its own next turn-end / idle sweep. It never reaches across to silence another session.
                if ((int)resp.StatusCode == 429)
                {
                    var retryAfter = RetryAfterHeader.Parse(resp.Headers);
                    FileLog.Write($"[WingmanVoiceService] tts 429 - no audio for this session this cycle, it retries on its own" +
                                  $"{(retryAfter is null ? "" : $" (provider Retry-After {retryAfter.Value.TotalSeconds:F0}s)")}");
                    return new TtsResult(null, null, HostedAiState.ServiceDown);
                }

                // Any other error status means the far end failed, and we KNOW it. This used to return a
                // bare null ("other provider error: logged, no shared state") - the reason was written to
                // a log nobody reads and thrown away three lines from where it was known, so the phone
                // fell back to "the Gateway has not made one, or this session's computer is offline".
                // Both false. On 2026-07-15 that cost ~45 minutes of the owner not being able to tell an
                // outage from a bug. Say what actually happened - this ONE session's ServiceDown - and
                // let it retry on its own next cycle. No shared cooldown reaches across to other sessions.
                FileLog.Write($"[WingmanVoiceService] tts {(int)resp.StatusCode} - server answered with a failure; this session has no audio this cycle and retries on its own");
                return new TtsResult(null, null, HostedAiState.ServiceDown);
            }
            var contentType = resp.Content.Headers.ContentType?.MediaType;
            return new TtsResult(await resp.Content.ReadAsByteArrayAsync(ct), contentType, null);
        }
        // A timeout (TtsSynthesis exhausted its attempts) or a transport failure is the same story from
        // the user's side: the service did not answer. It is not their fault and there is nothing for
        // them to fix, so it must reach them as ServiceDown rather than as silence.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A TimeoutException here is TtsSynthesis giving up after its attempts - the exact failure it
            // exists to bound. Same story for a transport failure: the service did not answer.
            //
            // A timeout is the ABSENCE of evidence - the service said nothing, so we know nothing about
            // the service. It is reported as Retrying, NOT ServiceDown: ServiceDown makes the phone say
            // "Voice service down", a claim about the service that a non-answer cannot support, whereas
            // Retrying tells the honest truth - the audio is not ready yet and this session is trying
            // again on its own next turn-end / idle sweep. (An ANSWERED failure - the 429/5xx branches
            // above - keeps ServiceDown, because there the service really did tell us it is failing.)
            // This is per session and touches nothing else: there is no shared gate for a timeout to arm.
            FileLog.Write($"[WingmanVoiceService] tts did not answer for this narration: {ex.Message} - " +
                          "no answer from the service, so this is Retrying (not down); this session retries on its own");
            return new TtsResult(null, null, HostedAiState.Retrying);
        }
        // NOTE: no `finally { http.Dispose(); }`. `http` is now either the caller's injected client or
        // the shared static, and disposing EITHER would be wrong - the static must outlive every call
        // (that is the entire point of sharing it) and the injected one belongs to the test that made it.
    }

    private static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var mediaType = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return mediaType.Length == 0 ? null : mediaType;
    }

    private static string DetectAudioContentType(byte[] audio)
    {
        if (audio.Length >= 4
            && audio[0] == (byte)'R'
            && audio[1] == (byte)'I'
            && audio[2] == (byte)'F'
            && audio[3] == (byte)'F')
            return "audio/wav";

        if (audio.Length >= 3
            && audio[0] == (byte)'I'
            && audio[1] == (byte)'D'
            && audio[2] == (byte)'3')
            return "audio/mpeg";

        if (audio.Length >= 2 && audio[0] == 0xFF && (audio[1] & 0xE0) == 0xE0)
            return "audio/mpeg";

        return "audio/mpeg";
    }
}
