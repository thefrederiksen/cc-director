using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

public class SessionViewModel : INotifyPropertyChanged
{
    // The sidebar colour strip reads the SHARED FOLD - SessionOrdering.EffectiveColor (see
    // StatusColorBrush below) - which is what makes Desktop, Cockpit and phone agree. It does NOT read
    // the SessionStatusWingman's Session.StatusColor, and has not since issue #1177 Phase 2: the Gateway
    // is the single fold and reads the Director's cooked StatusColor for NOTHING.
    //
    // This comment used to say the strip read Session.StatusColor "directly, so Desktop and Gateway
    // always show the same color" - a trap of exactly the kind the spec's section 4 lists (naming the
    // wingman as the source of truth). It named the wrong source AND inverted the reason for agreement:
    // agreement comes from calling the one shared fold, not from everyone reading the Director's colour.
    //
    // The colours the fold emits (see SessionOrdering.EffectiveColor) are:
    //   blue   = working          red    = needs you
    //   green  = ready (brand-new session, parked at its prompt with nothing needed)
    //   yellow = wingman narrating purple = parked on its own background task
    //   orange = dictation in flight, or a deep dive running
    //   grey   = parked (on hold) or exited      supporting = a live-controlled Worker's suppressed red
    //   error  = crashed (issue #959)
    //
    // The hexes are NOT here any more. They live in ONE table, StatusPalette, which the turn review
    // and the FIFO window also call, and which the spec's palette table is the written source for.
    // This class had its own private copy, and so did four other surfaces - that was defect 18.
    //
    // THE GREY IS ONE GREY. This strip used to pick between a light #9CA3AF and a #6A6A6A by
    // re-reading the raw Session.OnHold flag - a CLIENT INVENTING A DISTINCTION THE GATEWAY
    // DELIBERATELY DID NOT MAKE. The fold folds snoozed and exited to the same "grey" string
    // precisely so clients render them identically (SessionOrdering.RawActivityColor,
    // owner-approved). That split broke two closed laws at once: the client decided a colour, and
    // the desktop showed two greys where the phone showed one, so the devices disagreed.
    //
    // The distinction is NOT lost - it travels on the fold's own StateLabel ("Snoozed" vs "Exited"),
    // beside the dot. That is the Phase 3 precedent: lifecycle travels on badges and labels, never
    // on colour. Only the DOT collides. Whether snoozed deserves its own dot colour is an OPEN
    // PRODUCT QUESTION (see the spec's "Still open"); if the answer is ever yes it MUST arrive as a
    // distinct NAME from the fold (e.g. "onhold"), never as a client re-reading a raw flag.

    public Session Session { get; }

    public SessionViewModel(Session session)
    {
        Session = session;
        session.OnActivityStateChanged += OnActivityStateChanged;
        session.OnVerificationStatusChanged += OnVerificationStatusChanged;
        session.OnTerminalVerificationStatusChanged += OnTerminalVerificationStatusChanged;
        session.OnStatusColorChanged += OnStatusColorChanged;
        session.OnCachedExplainChanged += OnCachedExplainChangedVm;
        session.OnHoldChanged += OnHoldChangedVm;
        session.OnViewModeChanged += OnViewModeChangedVm;
        session.OnReceivingDictationChanged += OnReceivingDictationChangedVm;
        session.OnNumberChanged += OnNumberChangedVm;
        session.OnPendingDeletionChanged += OnPendingDeletionChangedVm;
        session.OnGatewayResolvedRoleChanged += OnGatewayResolvedRoleChangedVm;
        // THE THREE TRANSIENT OVERLAYS THE RAIL NEVER HEARD ABOUT. Map feeds IsBackgroundRunning,
        // IsTranscribing and IsAutoExplaining into the fold, which renders them purple, orange and yellow -
        // and nothing subscribed, so a desktop dictation or a background-task verdict reached the Gateway
        // (correct on the phone) while this rail kept its old dot, label, count and timer until an
        // unrelated event happened to repaint the row. Same class as the role-stamp bug, one layer earlier:
        // a fold input with no invalidation path. Found by review of pull request 1598.
        session.OnIsBackgroundRunningChanged += OnFoldInputChangedVm;
        session.OnIsTranscribingChanged += OnFoldInputChangedVm;
        session.OnIsExplainingChanged += OnFoldInputChangedVm;
        // The GATE on the purple and yellow overlays, not an overlay itself: turning the Wingman off on a
        // session parked on its background task flips the fold from purple "Background" to red "Needs you"
        // with no overlay flag changing. Easier to miss than a flag for exactly that reason.
        session.OnWingmanEnabledChanged += OnFoldInputChangedVm;

        if (session.PromptQueue != null)
        {
            _queueCount = session.PromptQueue.Count;
            session.PromptQueue.OnQueueChanged += OnQueueChanged;
        }
    }

    /// <summary>
    /// Re-read EVERYTHING the shared fold feeds. Every handler for a fold input calls exactly this, and
    /// none of them keeps a list of its own.
    ///
    /// WHY THIS EXISTS RATHER THAN SIX HAND-WRITTEN LISTS. Each handler used to name the properties it
    /// thought its fact touched, and they all disagreed: hold raised seven, activity three, the cached
    /// explain two, the role stamp three. Every list was a private chance to miss one, and missing one
    /// does not fail loudly - it renders a row that has HALF updated, where the dot reads "supporting" and
    /// the text beside it still reads "Needs you" with a live timer. That is worse than a stale row: a
    /// half-updated row looks deliberate, so the reader believes the wrong half.
    ///
    /// The lists were also wrong in a way no test caught: the fold's inputs GROW - role, dictation,
    /// background and auto-explain all arrived over this mission - and a new input meant editing six lists
    /// correctly. This asks the opposite question, "what does the fold feed?", once, in one place. Add a
    /// fold input and every handler already tells the truth about it.
    ///
    /// These are exactly the properties whose getters run FoldInput through SessionOrdering: the dot
    /// (StatusColorBrush), its tooltip (StatusReason), the row text (ActivityLabel), the triage verdict
    /// behind the "N need you" count (NeedsYou), and the waiting timer, which gates on the folded colour
    /// (HasWaitingDuration/WaitingDurationLabel). Raw flags that are NOT folded - IsOnHold, the number,
    /// the deletion badge - stay with their own handlers, because they are not this question.
    /// </summary>
    private void RaiseFoldProjection()
    {
        OnPropertyChanged(nameof(StatusColorBrush));
        OnPropertyChanged(nameof(StatusReason));
        OnPropertyChanged(nameof(ActivityLabel));
        OnPropertyChanged(nameof(NeedsYou));
        OnPropertyChanged(nameof(HasWaitingDuration));
        OnPropertyChanged(nameof(WaitingDurationLabel));
    }

    /// <summary>A fold input changed and carries nothing else - re-read the projection. Serves the three
    /// transient overlays (background task, dictation, auto-explain).</summary>
    private void OnFoldInputChangedVm(bool _) => Dispatcher.UIThread.Post(RaiseFoldProjection);

    // Issue #1181, Task 3b: the session started or stopped receiving a phone dictation - repaint the
    // rail strip (orange while receiving) and refresh its reason text.
    private void OnReceivingDictationChangedVm(bool receiving)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RaiseFoldProjection();
        });
    }

    private void OnStatusColorChanged(string oldColor, string newColor, string reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RaiseFoldProjection();
        });
    }

    /// <summary>
    /// The live session projected into the wire DTO the shared presentation fold reads, via the ONE mapper
    /// that already builds it for the Gateway - so the rail folds exactly the same inputs the phone does.
    ///
    /// Deliberately NOT cached. Map is pure property reads with no I/O, and the rail re-reads only when a
    /// change event raises a property, not in a loop. A cache here would buy nothing measurable and would
    /// add an invalidation obligation across nine change handlers, where missing one means the rail quietly
    /// shows stale state - the precise failure this whole change exists to remove.
    /// </summary>
    private SessionDto FoldInput => ControlEndpoints.Map(Session, directorId: "");

    /// <summary>
    /// The ONE presentation fold, shared with the Gateway and therefore with the Cockpit and the phone
    /// (<see cref="SessionOrdering"/> lives in CcDirector.Gateway.Contracts, which this app references).
    ///
    /// The desktop used to hand-roll three separate folds of its own - the strip read the hold flag, while
    /// the label and the "waiting" clock read the activity state and the wingman colour and had never heard
    /// of hold. That is why a snoozed session showed a grey strip next to a red "Your Turn" and a nagging
    /// hours-long clock, while the phone correctly showed "Snoozed": four readings of the same session, and
    /// nothing reconciled them. Calling the same function the other screens call makes agreement structural
    /// instead of something we re-verify every release.
    ///
    /// Known gap (Phase 2b): SessionRole is derived by the Gateway from the WHOLE fleet - the Director
    /// cannot know whether a controlled session's controller is alive on another machine - so it is absent
    /// here until the Gateway pushes it down. Until then a live Worker's red is suppressed on the Cockpit
    /// and the phone but still surfaces on this rail.
    /// </summary>
    private string EffectiveColor => SessionOrdering.EffectiveColor(FoldInput);

    /// <summary>
    /// The sidebar colour strip's brush: the shared fold's colour, mapped through the ONE palette.
    /// Hold, dictation, briefing and the activity colour are all folded by
    /// <see cref="SessionOrdering"/>, and the name-to-hex mapping is <see cref="StatusPalette"/> -
    /// this property decides nothing at all, which is the point.
    /// </summary>
    public ISolidColorBrush StatusColorBrush
    {
        get
        {
            var color = EffectiveColor;
            // The magenta sentinel is meant to be unmissable on the rail; this makes it diagnosable
            // too, by naming the colour in the log. Logged only on the edge - a binding getter runs
            // on every repaint, and a line per frame is not a diagnostic, it is a flood.
            if (!StatusPalette.Knows(color) && color != _lastUnknownColorLogged)
            {
                _lastUnknownColorLogged = color;
                StatusPalette.ReportUnknownColor(color, Session.Id.ToString());
            }
            return StatusPalette.BrushFor(color);
        }
    }

    private string? _lastUnknownColorLogged;

    /// <summary>
    /// True when this session is flagged for deletion and awaiting the reaper - drives the rail's
    /// "winding down" badge (defect 23).
    ///
    /// PENDING DELETION IS A BADGE, NEVER A COLOUR (owner's ruling, 14 July 2026). It says nothing about
    /// what the agent is DOING, and a flagged session may still be working - the reaper waits out a
    /// running final turn (SessionManager.ReapPendingDeletions). So it rides BESIDE the dot and never
    /// touches <see cref="StatusColorBrush"/>: a flagged session that is working shows a BLUE strip with
    /// this badge; a flagged session waiting on the user still shows red "Needs you" with this badge.
    ///
    /// Deliberately NOT folded into <see cref="EffectiveColor"/> - putting it there would make it a
    /// colour and would spend the dot, which says what a session is DOING, on saying it is scheduled to
    /// go. That is the same mistake as the "Supporting" grey that hid 23 minutes of real work.
    /// </summary>
    public bool IsPendingDeletion => Session.PendingDeletion;

    /// <summary>The "winding down" badge tooltip: the human reason captured when the session was
    /// flagged (e.g. "jobs-auto: nothing to report"), or a plain fallback when none was given.</summary>
    public string PendingDeletionTooltip => Session.DeletionReason is { } reason
        ? $"Marked for deletion - {reason}"
        : "Marked for deletion - reaping shortly";

    /// <summary>Issue: defect 23. The pending-deletion FACT changed on the Director - repaint the rail
    /// badge on the UI thread. This is the session's own signal (<see cref="Session.OnPendingDeletionChanged"/>);
    /// the rail used to learn about deletion only because MarkForDeletion wrote a colour, which it no
    /// longer does and was never allowed to.</summary>
    private void OnPendingDeletionChangedVm(bool _)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(IsPendingDeletion));
            OnPropertyChanged(nameof(PendingDeletionTooltip));
        });
    }

    // The Gateway stamped (or cleared) this session's role. That is a FOLD INPUT - SessionOrdering
    // suppresses a controlled worker's red to "supporting" - so the rail must re-read exactly as it does
    // for an activity or hold change. Nothing re-read on a role before this, so the stamp arrived, the
    // fold was right, and the dot stayed red anyway.
    //
    // RAISE EVERY PROPERTY THE FOLD FEEDS, NOT JUST THE DOT. The first version of this handler raised
    // only the brush, the reason and the count, and review caught it: ActivityLabel is
    // SessionOrdering.StateLabel(FoldInput), and HasWaitingDuration/WaitingDurationLabel gate on
    // EffectiveColor - all three read the role. So the dot repainted to "supporting" while the row text
    // still said "Needs you" with a live waiting timer beside it. A half-re-read is its own lie: one row
    // disagreeing with itself is worse than the stale row it replaced, because it looks deliberate.
    // Match OnStatusColorChanged and OnActivityStateChanged between them - the same fold, the same reads.
    private void OnGatewayResolvedRoleChangedVm(string? _)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RaiseFoldProjection();
        });
    }

    /// <summary>True when the user has parked this session on hold. Drives the menu toggle
    /// label and the light-gray strip color.</summary>
    public bool IsOnHold => Session.OnHold;

    /// <summary>Tooltip-ready reason for the current strip color. Reflects the on-hold
    /// override when set, otherwise the wingman's reason for <see cref="Session.StatusColor"/>.</summary>
    public string StatusReason => Session.OnHold
        ? "Snoozed (set aside by you)"
        : Session.IsReceivingDictation
            ? "Receiving a dictation from your phone"
            : Session.LastStatusReason ?? "";

    private void OnHoldChangedVm(bool onHold)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RaiseFoldProjection();
            // IsOnHold is a RAW flag the rail renders directly (the snooze glyph), not a fold output - so
            // it is not RaiseFoldProjection's business and stays here.
            OnPropertyChanged(nameof(IsOnHold));
        });
    }

    /// <summary>True when a phone is currently watching this session through the Voice (in-car)
    /// tab. Drives the rail's in-voice-mode ear indicator (issue #554). A pure passthrough of
    /// <see cref="Session.VoiceMode"/> so the rail never reads the model directly.</summary>
    public bool IsVoiceMode => Session.VoiceMode;

    private void OnViewModeChangedVm(MobileViewMode oldMode, MobileViewMode newMode)
    {
        Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(IsVoiceMode)));
    }

    private void OnCachedExplainChangedVm()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // The waiting duration is proxied from CachedExplainAt, which this briefing just
            // set. When a session is already red and its first briefing lands, HasWaitingDuration
            // flips false->true here; without raising it the "waiting Xm" list label would not
            // appear until the next 15s timer tick.
            RaiseFoldProjection();
        });
    }

    /// <summary>How long this session has been waiting on you, shown in the list only when red,
    /// so you can see at a glance WHICH needs-you session is the most stale and triage it first.
    /// Proxied from the last briefing time (generated at turn-end, when the session goes red).
    /// Reads the shared fold, NOT the raw wingman colour: a snoozed session is not red and must not nag
    /// with an hours-long clock - that mismatch is exactly what this change removes.</summary>
    public bool HasWaitingDuration =>
        string.Equals(EffectiveColor, "red", StringComparison.OrdinalIgnoreCase)
        && Session.CachedExplainAt is not null;

    public string WaitingDurationLabel
    {
        get
        {
            if (!HasWaitingDuration) return "";
            var d = DateTime.UtcNow - Session.CachedExplainAt!.Value;
            if (d.TotalMinutes < 1) return "waiting <1m";
            if (d.TotalMinutes < 60) return $"waiting {(int)d.TotalMinutes}m";
            return $"waiting {(int)d.TotalHours}h";
        }
    }

    /// <summary>Re-raise time-derived list labels; called periodically so the waiting duration
    /// ticks up without an event.</summary>
    public void RefreshTimeLabels()
    {
        OnPropertyChanged(nameof(WaitingDurationLabel));
        OnPropertyChanged(nameof(HasWaitingDuration));
    }

    public string DisplayName => Session.CustomName
        ?? Path.GetFileName(Session.RepoPath.TrimEnd('\\', '/'));

    /// <summary>The session's three-digit number as text (issue #820), e.g. "412", or empty when
    /// the session has no number. A separate field from <see cref="DisplayName"/> so it shows as a
    /// muted prefix in the rail and header and a rename never affects it.</summary>
    public string NumberBadge => Session.Number is int n ? n.ToString() : "";

    /// <summary>True when the session has a three-digit number to show (issue #820).</summary>
    public bool HasNumber => Session.Number.HasValue;

    /// <summary>Issue #1292: the Gateway assigned this session's number after creation - repaint the
    /// rail badge on the UI thread so the number appears when it arrives.</summary>
    private void OnNumberChangedVm()
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(NumberBadge));
            OnPropertyChanged(nameof(HasNumber));
        });
    }

    public string? CustomColor
    {
        get => Session.CustomColor;
        set
        {
            Session.CustomColor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCustomColor));
            OnPropertyChanged(nameof(CustomColorBrush));
            OnPropertyChanged(nameof(DragHandleBrush));
        }
    }

    public bool HasCustomColor => !string.IsNullOrWhiteSpace(CustomColor);

    private static readonly ISolidColorBrush DefaultDragHandleBrush = new SolidColorBrush(Color.Parse("#3C3C3C"));

    public ISolidColorBrush DragHandleBrush => HasCustomColor ? CustomColorBrush : DefaultDragHandleBrush;

    public ISolidColorBrush CustomColorBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CustomColor))
                return new SolidColorBrush(Colors.Transparent);
            try
            {
                var color = Color.Parse(CustomColor);
                return new SolidColorBrush(color);
            }
            catch
            {
                return new SolidColorBrush(Colors.Transparent);
            }
        }
    }

    /// <summary>
    /// The session's state in words. The shared fold's label - the SAME string the Cockpit and the phone
    /// render - so a session cannot read "Your Turn" here and "Snoozed" there. This used to read the raw
    /// activity state and therefore had no idea the session was held.
    /// </summary>
    public string ActivityLabel => SessionOrdering.StateLabel(FoldInput);

    /// <summary>
    /// True when this session is waiting on YOU - the shared fold's triage verdict, not a colour.
    /// Drives the "N need you" count beside the rail's SESSIONS header.
    ///
    /// Reads <see cref="SessionOrdering.Classify"/>, the SAME function the phone's web-push badge
    /// counts by (WebPushNeedsYouNotifier), so the number on the header and the number on the phone
    /// are folded by one rule. The header used to count the RAW cooked colour
    /// (<c>Session.StatusColor == "red"</c>) with no hold check, no role, and no overlays - so a
    /// snoozed session sat grey and labelled "Snoozed" underneath a header reading "1 need you".
    /// Three readings of one session, and nothing reconciled them.
    ///
    /// Known gap (Phase 2b, defect 5 - NOT closed here): SessionRole is derived by the Gateway from
    /// the WHOLE fleet, so it is absent from this Director-local fold input and a live Worker's red
    /// still counts on this rail. It is correctly suppressed on the Cockpit and the phone.
    /// </summary>
    public bool NeedsYou => SessionOrdering.Classify(FoldInput) == SessionOrdering.TriageBucket.NeedsYou;

    // Agent badge for the session list. Colored pill shown next to the session name
    // so it's visually obvious which agent CLI this session is running.
    private static readonly ISolidColorBrush ClaudeAgentBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly ISolidColorBrush PiAgentBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));
    private static readonly ISolidColorBrush CodexAgentBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xA3, 0x7F));
    private static readonly ISolidColorBrush GeminiAgentBrush = new SolidColorBrush(Color.FromRgb(0xEA, 0x43, 0x35));
    private static readonly ISolidColorBrush OpenCodeAgentBrush = new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16));
    private static readonly ISolidColorBrush CursorAgentBrush = new SolidColorBrush(Color.FromRgb(0x06, 0xB6, 0xD4));  // cyan - distinct from Claude blue / Pi violet / RawCli slate, readable on the dark rail
    private static readonly ISolidColorBrush GrokAgentBrush = new SolidColorBrush(Color.FromRgb(0xEA, 0xB3, 0x08));  // amber - Grok, distinct from OpenCode orange / Gemini red, readable on the dark rail
    private static readonly ISolidColorBrush CopilotAgentBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));  // emerald - GitHub Copilot, distinct from Cursor cyan / Grok amber, readable on the dark rail
    private static readonly ISolidColorBrush RawCliAgentBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));  // slate gray - neutral, not tied to any brand

    public string AgentLabel => LabelFor(Session.AgentKind);

    public ISolidColorBrush AgentBadgeBrush => BadgeBrushFor(Session.AgentKind);

    /// <summary>Pure agent-kind -> rail label mapping. Every provider has its own arm; the
    /// default is Claude Code (kind 0). Static so it can be unit-tested without a live
    /// <see cref="Session"/> (issue #517 regression: Cursor must not fall through to "Claude Code").</summary>
    public static string LabelFor(AgentKind kind) => kind switch
    {
        AgentKind.Pi => "Pi",
        AgentKind.Codex => "Codex",
        AgentKind.Gemini => "Gemini",
        AgentKind.OpenCode => "OpenCode",
        AgentKind.Cursor => "Cursor",
        AgentKind.Grok => "Grok",
        AgentKind.Copilot => "GitHub Copilot",
        AgentKind.RawCli => "Custom CLI",
        _ => "Claude Code"
    };

    /// <summary>Pure agent-kind -> rail badge brush mapping. Each provider uses its own brand
    /// hue; the default is the Claude blue (kind 0). Static for the same reason as
    /// <see cref="LabelFor"/> (issue #517).</summary>
    public static ISolidColorBrush BadgeBrushFor(AgentKind kind) => kind switch
    {
        AgentKind.Pi => PiAgentBrush,
        AgentKind.Codex => CodexAgentBrush,
        AgentKind.Gemini => GeminiAgentBrush,
        AgentKind.OpenCode => OpenCodeAgentBrush,
        AgentKind.Cursor => CursorAgentBrush,
        AgentKind.Grok => GrokAgentBrush,
        AgentKind.Copilot => CopilotAgentBrush,
        AgentKind.RawCli => RawCliAgentBrush,
        _ => ClaudeAgentBrush
    };

    // ===== Automatic session role (non-color rail glyph) =====

    private string _resolvedRole = SessionRoles.Standalone;

    /// <summary>
    /// This session's resolved automatic role - one of the <see cref="SessionRoles"/> constants.
    /// Stamped by MainWindow after construction (and on every list rebuild) from the local fleet,
    /// following the same stamped-property pattern as <see cref="IsGroupFirst"/>. Setting it raises
    /// change notification for the derived badge properties so the rail glyph refreshes.
    /// </summary>
    public string ResolvedRole
    {
        get => _resolvedRole;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? SessionRoles.Standalone : value;
            if (_resolvedRole == normalized) return;
            _resolvedRole = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasRoleGlyph));
            OnPropertyChanged(nameof(RoleGlyphText));
            OnPropertyChanged(nameof(RoleTooltip));
        }
    }

    /// <summary>True when the role warrants a rail glyph (Manager, Worker, or Architect). Standalone
    /// and any unknown value show nothing, so the badge stays out of the way for the common case.</summary>
    public bool HasRoleGlyph => RoleGlyphFor(ResolvedRole).Length > 0;

    /// <summary>The single-letter, non-color role glyph ("M"/"W"/"A") or "" for Standalone/unknown.</summary>
    public string RoleGlyphText => RoleGlyphFor(ResolvedRole);

    /// <summary>The full role name for the badge tooltip ("Manager"/"Worker"/"Architect") or "".</summary>
    public string RoleTooltip => HasRoleGlyph ? ResolvedRole : string.Empty;

    /// <summary>Pure role -> single-letter glyph mapping. Static so it can be unit-tested without a
    /// live <see cref="Session"/>, mirroring <see cref="LabelFor"/> / <see cref="BadgeBrushFor"/>.
    /// Manager -> "M", Worker -> "W", Architect -> "A"; Standalone and anything else -> "".</summary>
    public static string RoleGlyphFor(string? role) => role switch
    {
        SessionRoles.Manager => "M",
        SessionRoles.Worker => "W",
        SessionRoles.Architect => "A",
        _ => ""
    };

    // ===== Group membership (issue #225) =====

    private static readonly ISolidColorBrush GroupAccentBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x6D, 0xA3));

    /// <summary>The group this session belongs to (issue #225), or null when solo. Set by
    /// MainWindow after construction and on reorder so the bracket/header reflow.</summary>
    public Guid? GroupId => Session.GroupId;

    /// <summary>True when this session is a group member - drives all group visuals.</summary>
    public bool IsGroupMember => Session.GroupId is not null;

    private bool _isGroupFirst;
    private bool _isGroupLast;

    /// <summary>True for the TOP member of its group: renders the group header + top bracket.</summary>
    public bool IsGroupFirst
    {
        get => _isGroupFirst;
        set { if (_isGroupFirst != value) { _isGroupFirst = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowGroupHeader)); } }
    }

    /// <summary>True for the BOTTOM member of its group: renders the bottom bracket.</summary>
    public bool IsGroupLast
    {
        get => _isGroupLast;
        set { if (_isGroupLast != value) { _isGroupLast = value; OnPropertyChanged(); } }
    }

    /// <summary>The group header ("PRODUCT GROUP ...") renders above the first member only.</summary>
    public bool ShowGroupHeader => IsGroupMember && IsGroupFirst;

    /// <summary>Header label, e.g. "PRODUCT GROUP" - on the first member only.</summary>
    public string GroupHeaderText =>
        string.IsNullOrWhiteSpace(Session.GroupName) ? "GROUP" : Session.GroupName.ToUpperInvariant() + " GROUP";

    /// <summary>The brush for the group's left accent stripe + bracket.</summary>
    public ISolidColorBrush GroupAccent => GroupAccentBrush;

    private static readonly ISolidColorBrush GroupRowTintBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x23, 0x30));

    /// <summary>Subtle tint behind a group member's row (transparent for solo sessions),
    /// binding the members visually together.</summary>
    public IBrush GroupRowBackground => IsGroupMember ? GroupRowTintBrush : Brushes.Transparent;

    public string RepoPath => Session.RepoPath;

    private int _uncommittedCount;
    public int UncommittedCount
    {
        get => _uncommittedCount;
        set
        {
            if (_uncommittedCount == value) return;
            _uncommittedCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUncommittedChanges));
        }
    }

    public bool HasUncommittedChanges => _uncommittedCount > 0;

    private int _queueCount;
    public int QueueCount
    {
        get => _queueCount;
        set
        {
            if (_queueCount == value) return;
            _queueCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasQueuedItems));
        }
    }

    public bool HasQueuedItems => _queueCount > 0;

    public void Rename(string? newName, string? color = null)
    {
        Session.CustomName = newName;
        if (color != null)
            Session.CustomColor = color;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(CustomColor));
        OnPropertyChanged(nameof(HasCustomColor));
        OnPropertyChanged(nameof(CustomColorBrush));
        OnPropertyChanged(nameof(DragHandleBrush));
    }

    public bool IsVerified => Session.VerificationStatus == SessionVerificationStatus.Verified;

    public bool HasVerificationWarning =>
        Session.VerificationStatus is SessionVerificationStatus.FileNotFound
                                    or SessionVerificationStatus.Error
                                    or SessionVerificationStatus.ContentMismatch;

    public string VerificationStatusText => Session.VerificationStatus switch
    {
        SessionVerificationStatus.Verified => "Verified",
        SessionVerificationStatus.FileNotFound => "Session file not found",
        SessionVerificationStatus.NotLinked => "Waiting for Claude session ID...",
        SessionVerificationStatus.ContentMismatch => "Session content mismatch",
        SessionVerificationStatus.Error => "Verification error",
        _ => ""
    };

    public string? VerifiedFirstPrompt => Session.VerifiedFirstPrompt;

    public TerminalVerificationStatus TerminalVerificationStatus => Session.TerminalVerificationStatus;

    public string TerminalVerificationStatusText => Session.TerminalVerificationStatus switch
    {
        TerminalVerificationStatus.Waiting => "Waiting...",
        TerminalVerificationStatus.Potential => "Potential Match",
        TerminalVerificationStatus.Matched => "Matched",
        TerminalVerificationStatus.Failed => "Verification Failed",
        _ => ""
    };

    public bool ShowVerificationDot => Session.TerminalVerificationStatus is TerminalVerificationStatus.Waiting
                                                                           or TerminalVerificationStatus.Potential
                                                                           or TerminalVerificationStatus.Failed;

    private static readonly ISolidColorBrush VerificationWaitingBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly ISolidColorBrush VerificationPotentialBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly ISolidColorBrush VerificationFailedBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));

    public ISolidColorBrush VerificationDotBrush => Session.TerminalVerificationStatus switch
    {
        TerminalVerificationStatus.Waiting => VerificationWaitingBrush,
        TerminalVerificationStatus.Potential => VerificationPotentialBrush,
        TerminalVerificationStatus.Failed => VerificationFailedBrush,
        _ => VerificationWaitingBrush
    };

    private void OnQueueChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            QueueCount = Session.PromptQueue?.Count ?? 0;
        });
    }

    private void OnActivityStateChanged(ActivityState oldState, ActivityState newState)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Raised three and missed the waiting timer: ActivityState drives EffectiveColor through
            // SessionOrdering.RawActivityColor, and HasWaitingDuration gates on that - so a red row with a
            // cached explain could go blue/grey/error and keep a visible "waiting Xm" until the 15s tick.
            RaiseFoldProjection();
        });
    }

    private void OnVerificationStatusChanged(SessionVerificationStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(IsVerified));
            OnPropertyChanged(nameof(HasVerificationWarning));
            OnPropertyChanged(nameof(VerificationStatusText));
            OnPropertyChanged(nameof(VerifiedFirstPrompt));
        });
    }

    private void OnTerminalVerificationStatusChanged(TerminalVerificationStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(TerminalVerificationStatus));
            OnPropertyChanged(nameof(TerminalVerificationStatusText));
            OnPropertyChanged(nameof(ShowVerificationDot));
            OnPropertyChanged(nameof(VerificationDotBrush));
            OnPropertyChanged(nameof(IsVerified));
            OnPropertyChanged(nameof(HasVerificationWarning));
            OnPropertyChanged(nameof(VerificationStatusText));
        });
    }

    /// <summary>Refresh Claude metadata from sessions-index.json.</summary>
    public void RefreshClaudeMetadata()
    {
        Session.RefreshClaudeMetadata();
    }

    /// <summary>Notify UI that display properties may have changed.</summary>
    public void NotifyDisplayChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(CustomColor));
        OnPropertyChanged(nameof(HasCustomColor));
        OnPropertyChanged(nameof(CustomColorBrush));
        OnPropertyChanged(nameof(AgentLabel));
        OnPropertyChanged(nameof(AgentBadgeBrush));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
