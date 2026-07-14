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
        s.BriefingState == "Briefing" && RawActivityColor(s) == "red";

    /// <summary>
    /// True while a user-initiated "I am lost - explain" deep dive runs for the session
    /// (issue #217). Unlike <see cref="IsBriefing"/> there is NO raw-activity gate: the
    /// user pressed the button just now, so the orange must show regardless of whether
    /// the session is working, quiet, or red - suppressing it (the original red-gated
    /// implementation) left the rail blue while the brief pane said "explaining", the
    /// exact cross-surface contradiction issue #196 forbids.
    /// </summary>
    public static bool IsExplaining(SessionDto s) =>
        s.BriefingState == "Explaining";

    /// <summary>
    /// Issue #553: true while a VOICE-MODE waiting session is ACTIVELY generating its spoken summary,
    /// so the roster holds yellow ("preparing voice") rather than flashing red mid-generation. Once
    /// generation ends the session shows its real color - red "needs you", with the roster play
    /// triangle appearing separately when <see cref="SessionDto.VoiceAudioReady"/> is true.
    ///
    /// This used to ALSO hold yellow whenever audio was not yet ready (<c>|| !VoiceAudioReady</c>),
    /// on the assumption that audio always eventually arrives. It does not: a text-to-speech
    /// failure (a DevThrottle/DeepInfra 504 or timeout) produces NO audio, so <c>VoiceAudioReady</c>
    /// stayed false with nothing generating - and the session was stuck yellow/orange FOREVER while
    /// it actually needed the user (the "stuck orange, says needs you" report, 2026-07-08). Gating
    /// the hold on <see cref="SessionDto.VoiceGenerating"/> alone gives a terminal exit: when a turn's
    /// voice fails, the session correctly becomes red/needs-you instead of a permanent wedge.
    ///
    /// Gated on raw red and on WaitingForInput/WaitingForPerm so a working (blue) session is untouched.
    /// </summary>
    public static bool IsVoicePreparing(SessionDto s)
    {
        if (!s.VoiceMode) return false;
        // Issue #1177 (Phase 2): gate on the RAW activity color (from ActivityState), not the Director's
        // cooked StatusColor. Equivalent today (StatusColor=="red" iff the raw activity is Waiting/Idle).
        if (RawActivityColor(s) != "red") return false;
        var state = s.AssessedState ?? s.ActivityState;
        var waiting = string.Equals(state, "WaitingForInput", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(state, "WaitingForPerm", StringComparison.OrdinalIgnoreCase);
        if (!waiting) return false;
        return s.VoiceGenerating;
    }

    /// <summary>
    /// The ONE effective status color every client renders and triages on (issue #196).
    /// The Director stamps the raw <see cref="SessionDto.StatusColor"/> (it no longer knows
    /// about briefing since #187 moved the pipeline to the Gateway), and the Gateway stamps
    /// <see cref="SessionDto.BriefingState"/> on top. Folding the two HERE - instead of in
    /// each view - is what keeps the dot, the "wingman reading..." chip, and the triage
    /// bucket atomic: while the wingman reads a finished turn the session IS yellow; while
    /// a user-requested deep dive runs it IS orange (issue #217); red ("needs you") may
    /// only appear after the brief or report lands. Issue #553: a voice-mode session also
    /// holds yellow until its playable audio exists (<see cref="IsVoicePreparing"/>). A session
    /// whose dictated utterance is being transcribed in the background
    /// (<see cref="SessionDto.Transcribing"/>) shows orange ("Transcribing...") so no one else grabs it.
    /// </summary>
    public static string EffectiveColor(SessionDto s) =>
        s.OnHold ? "grey"
        // Transcribing orange fires for ANY dictation source: the Task 4 phase label (mobile Speak -
        // "Uploading from phone" or "Transcribing", s.DictationStatus), the legacy Gateway flag
        // (s.Transcribing), OR the Director raw fact (desktop dictation, s.IsTranscribing). All orange.
        : (s.DictationStatus != null || s.Transcribing || s.IsTranscribing) ? "orange"
        // The Gateway user-initiated deep dive (issue #217) is orange, ungated.
        : IsExplaining(s) ? "orange"
        : IsBriefing(s) ? "yellow"
        : IsVoicePreparing(s) ? "yellow"
        // Issue #1177 (Phase 2): the base color is now computed from RAW facts (the Gateway is the single
        // fold and reads the Director's cooked StatusColor for NOTHING).
        : BaseColor(s);

    // ===== Issue #1177 (Phase 2): the raw-fact base color (a port of the Director's SessionStatusWingman
    // ColorFor, computed from the wire's raw facts) =====

    /// <summary>
    /// The base presentation color from raw facts: the activity color with the purple (background),
    /// green (brand-new), and Director auto-explain (yellow) turn-end overlays, plus the slate
    /// "Supporting" overlay that suppresses a controlled Worker's RED. The briefing and Gateway
    /// deep-dive overlays are applied by <see cref="EffectiveColor"/> above (they win before this is
    /// reached), so they are intentionally not repeated here.
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
    /// the wire to read.</summary>
    private static string RawActivityColor(SessionDto s) => s.ActivityState switch
    {
        "Starting" => "blue",
        "Working" => "blue",
        "WaitingForInput" => "red",
        "WaitingForPerm" => "red",
        "Idle" => "red",
        "Exited" => s.Crashed ? "error" : "grey",
        _ => "unknown",
    };

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
        if (s.OnHold) return "Snoozed";
        // Issue #1181, Task 4: the honest phase label wins - "Uploading from phone" while the phone is still
        // sending the audio, "Transcribing" while the server turns it into text. Falls back to the blanket
        // "Transcribing" for the legacy flag / the desktop's own dictation.
        if (s.DictationStatus is { } dictationPhase) return dictationPhase;
        if (s.Transcribing || s.IsTranscribing) return "Transcribing";
        if (IsExplaining(s)) return "Explaining";
        if (IsBriefing(s)) return "Wingman reading";
        if (IsVoicePreparing(s)) return "Preparing voice";
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
    /// Classify a session for triage. On-hold takes precedence over color: a parked session sinks
    /// to the bottom even if it would otherwise be "needs you", because the user has explicitly
    /// deferred it. Uses <see cref="EffectiveColor"/>, NOT the raw Director color: a session the
    /// wingman is still reading stays in Active until the brief lands, instead of flopping into
    /// NEEDS YOU mid-brief and possibly back out (issue #196).
    /// </summary>
    public static TriageBucket Classify(SessionDto s) =>
        s.OnHold ? TriageBucket.OnHold
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
