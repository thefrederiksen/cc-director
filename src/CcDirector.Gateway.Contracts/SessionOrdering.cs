namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Shared client-side policy for how a roster of <see cref="SessionDto"/> is ordered and
/// triaged. Lives next to the DTO so every client (Cockpit today, others later) agrees on
/// the rules instead of each re-implementing them, and so the rules are unit-testable
/// without spinning up a UI.
/// </summary>
public static class SessionOrdering
{
    /// <summary>
    /// The stable "desktop order": honor the owning Director's <see cref="SessionDto.SortOrder"/>
    /// (the user-controlled, drag-to-reorder, persisted order), then <see cref="SessionDto.CreatedAt"/>
    /// as a deterministic tie-break. The tie-break is also the only signal when a Director predates
    /// SortOrder (every session reports 0). This is what keeps a session in a fixed slot instead of
    /// reshuffling as its name or activity state changes.
    /// </summary>
    public static IReadOnlyList<SessionDto> InDesktopOrder(IEnumerable<SessionDto> sessions) =>
        sessions.OrderBy(s => s.SortOrder).ThenBy(s => s.CreatedAt).ToList();

    /// <summary>Triage priority bucket for the "needs-you-first" view.</summary>
    public enum TriageBucket
    {
        /// <summary>Wants the user now (effective color "red"), and not parked.</summary>
        NeedsYou = 0,
        /// <summary>Anything else that isn't parked.</summary>
        Active = 1,
        /// <summary>Parked by the user or the agent (<see cref="SessionDto.OnHold"/>) - sinks to the bottom.</summary>
        OnHold = 2,
    }

    /// <summary>
    /// True while the session must present as "the wingman is reading": the Gateway's brief
    /// agent has the finished turn queued or in flight (<see cref="SessionDto.BriefingState"/>
    /// "Briefing") AND the RAW activity color is red (issue #1177, Phase 2: gated on the raw
    /// <see cref="SessionDto.ActivityState"/>, no longer the Director's cooked StatusColor). While a
    /// NEW turn is already running (blue) the stale in-flight brief is irrelevant - raw activity wins.
    /// </summary>
    public static bool IsBriefing(SessionDto s) =>
        s.BriefingState == "Briefing" && IsRawRed(s);

    // GAP 5: THE GATEWAY'S VOICE WINDOW NEEDS NO RULE HERE - IsVoicePreparing BELOW ALREADY IS IT.
    //
    // The Gateway used to get its voice-mode yellow by WRITING s.BriefingState = "Briefing" onto the row
    // during enrichment (GatewayEndpoints), gated on the Director's value being null/"None"/"Briefed". That
    // overwrite destroyed a field the Director owns: afterwards, BriefingState="Briefing" + VoiceGenerating
    // =true could not say WHO said it - a Director genuinely briefing (the desktop folds yellow too, so the
    // screens agree) and a Gateway that had overwritten a "None" (the desktop folds red - a real
    // disagreement) produced an identical row. The agreement check could only call that "indeterminate" and
    // refuse to grade it, which fixes the instrument rather than the product.
    //
    // The first attempt at this fix added an IsGatewayVoiceBriefing rule here, reading VoiceGenerating and
    // carrying the stamp's condition, on the theory that it preserved every pixel. THE SUITE REFUTED THAT
    // AND WAS RIGHT: it broke StateLabel_VoicePreparing_IsPreparingVoice and
    // EffectiveColor_NonVoiceWaiting_NoAudio_StaysRed, because IsVoicePreparing ALREADY folds the Gateway's
    // own VoiceGenerating fact - correctly, and more narrowly (it requires VoiceMode and an actually
    // WAITING session). A second rule for one fact is a second answer, which is this mission's entire
    // defect class.
    //
    // So the overwrite is deleted and NOTHING replaces it. That also fixes a lie nobody had noticed: by
    // hijacking BriefingState the Gateway made a voice-generating session read "Wingman reading", when its
    // own rule says the truer thing - "Preparing voice". The dot is yellow either way; the words are now
    // honest, the Director's fact survives, and the check can grade the row.

    /// <summary>
    /// True when the session's RAW activity fact reads red - it is parked at a prompt, waiting on a
    /// permission, or idle. THE fold-owned answer to "is this session red?", computed from
    /// <see cref="SessionDto.ActivityState"/> and nothing else.
    ///
    /// Public because the Gateway's own enrichment pipeline must ask this question BEFORE the fold runs
    /// (the voice-mode window stamps <see cref="SessionDto.BriefingState"/> only for a red session). That
    /// stamp used to gate on the DIRECTOR's cooked <see cref="SessionDto.StatusColor"/>, which made a
    /// Gateway-rendered colour depend on a Director-made decision - the one thing law 2 forbids. Exposing
    /// the raw question here is what let that call site stop reading the cooked colour.
    ///
    /// Do NOT read this as "cooked red and raw red are the same thing". They are not, and the difference
    /// is the whole reason this exists - see the note on <see cref="IsVoicePreparing"/>.
    /// </summary>
    public static bool IsRawRed(SessionDto s) =>
        string.Equals(RawActivityColor(s), "red", StringComparison.Ordinal);

    /// <summary>
    /// The dictation phase to paint and label, or null when no dictation should paint this session. A
    /// BLANK <see cref="SessionDto.DictationStatus"/> counts as absent, not as a dictation with no name.
    ///
    /// GAP 6 - THIS IS WHAT MAKES "StateLabel IS NEVER BLANK" A STRUCTURAL FACT RATHER THAN A HOPE.
    /// StateLabel used to return s.DictationStatus verbatim, so its non-blankness rested on a promise made
    /// somewhere else entirely: DictationPhase.For (Gateway/Transcription) only ever returns one of two
    /// non-empty constants or null. That promise is kept today - the hole was NOT reachable, and this is a
    /// hardening rather than a bug fix - but it was enforced two assemblies away from the only method that
    /// depends on it, by a producer that has no idea anything hangs on it. A future phase label read from a
    /// config file, a wire payload or a new producer would break the invariant without touching this file,
    /// and it would surface as a session labelled with the empty string.
    ///
    /// It mattered because a blank label was the ONE reachable-looking hole in Car Mode's old fallback
    /// chain, <c>StateLabel ?? (EffectiveColor ?? StatusColor)</c> - a chain that ended by SPEAKING the
    /// Director's cooked colour. Closing the hole here is what let that chain be deleted as provably dead
    /// rather than argued about: fix the producer, and the fallback has nothing left to catch.
    ///
    /// Asked by BOTH fold arms, so the dot and the label cannot disagree about whether a dictation exists.
    /// A blank reaching only one of them would paint an orange dot beside a label that had fallen through
    /// to "Idle" - a row contradicting itself, which is this mission's whole defect class.
    /// </summary>
    private static string? DictationPhaseLabel(SessionDto s) =>
        string.IsNullOrWhiteSpace(s.DictationStatus) ? null : s.DictationStatus;

    /// <summary>
    /// Issue #553: true while a VOICE-MODE waiting session does not yet have playable audio - either it is
    /// actively generating its spoken summary (<see cref="SessionDto.VoiceGenerating"/>) OR there is simply
    /// no audio ready yet (<c>!VoiceAudioReady</c>). The roster holds YELLOW ("preparing voice") the WHOLE
    /// time - across the gaps between generation attempts, not just while one is in flight - until the audio
    /// is ready.
    ///
    /// Voice mode is a FIRST-CLASS state, not an overlay on a red session. A voice session that has finished
    /// its turn does need the user, but until the voice is ready there is nothing to act on - so it presents
    /// as "needs you, preparing voice" (yellow), and you can read the text if you choose. It becomes red only
    /// once <see cref="SessionDto.VoiceAudioReady"/> is true - now there is something to play and act on.
    ///
    /// OWNER'S RULING, 2026-07-19: in voice mode the user must NEVER see red until the voice is available.
    /// This restores the <c>|| !VoiceAudioReady</c> hold that was removed on 2026-07-08. That removal narrowed
    /// the hold to <see cref="SessionDto.VoiceGenerating"/> ALONE to make it wedge-proof - the yellow could
    /// never get stuck - but it fell back to red in EVERY gap between retry attempts, so a phone in voice mode
    /// flashed red while its voice was still on the way. The wedge the 2026-07-08 change feared (a permanent
    /// text-to-speech failure sitting yellow forever, because "no audio yet" and "gave up" were the same
    /// value) is NOT re-introduced by widening this COLOR rule. It is prevented where it belongs: by making
    /// voice generation reliable (a sub-minute average; anything over three minutes is an exception to be
    /// flagged and fixed) and, separately, by giving voice a terminal "gave up" state. So do NOT re-narrow
    /// this to VoiceGenerating alone to "fix" a session stuck yellow - a stuck session is a voice-reliability
    /// bug, not a color bug, and narrowing the color only hides it behind a red flicker again.
    ///
    /// Gated on raw red and on WaitingForInput/WaitingForPerm so a working (blue) session is untouched.
    /// </summary>
    public static bool IsVoicePreparing(SessionDto s)
    {
        if (!s.VoiceMode) return false;
        // Issue #1177 (Phase 2): gate on the RAW activity color (from ActivityState), not the Director's
        // cooked StatusColor.
        //
        // This comment used to add: "Equivalent today (StatusColor=="red" iff the raw activity is
        // Waiting/Idle)". THAT IFF IS FALSE, and it was asserted rather than checked. The cooked colour has
        // a SECOND writer that never goes through the activity mapping at all: TransientErrorAutoResume
        // (Core/Wingman) writes StatusColor.Red with StatusColorSource.PositiveEvidence when auto-resume
        // gives up, described in its own comment as "sticky over the detector's plain activity-state
        // mapping until the user acts". So cooked-red can stand while the raw activity says otherwise.
        // Raw is the authority here - not because the two agree, but because the fold says raw wins.
        if (!IsRawRed(s)) return false;
        // A BRAND-NEW SESSION IS NEVER "PREPARING VOICE" - it is READY (green), and this arm must not
        // eat that (owner's ruling, 2026-07-27). A session that has taken no turn has produced no
        // assistant reply, so there is no turn to narrate: no generation will ever be attempted, no
        // audio will ever land, and the `|| !VoiceAudioReady` hold below would therefore be permanent.
        //
        // The bug this closes: green lives in BaseColor, the LAST arm of EffectiveColor, BELOW the
        // IsVoicePreparing arm. So with voice mode ON, every freshly-spawned session folded to yellow
        // "Preparing voice" and STAYED there - the green "Ready" state was unreachable for the entire
        // voice-mode fleet. "Preparing voice" was also simply false about it: nothing was being prepared.
        //
        // Not caught for the same reason it was easy to write: the brand-new-is-green tests build their
        // session with a helper that leaves VoiceMode at its default false, so green was only ever proven
        // for the voice-OFF case. The voice-mode variants now live beside them.
        if (s.IsBrandNew) return false;
        var state = s.AssessedState ?? s.ActivityState;
        var waiting = string.Equals(state, "WaitingForInput", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(state, "WaitingForPerm", StringComparison.OrdinalIgnoreCase);
        if (!waiting) return false;
        // Yellow while generating OR while there is simply no audio yet - held across the gaps between
        // attempts, until VoiceAudioReady flips true. See the summary for why this is not the 2026-07-08
        // wedge: a permanently failing voice is a reliability bug to fix at the source, not a color to hide.
        return s.VoiceGenerating || !s.VoiceAudioReady;
    }

    /// <summary>
    /// True when the Director's terminal sensor says bytes are flowing: the session IS WORKING.
    /// This is the top of the ladder and the only rule that cannot be overridden.
    ///
    /// Read from the RAW activity fact only. Nothing else may contribute - not hold, not
    /// dictation, not briefing, not role. Those answer "why is it NOT working?", which is a
    /// question that only means anything once this returns false.
    /// </summary>
    private static bool IsWorking(SessionDto s) =>
        string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s.ActivityState, "Starting", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The ONE effective status color every client renders and triages on (issue #196).
    ///
    /// THE LAW (owner's ruling, 2026-07-14, restated and final): IF A SESSION IS WORKING, IT IS
    /// BLUE. ALWAYS. NOTHING OUTRANKS WORKING. There are no exceptions and none may be added.
    /// If you are about to add a rule above the working check - don't. Every rule that ever sat
    /// above it has been a defect, and each one cost the owner a day.
    ///
    /// Everything below the working check answers a single question: "why is this session NOT
    /// working?" Grey = parked. Orange = its dictation is in flight.
    /// Yellow = the wingman is reading the finished turn, or voice is generating. Those are all
    /// states of a session that has STOPPED. A working session has stopped being none of them,
    /// so they cannot apply to it - that is not a policy choice, it is what the words mean.
    ///
    /// The Director stamps the raw facts; the Gateway stamps <see cref="SessionDto.BriefingState"/>
    /// on top. Folding them HERE - instead of in each view - is what keeps the dot, the
    /// "wingman reading..." chip, and the triage bucket atomic across every screen.
    /// </summary>
    public static string EffectiveColor(SessionDto s) =>
        // ===== ORDER 0: WORKING IS BLUE. NOTHING GOES ABOVE THIS LINE. =====
        IsWorking(s) ? "blue"
        // ===== Everything below applies ONLY to a session that is NOT working. =====
        : s.OnHold ? "grey"
        // Transcribing orange fires for ANY dictation source: the Task 4 phase label (mobile Speak -
        // "Uploading from phone" or "Transcribing", s.DictationStatus), the legacy Gateway flag
        // (s.Transcribing), OR the Director raw fact (desktop dictation, s.IsTranscribing). All orange.
        // It marks "a dictated utterance is in flight, do not grab this session" - which is only
        // meaningful at a prompt. Mid-turn it is invisible anyway: blue already won above.
        : (DictationPhaseLabel(s) != null || s.Transcribing || s.IsTranscribing) ? "orange"
        // THERE IS NO "EXPLAINING" ORANGE ARM HERE, AND THERE NEVER WORKED ONE. This used to read
        // `: IsExplaining(s) ? "orange"`, gated on BriefingState == "Explaining" (issue #217's
        // user-initiated "I am lost - explain" deep dive). #217's roster orange has never once worked -
        // it never fired, in any release, because no code path could produce the value it gates on:
        //   * SessionDto.BriefingState is stamped ONLY from the Director's BriefingState enum
        //     (ControlEndpoints.ToDto, `s.BriefingState.ToString()`), and that enum declares exactly
        //     None / Briefing / Briefed / Failed. "Explaining" is not a member, so it is unreachable.
        //   * The string exists only in the TurnBriefs pane's own response family, and even THERE it is
        //     dead: the live wiring (GatewayHost, TurnBriefGatewayEndpoints.Map) passes a briefingStateFor
        //     that returns only "Briefed" or "None", and passes requestExplainAsync: null - so the deep
        //     dive's request route answers 503 and the state is switched off at the composition root.
        // Do NOT "restore" this rule. Making it fire is not a bug fix, it is a feature: a request path, a
        // state producer, and a new value on the wire, with a product decision behind it.
        //
        // The Director's LEGACY auto-explain is a SEPARATE, WORKING feature and is untouched: it rides on
        // the raw fact SessionDto.IsAutoExplaining and folds to YELLOW in ResolveActivity below. Do not
        // conflate the two - deleting this orange did not delete that yellow.
        // The DIRECTOR's own briefing (its BriefingState) and the GATEWAY's voice generation (its
        // VoiceGenerating, folded by IsVoicePreparing) are separate facts with separate owners, and each
        // has exactly one rule. The Gateway used to reach the first arm by overwriting the Director's field
        // rather than letting the second arm do its job - same yellow, destroyed evidence, wrong words
        // (gap 5). Do not add a third rule for either fact.
        : IsBriefing(s) ? "yellow"
        : IsVoicePreparing(s) ? "yellow"
        // Issue #1177 (Phase 2): the base color is computed from RAW facts. NO GATEWAY-DECIDED COLOUR READS
        // THE DIRECTOR'S COOKED StatusColor - as of 2026-07-14 that is true of the pipeline as well as the
        // fold. It was NOT true before: the Gateway's voice-mode window (GatewayEndpoints, issue #531) gated
        // its BriefingState = "Briefing" stamp on s.StatusColor == "red", so a yellow rendered on the phone
        // and the Cockpit depended on a decision the Director made. That was the last Gateway consumer of
        // the cooked colour and it is now IsRawRed.
        //
        // Do NOT read that as "the cooked colour is dead" and delete the Director's colour computation. It
        // is not, and it is not ours to delete: the cooked field still crosses the wire, the ?statusColor=
        // query filter still selects on it, and several desktop surfaces still read it. What is now true is
        // narrower and is the part law 2 cares about - the Gateway decides its colours from raw facts alone.
        : BaseColor(s);

    // ===== Issue #1177 (Phase 2): the raw-fact base color (a port of the Director's SessionStatusWingman
    // ColorFromActivityState, computed from the wire's raw facts). The method named here used to be
    // "ColorFor", which does not exist and never did - the fifth copy of that wrong name in the codebase. =====

    /// <summary>
    /// The base presentation color from raw facts: the activity color with the purple (background),
    /// green (brand-new), and Director auto-explain (yellow) turn-end overlays, plus the slate
    /// "Supporting" overlay that suppresses a controlled Worker's RED. The briefing overlay is applied
    /// by <see cref="EffectiveColor"/> above (it wins before this is reached), so it is intentionally
    /// not repeated here.
    ///
    /// A WORKING session is BLUE, always - nothing outranks working (owner's ruling, 2026-07-14). This
    /// used to open with a slate overlay that returned "supporting" for ANY controlled session that was
    /// not red, which DISCARDED the real activity state: a controlled sub-agent 23 minutes into real
    /// work rendered gray and read "Sub-agent", indistinguishable from on-hold or exited. That rule
    /// implemented the 2026-07-10 decision in issue #1286 ("a controlled worker always shows the
    /// recessive Supporting colour"), which the owner has since VOIDED. Do not restore it.
    ///
    /// Ownership - who is driving a session - travels on the rail's role badge, a separate channel.
    /// Color says what a session is DOING and must never be spent saying who owns it.
    /// </summary>
    private static string BaseColor(SessionDto s)
    {
        var activity = ResolveActivity(s);
        var isRed = string.Equals(activity, "red", StringComparison.OrdinalIgnoreCase);

        // Automatic session roles (Layer 1 - "workers never nag the human"): a LIVE-controlled Worker
        // suppresses its red - it recedes to slate and never surfaces red to the human (its manager sees it
        // via the rail). SessionRole is stamped by the Gateway aggregation from the WHOLE fleet, so
        // "Worker" already means "controlled AND controller ALIVE". A Worker whose controller has DIED is
        // role Manager/Standalone (not "Worker"), so this does NOT fire and its red surfaces - the escape
        // hatch. Managers and Standalones are human-facing, so their red always breaks through.
        if (isRed && string.Equals(s.SessionRole, SessionRoles.Worker, StringComparison.OrdinalIgnoreCase))
            return "supporting";

        return activity;
    }

    /// <summary>The activity color plus the turn-end overlays the Director bakes: auto-explain yellow,
    /// background purple, brand-new green. Order matches the Director's <c>ResolveActivityColor</c>.</summary>
    private static string ResolveActivity(SessionDto s)
    {
        var atTurnEnd = IsAtTurnEnd(s);
        // Legacy auto-explain (ProactiveExplainService): yellow while WingmanEnabled and at a turn-end.
        if (s.WingmanEnabled && s.IsAutoExplaining && atTurnEnd) return "yellow";
        // Parked on its OWN background task: purple (turn-end, WingmanEnabled).
        if (s.WingmanEnabled && s.IsBackgroundRunning && atTurnEnd) return "purple";
        // Brand-new, has not yet taken a turn: green ("ready").
        if (s.IsBrandNew && atTurnEnd) return "green";
        return RawActivityColor(s);
    }

    /// <summary>The pure activity-state color. Starting/Working -&gt; blue; Waiting/Idle -&gt; red;
    /// Exited -&gt; "grey" (Phase 2.3, owner-approved: an exited session shows the SAME grey string as an
    /// OnHold one, so clients render it identically), EXCEPT a crashed one -&gt; "error" (issue #959: the
    /// deep red #B91C1C, deliberately darker than the bright "needs you" red, so a session that DIED is
    /// never mistaken for one that finished). This deliberately DIVERGES from the Director's standalone
    /// <c>ColorFromActivityState</c>, which keeps exited as "unknown" - the Gateway is the single source of
    /// truth for the fold. Any unrecognized state -&gt; "unknown".
    ///
    /// The crash arm reads <see cref="SessionDto.Crashed"/>, NOT the activity state: a crash was never
    /// modelled in ActivityState (a crashed session is "Exited" like any other), which is exactly how the
    /// deep red went missing for two releases - this fold reads raw facts, and the crash fact was not on
    /// the wire to read.
    ///
    /// Case-INSENSITIVE, matching every other reader of <see cref="SessionDto.ActivityState"/> in this
    /// file (<see cref="IsWorking"/>, <see cref="IsAtTurnEnd"/>, <see cref="IsVoicePreparing"/>, and the
    /// role rule in <see cref="BaseColor"/>). This used to be a C# constant-pattern switch, which is
    /// ORDINAL and case-SENSITIVE - so one file compared the same field both ways, six lines apart inside
    /// <see cref="IsVoicePreparing"/>. It could not fire today: the sole producer of the field
    /// (the Director's ToDto, `s.ActivityState.ToString()` over the ActivityState enum) emits exact
    /// PascalCase. This change therefore fixes NO observed bug - it removes a trap. Had a second producer
    /// ever emitted "waitingforinput", the failure would have been silent and would have eaten a red: the
    /// turn-end overlays would fire (they are case-insensitive) while this returned "unknown", rendering a
    /// session that needs the human as "Idle" with no red at all.</summary>
    private static string RawActivityColor(SessionDto s)
    {
        if (Is(s.ActivityState, "Starting") || Is(s.ActivityState, "Working")) return "blue";
        if (Is(s.ActivityState, "WaitingForInput") || Is(s.ActivityState, "WaitingForPerm")
            || Is(s.ActivityState, "Idle")) return "red";
        if (Is(s.ActivityState, "Exited")) return s.Crashed ? "error" : "grey";
        return "unknown";

        static bool Is(string? value, string name) =>
            string.Equals(value, name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the session is parked at a turn-end (WaitingForInput / WaitingForPerm), the
    /// gate the Director uses for its purple/green/auto-explain overlays.</summary>
    private static bool IsAtTurnEnd(SessionDto s) =>
        string.Equals(s.ActivityState, "WaitingForInput", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s.ActivityState, "WaitingForPerm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Issue #1177 (Phase 2): the ONE human-readable state label every client renders, computed by the
    /// Gateway from the same fold inputs as <see cref="EffectiveColor"/> (so the dot color and its label
    /// never disagree). Consolidates the label logic each client used to hand-roll.
    /// </summary>
    public static string StateLabel(SessionDto s)
    {
        // ORDER 0: WORKING. Mirrors EffectiveColor exactly - the label and the dot are folded from the
        // same inputs in the same order, so they cannot disagree. A snoozed session that starts working
        // is blue AND reads "Working"; it must never be a blue dot labelled "Snoozed".
        if (IsWorking(s)) return "Working";
        if (s.OnHold) return "Snoozed";
        // Issue #1181, Task 4: the honest phase label wins - "Uploading from phone" while the phone is still
        // sending the audio, "Transcribing" while the server turns it into text. Falls back to the blanket
        // "Transcribing" for the legacy flag / the desktop's own dictation.
        if (DictationPhaseLabel(s) is { } dictationPhase) return dictationPhase;
        if (s.Transcribing || s.IsTranscribing) return "Transcribing";
        // No "Explaining" arm: BriefingState can never be "Explaining" - see the tombstone in
        // EffectiveColor above. The label and the dot are folded from the same inputs in the same
        // order, so this deletion keeps them in lockstep.
        // Mirrors EffectiveColor's arms exactly, in the same order, so the label and the dot cannot
        // disagree. "Wingman reading" is the DIRECTOR's briefing; "Preparing voice" is the GATEWAY's voice
        // generation. The Gateway's old BriefingState overwrite made a voice-generating session take the
        // first arm and read "Wingman reading" - the wrong words, on top of a destroyed fact (gap 5).
        if (IsBriefing(s)) return "Wingman reading";
        if (IsVoicePreparing(s)) return VoiceHoldLabel(s);
        return BaseColor(s) switch
        {
            // "supporting" now means ONLY a Worker whose red was suppressed (see BaseColor). A working
            // controlled sub-agent reads "Working" like any other working session.
            "supporting" => "Sub-agent",
            "purple" => "Background",
            "green" => "Ready",
            "yellow" => "Wingman reading",   // Director auto-explain base yellow
            "blue" => "Working",
            "red" => "Needs you",
            "grey" => "Exited",              // Phase 2.3: an exited session's grey base (see RawActivityColor)
            "error" => "Crashed",            // issue #959: died, not finished - never reads as a clean "Exited"
            _ => "Idle",
        };
    }

    /// <summary>
    /// The WORDS for a session the voice hold is keeping yellow - "Preparing voice" only when something is
    /// genuinely being prepared, and the honest reason otherwise.
    ///
    /// WHY THIS IS NOT A SECOND RULE. <see cref="IsVoicePreparing"/> - the COLOR - reads two booleans, and
    /// deliberately so: yellow is held across the gaps between attempts and must never flash red (owner's
    /// ruling, 2026-07-19, see that method). But those two booleans are also the ONLY thing the label had,
    /// so every reason a session has no audio came out as one sentence claiming work was in flight. On
    /// 11 August a session sat on "Preparing voice" for 48 minutes while the Gateway's OWN verdict for it,
    /// on the same row, was "Nothing to read aloud" - a state that would never become audio. Another row
    /// carried the label "Preparing voice" beside a "No voice" chip, because the chip reads a fact the label
    /// could not see. See issue #2576.
    ///
    /// The verdict already exists and is already on the row: <see cref="SessionDto.VoiceDisplay"/>, folded
    /// by the Gateway's VoiceDisplayFold from all six voice facts. So this does not compute anything - it
    /// RENDERS the words that fold already chose, which is what keeps this from becoming a second answer to
    /// the same question. The dot is unchanged; only the words are.
    ///
    /// Deliberately narrow: it defers ONLY for the verdicts that mean "no audio, and here is why", and
    /// falls back to "Preparing voice" for everything else - including a row carrying no verdict at all
    /// (the display-push seam does not stamp one, and an older client may not send one either).
    /// </summary>
    private static string VoiceHoldLabel(SessionDto s)
    {
        var display = s.VoiceDisplay;
        if (display is null || string.IsNullOrWhiteSpace(display.Label)) return "Preparing voice";
        return display.Kind switch
        {
            // The THREE verdicts whose own words would be wrong ON THE RAIL, named explicitly:
            //  - "preparing" is the case the existing words are already right about.
            //  - "ready", "off" and "working" cannot honestly reach here at all: this arm runs only for a
            //    session the voice hold is keeping yellow, which requires voice mode, no audio, and a
            //    WAITING activity - and StateLabel has already returned "Working" above for anything
            //    actually working. So if one of them ever does arrive on a row, the safe answer is the old
            //    behaviour rather than a rail reading "Voice ready" or "Agent is working" beside a yellow
            //    dot on a session that is doing neither. ("working" was missed by the first version of this
            //    list and found in review - it is a kind VoiceDisplayFold really does emit.)
            "preparing" or "ready" or "off" or "working" => "Preparing voice",
            // EVERYTHING ELSE renders the fold's own words - including a verdict added later. The list runs
            // this way round deliberately: an allow-list of known-good kinds would mean adding a state to
            // VoiceDisplayFold and having the rail quietly keep saying "Preparing voice" about it, which is
            // a second rule wearing the shape of a default (found in review). A new state now reaches the
            // rail by being added ONCE, in the fold.
            _ => display.Label,
        };
    }

    /// <summary>
    /// THE ONE PLACE that turns "a prompt to this session did not go" into words (issue internal#811),
    /// or null when there is nothing to say. Every client renders the returned string VERBATIM - no client
    /// re-derives it, counts anything, or decides what a delivery failure means (CLAUDE.md rule 7).
    ///
    /// It speaks ONLY while the failure is UNRESOLVED - the last send threw and nothing has landed since,
    /// so the user's words are gone right now. Once a later prompt gets through, the alarm has nothing to
    /// warn about and goes quiet; the COUNTS stay on the row, because "this happened four times today" is a
    /// fact worth keeping and a lucky retry must not erase it.
    ///
    /// Why this is a notice and not a colour: the session's colour says what the AGENT is doing, and the
    /// agent is doing exactly what it was doing before - it never heard anything. Recolouring it would say
    /// something false about the agent to say something true about the delivery. The notice says the true
    /// thing in its own words.
    /// </summary>
    public static string? PromptDeliveryNotice(SessionDto s)
    {
        if (!s.PromptDeliveryUnresolved) return null;

        var reason = (s.LastPromptDeliveryFailureReason ?? "").Trim();
        return reason.Length == 0
            ? "Your last prompt was not delivered - the agent never received it."
            : $"Your last prompt was not delivered - the agent never received it. {reason}";
    }

    /// <summary>
    /// Classify a session for triage, folded in the same order as <see cref="EffectiveColor"/> and
    /// <see cref="StateLabel"/> so all three always agree.
    ///
    /// ORDER 0: a WORKING session is Active. Snooze is a statement about a session that has STOPPED
    /// ("do not nag me about this when it finishes"); it cannot park a session that is running right
    /// now. This used to read <c>s.OnHold ? OnHold</c> first, which sank a working session into the
    /// parked bucket at the bottom of the roster while its dot was blue - the colour said working, the
    /// list said parked. Snooze still wins for a session that is NOT working, which is the case it was
    /// built for and the only case it means anything in.
    ///
    /// Uses <see cref="EffectiveColor"/>, NOT the raw Director color: a session the wingman is still
    /// reading stays in Active until the brief lands, instead of flopping into NEEDS YOU mid-brief and
    /// possibly back out (issue #196).
    /// </summary>
    public static TriageBucket Classify(SessionDto s) =>
        IsWorking(s) ? TriageBucket.Active
        : s.OnHold ? TriageBucket.OnHold
        : EffectiveColor(s) == "red" ? TriageBucket.NeedsYou
        : TriageBucket.Active;

    /// <summary>All sessions in a given triage bucket, in desktop order.</summary>
    public static IReadOnlyList<SessionDto> InBucket(IEnumerable<SessionDto> sessions, TriageBucket bucket) =>
        InDesktopOrder(sessions.Where(s => Classify(s) == bucket));

    /// <summary>
    /// The display label for the "(no repo)" group: sessions whose <see cref="SessionDto.RepoPath"/>
    /// is empty (and that carry no <see cref="SessionDto.RemoteRepo"/>). Rendered last in the
    /// by-repo view (issue #219).
    /// </summary>
    public const string NoRepoGroup = "(no repo)";

    /// <summary>
    /// One repository's group in the by-repo rail view (issue #219): the display name (the repo's
    /// short name) plus its sessions in desktop order. <see cref="IsNoRepo"/> marks the trailing
    /// catch-all group for repo-less sessions.
    /// </summary>
    public sealed record RepoGroup(string Name, IReadOnlyList<SessionDto> Sessions, bool IsNoRepo);

    /// <summary>
    /// The repo-identity decision for the by-repo view (issue #219). Same repo regardless of where
    /// it is checked out: prefer the remote (<see cref="SessionDto.RemoteRepo"/>, normalized - trimmed,
    /// trailing ".git" dropped) so the SAME repo on two machines coalesces under one header; fall back
    /// to the leaf folder name of <see cref="SessionDto.RepoPath"/> when there is no remote. Returns
    /// null when the session has neither (it belongs in the "(no repo)" group). The returned value is
    /// the human-facing group name; grouping is case-insensitive (see <see cref="InRepoGroups"/>).
    /// </summary>
    public static string? RepoName(SessionDto s)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));

        var remote = NormalizeRemote(s.RemoteRepo);
        if (!string.IsNullOrEmpty(remote))
            return LeafName(remote);

        if (!string.IsNullOrWhiteSpace(s.RepoPath))
            return LeafName(s.RepoPath.Trim());

        return null;
    }

    /// <summary>
    /// Group a session roster by repository for the by-repo rail view (issue #219): one group per
    /// distinct repo (case-insensitive on the <see cref="RepoName"/>), named-repo groups sorted
    /// alphabetically (case-insensitive), then a single "(no repo)" group last for sessions with no
    /// repo. Sessions within each group are in <see cref="InDesktopOrder"/> so a row holds its slot
    /// and never reshuffles when only its status color changes. Sessions for the same repo on
    /// different machines/Directors land in ONE group (the key ignores machine/Director identity).
    /// </summary>
    public static IReadOnlyList<RepoGroup> InRepoGroups(IEnumerable<SessionDto> sessions)
    {
        if (sessions is null) throw new ArgumentNullException(nameof(sessions));

        var named = sessions
            .Where(s => RepoName(s) is not null)
            // GroupBy on the case-insensitive name so "cc-director" and "CC-Director" coalesce; the
            // group's display name is the first session's RepoName (stable under desktop order).
            .GroupBy(s => RepoName(s), StringComparer.OrdinalIgnoreCase)
            .Select(g => new RepoGroup(
                RepoName(InDesktopOrder(g)[0]) ?? g.Key ?? "",
                InDesktopOrder(g),
                IsNoRepo: false))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var noRepo = InDesktopOrder(sessions.Where(s => RepoName(s) is null));
        if (noRepo.Count > 0)
            named.Add(new RepoGroup(NoRepoGroup, noRepo, IsNoRepo: true));

        return named;
    }

    /// <summary>Normalize a remote-repo slug for grouping: trim, then drop a single trailing ".git".
    /// Empty/whitespace yields "".</summary>
    private static string NormalizeRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return "";
        var trimmed = remote.Trim();
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        return trimmed;
    }

    /// <summary>The leaf segment of a repo identifier: the last non-empty part after splitting on
    /// both path separators (so "owner/repo" -> "repo" and "C:\repos\cc-director" -> "cc-director").
    /// Returns the whole input when it has no separators.</summary>
    private static string LeafName(string value)
    {
        var parts = value.Split('/', '\\');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(parts[i]))
                return parts[i];
        }
        return value;
    }
}
