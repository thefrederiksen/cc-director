using System;
using System.Linq;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="SessionOrdering"/> - the shared client-side policy the Cockpit's
/// session rail uses for desktop-stable ordering (tree view) and needs-you-first triage.
/// </summary>
public sealed class SessionOrderingTests
{
    private static SessionDto S(string id, int sortOrder = 0, string color = "blue",
        bool onHold = false, DateTime createdAt = default, string briefingState = "None") => new()
    {
        SessionId = id,
        SortOrder = sortOrder,
        StatusColor = color,
        // Issue #1177 (Phase 2): the fold now derives the base color from the RAW ActivityState, not the
        // cooked StatusColor. These cases set `color` (a cooked color) but historically omitted the raw
        // activity that color implies; supply it here as the INVERSE of ColorFromActivityState so every
        // asserted output color stays byte-identical while the fold reads raw facts. Only "red"/"blue"
        // are mapped (the colors these cases assert through the base); grey/OnHold win before the base,
        // so those are left with the default state.
        ActivityState = color switch
        {
            "red" => "WaitingForInput",
            "blue" => "Working",
            _ => "",
        },
        OnHold = onHold,
        BriefingState = briefingState,
        CreatedAt = createdAt == default ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : createdAt,
    };

    [Fact]
    public void InDesktopOrder_SortsBySortOrder_Ascending()
    {
        var sessions = new[] { S("c", 2), S("a", 0), S("b", 1) };

        var ordered = SessionOrdering.InDesktopOrder(sessions);

        Assert.Equal(new[] { "a", "b", "c" }, ordered.Select(s => s.SessionId));
    }

    [Fact]
    public void InDesktopOrder_EqualSortOrder_FallsBackToCreatedAt()
    {
        // Every session reports SortOrder 0 (e.g. a Director predating the field): the
        // CreatedAt tie-break must give a deterministic, stable order.
        var t = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);
        var sessions = new[]
        {
            S("late",  0, createdAt: t.AddMinutes(10)),
            S("early", 0, createdAt: t),
            S("mid",   0, createdAt: t.AddMinutes(5)),
        };

        var ordered = SessionOrdering.InDesktopOrder(sessions);

        Assert.Equal(new[] { "early", "mid", "late" }, ordered.Select(s => s.SessionId));
    }

    [Fact]
    public void InDesktopOrder_DoesNotMutateInput()
    {
        var sessions = new[] { S("c", 2), S("a", 0) };

        _ = SessionOrdering.InDesktopOrder(sessions);

        Assert.Equal(new[] { "c", "a" }, sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void Classify_Red_NotHeld_IsNeedsYou()
    {
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou,
            SessionOrdering.Classify(S("x", color: "red")));
    }

    [Fact]
    public void Classify_NonRed_NotHeld_IsActive()
    {
        Assert.Equal(SessionOrdering.TriageBucket.Active,
            SessionOrdering.Classify(S("x", color: "blue")));
    }

    [Fact]
    public void Classify_OnHold_TakesPrecedenceOverRed()
    {
        // A parked session sinks to the bottom even when it would otherwise be "needs you".
        Assert.Equal(SessionOrdering.TriageBucket.OnHold,
            SessionOrdering.Classify(S("x", color: "red", onHold: true)));
    }

    // ----- effective color while the wingman is reading (issue #196) -----

    [Fact]
    public void EffectiveColor_RedWhileBriefing_IsYellow()
    {
        // The Director stamps raw red at turn-end (it no longer knows about briefing,
        // #187); the Gateway stamps BriefingState=Briefing. The ONE presented color
        // must be yellow - never a red dot next to a "wingman reading..." chip.
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(S("x", color: "red", briefingState: "Briefing")));
    }

    [Fact]
    public void EffectiveColor_RedAfterBriefLands_IsRed()
    {
        Assert.Equal("red", SessionOrdering.EffectiveColor(S("x", color: "red", briefingState: "Briefed")));
    }

    [Fact]
    public void EffectiveColor_BlueWhileBriefing_StaysBlue()
    {
        // A NEW turn already running: raw activity wins, the stale in-flight brief
        // must not paint a working session yellow.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(S("x", color: "blue", briefingState: "Briefing")));
    }

    [Fact]
    public void IsBriefing_OnlyWhenBriefingAndRed()
    {
        Assert.True(SessionOrdering.IsBriefing(S("x", color: "red", briefingState: "Briefing")));
        Assert.False(SessionOrdering.IsBriefing(S("x", color: "blue", briefingState: "Briefing")));
        Assert.False(SessionOrdering.IsBriefing(S("x", color: "red", briefingState: "Briefed")));
        Assert.False(SessionOrdering.IsBriefing(S("x", color: "red", briefingState: "None")));
    }

    // ----- effective color while a user-requested deep dive runs (issue #217) -----

    [Fact]
    public void IsExplaining_AnyRawColor_TrueWhileExplaining()
    {
        // Explain is USER-initiated: the orange must show no matter what the session is
        // doing underneath (the original red gate suppressed it on working sessions and
        // left the rail contradicting the brief pane).
        Assert.True(SessionOrdering.IsExplaining(S("x", color: "blue", briefingState: "Explaining")));
        Assert.True(SessionOrdering.IsExplaining(S("x", color: "red", briefingState: "Explaining")));
        Assert.True(SessionOrdering.IsExplaining(S("x", color: "green", briefingState: "Explaining")));
        Assert.False(SessionOrdering.IsExplaining(S("x", color: "red", briefingState: "Briefing")));
        Assert.False(SessionOrdering.IsExplaining(S("x", color: "blue", briefingState: "None")));
    }

    [Fact]
    public void EffectiveColor_WhileExplaining_IsOrange_ONLYWhenNotWorking()
    {
        // Renamed and flipped, 2026-07-14. This test used to be called
        // "..._IsOrange_EvenWhenWorking" and asserted that a deep dive paints a WORKING session
        // orange. That is the old law, and the owner has voided it: working is BLUE, always.
        //
        // Keep reading, because this is the whole reason the bug kept coming back. The old
        // assertion was not an accident - it was deliberate, named after its own intent, and
        // green. An agent sent to make working blue would flip the code, watch THIS test fail,
        // conclude it had misunderstood the requirement, and revert. The test defended the defect.
        Assert.Equal("orange", SessionOrdering.EffectiveColor(S("x", color: "red", briefingState: "Explaining")));

        // ... and the working case, which is now the law:
        Assert.Equal("blue", SessionOrdering.EffectiveColor(S("x", color: "blue", briefingState: "Explaining")));
    }

    // ---------- Transcribing "orange while a dictated utterance is being transcribed" ----------

    [Fact]
    public void EffectiveColor_WhileTranscribing_IsOrange_RegardlessOfRawColor()
    {
        // The phone released the Speak dialog and the audio is uploading/transcribing in the
        // background: orange no matter what the session is doing underneath, so nobody else grabs it.
        Assert.Equal("orange", SessionOrdering.EffectiveColor(new SessionDto { SessionId = "x", StatusColor = "blue", Transcribing = true }));
        Assert.Equal("orange", SessionOrdering.EffectiveColor(new SessionDto { SessionId = "x", StatusColor = "green", Transcribing = true }));
    }

    [Fact]
    public void EffectiveColor_OnHold_WinsOverTranscribing()
    {
        // A parked session stays grey; transcribing does not override the user's explicit hold.
        Assert.Equal("grey", SessionOrdering.EffectiveColor(new SessionDto { SessionId = "x", StatusColor = "blue", OnHold = true, Transcribing = true }));
    }

    [Fact]
    public void Classify_Transcribing_IsActive_NotNeedsYou()
    {
        // Orange is not red, so a transcribing session sits in Active - it must never jump into the
        // needs-you group just because it is busy transcribing.
        Assert.Equal(SessionOrdering.TriageBucket.Active,
            SessionOrdering.Classify(new SessionDto { SessionId = "x", StatusColor = "red", Transcribing = true }));
    }

    // ---------- Voice-mode "yellow until audio ready" (issue #553) ----------

    private static SessionDto Voice(string color, string activityState, bool generating, bool audioReady) => new()
    {
        SessionId = "v",
        StatusColor = color,
        ActivityState = activityState,
        VoiceMode = true,
        VoiceGenerating = generating,
        VoiceAudioReady = audioReady,
    };

    [Fact]
    public void EffectiveColor_VoiceWaiting_NoAudio_NotGenerating_IsRed_NotStuckYellow()
    {
        // Regression (2026-07-08): a text-to-speech failure (DeepInfra 504/timeout) produces no
        // audio, so a voice-mode waiting session ends with audioReady=false and nothing generating.
        // This must resolve to red "needs you" - NOT the old permanent yellow/orange wedge that had
        // no exit when audio never arrived. The hold is now gated on VoiceGenerating alone.
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Voice("red", "WaitingForInput", generating: false, audioReady: false)));
    }

    [Fact]
    public void EffectiveColor_VoiceWaiting_Generating_IsYellow_EvenWithStaleAudio()
    {
        // A new turn is being summarized: yellow regardless of any stale cached audio.
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(
            Voice("red", "WaitingForPerm", generating: true, audioReady: true)));
    }

    [Fact]
    public void EffectiveColor_VoiceWaiting_AudioReady_IsRed()
    {
        // Playable audio exists and nothing is generating -> red "needs you".
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Voice("red", "WaitingForInput", generating: false, audioReady: true)));
    }

    [Fact]
    public void EffectiveColor_VoiceWorking_StaysBlue()
    {
        // While the agent is working the dot stays blue even in voice mode with no audio.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(
            Voice("blue", "Working", generating: true, audioReady: false)));
    }

    [Fact]
    public void EffectiveColor_NonVoiceWaiting_NoAudio_StaysRed()
    {
        // A non-voice session is untouched by the voice rule: waiting + red stays red.
        var s = Voice("red", "WaitingForInput", generating: true, audioReady: false);
        s.VoiceMode = false;
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
    }

    [Fact]
    public void Classify_VoiceWaiting_StillGenerating_IsActive_NotNeedsYou()
    {
        // While voice is ACTIVELY generating the session must not sit in NEEDS YOU (it is preparing).
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(
            Voice("red", "WaitingForInput", generating: true, audioReady: false)));
    }

    [Fact]
    public void Classify_VoiceWaiting_NoAudio_NotGenerating_IsNeedsYou()
    {
        // Regression (2026-07-08): once generation has ended with no audio (text-to-speech failed),
        // the session genuinely needs the user - it must surface in NEEDS YOU, not hide in Active
        // behind a stuck "preparing voice" state forever.
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(
            Voice("red", "WaitingForInput", generating: false, audioReady: false)));
    }

    [Fact]
    public void Classify_RedWhileExplaining_IsActive_NotNeedsYou()
    {
        // Same #196 rule as briefing: while the deep dive runs the session must not sit
        // in NEEDS YOU - red may only return after the report lands.
        Assert.Equal(SessionOrdering.TriageBucket.Active,
            SessionOrdering.Classify(S("x", color: "red", briefingState: "Explaining")));
    }

    [Fact]
    public void Classify_RedWhileBriefing_IsActive_NotNeedsYou()
    {
        // The triage regression in issue #196: a mid-brief session must NOT enter the
        // NEEDS YOU bucket (and then flop back out when the brief lands or refutes).
        Assert.Equal(SessionOrdering.TriageBucket.Active,
            SessionOrdering.Classify(S("x", color: "red", briefingState: "Briefing")));
    }

    [Fact]
    public void Classify_RedAfterBriefLands_IsNeedsYou()
    {
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou,
            SessionOrdering.Classify(S("x", color: "red", briefingState: "Briefed")));
    }

    [Fact]
    public void Classify_OnHold_TakesPrecedenceOverBriefing()
    {
        Assert.Equal(SessionOrdering.TriageBucket.OnHold,
            SessionOrdering.Classify(S("x", color: "red", briefingState: "Briefing", onHold: true)));
    }

    [Fact]
    public void InBucket_FiltersToBucket_AndKeepsDesktopOrder()
    {
        var sessions = new[]
        {
            S("active1", sortOrder: 1, color: "blue"),
            S("needs1",  sortOrder: 3, color: "red"),
            S("held1",   sortOrder: 0, color: "red", onHold: true),
            S("needs0",  sortOrder: 2, color: "red"),
        };

        var needs = SessionOrdering.InBucket(sessions, SessionOrdering.TriageBucket.NeedsYou);
        var active = SessionOrdering.InBucket(sessions, SessionOrdering.TriageBucket.OnHold);

        // Only the two non-held red sessions, in SortOrder order (2 before 3).
        Assert.Equal(new[] { "needs0", "needs1" }, needs.Select(s => s.SessionId));
        // The held-red session lands in OnHold, not NeedsYou.
        Assert.Equal(new[] { "held1" }, active.Select(s => s.SessionId));
    }

    // ===== Issue #1177 (Phase 2): the Gateway fold now computes EVERY color from RAW facts (migrated
    // from SessionStatusWingmanTests, rebuilt against the raw-fact inputs the Director reports). =====

    /// <summary>A session built from the RAW facts the Director reports (no cooked StatusColor).</summary>
    private static SessionDto Raw(string activityState, bool wingmanEnabled = false, bool brandNew = false,
        bool backgroundRunning = false, bool controlled = false, string? controllerId = null,
        bool transcribing = false, bool autoExplaining = false, string briefingState = "None",
        string? sessionRole = null) => new()
    {
        SessionId = "raw",
        ActivityState = activityState,
        WingmanEnabled = wingmanEnabled,
        IsBrandNew = brandNew,
        IsBackgroundRunning = backgroundRunning,
        IsControlled = controlled,
        ControllerSessionId = controllerId,
        IsTranscribing = transcribing,
        IsAutoExplaining = autoExplaining,
        BriefingState = briefingState,
        SessionRole = sessionRole,
        // StatusColor deliberately left at its default "unknown" to PROVE the fold never reads it.
    };

    [Fact]
    public void EffectiveColor_Working_IsBlue_FromRawFacts()
    {
        Assert.Equal("blue", SessionOrdering.EffectiveColor(Raw("Working")));
    }

    [Fact]
    public void EffectiveColor_Waiting_IsRed_FromRawFacts()
    {
        Assert.Equal("red", SessionOrdering.EffectiveColor(Raw("WaitingForInput")));
        Assert.Equal("red", SessionOrdering.EffectiveColor(Raw("WaitingForPerm")));
        Assert.Equal("red", SessionOrdering.EffectiveColor(Raw("Idle")));
    }

    [Fact]
    public void EffectiveColor_Exited_IsGrey()
    {
        // Phase 2.3 (owner-approved behavior change): an exited session shows the SAME grey string as an
        // OnHold session, so clients render the two identically. No longer byte-identical for exited.
        Assert.Equal("grey", SessionOrdering.EffectiveColor(Raw("Exited")));
    }

    [Fact]
    public void EffectiveColor_BrandNewAtTurnEnd_IsGreen_FromRawFacts()
    {
        // A brand-new session sitting at its prompt is "ready" (green), not red "needs you".
        Assert.Equal("green", SessionOrdering.EffectiveColor(Raw("WaitingForInput", brandNew: true)));
    }

    [Fact]
    public void EffectiveColor_BrandNewWhileWorking_IsBlue_NotGreen()
    {
        // Green only applies at a turn-end; a brand-new session that is working is blue.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(Raw("Working", brandNew: true)));
    }

    [Fact]
    public void EffectiveColor_BackgroundRunningAtTurnEnd_IsPurple_FromRawFacts()
    {
        Assert.Equal("purple", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", wingmanEnabled: true, backgroundRunning: true)));
    }

    [Fact]
    public void EffectiveColor_BackgroundRunning_NoWingman_IsRed_NotPurple()
    {
        // Purple is gated on WingmanEnabled (matching the Director) - without it, the base red shows.
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", wingmanEnabled: false, backgroundRunning: true)));
    }

    [Fact]
    public void EffectiveColor_ControlledAndWorking_IsBlue_NothingOutranksWorking()
    {
        // Owner's ruling, 2026-07-14: if a session is working, it is BLUE - no matter what. Being driven
        // by another session is NOT a reason to hide that it is working. This REPLACES the 2026-07-10
        // decision in issue #1286 (a controlled session that was not red returned "supporting", which threw
        // the real activity state away and painted a busy sub-agent gray "Sub-agent"). That rule is void.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(
            Raw("Working", controlled: true, controllerId: Guid.NewGuid().ToString())));
    }

    [Fact]
    public void EffectiveColor_ControlledWorker_WhileWorking_IsStillBlue()
    {
        // The exact shape that failed: a controlled sub-agent with the Worker role, mid-turn. Red
        // suppression must not leak into a working session - only a Worker's RED recedes.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(
            Raw("Working", controlled: true, controllerId: Guid.NewGuid().ToString(), sessionRole: "Worker")));
    }

    [Fact]
    public void EffectiveColor_ControlledButRed_BreaksThroughSupporting()
    {
        // Red "needs you" breaks through the slate overlay so a blocked sub-agent still surfaces.
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", controlled: true, controllerId: Guid.NewGuid().ToString())));
    }

    [Fact]
    public void EffectiveColor_LiveWorker_SuppressesRed_RecedesToSupporting()
    {
        // Automatic roles (Layer 1): a LIVE-controlled Worker's red is SUPPRESSED - it recedes to slate and
        // never nags the human (its manager sees it via the rail). The aggregation stamps SessionRole=Worker
        // only when the controller is alive, so red suppression is exactly "live worker".
        Assert.Equal("supporting", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", controlled: true, controllerId: Guid.NewGuid().ToString(), sessionRole: "Worker")));
    }

    [Fact]
    public void EffectiveColor_ManagerRed_IsAllowed_HumanFacing()
    {
        // A Manager is human-facing: its red always surfaces.
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", sessionRole: "Manager")));
    }

    [Fact]
    public void EffectiveColor_StandaloneRed_IsAllowed_HumanFacing()
    {
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", sessionRole: "Standalone")));
    }

    [Fact]
    public void EffectiveColor_DeadControllerWorkerRed_IsAllowed_EscapeHatch()
    {
        // A Worker whose controller has DIED is role Standalone (not "Worker"), so its red is NOT suppressed
        // and surfaces to the human - the escape hatch, so a stranded worker is never lost. The controller
        // id may still be on the DTO, but the role (not the id) drives the suppression.
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", controlled: true, controllerId: Guid.NewGuid().ToString(), sessionRole: "Standalone")));
    }

    [Fact]
    public void EffectiveColor_Architect_RedAllowed_EvenWithAController()
    {
        // Chunk 2.5: an explicit Architect is human-facing. Even one that happens to carry a controller keeps
        // its red - the aggregation's explicit-wins precedence resolves it to Architect (not Worker), so the
        // fold (which only suppresses Worker) never suppresses it.
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", controlled: true, controllerId: Guid.NewGuid().ToString(), sessionRole: SessionRoles.Architect)));
    }

    [Fact]
    public void EffectiveColor_ControlledWithNoControllerId_IsNotSupporting()
    {
        // Without a controller id present the slate overlay does not apply (it paints its normal color).
        Assert.Equal("blue", SessionOrdering.EffectiveColor(
            Raw("Working", controlled: true, controllerId: null)));
    }

    [Fact]
    public void EffectiveColor_DesktopTranscribing_IsOrange_WhenNotWorking()
    {
        // Issue #1177 audit: the desktop-dictation transcribing fact (Session.IsTranscribing) paints
        // orange exactly as the mobile Speak flag does. Gated on NOT working since 2026-07-14 - orange
        // marks "dictation in flight, do not grab this session", which is a statement about a session
        // sitting at a prompt. A working session is blue.
        Assert.Equal("orange", SessionOrdering.EffectiveColor(Raw("WaitingForInput", transcribing: true)));
        Assert.Equal("blue", SessionOrdering.EffectiveColor(Raw("Working", transcribing: true)));
    }

    [Fact]
    public void EffectiveColor_AutoExplainingAtTurnEnd_IsYellow_FromRawFact()
    {
        // Newly covered (issue #1177 audit): the legacy auto-explain (ProactiveExplainService,
        // Session.IsExplaining) must paint yellow while WingmanEnabled and at a turn-end - previously this
        // survived ONLY via the cooked StatusColor fall-through and was untested. Distinct from the Gateway
        // deep-dive overlay (BriefingState=="Explaining"), which is ORANGE.
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", wingmanEnabled: true, autoExplaining: true)));
    }

    [Fact]
    public void EffectiveColor_AutoExplaining_NoWingman_IsRed_NotYellow()
    {
        // Auto-explain yellow is gated on WingmanEnabled (matching the Director) - without it, base red shows.
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", wingmanEnabled: false, autoExplaining: true)));
    }

    [Fact]
    public void EffectiveColor_GatewayDeepDiveExplaining_IsOrange_NotYellow()
    {
        // The Gateway user-initiated deep dive (BriefingState=="Explaining", issue #217) stays ORANGE,
        // distinct from the Director auto-explain yellow above.
        Assert.Equal("orange", SessionOrdering.EffectiveColor(Raw("WaitingForInput", briefingState: "Explaining")));
    }

    // ----- StateLabel: one per color / overlay (issue #1177, Phase 2) -----

    [Fact]
    public void StateLabel_OnHold_IsSnoozed()
    {
        var s = Raw("WaitingForInput");
        s.OnHold = true;
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void StateLabel_Transcribing_IsTranscribing_WhenNotWorking()
    {
        // Gated on NOT working since 2026-07-14, mirroring EffectiveColor so the dot and the label
        // are folded from the same inputs in the same order and cannot disagree.
        Assert.Equal("Transcribing", SessionOrdering.StateLabel(Raw("WaitingForInput", transcribing: true)));
        var mobile = Raw("WaitingForInput");
        mobile.Transcribing = true;
        Assert.Equal("Transcribing", SessionOrdering.StateLabel(mobile));

        // A working session reads "Working", whatever else is in flight.
        Assert.Equal("Working", SessionOrdering.StateLabel(Raw("Working", transcribing: true)));
    }

    [Fact]
    public void StateLabel_GatewayDeepDive_IsExplaining()
    {
        Assert.Equal("Explaining", SessionOrdering.StateLabel(Raw("WaitingForInput", briefingState: "Explaining")));
    }

    [Fact]
    public void StateLabel_Briefing_IsWingmanReading()
    {
        Assert.Equal("Wingman reading", SessionOrdering.StateLabel(Raw("WaitingForInput", briefingState: "Briefing")));
    }

    [Fact]
    public void StateLabel_AutoExplain_IsWingmanReading()
    {
        Assert.Equal("Wingman reading", SessionOrdering.StateLabel(
            Raw("WaitingForInput", wingmanEnabled: true, autoExplaining: true)));
    }

    [Fact]
    public void StateLabel_VoicePreparing_IsPreparingVoice()
    {
        var s = Raw("WaitingForInput");
        s.VoiceMode = true;
        s.VoiceGenerating = true;
        Assert.Equal("Preparing voice", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void StateLabel_BackgroundRunning_IsBackground()
    {
        Assert.Equal("Background", SessionOrdering.StateLabel(
            Raw("WaitingForInput", wingmanEnabled: true, backgroundRunning: true)));
    }

    [Fact]
    public void StateLabel_ControlledAndWorking_ReadsWorking_NotSubAgent()
    {
        // The label must agree with the blue dot: a controlled sub-agent that is working reads "Working"
        // like any other working session. It used to read "Sub-agent" (the void issue #1286 rule), which
        // is what let a session 23 minutes into real work look parked.
        Assert.Equal("Working", SessionOrdering.StateLabel(
            Raw("Working", controlled: true, controllerId: Guid.NewGuid().ToString())));
    }

    [Fact]
    public void StateLabel_LiveWorker_WhoseRedIsSuppressed_IsSubAgent()
    {
        // "Sub-agent" survives for the ONE case that still recedes: a live Worker whose red is suppressed
        // so the need routes to its manager rather than the human.
        Assert.Equal("Sub-agent", SessionOrdering.StateLabel(
            Raw("WaitingForInput", controlled: true, controllerId: Guid.NewGuid().ToString(), sessionRole: "Worker")));
    }

    [Fact]
    public void StateLabel_BrandNew_IsReady()
    {
        Assert.Equal("Ready", SessionOrdering.StateLabel(Raw("WaitingForInput", brandNew: true)));
    }

    [Fact]
    public void StateLabel_Working_IsWorking()
    {
        Assert.Equal("Working", SessionOrdering.StateLabel(Raw("Working")));
    }

    [Fact]
    public void StateLabel_Waiting_IsNeedsYou()
    {
        Assert.Equal("Needs you", SessionOrdering.StateLabel(Raw("WaitingForInput")));
    }

    [Fact]
    public void StateLabel_Exited_IsExited()
    {
        Assert.Equal("Exited", SessionOrdering.StateLabel(Raw("Exited")));
    }

    // ===== by-repo grouping (issue #219) =====

    /// <summary>Builds a session with the fields the repo grouping reads. RemoteRepo wins over
    /// RepoPath when present; MachineName/DirectorId are carried to prove they do NOT affect the
    /// group key.</summary>
    private static SessionDto R(string id, string repoPath = "", string remoteRepo = "",
        int sortOrder = 0, string machine = "", string directorId = "") => new()
    {
        SessionId = id,
        RepoPath = repoPath,
        RemoteRepo = remoteRepo,
        SortOrder = sortOrder,
        MachineName = machine,
        DirectorId = directorId,
        StatusColor = "blue",
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void RepoName_PrefersNormalizedRemote_LeafCaseInsensitiveDotGitStripped()
    {
        Assert.Equal("devthrottle", SessionOrdering.RepoName(R("x", remoteRepo: "example-org/devthrottle.git")));
        Assert.Equal("devthrottle", SessionOrdering.RepoName(R("x", remoteRepo: "  example-org/devthrottle  ")));
    }

    [Fact]
    public void RepoName_FallsBackToRepoPathLeaf_WhenNoRemote()
    {
        Assert.Equal("cc-director", SessionOrdering.RepoName(R("x", repoPath: @"C:\repos\cc-director")));
        Assert.Equal("cc-director", SessionOrdering.RepoName(R("x", repoPath: "/home/user/src/cc-director/")));
    }

    [Fact]
    public void RepoName_NoRemoteNoPath_IsNull()
    {
        Assert.Null(SessionOrdering.RepoName(R("x")));
    }

    [Fact]
    public void InRepoGroups_HeadersAreAlphabetical_CaseInsensitive()
    {
        var sessions = new[]
        {
            R("z", repoPath: @"D:\zebra"),
            R("a", repoPath: @"D:\Apple"),
            R("b", repoPath: @"D:\banana"),
        };

        var groups = SessionOrdering.InRepoGroups(sessions);

        // "Apple" sorts before "banana" before "zebra" ignoring case.
        Assert.Equal(new[] { "Apple", "banana", "zebra" }, groups.Select(g => g.Name));
    }

    [Fact]
    public void InRepoGroups_NoRepoGroup_IsPlacedLast()
    {
        var sessions = new[]
        {
            R("none", repoPath: ""),
            R("named", repoPath: @"D:\alpha"),
        };

        var groups = SessionOrdering.InRepoGroups(sessions);

        Assert.Equal(2, groups.Count);
        Assert.Equal("alpha", groups[0].Name);
        Assert.False(groups[0].IsNoRepo);
        Assert.Equal(SessionOrdering.NoRepoGroup, groups[^1].Name);
        Assert.True(groups[^1].IsNoRepo);
    }

    [Fact]
    public void InRepoGroups_WithinGroup_UsesDesktopOrder()
    {
        // Two sessions in the same repo; lower SortOrder must render first regardless of input order.
        var sessions = new[]
        {
            R("second", repoPath: @"D:\repo", sortOrder: 2),
            R("first",  repoPath: @"D:\repo", sortOrder: 1),
        };

        var groups = SessionOrdering.InRepoGroups(sessions);

        var repo = Assert.Single(groups);
        Assert.Equal(new[] { "first", "second" }, repo.Sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void InRepoGroups_SameRepoAcrossMachines_CoalescesIntoOneGroup()
    {
        // Same repo (same RemoteRepo) on two different machines / Directors must land under ONE header.
        var sessions = new[]
        {
            R("onA", remoteRepo: "example-org/devthrottle.git", machine: "MACHINE_A", directorId: "dirA"),
            R("onB", remoteRepo: "example-org/devthrottle",     machine: "MACHINE_B", directorId: "dirB"),
        };

        var groups = SessionOrdering.InRepoGroups(sessions);

        var repo = Assert.Single(groups);
        Assert.Equal("devthrottle", repo.Name);
        Assert.Equal(new[] { "onA", "onB" }, repo.Sessions.Select(s => s.SessionId).OrderBy(x => x));
    }

    [Fact]
    public void InRepoGroups_DoesNotMutateInput()
    {
        var sessions = new[]
        {
            R("b", repoPath: @"D:\repo", sortOrder: 2),
            R("a", repoPath: @"D:\repo", sortOrder: 1),
        };

        _ = SessionOrdering.InRepoGroups(sessions);

        // Original array order preserved (grouping snapshots, never sorts in place).
        Assert.Equal(new[] { "b", "a" }, sessions.Select(s => s.SessionId));
    }

    // ===================================================================================
    // THE LAW: a working session is BLUE, always. Nothing outranks working.
    // (Owner's ruling, 2026-07-14. See docs/new_architecture/session-state.html.)
    //
    // These tests exist to make the law UNBREAKABLE. Every colour that has ever been put
    // above working in the ladder gets its own case below. If you are here because one of
    // these went red, you have re-introduced a defect the owner has personally reported
    // more than once - do not "fix" the test. Fix the code, or the law changed and the
    // owner said so out loud.
    // ===================================================================================

    /// <summary>Every overlay that has ever outranked working, asserted one at a time.</summary>
    public static TheoryData<string, SessionDto> OverlaysThatMustNotBeatWorking() => new()
    {
        { "snoozed (OnHold)",        new SessionDto { SessionId = "w", ActivityState = "Working", OnHold = true } },
        { "mobile dictation phase",  new SessionDto { SessionId = "w", ActivityState = "Working", DictationStatus = "Uploading from phone" } },
        { "gateway transcribing",    new SessionDto { SessionId = "w", ActivityState = "Working", Transcribing = true } },
        { "director transcribing",   new SessionDto { SessionId = "w", ActivityState = "Working", IsTranscribing = true } },
        { "explaining (deep dive)",  new SessionDto { SessionId = "w", ActivityState = "Working", BriefingState = "Explaining" } },
        { "wingman briefing",        new SessionDto { SessionId = "w", ActivityState = "Working", BriefingState = "Briefing" } },
        { "voice preparing",         new SessionDto { SessionId = "w", ActivityState = "Working", VoiceMode = true, VoiceGenerating = true } },
        { "controlled sub-agent",    new SessionDto { SessionId = "w", ActivityState = "Working", SessionRole = SessionRoles.Worker, ControllerSessionId = "mgr" } },
    };

    [Theory]
    [MemberData(nameof(OverlaysThatMustNotBeatWorking))]
    public void EffectiveColor_Working_IsAlwaysBlue_NoMatterWhatElseIsTrue(string overlay, SessionDto s)
    {
        var actual = SessionOrdering.EffectiveColor(s);
        Assert.True(actual == "blue",
            $"A WORKING session rendered '{actual}' because {overlay} was true. Working is BLUE, always - " +
            $"nothing outranks it. Remove the rule you put above the working check in EffectiveColor.");
    }

    [Theory]
    [MemberData(nameof(OverlaysThatMustNotBeatWorking))]
    public void StateLabel_Working_AlwaysReadsWorking_NoMatterWhatElseIsTrue(string overlay, SessionDto s)
    {
        // The dot and its label are folded from the same inputs in the same order: a blue dot
        // labelled "Snoozed" is the contradiction this pins shut.
        var actual = SessionOrdering.StateLabel(s);
        Assert.True(actual == "Working",
            $"A WORKING session was labelled '{actual}' because {overlay} was true. The label must " +
            $"match the dot: a working session is blue and reads 'Working'.");
    }

    [Theory]
    [MemberData(nameof(OverlaysThatMustNotBeatWorking))]
    public void Classify_Working_IsAlwaysActive_NeverParked(string overlay, SessionDto s)
    {
        // A working session never sinks into the parked bucket at the bottom of the roster.
        var actual = SessionOrdering.Classify(s);
        Assert.True(actual == SessionOrdering.TriageBucket.Active,
            $"A WORKING session was triaged '{actual}' because {overlay} was true. A running session " +
            $"is Active - it cannot be parked while it is working.");
    }

    [Fact]
    public void EffectiveColor_EverySignalAtOnce_StillBlue()
    {
        // The pathological case: every overlay in the ladder is true at the same instant, and the
        // session is working. Working still wins. "Nothing outranks working" means nothing.
        var s = new SessionDto
        {
            SessionId = "w",
            ActivityState = "Working",
            OnHold = true,
            DictationStatus = "Transcribing",
            Transcribing = true,
            IsTranscribing = true,
            BriefingState = "Explaining",
            VoiceMode = true,
            VoiceGenerating = true,
            WingmanEnabled = true,
            IsAutoExplaining = true,
            IsBackgroundRunning = true,
            IsBrandNew = true,
            SessionRole = SessionRoles.Worker,
            ControllerSessionId = "mgr",
        };

        Assert.Equal("blue", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Working", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(s));
    }

    [Fact]
    public void EffectiveColor_Starting_IsAlsoBlue()
    {
        // "Starting" is the sensor's first working byte - the session is running, so it is blue.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(
            new SessionDto { SessionId = "s", ActivityState = "Starting", OnHold = true }));
    }

    [Fact]
    public void EffectiveColor_WorkingIsCaseInsensitive()
    {
        // Defect 16: RawActivityColor was a case-sensitive switch while the role rules compared
        // case-insensitively on the same field. The working check must never be the fold's weak link.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(
            new SessionDto { SessionId = "w", ActivityState = "working", OnHold = true }));
    }

    // ===== The other half of the law: the overlays STILL work for a session that has stopped. =====
    // Hoisting working to the top must not weaken any rule below it - each one keeps its meaning
    // for the case it was actually built for, which is a session that is NOT running.

    [Fact]
    public void EffectiveColor_NotWorking_SnoozeStillWins()
    {
        Assert.Equal("grey", SessionOrdering.EffectiveColor(
            new SessionDto { SessionId = "q", ActivityState = "WaitingForInput", OnHold = true }));
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(
            new SessionDto { SessionId = "q", ActivityState = "WaitingForInput", OnHold = true }));
    }

    [Fact]
    public void EffectiveColor_NotWorking_TranscribingStillOrange()
    {
        // The case orange exists for: dictation in flight at a PROMPT, so nobody else grabs it.
        Assert.Equal("orange", SessionOrdering.EffectiveColor(
            new SessionDto { SessionId = "q", ActivityState = "WaitingForInput", Transcribing = true }));
    }

    [Fact]
    public void Classify_NotWorking_SnoozedRedStillSinksToOnHold()
    {
        // The deferral the snooze was built for: a red session the user parked stays parked.
        Assert.Equal(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(
            new SessionDto { SessionId = "q", ActivityState = "WaitingForInput", OnHold = true }));
    }
}
