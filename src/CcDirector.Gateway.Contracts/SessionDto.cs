namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Describes a single agent session (Claude/Pi/Codex/Gemini) running inside a Director.
/// Returned by /sessions on both the Director Control API and the Gateway.
/// </summary>
public sealed class SessionDto
{
    /// <summary>CC Director's internal session GUID. Stable for the life of the session.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Which Director owns this session. Empty in Director-local responses.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>Agent CLI kind: ClaudeCode, Pi, Codex, Gemini, OpenCode, RawCli.</summary>
    public string Agent { get; set; } = "";

    /// <summary>Group identity (issue #225) when this session belongs to a group; null for
    /// solo sessions. Lets a by-repo / fleet view keep group members adjacent.</summary>
    public string? GroupId { get; set; }

    /// <summary>The session's role within its group (issue #225); null for solo sessions.</summary>
    public string? GroupRole { get; set; }

    /// <summary>Repository / working directory.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>Process lifecycle status: Starting / Running / Exiting / Exited / Failed.</summary>
    public string Status { get; set; } = "";

    /// <summary>Cognitive activity state: Starting / Idle / Working / WaitingForInput / WaitingForPerm / Exited.
    /// This is the RAW state (issue #186 two-owner model): owned by the Director's dumb
    /// quiet-timer detector, purely mechanical, written by nothing else.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>
    /// The ASSESSED state (issue #186 two-owner model): owned by the GATEWAY, whose brain
    /// reads the transcript after a turn end and can refute the mechanical quiet signal
    /// (e.g. quiet but nothing needs you -> "Idle"). Null when no assessment stands - new
    /// PTY bytes auto-invalidate it. UIs display AssessedState ?? ActivityState. On a
    /// Director's own API this carries the display annotation the Gateway pushed down;
    /// it is NEVER fed into the detector and never re-pushed.
    /// </summary>
    public string? AssessedState { get; set; }

    /// <summary>UTC timestamp the session was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Total bytes the terminal buffer has accumulated since session start. Use as a cursor in /buffer?since=. </summary>
    public long TotalBufferBytes { get; set; }

    /// <summary>
    /// True when the agent currently has the terminal in the alternate screen buffer
    /// (full screen mode). While true, local scrollback is empty and terminal history
    /// capture does not work, so classify the session as full screen and use a transcript
    /// provider or screen reconstruction rather than the scrollback. Reflects the live
    /// terminal state at query time; it flips as the agent enters and leaves full screen.
    /// </summary>
    public bool IsAlternateScreen { get; set; }

    /// <summary>
    /// Claude's current session id, reported by the Claude SessionStart hook and updated across
    /// /clear and compaction (Claude mints a new id on each). Null for non-Claude sessions or
    /// before the first hook fires.
    /// </summary>
    public string? ClaudeSessionId { get; set; }

    /// <summary>
    /// Absolute path to Claude's current transcript .jsonl, reported by the SessionStart hook.
    /// Authoritative across /clear and compaction. Null for non-Claude sessions or before the
    /// first hook fires.
    /// </summary>
    public string? ClaudeTranscriptPath { get; set; }

    /// <summary>Optional friendly name for the session.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The Mission this session is ATTACHED to (see
    /// docs/new_architecture/mission-as-first-class-unit-of-work.md), or null when it is attached to no
    /// Mission. A Mission is its own persisted record (<see cref="MissionDto"/>); this is the attachment
    /// link, stamped at spawn (from <see cref="NewSessionRequest.MissionId"/>) or later via
    /// POST /sessions/{id}/mission. Mirrors <c>Session.MissionId</c> on the owning Director; passes through
    /// the Gateway aggregation unchanged. Null on Directors that predate this field.
    /// </summary>
    public Guid? MissionId { get; set; }

    /// <summary>
    /// The attached Mission's display name, CACHED on the session for at-a-glance rendering so a client
    /// does not have to resolve <see cref="MissionId"/> against the Mission store. Resolved and stamped at
    /// attach time; the Mission record (<see cref="MissionId"/>) remains the source of truth. Null when the
    /// session is attached to no Mission. Mirrors <c>Session.MissionName</c> on the owning Director.
    /// </summary>
    public string? MissionName { get; set; }

    /// <summary>
    /// The session's short, human-friendly three-digit number (100-999), or null when the session
    /// has no number (issue #820). Allocated by the owning Director at creation and stable for the
    /// life of the session - a separate field from <see cref="Name"/>, so a rename never changes it.
    /// Passes through the Gateway aggregation unchanged. Null on Directors that predate this field.
    /// </summary>
    public int? Number { get; set; }

    /// <summary>
    /// The session's position in its owning Director's list, mirroring
    /// <c>Session.SortOrder</c>. This is the user-controlled desktop order
    /// (set by drag-drop on the desktop and persisted), so a client that sorts
    /// by it keeps each session in a stable slot instead of reshuffling. 0 when
    /// the owning Director predates this field. Populated by the Director.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Aggregate at-a-glance status color, written by the Director's
    /// SessionStatusWingman. The UI renders this verbatim and never derives it
    /// from other fields.
    /// Values: "blue" (agent is working), "red" (needs the user - input/permission/idle),
    /// "yellow" (the Wingman is reading the screen and narrating), "purple" (parked on its
    /// own background task, will resume itself), "supporting" (a controlled sub-agent another
    /// session is driving - issue #815, rendered as a recessive slate #64748B), "unknown"
    /// (process exited, or source unreachable/unparseable - rendered gray). On-hold is separate:
    /// see <see cref="OnHold"/>. ("green" is legacy and no longer emitted.)
    /// </summary>
    public string StatusColor { get; set; } = "unknown";

    /// <summary>
    /// True when the agent process ended UNEXPECTEDLY - a crash (issue #959). Mirrors
    /// <c>Session.Crashed</c> on the owning Director, which decides it from the exit code AND whether the
    /// session dropped out while working (some crashes exit 0, so the code alone is not enough).
    ///
    /// This is a RAW fact and the fold reads it directly. It exists because a crash was NEVER modelled in
    /// <see cref="ActivityState"/> - a crashed session is <c>Exited</c> like any other - so without it the
    /// wire could not tell a crash from a clean exit and every client rendered both as the same grey. That
    /// is precisely what happened: the Director kept setting its deep-red Error colour, and the fold, which
    /// reads only raw facts, had no raw fact to read.
    ///
    /// Deliberately NOT inferred from <see cref="Status"/> == "Failed". The two coincide today only because
    /// the one other writer of Failed (a create-time failure) disposes the session before it ever reaches
    /// the roster. Reading the producer's own fact keeps that coincidence from becoming a dependency.
    /// </summary>
    public bool Crashed { get; set; }

    /// <summary>
    /// Short human-readable reason for the current <see cref="StatusColor"/>
    /// (e.g. "session created", "working", "waiting for input"). Surfaced as a
    /// tooltip on the dot so the user can see WHY the color is what it is.
    /// </summary>
    public string LastStatusReason { get; set; } = "";

    /// <summary>Wingman briefing-pipeline state: "None" | "Briefing" | "Briefed" | "Failed".
    /// Orthogonal to ActivityState (a session can ask AND keep working). "Briefing" renders
    /// the rail's yellow "wingman reading..." chip. Defaults to None on old Directors.</summary>
    public string BriefingState { get; set; } = "None";

    /// <summary>The latest turn brief's needs-you one-liner (&lt;=8 words) for the rail /
    /// FIFO / voice. Null when nothing is needed or no brief exists.</summary>
    public string? RailLine { get; set; }

    /// <summary>
    /// Issue #218: UTC timestamp the session ENTERED the red / NEEDS-YOU effective state
    /// (<see cref="SessionOrdering.EffectiveColor"/> == "red"). Gateway-owned: stamped by the
    /// Gateway during the /sessions aggregation the first refresh the session presents red,
    /// held stable while it stays red, and cleared to null when it leaves red. Drives the
    /// Cockpit's "how long has this been waiting" triage label. Null whenever the session is
    /// not red (including while the wingman is still briefing/explaining - effective yellow/
    /// orange is not yet waiting time). In-memory only (like AssessedState): a Gateway restart
    /// re-derives it on the next red transition. Always null in Director-local responses.
    /// </summary>
    public DateTime? NeedsYouSince { get; set; }

    /// <summary>
    /// Gateway-owned presentation color after all overlays are folded in
    /// (<see cref="SessionOrdering.EffectiveColor"/>): on-hold, transcribing, explaining,
    /// briefing, and voice-generation state. Stamped by the Gateway aggregator so browser
    /// clients do not reimplement the session state machine. Required on Gateway /sessions
    /// responses; Director-local responses may leave it null.
    /// </summary>
    public string? EffectiveColor { get; set; }

    /// <summary>
    /// Gateway-owned triage bucket after the same effective-color fold:
    /// "needsYou" | "active" | "onHold". Stamped by the Gateway aggregator as the
    /// authoritative grouping value for mobile/Cockpit rosters. Required on Gateway /sessions
    /// responses; Director-local responses may leave it null.
    /// </summary>
    public string? TriageBucket { get; set; }

    /// <summary>
    /// Gateway-owned human-readable state label after the same fold (issue #1177, Phase 2):
    /// e.g. "Needs you" | "Working" | "Ready" | "Wingman reading" | "Preparing voice" |
    /// "Transcribing" | "Explaining" | "Sub-agent" | "Background" | "Snoozed" | "Exited".
    /// Computed by <see cref="SessionOrdering.StateLabel"/> from the same raw facts + overlays as
    /// <see cref="EffectiveColor"/>, so every client renders ONE consistent label instead of each
    /// hand-rolling its own. Stamped by the Gateway aggregator; Director-local responses may leave it null.
    /// </summary>
    public string? StateLabel { get; set; }

    /// <summary>Backend type: ConPty / UnixPty / Pipe / Studio / Embedded.</summary>
    public string BackendType { get; set; } = "";

    /// <summary>
    /// The session's agent-driver capability names (e.g. "Cancel", "Interrupt",
    /// "ClearContext", "History", "TranscriptRead"). UIs render action buttons from
    /// this list verbatim - a verb a tool lacks is simply absent, never guessed.
    /// Empty on Directors that predate the driver layer.
    /// </summary>
    public List<string> DriverCapabilities { get; set; } = new();

    /// <summary>
    /// UTC timestamp of the most recent terminal-buffer write. Falls back to
    /// <see cref="CreatedAt"/> if the session has produced no output yet.
    /// Drives the "Idle Xm" freshness column in the Gateway directory view.
    /// </summary>
    public DateTime? LastActivityAt { get; set; }

    /// <summary>
    /// Seconds since the most recent terminal-buffer write -- the raw "time since the last
    /// ConPTY character" idle clock, computed server-side so external callers (e.g. the test
    /// harness) need no clock math. 0 when the session has produced no output yet.
    /// </summary>
    public double IdleSeconds { get; set; }

    /// <summary>
    /// The terminal-state detector's quiet threshold in seconds: how long the terminal must be
    /// COMPLETELY byte-silent before the turn is even a candidate for being over. Paired with
    /// <see cref="IdleSeconds"/> so a test can assert against the live threshold directly.
    /// </summary>
    public double QuietThresholdSeconds { get; set; }

    /// <summary>
    /// Machine name of the Director that owns this session. Populated by the
    /// Gateway aggregator. Empty in Director-local responses.
    /// </summary>
    public string MachineName { get; set; } = "";

    /// <summary>
    /// User the owning Director is running as. Populated by the Gateway aggregator.
    /// Empty in Director-local responses.
    /// </summary>
    public string User { get; set; } = "";

    /// <summary>
    /// Tailnet-reachable base URL of the owning Director (no trailing slash).
    /// Clients use this to talk directly to the session (prompts, renames, etc.)
    /// without going through the Gateway. Populated by the Gateway aggregator.
    /// Empty in Director-local responses.
    /// </summary>
    public string TailnetEndpoint { get; set; } = "";

    /// <summary>
    /// Full URL of this session's view page on its owning Director.
    /// Format: <c>{TailnetEndpoint}/sessions/{SessionId}/view</c>.
    /// Populated by the Gateway aggregator. Empty in Director-local responses.
    /// </summary>
    public string ViewUrl { get; set; } = "";

    /// <summary>
    /// True when this session is currently in walkie-talkie voice mode (a client is
    /// driving it by voice). The authoritative flag every client reads so the desktop
    /// tile, the HTML view, and the Android roster all agree the session is spoken-to
    /// rather than typed-at. Mirrors <c>Session.VoiceMode</c> on the owning Director.
    /// </summary>
    public bool VoiceMode { get; set; }

    /// <summary>
    /// Where this session sits in the hold ("Snooze") machine - one of the three
    /// <see cref="HoldStates"/> values: <c>None</c>, <c>Held</c>, or <c>DeferredHold</c>. Mirrors
    /// <c>Session.HoldState</c> on the owning Director, which is the only writer.
    ///
    /// THIS IS THE AUTHORITATIVE HOLD FIELD (defect 12, fixed 14 July 2026). It replaced the boolean
    /// <see cref="OnHold"/>, which could not distinguish <c>None</c> from <c>DeferredHold</c> - both
    /// reported false - and so lost the one fact the Gateway's expiry sweep needed. Anything deciding
    /// what to DO about a hold reads this; see <see cref="OnHold"/> for the render-side boolean.
    /// </summary>
    public string HoldState { get; set; } = HoldStates.None;

    /// <summary>
    /// True when the user has parked this session in the FIFO voice queue ("deal with this later") -
    /// i.e. it is parked RIGHT NOW. A user override orthogonal to <see cref="ActivityState"/> and
    /// <see cref="StatusColor"/>: the underlying state is still reported truthfully, but the FIFO
    /// conductor skips held sessions. The UI renders this verbatim and never derives it.
    ///
    /// DERIVED from <see cref="HoldState"/>, never stored (defect 12): it is exactly
    /// <c>HoldState == Held</c>, so the boolean and the tri-state cannot disagree - which is what went
    /// wrong when they were two independent fields. A <c>DeferredHold</c> reads FALSE here, correctly:
    /// it is not parked yet. That is precisely why nothing that must tell "not held" from "about to be
    /// held" may read this field - the snooze sweep reads <see cref="HoldState"/>.
    ///
    /// It stays on the wire, settable, for backward compatibility: a client that predates
    /// <see cref="HoldState"/> still reads the boolean it always did, and a Director that predates it
    /// sends only the boolean, which the setter maps onto the tri-state (true -&gt; Held, false -&gt;
    /// None). The setter is deliberately idempotent, so a payload carrying BOTH fields lands on the same
    /// state whichever order the deserializer assigns them in.
    /// </summary>
    public bool OnHold
    {
        get => HoldStates.IsHeld(HoldState);
        set
        {
            if (value)
            {
                if (!OnHold) HoldState = HoldStates.Held;
            }
            else
            {
                // Only a LANDED hold is released here. A DeferredHold already reads OnHold=false, so
                // "set false" is a no-op against it - it must not be silently destroyed by a writer
                // that only knows about the boolean (the snooze expiry overlay is one such writer).
                if (OnHold) HoldState = HoldStates.None;
            }
        }
    }

    /// <summary>
    /// Snooze Length mission: display-only marker that this session has just RETURNED from an expired
    /// snooze - its Gateway-owned snooze timer elapsed and the fold put it back into "needs you" on its
    /// own (the dead-man's switch fired). Stamped by the Gateway aggregation overlay while the snooze
    /// registry still holds an expired entry for the session; it is NOT a Director fact and nothing is
    /// injected into the session's conversation. Clients render it as a distinct "Snooze ended" badge so
    /// the owner knows this is a "go investigate why it went quiet" item rather than a fresh turn-end, and
    /// the phone push reads "Snooze ended - still waiting on you" the one time it first appears.
    /// </summary>
    public bool SnoozeExpired { get; set; }

    /// <summary>
    /// True when this session has asked (or been asked) to be deleted and is awaiting the owning
    /// Director's deletion reaper. Set through POST /sessions/{id}/request-deletion; cleared by
    /// DELETE /sessions/{id}/request-deletion during the grace window. Mirrors
    /// <c>Session.PendingDeletion</c>. While true the row paints as a winding-down grey with a
    /// "Marked for deletion" reason and is removed within roughly a minute. The common case is an
    /// unattended run flagging ITSELF once it has nothing left for the user, so the session tears
    /// itself down instead of lingering as a dead tab. The UI renders this verbatim.
    /// </summary>
    public bool PendingDeletion { get; set; }

    /// <summary>Short human reason captured when <see cref="PendingDeletion"/> was set
    /// (e.g. "jobs-auto: nothing to report"). Null when the session is not flagged.
    /// Mirrors <c>Session.DeletionReason</c>.</summary>
    public string? DeletionReason { get; set; }

    /// <summary>
    /// Issue #553: true while the Gateway's wingman is actively producing this session's spoken
    /// summary (<c>WingmanVoiceService.IsGenerating</c>). Stamped by the Gateway aggregator only;
    /// always false in Director-local responses. Drives the voice-mode "yellow until ready" color
    /// rule (see <see cref="SessionOrdering.EffectiveColor"/>): a voice-mode session waiting for the
    /// user stays yellow while this is true.
    /// </summary>
    public bool VoiceGenerating { get; set; }

    /// <summary>
    /// Issue #553: true when the Gateway has fetchable, playable cached audio for this session
    /// (<c>WingmanVoiceService.HasVoice</c>) - the SINGLE truthful "there is voice you can play right
    /// now" signal. Stamped by the Gateway aggregator only; always false in Director-local responses.
    /// This controls whether a play affordance is available; the "preparing voice" color is driven
    /// by <see cref="VoiceGenerating"/>, not by missing audio, so a TTS failure cannot wedge a
    /// session outside Needs You forever.
    /// </summary>
    public bool VoiceAudioReady { get; set; }

    /// <summary>
    /// Issue #939 (epic #937): set when the Gateway could NOT keep this session's voice because hosted
    /// AI is unavailable (out of credits, monthly cap reached, or - in bring-your-own mode - no key).
    /// Carries the ONE shared message + call-to-action (built from the single-source
    /// <c>HostedAiMessages</c>), so the owning UI shows the consistent "add credit / add a key" state
    /// instead of a silently missing play triangle. Null when voice is fine. Stamped by the Gateway
    /// aggregator only; cleared on the next successful generation (dismissible).
    /// </summary>
    public HostedAiMessageDto? VoiceUnavailable { get; set; }

    /// <summary>
    /// True while a client is transcribing a dictated utterance into this session: the phone has
    /// released the Speak dialog and the Gateway is uploading + transcribing the recorded audio in
    /// the background, which will then be submitted into the session. Stamped by the Gateway
    /// aggregator only (from the in-memory <c>TranscribingSessions</c> store); always false in
    /// Director-local responses. Drives the orange "Transcribing..." roster color so every client
    /// sees the session is busy and nobody else starts using it (see
    /// <see cref="SessionOrdering.EffectiveColor"/>).
    /// </summary>
    public bool Transcribing { get; set; }

    /// <summary>
    /// Issue #1181, Task 4: the phone-dictation presentation sub-state - which PHASE an inbound
    /// dictation is in, so a client can show the honest label instead of one blanket "Transcribing...".
    /// One of: <c>"Uploading from phone"</c> (the phone is still sending the audio up - computed from the
    /// durable PENDING delivery marker, so it never wedges); <c>"Transcribing"</c> (the audio is up and
    /// the server is turning it into text - computed from an active transcription run, NOT the 90-second
    /// idle mark); or <c>null</c> when no dictation is inbound. Stamped by the Gateway aggregator only
    /// (always null in Director-local responses). Both non-null values drive the orange roster color; the
    /// clients render this string as the label.
    /// </summary>
    public string? DictationStatus { get; set; }

    /// <summary>
    /// Whether the Wingman experience is enabled for this session: auto-explain briefing on
    /// turn-end, Voice + Wingman tabs visible, Yellow "Wingman is reading" state available.
    /// Default OFF. When false the session behaves as a plain terminal -- clients hide the
    /// Voice + Wingman tabs and the dot goes straight Blue->Red on turn-end (no Yellow).
    /// Mirrors <c>Session.WingmanEnabled</c> on the owning Director.
    /// </summary>
    public bool WingmanEnabled { get; set; } = false;

    /// <summary>
    /// Scheduled-run auto-dismiss (issue #1200): true when this session is an AUTOMATED run that should
    /// close itself when it finishes with nothing needing a human (mirrors <c>Session.AutoDismiss</c>).
    /// Only sessions carrying this flag are ever auto-closed; human-started sessions leave it false and are
    /// never touched. Rides the existing snapshot/delta path; defaults false on Directors that predate it.
    /// </summary>
    public bool AutoDismiss { get; set; } = false;

    /// <summary>
    /// Scheduled-run auto-dismiss (issue #1200): the agent's explicit end-of-run verdict parsed from its
    /// final message (mirrors <c>Session.DismissVerdict</c>). <c>"done"</c> = nothing needs the human, safe
    /// to auto-close; <c>"needs-human"</c> = keep the session open. Null when the run has not yet emitted a
    /// verdict (the conservative default - a session is never auto-closed without an explicit <c>done</c>).
    /// Only meaningful when <see cref="AutoDismiss"/> is true.
    /// </summary>
    public string? DismissVerdict { get; set; }

    // ===== Raw local facts for the Gateway color fold (issue #1177, Phase 2) =====
    // These are RAW FACTS the Director reports straight from the Session - it does NOT fold them into a
    // color. The Gateway is the single fold: it computes EffectiveColor / TriageBucket / StateLabel from
    // these plus its own overlays (Transcribing/Briefing/Explaining/VoicePreparing). Additive: they ride
    // the existing snapshot/delta path, and default to false/null on Directors that predate them.

    /// <summary>
    /// RAW FACT: a brand-new session that has not yet taken its first turn (mirrors
    /// <c>Session.IsBrandNew</c>). At a turn-end this is what the fold paints green ("ready") - the
    /// session is sitting at its prompt, available, and must not read as red "needs you".
    /// </summary>
    public bool IsBrandNew { get; set; }

    /// <summary>
    /// RAW FACT: this session is a controlled "Supporting" sub-agent that another session is driving
    /// (mirrors <c>Session.IsControlled</c>, i.e. <see cref="ControllerSessionId"/> is set). The fold
    /// recedes it to slate while its controller is alive, except red "needs you" breaks through.
    /// </summary>
    public bool IsControlled { get; set; }

    /// <summary>
    /// RAW FACT: the id of the session controlling this one (mirrors <c>Session.ControllerSessionId</c>),
    /// or null when this is a normal (uncontrolled) session. The fold uses its presence for the slate
    /// overlay; the roster can confirm the controller is still alive.
    /// </summary>
    public string? ControllerSessionId { get; set; }

    /// <summary>
    /// Automatic session role, COMPUTED by the Gateway aggregation from the whole fleet (not stored):
    /// <c>Worker</c> = this session is controlled AND its controller is still alive; <c>Manager</c> = a
    /// non-worker that controls at least one LIVE worker; <c>Standalone</c> = neither. Dynamic - a
    /// Standalone becomes a Manager when it gains a live worker and reverts when the last dies. The fold
    /// (<see cref="SessionOrdering.EffectiveColor"/>) SUPPRESSES a live Worker's red (it recedes and never
    /// nags the human); Manager/Standalone stay human-facing. Empty on a Director-local response (the role
    /// needs the fleet view). One of "Standalone" / "Worker" / "Manager".
    /// </summary>
    public string? SessionRole { get; set; }

    /// <summary>
    /// RAW FACT: the sticky EXPLICIT role a human/session declared for this session (mirrors
    /// <c>Session.ExplicitRole</c>), or null when none was set. When present it WINS over auto-derivation in
    /// the aggregation's role resolution (so an explicit <see cref="SessionRoles.Architect"/> - which can
    /// never be inferred from the spawn graph - stays Architect and stays human-facing). One of the
    /// <see cref="SessionRoles"/> values, or null.
    /// </summary>
    public string? ExplicitRole { get; set; }

    /// <summary>
    /// RAW FACT: a still-Working Worker wants its manager's attention (an explicit escalation signal),
    /// default false. Reserved for the manager-facing rail; the global fold does not consume it yet.
    /// </summary>
    public bool NeedsManager { get; set; }

    /// <summary>
    /// RAW FACT: the display name was AUTO-composed at birth (mirrors <c>Session.IsAutoNamed</c>); false once
    /// a human/self explicitly renamed it. Automatic session roles (chunk 3): the marker any future
    /// auto-rename gates on so a self/human name is never re-auto-named.
    /// </summary>
    public bool IsAutoNamed { get; set; }

    /// <summary>
    /// RAW FACT: the Wingman determined this session is parked on its OWN background task (a build, a
    /// running shell) rather than on the user (mirrors <c>Session.IsBackgroundRunning</c>). At a turn-end
    /// the fold paints this purple instead of red.
    /// </summary>
    public bool IsBackgroundRunning { get; set; }

    /// <summary>
    /// RAW FACT: a dictated utterance is being transcribed and submitted into this session in the
    /// background on the DIRECTOR (the Avalonia desktop <c>BackgroundDictationSend</c> flow; mirrors
    /// <c>Session.IsTranscribing</c>). Distinct from <see cref="Transcribing"/>, which is the Gateway's
    /// OWN transcribing flag (the mobile Speak flow). The fold paints EITHER source orange, so a
    /// desktop-dictating session shows "Transcribing..." the same as a phone-dictating one.
    /// </summary>
    public bool IsTranscribing { get; set; }

    /// <summary>
    /// RAW FACT: the legacy auto-explain deep dive (<c>ProactiveExplainService</c>) is running for this
    /// session at a turn-end (mirrors <c>Session.IsExplaining</c>). While it runs, and the session is
    /// WingmanEnabled and at a turn-end, the fold paints yellow ("wingman is reading"). This is DISTINCT
    /// from the Gateway's user-initiated deep dive (<c>BriefingState == "Explaining"</c>, issue #217),
    /// which the fold paints ORANGE - see <see cref="SessionOrdering.IsExplaining"/>.
    /// </summary>
    public bool IsAutoExplaining { get; set; }

    /// <summary>
    /// For GitHub Actions remote sessions: the "owner/repo" the session runs against.
    /// Empty for local sessions. Mirrors the backend's repo slug.
    /// </summary>
    public string RemoteRepo { get; set; } = "";

    /// <summary>
    /// For GitHub Actions remote sessions: web URL of the issue/PR thread driving
    /// the session, or empty until the thread is established / for local sessions.
    /// </summary>
    public string RemoteThreadUrl { get; set; } = "";

    /// <summary>
    /// For GitHub Actions remote sessions: web URL of the most recent workflow run,
    /// or empty when none yet / for local sessions.
    /// </summary>
    public string RemoteRunUrl { get; set; } = "";

    /// <summary>
    /// For GitHub Actions remote sessions: last observed run status
    /// (queued / in_progress / completed / none). Empty for local sessions.
    /// </summary>
    public string RemoteRunStatus { get; set; } = "";

    /// <summary>
    /// DevThrottle Stats: this session's input tally (submitted turns + character volume by modality and
    /// surface), taken at the Director's SendInput/SendTextAsync choke point so desktop-local input is
    /// counted too. Rides the existing snapshot/delta path up to the Gateway, which aggregates every
    /// session's tally into the always-available stats page. Null when the session has taken no counted
    /// input yet, or on Directors that predate this field. Only counts ever travel - never the text of
    /// anything typed or said (mission decision 5).
    /// </summary>
    public InputStatsDto? InputStats { get; set; }

    /// <summary>
    /// Issue #1176 (Phase 1a): a copy safe to hand out from the Gateway's pushed-session cache. The
    /// <c>/sessions</c> aggregation stamps scalar fields (EffectiveColor, DirectorId, MachineName,
    /// voice/transcription overlays, etc.) on the object it serves, so callers must never receive the
    /// cached instance itself or one request would contaminate the cache for later ones. Reference-type
    /// members the aggregator could mutate in place are re-created here; <see cref="VoiceUnavailable"/>
    /// is only ever replaced wholesale by the aggregator (never mutated), so sharing its reference is safe.
    /// </summary>
    public SessionDto Clone()
    {
        var copy = (SessionDto)MemberwiseClone();
        copy.DriverCapabilities = new List<string>(DriverCapabilities);
        return copy;
    }
}
