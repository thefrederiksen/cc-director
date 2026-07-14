using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Memory;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Terminal.Core;
using CcDirector.Terminal.Core.Rendering;

namespace CcDirector.Core.Sessions;

public enum SessionStatus
{
    Starting,
    Running,
    Exiting,
    Exited,
    Failed
}

/// <summary>
/// Which mobile view (if any) a phone is currently watching this session through. Set by the
/// active mobile tab via the Control API. The wingman keys its remark STYLE off this.
/// </summary>
public enum MobileViewMode
{
    /// <summary>No phone is watching remotely (desktop, or the phone navigated away).
    /// No proactive briefings; the wingman writes normal text remarks.</summary>
    Off,

    /// <summary>Phone is on the Session (text) tab. Proactive briefings on; normal text remarks.</summary>
    Text,

    /// <summary>Phone is on the Voice (in-car) tab. Proactive briefings on; the wingman writes
    /// spoken-friendly remarks for hands-free / driving use.</summary>
    Voice
}

/// <summary>
/// Status of terminal-based verification (matching terminal content to .jsonl files).
/// </summary>
public enum TerminalVerificationStatus
{
    /// <summary>Waiting - no match found yet.</summary>
    Waiting,
    /// <summary>Potential match found but not yet confirmed (< 50 lines).</summary>
    Potential,
    /// <summary>Matched - terminal content confirmed (50+ lines).</summary>
    Matched,
    /// <summary>Failed - could not find a matching .jsonl file after 50+ lines.</summary>
    Failed
}

/// <summary>
/// Result of verifying a session by matching terminal content to .jsonl files.
/// </summary>
public sealed class TerminalVerificationResult
{
    public bool IsMatched { get; init; }
    public bool IsPotential { get; init; }
    public string? MatchedSessionId { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// One styled run within a snapshot screen row: a stretch of characters sharing the same
/// colours and weight. <see cref="Fg"/>/<see cref="Bg"/> are "#RRGGBB" strings, or null when
/// the cell carried no explicit colour (the consumer renders that with its default brush).
/// Produced by <see cref="Session.SnapshotScreenColoredRows"/> so the captured terminal can be
/// reproduced in colour, not flattened to monochrome text.
/// </summary>
public sealed record ScreenSegment(string Text, string? Fg, string? Bg, bool Bold);

/// <summary>
/// Represents a single Claude session. Delegates process management to an ISessionBackend.
/// Session handles metadata, activity state, and routing - backend handles process I/O.
/// </summary>
public sealed class Session : IDisposable
{
    /// <summary>Minimum length of first prompt required for verification (avoid verifying too early).</summary>
    public const int MinVerificationLength = 50;

    private readonly ISessionBackend _backend;
    private bool _disposed;

    // ===== HTML-view terminal emulator =====
    // The Avalonia terminal owns its own AnsiParser bound to the live window
    // size. The HTML "Raw terminal" tab needs an independent emulator with a
    // fixed grid (browser-side resize must not perturb ConPty width). We feed
    // it from the buffer's OnBytesWritten event, gated by _htmlParserLock so
    // request threads can take snapshots concurrently.
    private const int HtmlGridCols = 220;
    private const int HtmlGridRows = 40;
    private const int HtmlMaxScrollback = 5000;
    private readonly object _htmlParserLock = new();
    private TerminalCell[,]? _htmlCells;
    private List<TerminalCell[]>? _htmlScrollback;
    private AnsiParser? _htmlParser;
    private Action<byte[]>? _htmlParserFeed;

    // ===== Live-attach terminal emulator (the WebSocket stream's attach snapshot) =====
    // A SECOND authoritative parser, fed the same bytes from byte 0 but sized to track the REAL
    // PTY geometry (unlike the fixed-grid _htmlParser above). A freshly-attaching browser client
    // cannot rebuild a long session's screen from a mid-stream byte slice (relative cursor moves and
    // scrolls land on a baseline the slice never established, so an incrementally-repainting agent
    // like Codex reconstructs torn). This parser always holds the correct current screen, so the
    // stream endpoint serializes it into a self-contained "prime" frame on attach - the reattach
    // strategy tmux/mosh use. Kept separate from _htmlParser so its geometry tracking cannot perturb
    // the Cockpit "Raw terminal" tab or the Wingman screen detection that read _htmlParser. Fed and
    // read under the same _htmlParserLock so both parsers and the snapshot are always consistent.
    private const int StreamMaxScrollback = 5000;
    private TerminalCell[,]? _streamCells;
    private List<TerminalCell[]>? _streamScrollback;
    private AnsiParser? _streamParser;
    private int _streamGridCols;
    private int _streamGridRows;
    // Total bytes the live-attach parser has consumed. Captured with the snapshot (under the same
    // lock) so the stream endpoint resumes live output at EXACTLY the byte the snapshot reflects -
    // no gap (missing bytes) and no overlap (double-applied bytes). Equals the buffer's absolute
    // position because the parser is subscribed from session start, before any output.
    private long _streamBytesReflected;

    public SessionBackendType BackendType { get; }

    /// <summary>True when this session is a GitHub Actions remote session.</summary>
    public bool IsRemote => BackendType == SessionBackendType.GitHubActions;

    /// <summary>Remote repo slug ("owner/repo") for GitHub Actions sessions, else null.</summary>
    public string? RemoteRepo => (_backend as GitHubActionsBackend)?.RepoSlug;

    /// <summary>Web URL of the issue/PR thread for GitHub Actions sessions, else null.</summary>
    public string? RemoteThreadUrl => (_backend as GitHubActionsBackend)?.ThreadUrl;

    /// <summary>Web URL of the most recent workflow run for GitHub Actions sessions, else null.</summary>
    public string? RemoteRunUrl => (_backend as GitHubActionsBackend)?.CurrentRunUrl;

    /// <summary>Last observed run status for GitHub Actions sessions, else null.</summary>
    public string? RemoteRunStatus => (_backend as GitHubActionsBackend)?.RunStatus;

    /// <summary>Which agent CLI this session is running (Claude Code, Pi, etc).
    /// Defaults to ClaudeCode for sessions created via legacy code paths.</summary>
    public AgentKind AgentKind { get; internal set; } = AgentKind.ClaudeCode;

    /// <summary>If this session was created as part of a group (issue #225), the shared
    /// group identity its members travel by; null for a solo session. Members of the same
    /// group sort adjacently and drag as one unit. Stamped at creation, immutable.</summary>
    public Guid? GroupId { get; internal set; }

    /// <summary>The session's role within its group (issue #225), e.g. "Submitter",
    /// "Implementer", "QA" - a descriptive label. Null for a solo session.</summary>
    public string? GroupRole { get; internal set; }

    /// <summary>The group's display name (issue #225), e.g. "Product" - shown in the desktop
    /// group header. Same for every member of a group; null for a solo session.</summary>
    public string? GroupName { get; internal set; }

    /// <summary>When this session was spawned to be controlled by ANOTHER session (issue #815) -
    /// a "Supporting" sub-agent - the id of the controlling session; null for a normal session.
    /// Set ONLY at birth and immutable afterwards (stamped by the create/restore paths, like
    /// <see cref="GroupId"/>). Drives the recessive "Supporting" status color, which is honored
    /// only while the controlling session still exists; a red "needs you" still breaks through.</summary>
    public Guid? ControllerSessionId { get; internal set; }

    /// <summary>True when this session is a controlled sub-agent (issue #815) - it carries a
    /// <see cref="ControllerSessionId"/>. Whether the recessive "Supporting" color is actually
    /// painted also depends on the controller still being alive; see the SessionStatusWingman.</summary>
    public bool IsControlled => ControllerSessionId.HasValue;

    /// <summary>
    /// The sticky EXPLICIT role a human/session declared for this session (automatic session roles), or null
    /// for none. When set it WINS over the Gateway's auto-derivation of the role - the only way to be an
    /// Architect, which cannot be inferred from the spawn graph. Settable at birth (from the create request)
    /// and later via the set-role verb (<see cref="SetExplicitRole"/>); a null/blank value clears it. One of
    /// the SessionRoles values (validated by the caller). Persisted so it survives a Director restart.
    /// </summary>
    public string? ExplicitRole { get; internal set; }

    /// <summary>Set (or clear, on a null/blank value) this session's sticky explicit role. The value is
    /// validated against the role set by the caller; this only stores it.</summary>
    public void SetExplicitRole(string? role)
    {
        ExplicitRole = string.IsNullOrWhiteSpace(role) ? null : role.Trim();
        FileLog.Write($"[Session] {Id} explicit role set to {ExplicitRole ?? "(none)"}");
    }

    /// <summary>
    /// The Mission this session is ATTACHED to (see
    /// docs/new_architecture/mission-as-first-class-unit-of-work.md), or null when it is attached to no
    /// Mission. A Mission is its own persisted record (<see cref="Mission"/>); this is the attachment link
    /// that binds a pod (Architect + Manager + Workers all attach to one Mission). Stamped at spawn (from
    /// the create request) or later via the attach verb (<see cref="AttachToMission"/>). Persisted so the
    /// attachment survives a Director restart.
    /// </summary>
    public Guid? MissionId { get; internal set; }

    /// <summary>
    /// The attached Mission's display name, CACHED here so a client can render the Mission without resolving
    /// <see cref="MissionId"/> against the Mission store. Set alongside <see cref="MissionId"/> at attach
    /// time; the Mission record remains the source of truth. Null when attached to no Mission. Persisted.
    /// </summary>
    public string? MissionName { get; internal set; }

    /// <summary>
    /// Attach this session to a Mission (or DETACH it when <paramref name="missionId"/> is null). Stamps
    /// <see cref="MissionId"/> and caches the resolved <see cref="MissionName"/>. The caller resolves the
    /// name from the Mission store; this only stores what it is given.
    /// </summary>
    public void AttachToMission(Guid? missionId, string? missionName)
    {
        MissionId = missionId;
        MissionName = missionId is null ? null : (string.IsNullOrWhiteSpace(missionName) ? null : missionName.Trim());
        FileLog.Write($"[Session] {Id} attached to mission {MissionId?.ToString() ?? "(none)"} (name={MissionName ?? "(none)"})");
    }

    public Guid Id { get; }

    /// <summary>
    /// The session's short, human-friendly three-digit number (100-999), or null when the session
    /// has no number (allocated before this feature ran, or the Director's number pool was exhausted
    /// at creation). Issue #820. Assigned by the <see cref="SessionManager"/> at creation (or
    /// re-applied from persistence on restore) and stable for the life of the session - a rename
    /// never changes it because the number is a separate field from the display name.
    ///
    /// Issue #1292: the number is now handed out by the Gateway (unique across the whole fleet), so
    /// for a brand-new session it may be assigned a moment AFTER creation - once the Gateway answers.
    /// Set through <see cref="SetNumber"/> so <see cref="OnNumberChanged"/> fires and the rail shows
    /// the number when it arrives.
    /// </summary>
    public int? Number { get; internal set; }

    /// <summary>Raised when <see cref="Number"/> changes, so a view (the desktop rail) can show the
    /// number when the Gateway assigns it after creation (issue #1292).</summary>
    public event Action? OnNumberChanged;

    /// <summary>Set <see cref="Number"/> and notify listeners. No-op when the value is unchanged.</summary>
    internal void SetNumber(int? number)
    {
        if (Number == number) return;
        Number = number;
        try { OnNumberChanged?.Invoke(); }
        catch (Exception ex) { FileLog.Write($"[Session] {Id} OnNumberChanged handler threw: {ex.Message}"); }
    }

    public string RepoPath { get; }
    public string WorkingDirectory { get; }
    public SessionStatus Status { get; internal set; }
    public DateTimeOffset CreatedAt { get; }
    public string? ClaudeArgs { get; }

    /// <summary>The EFFECTIVE launch command line the agent process was actually started with -
    /// the merged result of <see cref="ClaudeArgs"/> (per-session args) and the configured agent
    /// defaults (e.g. <c>AgentOptions.DefaultClaudeArgs</c>), as produced by the agent's
    /// BuildLaunchSpec. This is the authoritative source of the launched <c>--model</c> value
    /// (issue #803): <see cref="ClaudeArgs"/> is null whenever the model comes from the configured
    /// default rather than a per-session override, so the context gauge must read the effective
    /// args, not the raw per-session ones. Set by SessionManager at launch.</summary>
    public string? EffectiveLaunchArgs { get; internal set; }

    public int? ExitCode { get; internal set; }

    /// <summary>
    /// True when the agent process ended UNEXPECTEDLY - a crash (issue #959). Set on process exit
    /// when the exit was not a clean, intentional end (see <see cref="IsUnexpectedExit"/>). A crashed
    /// session is kept in the roster in an Error state rather than being auto-removed, so the user
    /// sees that work stopped instead of the session silently disappearing.
    /// </summary>
    public bool Crashed { get; private set; }

    /// <summary>
    /// The pure decision for "did this session crash?" A process exit is treated as a crash when it
    /// returns a non-zero exit code OR when the session dropped out while it was actively working
    /// (some crashes exit with code 0, so the exit code alone is not enough). A clean exit (code 0)
    /// from an idle/waiting session is an intentional end and is NOT a crash.
    /// </summary>
    public static bool IsUnexpectedExit(int exitCode, bool wasWorking) => exitCode != 0 || wasWorking;

    /// <summary>The terminal buffer from the backend. May be null for Embedded mode.</summary>
    public CircularTerminalBuffer? Buffer => _backend.Buffer;

    /// <summary>
    /// Current PTY column count. Initialized to the size the backend is started
    /// with (120) and updated by <see cref="Resize"/> when the desktop terminal
    /// pane drives a resize. The phone's xterm.js view reads this so it renders
    /// the grid at the true PTY width instead of guessing.
    /// </summary>
    public short CurrentCols { get; private set; } = 120;

    /// <summary>Current PTY row count. See <see cref="CurrentCols"/>.</summary>
    public short CurrentRows { get; private set; } = 30;

    /// <summary>Process ID from the backend.</summary>
    public int ProcessId => _backend.ProcessId;

    /// <summary>
    /// Claude's cognitive activity state, driven by hook events.
    /// Initial state is <see cref="ActivityState.WaitingForInput"/>: a freshly spawned session is
    /// literally sitting at Claude Code's input prompt with no turn in flight. This pairs with the
    /// IsBrandNew guard in <c>TerminalStateDetector</c>, which suppresses the byte->Working flip
    /// while the startup splash is painting, so the session stays parked at its prompt from the
    /// moment the row appears until the user submits their first prompt. While
    /// <see cref="IsBrandNew"/> holds, the wingman paints that parked state green ("ready") rather
    /// than red ("needs you") - see <c>SessionStatusWingman.ColorFor</c>.
    /// </summary>
    public ActivityState ActivityState { get; private set; } = ActivityState.WaitingForInput;

    /// <summary>The session_id reported by Claude hooks, used for routing.</summary>
    public string? ClaudeSessionId { get; internal set; }

    /// <summary>
    /// The absolute path to the current Claude transcript .jsonl, as reported by the Claude
    /// SessionStart hook. Authoritative across /clear and compaction (Claude mints a new id
    /// and file on each), where deriving the path from a stale <see cref="ClaudeSessionId"/>
    /// would be wrong. Null until the first hook fires.
    /// </summary>
    public string? ClaudeTranscriptPath { get; private set; }

    /// <summary>
    /// Update the live Claude session pointer from a SessionStart hook. Claude mints a NEW
    /// session id (and transcript file) on /clear and on auto-compaction; the hook reports the
    /// current id and transcript path so the Director keeps tracking the right transcript
    /// instead of the stale one it preassigned at launch.
    /// </summary>
    public void UpdateClaudeSessionPointer(string? claudeSessionId, string? transcriptPath, string? source)
    {
        if (!string.IsNullOrWhiteSpace(claudeSessionId))
            ClaudeSessionId = claudeSessionId;
        if (!string.IsNullOrWhiteSpace(transcriptPath))
            ClaudeTranscriptPath = transcriptPath;
        FileLog.Write($"[Session] UpdateClaudeSessionPointer: sid={Id} source={source ?? "(none)"} claudeId={claudeSessionId ?? "(none)"} transcript={transcriptPath ?? "(none)"}");
    }

    /// <summary>Cached metadata from Claude's sessions-index.json.</summary>
    public ClaudeSessionMetadata? ClaudeMetadata { get; private set; }

    /// <summary>Fires when ClaudeMetadata is refreshed.</summary>
    public event Action<ClaudeSessionMetadata?>? OnClaudeMetadataChanged;

    /// <summary>Status of session file verification (whether .jsonl exists and is readable).</summary>
    public SessionVerificationStatus VerificationStatus { get; private set; } = SessionVerificationStatus.NotLinked;

    /// <summary>The first prompt snippet from the verified .jsonl file.</summary>
    public string? VerifiedFirstPrompt { get; private set; }

    /// <summary>The expected first prompt to verify against (set from persisted state).</summary>
    public string? ExpectedFirstPrompt { get; set; }

    /// <summary>Fires when verification status changes.</summary>
    public event Action<SessionVerificationStatus>? OnVerificationStatusChanged;

    /// <summary>User-defined display name for this session. Null means use default (repo folder name).</summary>
    public string? CustomName { get; set; }

    /// <summary>
    /// True when <see cref="CustomName"/> was AUTO-composed at birth (no human/explicit name); false once a
    /// human or the session itself explicitly renamed it (automatic session roles, chunk 3). A self/human
    /// name always wins and must never be re-auto-named - this is the marker that gates any future
    /// auto-rename. Set at birth by the create path and cleared by an explicit
    /// <see cref="SessionManager.RenameSession"/>. Persisted so the auto-vs-explicit distinction survives a
    /// restart.
    /// </summary>
    public bool IsAutoNamed { get; set; }

    /// <summary>
    /// The structured question, plan, or permission ask the agent is currently
    /// waiting on, or null when nothing is pending. Cleared automatically when the
    /// activity state moves out of
    /// <see cref="ActivityState.WaitingForInput"/> / <see cref="ActivityState.WaitingForPerm"/>.
    /// Volatile state; not persisted across Director restarts.
    /// </summary>
    public PendingInteraction? PendingInteraction { get; private set; }

    /// <summary>User-chosen header color (hex string like "#2563EB"). Null means default dark header.</summary>
    public string? CustomColor { get; set; }

    /// <summary>Links this session to a SessionHistoryEntry for persistent workspace tracking.</summary>
    public Guid? HistoryEntryId { get; set; }

    /// <summary>Raw terminal output captured during Claude Code startup. Preserved for future parsing.</summary>
    public string? RawStartupText { get; set; }

    /// <summary>Terminal-based verification status (matching terminal to .jsonl).</summary>
    public TerminalVerificationStatus TerminalVerificationStatus { get; private set; } = TerminalVerificationStatus.Waiting;

    /// <summary>Fires when terminal verification status changes.</summary>
    public event Action<TerminalVerificationStatus>? OnTerminalVerificationStatusChanged;

    /// <summary>Number of confirmation attempts made (at 50+ lines). Allows retries up to a limit.</summary>
    private volatile int _confirmationAttempts;

    /// <summary>Max confirmation attempts before giving up permanently.</summary>
    private const int MaxConfirmationAttempts = 5;

    /// <summary>Guard to prevent concurrent verification runs.</summary>
    private int _verificationRunning;

    /// <summary>
    /// Mark this session as pre-verified (for restored sessions that already have a ClaudeSessionId).
    /// This skips terminal verification since the session was previously verified.
    /// </summary>
    public void MarkAsPreVerified()
    {
        if (!string.IsNullOrEmpty(ClaudeSessionId))
        {
            _confirmationAttempts = MaxConfirmationAttempts;
            SetTerminalVerificationStatus(TerminalVerificationStatus.Matched);
        }
    }

    /// <summary>JSONL history snapshots for rewind/fork support.</summary>
    public SessionHistory? History { get; private set; }

    /// <summary>
    /// Initialize the session history tracker once the ClaudeSessionId is known.
    /// Must be called after ClaudeSessionId is set.
    /// </summary>
    public void InitializeHistory()
    {
        if (History != null)
            return;

        if (string.IsNullOrEmpty(ClaudeSessionId))
        {
            FileLog.Write("[Session] InitializeHistory: no ClaudeSessionId, skipping");
            return;
        }

        FileLog.Write($"[Session] InitializeHistory: sessionId={ClaudeSessionId}");
        History = new SessionHistory(ClaudeSessionId, RepoPath);
    }

    /// <summary>Chat messages for the Simple Chat view.</summary>
    public SessionChatHistory ChatHistory { get; } = new();

    private string? _pendingPromptText;

    /// <summary>
    /// Prompt text the user was composing but hasn't sent yet. Persisted across
    /// switches and restarts. Two writers exist: the UI when the user types (via
    /// the property setter, source="user"), and the SessionStatusWingman when
    /// it detects Claude Code has injected a suggestion into its own input line
    /// (via <see cref="SetPendingPromptText"/>, source="wingman"). Subscribers
    /// to <see cref="OnPendingPromptTextChanged"/> can distinguish the two and
    /// decide whether to apply.
    /// </summary>
    public string? PendingPromptText
    {
        get => _pendingPromptText;
        set => SetPendingPromptText(value, "user");
    }

    /// <summary>
    /// Fires when <see cref="PendingPromptText"/> changes. Args: (newText, source).
    /// source is "user" for property-setter writes, or whatever string the caller
    /// passes to <see cref="SetPendingPromptText"/> — currently "wingman" for
    /// terminal-injection detection.
    /// </summary>
    public event Action<string?, string>? OnPendingPromptTextChanged;

    /// <summary>
    /// Set the pending prompt text with an explicit source tag. Idempotent: a
    /// write with the same value as the current one does not fire the event.
    /// </summary>
    public void SetPendingPromptText(string? value, string source)
    {
        if (_pendingPromptText == value) return;
        _pendingPromptText = value;
        OnPendingPromptTextChanged?.Invoke(value, source ?? "user");
    }

    /// <summary>Name of the last selected tab (e.g. "Terminal", "Agent", "SourceControl"). Persisted across switches and restarts.</summary>
    public string? SelectedTabName { get; set; }

    /// <summary>Queue of prompts the user wants to send later. Persisted across switches and restarts.</summary>
    public PromptQueue PromptQueue { get; } = new();

    /// <summary>Order in the session list, used to restore UI order after restart.</summary>
    public int SortOrder { get; set; }

    /// <summary>Fires when ActivityState changes. Args: (oldState, newState).</summary>
    public event Action<ActivityState, ActivityState>? OnActivityStateChanged;

    /// <summary>
    /// Fires exactly once when the underlying process exits, carrying the exit code.
    /// Lets the <see cref="SessionManager"/> reap a session whose agent process died on
    /// its own (clean exit) so it does not linger as a dead "Exited" row with no process
    /// behind it. Abnormal exits are deliberately left in place for crash recovery (#212).
    /// </summary>
    public event Action<int>? OnExited;

    // ---------- Wingman turn briefing (TURN_BRIEFING.md; orthogonal to ActivityState) ----------

    /// <summary>
    /// The wingman briefing-pipeline state for the CURRENT turn. Orthogonal to
    /// <see cref="ActivityState"/> (a session can ask AND keep working). Since issue #187
    /// deleted the Director-side pipeline, NOTHING on the Director writes this anymore -
    /// it stays None and the GATEWAY stamps the real value onto the aggregated session
    /// view. Kept because the local status wingman still reacts to it (and a future
    /// gateway push-down could write it).
    /// </summary>
    public BriefingState BriefingState { get; private set; } = BriefingState.None;

    /// <summary>Fires when <see cref="BriefingState"/> changes.</summary>
    public event Action<BriefingState>? OnBriefingStateChanged;

    internal void SetBriefingState(BriefingState state)
    {
        if (_disposed || BriefingState == state) return;
        BriefingState = state;
        OnBriefingStateChanged?.Invoke(state);
    }

    /// <summary>
    /// The latest stored brief's railLine (the &lt;=8-word needs-you one-liner). Since
    /// issue #187 deleted the Director-side pipeline this stays null on the Director;
    /// the GATEWAY stamps the real value onto the aggregated session view.
    /// </summary>
    public string? LatestBriefRailLine { get; internal set; }

    // ---------- Mobile mode + proactive wingman explain (remote experience) ----------

    /// <summary>
    /// Which mobile view a phone is currently watching this session through, set by the active
    /// mobile tab via the Control API (Session tab -> Text, Voice tab -> Voice; Off when no phone
    /// is watching). Single source of truth for the mobile experience; <see cref="MobileMode"/>
    /// and <see cref="VoiceMode"/> are derived from it. Off by default. Not persisted: it tracks
    /// what a remote viewer is looking at right now, not durable session state.
    /// </summary>
    public MobileViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (_viewMode == value) return;
            var old = _viewMode;
            _viewMode = value;
            OnViewModeChanged?.Invoke(old, value);
        }
    }
    private MobileViewMode _viewMode = MobileViewMode.Off;

    /// <summary>
    /// Raised when <see cref="ViewMode"/> changes (old, new). The desktop session view-model
    /// listens so the in-voice-mode ear indicator (issue #554) appears and clears live as a
    /// phone enters and leaves the Voice tab, without waiting for a list rebuild.
    /// </summary>
    public event Action<MobileViewMode, MobileViewMode>? OnViewModeChanged;

    /// <summary>
    /// True when a phone is actively watching this session in either mobile mode (Text or Voice).
    /// Gates the proactive wingman "explain" briefing regeneration (see <see cref="CachedExplainText"/>)
    /// so the Opus cost stays off sessions nobody is watching remotely. Derived from <see cref="ViewMode"/>.
    /// </summary>
    public bool MobileMode => ViewMode != MobileViewMode.Off;

    /// <summary>
    /// True when the active mobile view is the Voice (in-car) tab. The wingman keys its remark
    /// STYLE off this -- spoken-friendly remarks while it holds -- but that read lives in the
    /// wingman, not here. Derived from <see cref="ViewMode"/>.
    /// </summary>
    public bool VoiceMode => ViewMode == MobileViewMode.Voice;

    /// <summary>
    /// Where this session sits in the hold state machine: the user's "I do not want to deal with this one
    /// right now". Design and diagram: docs/architecture/session-state-machine-2026-07-14.html.
    ///
    /// This ONE field replaces what used to be three that could disagree (a public OnHold flag, a private
    /// turn-in-flight latch gating the auto-lift, and a private pending-hold flag). Both questions the hold
    /// has to answer - "should this hold be deferred?" and "may this hold lift?" - now read the same
    /// authoritative fact, <see cref="ActivityState"/>, so they cannot fall out of step with each other.
    ///
    /// Runtime-only (not persisted across a Director restart): it tracks what the user is currently
    /// choosing to defer, not durable session state.
    /// </summary>
    public HoldState HoldState
    {
        get => _holdState;
        private set
        {
            if (_holdState == value) return;
            var wasOnHold = OnHold;
            _holdState = value;
            // OnHoldChanged is the "is it parked?" signal (rail strip, FIFO conductor), so it fires only
            // when that answer actually flips - None <-> DeferredHold does not park anything.
            if (OnHold != wasOnHold) OnHoldChanged?.Invoke(OnHold);
            // HoldStateChanged fires on EVERY transition: a client's label distinguishes DeferredHold
            // ("Working, snoozing when done") from None, so it must hear about that edge too.
            HoldStateChanged?.Invoke(value);
        }
    }
    private HoldState _holdState;

    /// <summary>
    /// True when the session is parked right now. Derived from <see cref="HoldState"/>; a DeferredHold is
    /// NOT parked yet - the user asked for it while the agent was working and it lands when the work stops.
    /// Read-only: every transition goes through <see cref="RequestHold"/> or the machine in
    /// <see cref="SetActivityState"/>, so no caller can put the state machine into a state it cannot reach.
    /// </summary>
    public bool OnHold => _holdState == HoldState.Held;

    /// <summary>True when the agent is producing output right now. The single authoritative input to the
    /// hold machine: it decides both whether an incoming hold defers and whether a held session lifts.
    /// Starting counts as working - a session that has not settled yet has a turn ahead of it.</summary>
    private bool IsWorking => ActivityState is ActivityState.Working or ActivityState.Starting;

    /// <summary>Fires when <see cref="OnHold"/> changes. Arg: new value. The desktop
    /// session list subscribes so the color strip can repaint (held) without
    /// the wingman touching <see cref="StatusColor"/>; OnHold sits on top of it.</summary>
    public event Action<bool>? OnHoldChanged;

    /// <summary>Fires on EVERY <see cref="HoldState"/> transition, including ones that leave
    /// <see cref="OnHold"/> unchanged (None &lt;-&gt; DeferredHold). The Control API subscribes so a hold
    /// change is pushed to the Gateway the instant it happens, exactly as an activity change already is -
    /// without it, a hold toggle is invisible to every other screen until the next 10-second heartbeat.</summary>
    public event Action<HoldState>? HoldStateChanged;

    /// <summary>Outcome of a <see cref="RequestHold"/> call.</summary>
    public enum HoldOutcome
    {
        /// <summary>The hold was applied immediately (the session was not working).</summary>
        Held,
        /// <summary>The session was working, so the hold was DEFERRED and lands when the work stops.</summary>
        Pending,
        /// <summary>The session was taken OFF hold, clearing any pending deferral too.</summary>
        Released,
    }

    /// <summary>
    /// Request an EXPLICIT hold / un-hold - the user pressing Snooze. Un-hold clears the hold and any
    /// pending deferral at once. A hold requested while the agent is WORKING is deferred (the user's "hold
    /// this one when it finishes"); a hold requested while it is settled applies immediately. Returns which
    /// of those happened so the caller's button can say what it did.
    ///
    /// The defer decision reads <see cref="IsWorking"/> - the live state - deliberately NOT a turn latch.
    /// The old latch was armed only by a submitted turn and destroyed by any 10 seconds of terminal quiet
    /// (see TerminalStateDetector.QuietThreshold), which an ordinary slow command produces mid-turn, so a
    /// hold requested after such a gap read as "no turn in flight" and was applied immediately instead of
    /// deferred - and could then never lift itself.
    /// </summary>
    public HoldOutcome RequestHold(bool onHold)
    {
        if (!onHold)
        {
            HoldState = HoldState.None;
            return HoldOutcome.Released;
        }
        if (IsWorking)
        {
            FileLog.Write($"[Session] Hold requested while working - deferring until it stops: session={Id}");
            HoldState = HoldState.DeferredHold;
            return HoldOutcome.Pending;
        }
        HoldState = HoldState.Held;
        return HoldOutcome.Held;
    }

    /// <summary>
    /// Whether this session participates in the Wingman experience: the auto-explain
    /// briefing on turn-end, the Voice/Wingman tabs, and the Yellow "Wingman is reading"
    /// state. OFF by default for every new session (the Wingman is not reliable enough
    /// yet to opt every session in). When OFF the session behaves like a plain terminal:
    /// ProactiveExplainService skips it, the dot goes straight Blue->Red, and the clients
    /// hide the Voice + Wingman tabs. Opt in per session via the context menu / new-session
    /// dialog. Durable per session (persisted via <see cref="PersistedSession.WingmanEnabled"/>).
    /// </summary>
    public bool WingmanEnabled { get; set; } = false;

    /// <summary>
    /// Scheduled-run auto-dismiss (issue #1200): true when this session is an AUTOMATED run (a cron seed)
    /// that should close itself when it finishes with nothing needing a human. Only sessions with this set
    /// are ever auto-closed; a human-started session leaves it false and is never touched. Set once at birth
    /// from <see cref="Gateway.Contracts.NewSessionRequest.AutoDismiss"/>. See <see cref="DismissVerdict"/>.
    /// </summary>
    public bool AutoDismiss { get; set; } = false;

    /// <summary>
    /// Scheduled-run auto-dismiss (issue #1200): the agent's explicit end-of-run verdict, parsed from the
    /// finished turn's final message (a <c>CC-DISMISS</c> block, see <see cref="Wingman.DismissVerdictSignal"/>).
    /// <c>"done"</c> = nothing needs the human (safe to auto-close); <c>"needs-human"</c> = keep it open.
    /// Null until a verdict is seen - the conservative default that guarantees a session is never auto-closed
    /// without an explicit <c>done</c>. Set on the Director via <see cref="SetDismissVerdict"/>; flows to the
    /// Gateway on <see cref="Gateway.Contracts.SessionDto.DismissVerdict"/>.
    /// </summary>
    public string? DismissVerdict { get; private set; }

    /// <summary>
    /// Record the agent's parsed end-of-run verdict for auto-dismiss (issue #1200). Idempotent per value:
    /// only logs on a change. A null/blank argument clears it (a fresh turn that produced no verdict). This
    /// is the only writer of <see cref="DismissVerdict"/>.
    /// </summary>
    public void SetDismissVerdict(string? verdict)
    {
        var normalized = string.IsNullOrWhiteSpace(verdict) ? null : verdict.Trim().ToLowerInvariant();
        if (string.Equals(normalized, DismissVerdict, StringComparison.Ordinal))
            return;
        DismissVerdict = normalized;
        FileLog.Write($"[Session] SetDismissVerdict: session={Id} verdict={normalized ?? "(cleared)"}");
    }

    /// <summary>
    /// The <see cref="WingmanEnabled"/> value captured just before voice mode was enabled
    /// for this session, so the prior state can be restored when voice mode ends. Null when
    /// voice mode is not active (the value is transient and is not persisted). Voice mode
    /// requires the Wingman for reply summarization, so it force-enables WingmanEnabled on
    /// entry regardless of the user's normal setting; this field records what to restore.
    /// </summary>
    public bool? PreVoiceWingmanEnabled { get; set; } = null;

    private bool _isExplaining;

    /// <summary>
    /// True while <c>ProactiveExplainService</c> has a Wingman briefing in flight for
    /// this session. Set just before the call, cleared in <c>finally</c>. Transient
    /// (in-memory only). The Yellow status is keyed off this flag together with
    /// <see cref="WingmanEnabled"/>; <see cref="OnIsExplainingChanged"/> notifies the
    /// SessionStatusWingman so it can repaint the dot.
    /// </summary>
    public bool IsExplaining
    {
        get => _isExplaining;
        set
        {
            if (_isExplaining == value) return;
            _isExplaining = value;
            OnIsExplainingChanged?.Invoke(value);
        }
    }

    /// <summary>Fires when <see cref="IsExplaining"/> changes. Arg: new value.</summary>
    public event Action<bool>? OnIsExplainingChanged;

    private bool _isTranscribing;

    /// <summary>
    /// True while a dictated utterance is being transcribed and submitted into this session in the
    /// background: the desktop Speak dialog released the screen the instant Send was pressed, and the
    /// recorded audio is being transcribed off the UI thread (see the desktop background dictation
    /// send). A user-driven overlay ORTHOGONAL to <see cref="ActivityState"/>, exactly like
    /// <see cref="IsExplaining"/>: the underlying activity state is still reported truthfully, and this
    /// flag sits on top so <c>SessionStatusWingman</c> paints the badge Orange ("Transcribing...") so
    /// nobody else starts typing into the session mid-dictation. Set true when Send is pressed and
    /// cleared when the background transcribe-and-submit finishes or fails. Transient (in-memory only);
    /// <see cref="OnIsTranscribingChanged"/> notifies the SessionStatusWingman so it can repaint the dot.
    /// </summary>
    public bool IsTranscribing
    {
        get => _isTranscribing;
        set
        {
            if (_isTranscribing == value) return;
            _isTranscribing = value;
            OnIsTranscribingChanged?.Invoke(value);
        }
    }

    /// <summary>Fires when <see cref="IsTranscribing"/> changes. Arg: new value. The
    /// SessionStatusWingman subscribes so it can repaint the badge Orange/back.</summary>
    public event Action<bool>? OnIsTranscribingChanged;

    private bool _isBackgroundRunning;
    private string _backgroundReason = "running in background";

    /// <summary>
    /// True when the Wingman has read the screen and determined this session is parked
    /// waiting on its OWN background task (a long build, "N shell still running") rather
    /// than on the user. A Wingman-owned overlay ORTHOGONAL to <see cref="ActivityState"/>,
    /// exactly like <see cref="IsExplaining"/>: the <c>TerminalStateDetector</c> still reports
    /// the true underlying <see cref="ActivityState.WaitingForInput"/> (the dumb 10s silence
    /// timer cannot tell a background-wait apart from "your turn"), and this flag sits on top
    /// so <c>SessionStatusWingman</c> can paint the badge Purple ("running in background")
    /// instead of Red ("needs you"). Set by <c>ProactiveExplainService</c> from the explain
    /// verdict via <see cref="SetBackgroundRunning"/>; auto-cleared the moment real output
    /// resumes (the session transitions off WaitingForInput in <see cref="SetActivityState"/>).
    /// Transient (in-memory only); it tracks a live read of the screen, not durable state.
    /// </summary>
    public bool IsBackgroundRunning
    {
        get => _isBackgroundRunning;
        private set
        {
            if (_isBackgroundRunning == value) return;
            _isBackgroundRunning = value;
            OnIsBackgroundRunningChanged?.Invoke(value);
        }
    }

    /// <summary>Short reason for the Purple background state, shown as the badge tooltip,
    /// e.g. "running in background". Set alongside <see cref="IsBackgroundRunning"/>.</summary>
    public string BackgroundReason => _backgroundReason;

    /// <summary>Fires when <see cref="IsBackgroundRunning"/> changes. Arg: new value. The
    /// SessionStatusWingman subscribes so it can repaint the badge Purple/Red.</summary>
    public event Action<bool>? OnIsBackgroundRunningChanged;

    /// <summary>
    /// Set (or clear) the Wingman's "parked on a background task" verdict for this session.
    /// Sole caller is <c>ProactiveExplainService</c> after an explain briefing. Pass a short
    /// reason when <paramref name="running"/> is true (used as the badge tooltip); clearing
    /// resets the reason to the default. The flag only affects the badge while the session is
    /// parked at a turn-end (see <c>SessionStatusWingman.ColorFor</c>).
    /// </summary>
    public void SetBackgroundRunning(bool running, string? reason = null)
    {
        // string.IsNullOrWhiteSpace is annotated [NotNullWhen(false)], so in the else branch
        // the compiler already knows reason is non-null -- no null-forgiving operator needed.
        if (running)
            _backgroundReason = string.IsNullOrWhiteSpace(reason) ? "running in background" : reason.Trim();
        else
            _backgroundReason = "running in background";
        IsBackgroundRunning = running;
    }

    /// <summary>
    /// True until the user submits real input for the first time. New sessions boot with
    /// this flag set so the ProactiveExplainService can skip the first turn-end briefing
    /// (there is nothing yet to explain) and the Wingman tab can show a canned greeting
    /// instead. Cleared on the first <see cref="SendInput"/> with a submit byte or the
    /// first <see cref="SendTextAsync"/>. Restored sessions start with this <c>false</c>
    /// because they already have history.
    /// </summary>
    public bool IsBrandNew { get; set; } = true;

    /// <summary>Latest proactively-generated wingman briefing, or null if none yet.</summary>
    public string? CachedExplainText { get; private set; }

    /// <summary>When <see cref="CachedExplainText"/> was last generated (UTC).</summary>
    public DateTime? CachedExplainAt { get; private set; }

    /// <summary>Model that produced the cached briefing (e.g. "opus").</summary>
    public string? CachedExplainModel { get; private set; }

    /// <summary>Tap-to-answer options from the latest briefing (may be empty).</summary>
    public IReadOnlyList<string> CachedQuickReplies { get; private set; } = System.Array.Empty<string>();

    /// <summary>One-line headline from the latest briefing for the session card / list view.</summary>
    public string? CachedExplainHeadline { get; private set; }

    /// <summary>Latest briefing's on-screen "what's happened" QUICK line (one short sentence, scan-friendly).</summary>
    public string? CachedExplainWhatHappened { get; private set; }

    /// <summary>Latest briefing's on-screen "what's happened" LONGER detail (1-2 short paragraphs, may contain a markdown table).</summary>
    public string? CachedExplainLongDescription { get; private set; }

    /// <summary>Latest briefing's on-screen "what Claude wants" section (verbatim agent question when state is red).</summary>
    public string? CachedExplainWhatClaudeWants { get; private set; }

    /// <summary>Latest briefing's trust anchor: the agent's decisive line copied verbatim from the
    /// terminal (server-side validated, empty when unverified or nothing pending). Rendered as
    /// Claude's own words above <see cref="CachedExplainWhatClaudeWants"/> so a drifting summary
    /// is visible against it - mirrors the turn brief's verbatim evidence.</summary>
    public string? CachedExplainClaudeVerbatim { get; private set; }

    /// <summary>Latest briefing's spoken-version field, used by the phone's voice mode on demand. No markdown.</summary>
    public string? CachedExplainSay { get; private set; }

    /// <summary>
    /// Store a freshly-generated proactive explain briefing. Only replaces the cache when
    /// <paramref name="text"/> is non-empty, so a failed or timed-out regeneration preserves
    /// the last good briefing instead of blanking the phone screen on a huge-context turn.
    /// </summary>
    public void SetCachedExplain(string? text, string? model, IReadOnlyList<string>? quickReplies = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        CachedExplainText = text;
        CachedExplainModel = model;
        CachedQuickReplies = quickReplies ?? System.Array.Empty<string>();
        CachedExplainAt = DateTime.UtcNow;
        OnCachedExplainChanged?.Invoke();
    }

    /// <summary>Fires after <see cref="SetCachedExplain"/> stores a new briefing. The
    /// Wingman tab subscribes so it can re-render whenever the proactive explain pipeline
    /// has produced a fresh result. Re-renders read the structured fields off the session
    /// directly; the event carries no payload.</summary>
    public event Action? OnCachedExplainChanged;

    /// <summary>
    /// Store the structured fields from a freshly-generated explain briefing alongside the
    /// joined text. Fields are independent of <see cref="SetCachedExplain"/> so the caller
    /// can update them in one shot from <see cref="WingmanAskResult"/>.
    /// </summary>
    public void SetCachedExplainStructured(string? headline, string? whatHappened, string? longDescription, string? whatClaudeWants, string? say, string? claudeVerbatim = null)
    {
        CachedExplainHeadline = string.IsNullOrWhiteSpace(headline) ? null : headline.Trim();
        CachedExplainWhatHappened = string.IsNullOrWhiteSpace(whatHappened) ? null : whatHappened.Trim();
        CachedExplainLongDescription = string.IsNullOrWhiteSpace(longDescription) ? null : longDescription.Trim();
        CachedExplainWhatClaudeWants = string.IsNullOrWhiteSpace(whatClaudeWants) ? null : whatClaudeWants.Trim();
        CachedExplainClaudeVerbatim = string.IsNullOrWhiteSpace(claudeVerbatim) ? null : claudeVerbatim.Trim();
        CachedExplainSay = string.IsNullOrWhiteSpace(say) ? null : say.Trim();
    }

    /// <summary>
    /// Aggregate at-a-glance color for this session. Owned by the
    /// SessionStatusWingman on the Director; the rest of the system reads it but never
    /// writes it. Defaults to "blue" ("working/starting") at construction, which the
    /// wingman immediately confirms. The detector only ever drives blue (working) and
    /// red (needs you); "unknown" is used for an exited session.
    /// </summary>
    public string StatusColor { get; private set; } = "blue";

    /// <summary>
    /// Short human-readable reason for the current StatusColor, e.g.
    /// "session created", "working", "waiting for input", "clean turn". Shown
    /// as the dot tooltip in the Gateway directory view. Set together with
    /// <see cref="StatusColor"/> via <see cref="SetStatusColor"/>.
    /// </summary>
    public string LastStatusReason { get; private set; } = "session created";

    /// <summary>
    /// The verbatim text of the most recent prompt the user submitted to this session,
    /// or null if none has been seen. Its only source was the Claude Code
    /// <c>UserPromptSubmit</c> hook, which has been removed (terminal-driven detection
    /// does not parse user prompts), so it is currently always null. Kept because
    /// <c>GET /sessions/{sid}/wingman</c> surfaces it; a terminal-derived source can
    /// repopulate it later.
    /// </summary>
    public string? LastUserPrompt { get; private set; }

    /// <summary>UTC time <see cref="LastUserPrompt"/> was captured, or null if none.</summary>
    public DateTime? LastUserPromptAt { get; private set; }

    /// <summary>
    /// UTC time this session was flagged for deletion via the Control API
    /// (POST /sessions/{id}/request-deletion), or null if it is not flagged. When set, the
    /// Director's deletion reaper removes the session on its next sweep once the grace window
    /// has elapsed AND the session is not actively Working. Set/cleared via
    /// <see cref="MarkForDeletion"/> / <see cref="CancelDeletion"/>. The common case is a session
    /// flagging ITSELF: an unattended run that has nothing left for the user asks to be reaped and
    /// then finishes its turn normally - the reaper does the removal asynchronously, so the calling
    /// process is never yanked out from under an in-flight request. In-memory only (not persisted):
    /// a session is reaped within ~a minute, so it effectively never survives to a Director restart.
    /// </summary>
    public DateTime? DeletionRequestedAt { get; private set; }

    /// <summary>Short human reason captured when <see cref="DeletionRequestedAt"/> was set
    /// (e.g. "jobs-auto: nothing to report"), surfaced in the roster tooltip. Null when not flagged.</summary>
    public string? DeletionReason { get; private set; }

    /// <summary>True when this session has been flagged for deletion and is awaiting the reaper.</summary>
    public bool PendingDeletion => DeletionRequestedAt.HasValue;

    /// <summary>Fires when StatusColor changes. Args: (oldColor, newColor, reason).</summary>
    public event Action<string, string, string>? OnStatusColorChanged;

    /// <summary>
    /// One decision the SessionStatusWingman wrote onto this session: what color it
    /// chose, what reason, when, and which path produced it ("activity" | "turn-summary"
    /// | "promote" | "init" | "buffer-marker").
    /// </summary>
    public sealed record WingmanEvent(
        DateTime At,
        string OldColor,
        string NewColor,
        string Reason,
        bool Llm = false);

    private const int WingmanEventLogCapacity = 50;
    private readonly LinkedList<WingmanEvent> _wingmanEvents = new();
    private readonly object _wingmanEventsLock = new();

    /// <summary>
    /// One actuation the Wingman performed on this session's terminal (structured-intent
    /// path): the action kind ("type" | "send_keys" | "submit"), a short detail of what
    /// was sent, and the Wingman's stated reason. Distinct from <see cref="WingmanEvent"/>
    /// (a colour change) - this is a WRITE the Wingman made, and the audit trail for it.
    /// </summary>
    public sealed record WingmanActionRecord(
        DateTime At,
        string Action,
        string Detail,
        string Reason);

    private const int WingmanActionLogCapacity = 50;
    private readonly LinkedList<WingmanActionRecord> _wingmanActions = new();
    private readonly object _wingmanActionsLock = new();

    /// <summary>
    /// One activity-state transition for this session: when it happened and the state it
    /// moved from -&gt; to. The detector's only rule produces Working (bytes) and
    /// WaitingForInput (silence), so in practice this is the blue&lt;-&gt;red history the
    /// Wingman tab renders. Distinct from <see cref="WingmanEvent"/>, which records the
    /// resulting colour change rather than the underlying state.
    /// </summary>
    public sealed record StateChange(
        DateTime At,
        ActivityState From,
        ActivityState To);

    private const int StateChangeLogCapacity = 100;
    private readonly LinkedList<StateChange> _stateChanges = new();
    private readonly object _stateChangesLock = new();

    /// <summary>
    /// UTC time the terminal buffer last received ANY bytes (raw "characters moved",
    /// before the detector's cosmetic-vs-content filtering). The Wingman tab shows
    /// "how long ago the terminal moved" off this; a large value next to a "working"
    /// badge is the tell that the quiet gate has stalled. Updated on every buffer write.
    /// </summary>
    public DateTime LastOutputAtUtc => new(Volatile.Read(ref _lastOutputTicks), DateTimeKind.Utc);
    private long _lastOutputTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// UTC time the screen BODY last changed, as judged by the <c>TerminalStateDetector</c>'s
    /// content rule. For agents whose idle terminal never goes byte-silent (Grok), this is the
    /// honest idle clock: <see cref="LastOutputAtUtc"/> keeps moving forever as the animated
    /// footer repaints, but this only advances when the conversation body actually changes, so
    /// "time since the last body change" is a true measure of how long the agent has been idle.
    /// The Control API surfaces idle seconds off this for continuous-idle agents. Defaults to the
    /// creation time; only the detector advances it, via <see cref="StampBodyActivity"/>.
    /// </summary>
    public DateTime LastBodyActivityAtUtc => new(Volatile.Read(ref _lastBodyActivityTicks), DateTimeKind.Utc);
    private long _lastBodyActivityTicks = DateTime.UtcNow.Ticks;

    /// <summary>Record that the screen body changed now. Called by the detector's content rule;
    /// independent of whether the detector is driving state, so the idle clock is correct even in
    /// observe-only mode.</summary>
    internal void StampBodyActivity() => Volatile.Write(ref _lastBodyActivityTicks, DateTime.UtcNow.Ticks);

    /// <summary>
    /// UTC instant until which terminal byte activity must NOT be counted as agent work by
    /// the <c>TerminalStateDetector</c>. Set whenever the Director itself issues a PTY resize
    /// (on attaching/switching to a session, force-refresh, or a layout change): a resize is a
    /// SIGWINCH-equivalent that makes Claude Code repaint its whole screen, emitting a burst of
    /// real bytes that are OUR doing, not the agent producing output. Without this guard the
    /// detector flips an idle session to "Working" the instant you switch to it. The window is
    /// short (well under the detector's quiet threshold), so a genuine work-start that happens
    /// to land inside it is only delayed until the next byte after the window. Read by the
    /// detector; written via <see cref="SuppressActivityFor"/>.
    /// </summary>
    public DateTime SuppressActivityUntilUtc => new(Volatile.Read(ref _suppressActivityUntilTicks), DateTimeKind.Utc);
    private long _suppressActivityUntilTicks = DateTime.MinValue.Ticks;

    /// <summary>
    /// Mark the next <paramref name="window"/> of terminal byte activity as a Director-induced
    /// repaint that the <c>TerminalStateDetector</c> must ignore. Called right before a PTY
    /// resize. Always extends (never shortens) the current suppression window.
    /// </summary>
    public void SuppressActivityFor(TimeSpan window)
    {
        if (_disposed) return;
        var until = DateTime.UtcNow.Add(window).Ticks;
        long current;
        do
        {
            current = Volatile.Read(ref _suppressActivityUntilTicks);
            if (until <= current) return;
        }
        while (Interlocked.CompareExchange(ref _suppressActivityUntilTicks, until, current) != current);
    }

    /// <summary>
    /// Most recent activity-state transitions for this session, newest first. Ring-buffered
    /// at <see cref="StateChangeLogCapacity"/>. Populated by <see cref="RecordStateChange"/>
    /// (from <see cref="SetActivityState"/>) and rendered live by the Wingman tab.
    /// </summary>
    public IReadOnlyList<StateChange> RecentStateChanges
    {
        get
        {
            lock (_stateChangesLock)
                return _stateChanges.ToList();
        }
    }

    /// <summary>Fires when a new state transition is recorded, so the Wingman tab can
    /// refresh without polling. No args; the listener re-reads
    /// <see cref="RecentStateChanges"/>.</summary>
    public event Action? OnStateChangeRecorded;

    /// <summary>
    /// Record an activity-state transition into the in-memory ring (for the live Wingman
    /// tab) and notify listeners. Durable persistence is the caller's concern (see
    /// <c>StateChangeLog</c>), keeping this type free of file I/O.
    /// </summary>
    private void RecordStateChange(ActivityState from, ActivityState to)
    {
        lock (_stateChangesLock)
        {
            _stateChanges.AddFirst(new StateChange(DateTime.UtcNow, from, to));
            while (_stateChanges.Count > StateChangeLogCapacity)
                _stateChanges.RemoveLast();
        }
        OnStateChangeRecorded?.Invoke();
    }

    /// <summary>
    /// Monotonic counter bumped on every <see cref="ActivityState"/> change. Used by
    /// <see cref="SetStatusColor"/> to scope colour-source precedence to the current
    /// state "generation" (issue #136 option C).
    /// </summary>
    private long _activityGeneration;

    /// <summary>
    /// Current activity-state generation (see <see cref="_activityGeneration"/>). An
    /// async colour writer (e.g. the ~10s turn-summary) can sample this when its turn
    /// ends and pass it back so its write is dropped if the state has since moved on.
    /// </summary>
    public long ActivityGeneration => Interlocked.Read(ref _activityGeneration);

    /// <summary>The source of the last accepted colour write, and the generation it
    /// was accepted in. Together they make a positive-evidence verdict sticky within
    /// its generation so a lower-confidence write cannot repaint over it.</summary>
    private StatusColorSource _lastColorSource = StatusColorSource.ActivityState;
    private long _lastColorGeneration;

    /// <summary>
    /// Most recent wingman decisions for this session, newest first. Ring-buffered
    /// at <c>WingmanEventLogCapacity</c>. Surfaced via <c>GET /sessions/{sid}/wingman</c>
    /// so the UI can show WHY a dot is the color it is.
    /// </summary>
    /// <summary>
    /// Most recent Wingman actuations on this session, newest first. Ring-buffered at
    /// <c>WingmanActionLogCapacity</c>. Written only by <c>WingmanActionExecutor</c> via
    /// <see cref="RecordWingmanAction"/>; surfaced via <c>GET /sessions/{sid}/wingman</c>.
    /// </summary>
    public IReadOnlyList<WingmanActionRecord> RecentWingmanActions
    {
        get
        {
            lock (_wingmanActionsLock)
                return _wingmanActions.ToList();
        }
    }

    /// <summary>UTC time of the last Wingman injection, or null if none. With
    /// <see cref="LastActedScreenHash"/> this is the executor's idempotency/cooldown guard.</summary>
    public DateTime? LastWingmanInjectionAt { get; private set; }

    /// <summary>Screen hash the Wingman last acted on, or null. Used to suppress a repeat
    /// action on an unchanged screen.</summary>
    public string? LastActedScreenHash { get; private set; }

    /// <summary>Record that the Wingman just injected against the given screen hash. Sole
    /// writer is <c>WingmanActionExecutor</c>, called once per performed action.</summary>
    public void MarkWingmanInjection(string screenHash)
    {
        LastWingmanInjectionAt = DateTime.UtcNow;
        LastActedScreenHash = screenHash;
    }

    /// <summary>Append a performed actuation to the audit ring.</summary>
    public void RecordWingmanAction(WingmanActionRecord rec)
    {
        lock (_wingmanActionsLock)
        {
            _wingmanActions.AddFirst(rec);
            while (_wingmanActions.Count > WingmanActionLogCapacity)
                _wingmanActions.RemoveLast();
        }
    }

    public IReadOnlyList<WingmanEvent> RecentWingmanEvents
    {
        get
        {
            lock (_wingmanEventsLock)
                return _wingmanEvents.ToList();
        }
    }

    /// <summary>
    /// Sole writer of <see cref="StatusColor"/>. Called by the
    /// SessionStatusWingman. No other code path may set the color — that's
    /// how we keep the UI a faithful mirror of the wingman's verdict.
    /// </summary>
    public void SetStatusColor(string color, string reason, bool llm = false,
        StatusColorSource source = StatusColorSource.ActivityState)
    {
        if (string.IsNullOrEmpty(color)) return;

        // Source precedence (issue #136 option C). Within one activity-state
        // generation, a positive-evidence verdict (a real on-screen question /
        // permission gate / corroborated needs-user) is sticky: a lower-confidence
        // write -- the activity-state mapping or a byte-stream guess -- cannot
        // repaint over it. This is what stops the badge flip-flopping. A genuine
        // state change bumps the generation (SetActivityState) and releases it.
        var gen = Interlocked.Read(ref _activityGeneration);
        if (gen == _lastColorGeneration
            && _lastColorSource == StatusColorSource.PositiveEvidence
            && source != StatusColorSource.PositiveEvidence)
        {
            FileLog.Write($"[Session] SetStatusColor dropped (lower precedence than sticky positive-evidence): color={color}, source={source}, gen={gen}");
            return;
        }

        var old = StatusColor;
        var newReason = reason ?? "";
        // Record precedence even when colour+reason are unchanged, so a repeated
        // positive-evidence verdict keeps (or re-establishes) its stickiness.
        _lastColorSource = source;
        _lastColorGeneration = gen;
        if (old == color && LastStatusReason == newReason) return;
        StatusColor = color;
        LastStatusReason = newReason;

        var evt = new WingmanEvent(DateTime.UtcNow, old, color, newReason, llm);
        lock (_wingmanEventsLock)
        {
            _wingmanEvents.AddFirst(evt);
            while (_wingmanEvents.Count > WingmanEventLogCapacity)
                _wingmanEvents.RemoveLast();
        }

        OnStatusColorChanged?.Invoke(old, color, LastStatusReason);
    }

    /// <summary>
    /// Flag this session for deletion. Idempotent: re-flagging refreshes the reason but keeps the
    /// ORIGINAL request time, so the grace window is always measured from the first request. Paints a
    /// sticky grey badge (the "unknown" colour every client already renders as gray) with a "Marked
    /// for deletion" reason, so the row visibly winds down on the desktop, phone, and CLI with no
    /// client change. The actual removal is done asynchronously by the Director's deletion reaper -
    /// this call never touches the process, so a session can safely flag ITSELF and still finish its
    /// turn. Uses <see cref="StatusColorSource.PositiveEvidence"/> so the wingman's activity mapping
    /// cannot repaint over the winding-down badge before the reaper removes it.
    /// </summary>
    public void MarkForDeletion(string? reason)
    {
        if (_disposed) return;
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        DeletionReason = trimmed;
        DeletionRequestedAt ??= DateTime.UtcNow;
        var why = trimmed is null
            ? "Marked for deletion - reaping shortly"
            : $"Marked for deletion - {trimmed}";
        SetStatusColor(Wingman.StatusColor.Unknown, why, source: StatusColorSource.PositiveEvidence);
        FileLog.Write($"[Session] MarkForDeletion: session={Id} reason={trimmed ?? "(none)"}");
    }

    /// <summary>Clear a pending-deletion flag (an operator cancelled the reap during the grace
    /// window). No-op when the session was not flagged.</summary>
    public void CancelDeletion()
    {
        if (DeletionRequestedAt is null) return;
        DeletionRequestedAt = null;
        DeletionReason = null;
        FileLog.Write($"[Session] CancelDeletion: session={Id}");
    }

    /// <summary>
    /// Drop the per-session Wingman context that describes the conversation BEFORE
    /// a <c>/clear</c>: the status-event log and the terminal replay buffer. Claude
    /// Code rotates its session id on <c>/clear</c> (new, empty JSONL transcript), so
    /// without this the Wingman keeps narrating the pre-clear conversation. NOT called
    /// for <c>/compact</c>, which keeps the conversation going. The turn-summary cache
    /// lives outside the Session and is cleared by its owner via
    /// <see cref="SessionManager.OnSessionContextReset"/>.
    /// </summary>
    public void ClearWingmanContext()
    {
        FileLog.Write($"[Session] ClearWingmanContext: session={Id}");
        lock (_wingmanEventsLock)
            _wingmanEvents.Clear();
        Buffer?.Clear();
    }

    // ---- Wingman goal management ----
    // The session's stated objective plus the Wingman's latest verdict on whether
    // the session is still working toward it. Goal-tracking is dormant until a goal
    // is set. Observational only: the verdict is surfaced, never auto-acted on.
    private readonly object _goalLock = new();
    private string? _wingmanGoal;
    private DateTime? _wingmanGoalSetAt;
    private string _wingmanGoalState = Gateway.Contracts.GoalStates.Unknown;
    private string _wingmanGoalReason = "";
    private DateTime? _wingmanGoalEvaluatedAt;

    /// <summary>The session's stated goal, or null if none set.</summary>
    public string? WingmanGoal { get { lock (_goalLock) return _wingmanGoal; } }

    /// <summary>UTC time the goal was last set, or null if none.</summary>
    public DateTime? WingmanGoalSetAt { get { lock (_goalLock) return _wingmanGoalSetAt; } }

    /// <summary>Latest goal verdict: on_track | drifting | complete | unknown.</summary>
    public string WingmanGoalState { get { lock (_goalLock) return _wingmanGoalState; } }

    /// <summary>Short plain-language reason for <see cref="WingmanGoalState"/>.</summary>
    public string WingmanGoalReason { get { lock (_goalLock) return _wingmanGoalReason; } }

    /// <summary>UTC time the goal was last assessed, or null if never.</summary>
    public DateTime? WingmanGoalEvaluatedAt { get { lock (_goalLock) return _wingmanGoalEvaluatedAt; } }

    /// <summary>
    /// Set (or clear) the session goal. Setting a new goal resets the verdict to
    /// "unknown" so a stale on_track/drifting/complete does not linger. Pass null
    /// or empty to clear the goal and stop goal-tracking.
    /// </summary>
    public void SetWingmanGoal(string? goal)
    {
        lock (_goalLock)
        {
            _wingmanGoal = string.IsNullOrWhiteSpace(goal) ? null : goal.Trim();
            _wingmanGoalSetAt = _wingmanGoal is null ? null : DateTime.UtcNow;
            _wingmanGoalState = Gateway.Contracts.GoalStates.Unknown;
            _wingmanGoalReason = "";
            _wingmanGoalEvaluatedAt = null;
        }
    }

    /// <summary>
    /// Record the Wingman's latest goal verdict. Ignored if no goal is set or the
    /// state is not one of the four valid values (we never store a fabricated verdict).
    /// </summary>
    public void SetWingmanGoalAssessment(string state, string reason, DateTime evaluatedAt)
    {
        if (!Gateway.Contracts.GoalStates.IsValid(state)) return;
        lock (_goalLock)
        {
            if (_wingmanGoal is null) return;
            _wingmanGoalState = state;
            _wingmanGoalReason = reason ?? "";
            _wingmanGoalEvaluatedAt = evaluatedAt;
        }
    }

    /// <summary>Access to the underlying backend for mode-specific operations.</summary>
    public ISessionBackend Backend => _backend;

    /// <summary>
    /// Create a new session with the specified backend.
    /// </summary>
    internal Session(
        Guid id,
        string repoPath,
        string workingDirectory,
        string? claudeArgs,
        ISessionBackend backend,
        SessionBackendType backendType,
        DateTimeOffset? createdAt = null)
    {
        Id = id;
        RepoPath = repoPath;
        WorkingDirectory = workingDirectory;
        ClaudeArgs = claudeArgs;
        _backend = backend;
        BackendType = backendType;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        Status = SessionStatus.Starting;

        // Subscribe to backend events
        _backend.ProcessExited += OnBackendProcessExited;
        _backend.StatusChanged += OnBackendStatusChanged;
        InitializeHtmlParser();
    }

    /// <summary>
    /// Create a session for restoring a persisted embedded session.
    /// </summary>
    internal Session(
        Guid id,
        string repoPath,
        string workingDirectory,
        string? claudeArgs,
        ISessionBackend backend,
        string? claudeSessionId,
        ActivityState activityState,
        DateTimeOffset createdAt,
        string? customName,
        string? customColor,
        string? pendingPromptText = null)
    {
        Id = id;
        RepoPath = repoPath;
        WorkingDirectory = workingDirectory;
        ClaudeArgs = claudeArgs;
        _backend = backend;
        BackendType = SessionBackendType.Embedded;
        ClaudeSessionId = claudeSessionId;
        ActivityState = activityState;
        CreatedAt = createdAt;
        CustomName = customName;
        CustomColor = customColor;
        PendingPromptText = pendingPromptText;
        Status = SessionStatus.Running;

        _backend.ProcessExited += OnBackendProcessExited;
        _backend.StatusChanged += OnBackendStatusChanged;
        InitializeHtmlParser();

        // Initialize history for restored sessions that already have a ClaudeSessionId
        InitializeHistory();
    }

    private void InitializeHtmlParser()
    {
        var buffer = _backend.Buffer;
        if (buffer is null)
        {
            FileLog.Write($"[Session] InitializeHtmlParser: sessionId={Id}, backend has no buffer (Embedded?), skipping");
            return;
        }

        _htmlCells = new TerminalCell[HtmlGridCols, HtmlGridRows];
        _htmlScrollback = new List<TerminalCell[]>();
        _htmlParser = new AnsiParser(_htmlCells, HtmlGridCols, HtmlGridRows, _htmlScrollback, HtmlMaxScrollback);

        // The live-attach parser tracks the real PTY size so its screen matches what a browser
        // xterm at the same geometry shows. It starts at the current PTY dimensions and is resized
        // in Resize() as the PTY changes.
        _streamGridCols = Math.Max(1, (int)CurrentCols);
        _streamGridRows = Math.Max(1, (int)CurrentRows);
        _streamCells = new TerminalCell[_streamGridCols, _streamGridRows];
        _streamScrollback = new List<TerminalCell[]>();
        _streamParser = new AnsiParser(_streamCells, _streamGridCols, _streamGridRows, _streamScrollback, StreamMaxScrollback);

        _htmlParserFeed = data =>
        {
            // Raw "the terminal moved" timestamp -- every byte, no cosmetic filtering.
            // The Wingman tab reads this to show how long ago output last appeared.
            Volatile.Write(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
            lock (_htmlParserLock)
            {
                _htmlParser?.Parse(data);
                _streamParser?.Parse(data);
                _streamBytesReflected += data.Length;
            }
        };
        buffer.OnBytesWritten += _htmlParserFeed;
        FileLog.Write($"[Session] InitializeHtmlParser: sessionId={Id}, grid={HtmlGridCols}x{HtmlGridRows}, streamGrid={_streamGridCols}x{_streamGridRows}, maxScrollback={HtmlMaxScrollback}");
    }

    /// <summary>
    /// Render the current terminal grid + scrollback as styled HTML, suitable
    /// for the "Raw terminal" tab in the HTML session view. Returns an empty
    /// string when the session has no backend buffer (Embedded mode).
    /// </summary>
    public string GetHtmlSnapshot()
    {
        if (_htmlParser is null || _htmlCells is null || _htmlScrollback is null)
            return string.Empty;

        lock (_htmlParserLock)
        {
            return AnsiToHtmlConverter.ConvertToHtml(_htmlScrollback, _htmlCells, HtmlGridCols, HtmlGridRows);
        }
    }

    /// <summary>
    /// Render scrollback and visible grid as two separate HTML strings, so the
    /// web client can render them into distinct DOM regions (scrollback above,
    /// sticky live grid at the viewport bottom). See
    /// <see cref="AnsiToHtmlConverter.ConvertToHtmlSplit"/> for the rationale.
    /// Returns ("", "", 0) when the session has no backend buffer.
    /// </summary>
    public (string ScrollbackHtml, string GridHtml, int ScrollbackCount) GetHtmlSnapshotSplit()
    {
        if (_htmlParser is null || _htmlCells is null || _htmlScrollback is null)
            return ("", "", 0);

        lock (_htmlParserLock)
        {
            var (sb, grid) = AnsiToHtmlConverter.ConvertToHtmlSplit(
                _htmlScrollback, _htmlCells, HtmlGridCols, HtmlGridRows);
            return (sb, grid, _htmlScrollback.Count);
        }
    }

    /// <summary>
    /// Snapshot the CURRENT visible terminal grid (not scrollback) as plain-text rows,
    /// trailing-trimmed, top to bottom. Unlike the raw byte buffer this is the RESOLVED
    /// on-screen state, so a spinner cell or a churning status line shows only its
    /// current value and old frames do not linger concatenated. The
    /// <c>TerminalStateDetector</c> uses this to tell a working spinner ("esc to
    /// interrupt" on screen) apart from an idle status-line repaint, which the raw
    /// byte stream cannot. Returns an empty array when there is no grid (Embedded mode).
    /// </summary>
    public string[] SnapshotScreenRows() => SnapshotScreenRowsWithCursor().Rows;

    /// <summary>
    /// True when the agent currently has the terminal in the alternate screen buffer
    /// (full screen mode, the <c>ESC[?1049h</c> sequence). While this is true the local
    /// scrollback is intentionally empty and terminal-based history capture does not work,
    /// so a consumer should classify the session as full screen and rely on a transcript
    /// provider (or screen reconstruction) instead of the scrollback. Reflects the live
    /// parser state at the moment of the call; it flips as the agent enters and leaves the
    /// alternate screen. False for Embedded sessions that have no server-side parser.
    /// </summary>
    public bool IsAlternateScreen
    {
        get
        {
            lock (_htmlParserLock)
                return _htmlParser?.IsAlternateScreen ?? false;
        }
    }

    /// <summary>
    /// True when the terminal application has requested bracketed paste mode (DEC private mode
    /// ?2004). This is read-only parser state; submit strategies can use it as a gate before
    /// sending bracketed paste delimiters.
    /// </summary>
    public bool BracketedPasteEnabled
    {
        get
        {
            lock (_htmlParserLock)
                return _htmlParser?.BracketedPasteEnabled ?? false;
        }
    }

    /// <summary>
    /// Like <see cref="SnapshotScreenRows"/> but also returns the live cursor cell
    /// (0-based grid row/col). The grid text and the cursor are captured under the
    /// same lock so they describe the same frame. This lets callers tell text the
    /// user (or Claude Code) actually authored in the input box apart from a dim
    /// history/autocomplete suggestion: the suggestion always lives to the RIGHT of
    /// the cursor. Returns CursorRow/CursorCol of -1 when there is no grid (Embedded mode).
    /// </summary>
    public (string[] Rows, int CursorRow, int CursorCol) SnapshotScreenRowsWithCursor()
    {
        if (_htmlCells is null || _htmlParser is null)
            return (System.Array.Empty<string>(), -1, -1);
        // Read the parser's ACTIVE grid, not our held _htmlCells array. On the alternate
        // screen (Grok, and now Claude Code) the parser draws into an internal buffer that
        // _htmlCells no longer points at, so iterating _htmlCells here would return the
        // frozen pre-alternate-screen content. SnapshotActiveRows reflects what is on screen.
        lock (_htmlParserLock)
            return _htmlParser.SnapshotActiveRows();
    }

    /// <summary>
    /// Snapshot the CURRENT visible terminal grid as rows of styled <see cref="ScreenSegment"/>
    /// runs, preserving the foreground/background colours and bold weight that
    /// <see cref="SnapshotScreenRows"/> throws away. Adjacent cells sharing a style are coalesced
    /// into one segment (matching how <c>AnsiToHtmlConverter</c> builds spans), trailing blank
    /// cells per row and trailing blank rows are trimmed. Returns an empty list when there is no
    /// grid (Embedded mode). Used by the turn-review log so a captured screen can be replayed in
    /// colour for a human reviewer.
    /// </summary>
    public List<IReadOnlyList<ScreenSegment>> SnapshotScreenColoredRows()
    {
        var result = new List<IReadOnlyList<ScreenSegment>>();
        if (_htmlCells is null || _htmlParser is null)
            return result;

        lock (_htmlParserLock)
        {
            for (int r = 0; r < HtmlGridRows; r++)
            {
                // Trim trailing blanks: the last column with a glyph or an explicit background.
                int lastCol = -1;
                for (int c = HtmlGridCols - 1; c >= 0; c--)
                {
                    var cell = _htmlCells[c, r];
                    if ((cell.Character != '\0' && cell.Character != ' ') || cell.Background != default)
                    {
                        lastCol = c;
                        break;
                    }
                }

                var row = new List<ScreenSegment>();
                if (lastCol >= 0)
                {
                    var sb = new System.Text.StringBuilder(HtmlGridCols);
                    string? curFg = null, curBg = null;
                    bool curBold = false, started = false;

                    for (int c = 0; c <= lastCol; c++)
                    {
                        var cell = _htmlCells[c, r];
                        var ch = cell.Character == '\0' ? ' ' : cell.Character;
                        // The parser paints uncoloured text with its default foreground
                        // (TerminalColor.LightGray); treat that - and an untouched cell - as
                        // "no explicit colour" so the viewer renders it with its own default.
                        var fg = cell.Foreground == default || cell.Foreground == TerminalColor.LightGray
                            ? null
                            : cell.Foreground.ToString();
                        var bg = cell.Background == default ? null : cell.Background.ToString();

                        if (!started)
                        {
                            curFg = fg; curBg = bg; curBold = cell.Bold; started = true;
                        }
                        else if (fg != curFg || bg != curBg || cell.Bold != curBold)
                        {
                            row.Add(new ScreenSegment(sb.ToString(), curFg, curBg, curBold));
                            sb.Clear();
                            curFg = fg; curBg = bg; curBold = cell.Bold;
                        }

                        sb.Append(ch);
                    }

                    if (sb.Length > 0)
                        row.Add(new ScreenSegment(sb.ToString(), curFg, curBg, curBold));
                }

                result.Add(row);
            }

            while (result.Count > 0 && result[^1].Count == 0)
                result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    /// <summary>
    /// The DevThrottle Stats per-session input tally (submitted turns + character volume by modality and
    /// surface). Recorded at THIS choke point so desktop-local input is counted too; read by the Director
    /// mapper for flow-up and by the Director stats store for persistence. Always present; empty until the
    /// first counted input.
    /// </summary>
    public SessionInputStats InputStats { get; } = new();

    /// <summary>Send raw bytes to the backend. <paramref name="origin"/> tags this input for the
    /// DevThrottle Stats tally as typed CHARACTER volume for its surface (never a turn - a bare keystroke
    /// is the user composing, not a submitted turn). Null origin = framework-internal, not counted.</summary>
    /// <summary>Printable characters typed at the terminal since the last submission, accumulated so
    /// the origin event written on Enter carries the size of the whole line rather than of the final
    /// keystroke. Reset at each submission. See <see cref="RecordOrigin"/>.</summary>
    private int _pendingOriginChars;

    public void SendInput(byte[] data, InputOrigin? origin = null)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        FileLog.Write($"[Session] SendInput: session={Id}, bytes={data.Length}, firstByte=0x{(data.Length > 0 ? data[0].ToString("X2") : "00")}");
        _backend.Write(data);
        if (origin is InputOrigin o)
        {
            InputStats.RecordCharacters(o, CountPrintable(data));
            _pendingOriginChars += CountPrintable(data);
        }
        // Only promote to Working when the write contains an actual submission
        // (CR or LF). A bare keystroke is the user composing at the prompt --
        // Claude Code hasn't received a turn yet. Treating every byte as Working
        // flickered the sidebar dot blue on every character typed.
        if (ContainsSubmit(data))
        {
            // A submission is the moment to note the origin (issue #1551). Terminal typing arrives
            // here one keystroke at a time, so the prompt TEXT cannot be reconstructed from these
            // bytes - backspace, arrow-key edits, history recall, paste and the agent's own
            // autocomplete all mutate the line invisibly, and replaying keystrokes would silently
            // record prompts the user never sent. Only the provenance is recorded here; the text is
            // read back from the agent's own transcript by the conversation ingest and joined to
            // this event by timestamp.
            if (origin is InputOrigin so)
                RecordOrigin(so, _pendingOriginChars);
            _pendingOriginChars = 0;
            IsBrandNew = false;
            // A real submission means the user is driving this session again, so neither a hold nor a
            // not-yet-landed deferral reflects intent any more - both are superseded (issue #470).
            // The HoldState setter no-ops when already None.
            HoldState = HoldState.None;
            SetActivityState(ActivityState.Working);
        }
    }

    private static bool ContainsSubmit(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
            if (data[i] == 0x0D || data[i] == 0x0A) return true;
        return false;
    }

    /// <summary>
    /// Count the printable bytes in a raw keystroke buffer for the DevThrottle Stats character tally:
    /// every byte at or above 0x20 except DEL (0x7F). Control bytes (Enter, arrows' leading ESC, Backspace)
    /// do not count. This is an honest approximation of typed character volume - an escape sequence's
    /// trailing letters (e.g. "[A" from an arrow key) count as a couple of characters of navigation
    /// activity, which the secondary character metric tolerates; the headline metric is TURNS, not chars.
    /// </summary>
    private static int CountPrintable(byte[] data)
    {
        int n = 0;
        for (int i = 0; i < data.Length; i++)
            if (data[i] >= 0x20 && data[i] != 0x7F) n++;
        return n;
    }

    /// <summary>
    /// Host-injected predicate answering "is this session locked because a dictation is inbound to
    /// it?" (issue #1181, Task 3b). The Director host wires this to <see cref="DictationLockReader"/>
    /// at startup; left null (never locked) in any process that does not run dictation delivery, and
    /// in unit tests. A single process-wide hook keeps the lock check on ONE fail-closed checkpoint
    /// (<see cref="SendTextAsync(string, SendSource)"/>) without threading a dependency through every
    /// session-creation path. Tests that set it must reset it to null in teardown.
    /// </summary>
    public static Func<Guid, bool>? DictationLockCheck { get; set; }

    /// <summary>
    /// True when a dictation is inbound to THIS session, so a human send into it is refused (issue
    /// #1181, Task 3b). A pure projection of the durable PENDING delivery marker via the host-injected
    /// <see cref="DictationLockCheck"/>; false when no host wired the check. This is the single
    /// predicate both the in-process throw (in <see cref="SendTextAsync(string, SendSource)"/>) and the
    /// control-API executor's explicit pre-check read, so the two paths cannot disagree.
    /// </summary>
    public bool IsDictationLocked => DictationLockCheck?.Invoke(Id) == true;

    private bool _isReceivingDictation;

    /// <summary>
    /// Cached, change-notifying view of <see cref="IsDictationLocked"/> for the UI (issue #1181, Task 3b).
    /// The roster reads THIS (a cheap bool) to paint the "receiving a dictation" orange, instead of doing a
    /// disk read on every render. The host re-evaluates it on a timer via <see cref="RefreshReceivingDictation"/>;
    /// this is the desktop presentation of the state (Task 4 will additionally surface it from the Gateway for
    /// the phone and cockpit).
    /// </summary>
    public bool IsReceivingDictation => _isReceivingDictation;

    /// <summary>Raised (with the new value) when <see cref="IsReceivingDictation"/> flips.</summary>
    public event Action<bool>? OnReceivingDictationChanged;

    /// <summary>
    /// Recompute <see cref="IsReceivingDictation"/> from the durable dictation marker and raise
    /// <see cref="OnReceivingDictationChanged"/> when it changes. Called on the host's roster-refresh tick
    /// so the disk read happens once per tick, not once per render.
    /// </summary>
    public void RefreshReceivingDictation()
    {
        var now = IsDictationLocked;
        if (now == _isReceivingDictation) return;
        _isReceivingDictation = now;
        FileLog.Write($"[Session] IsReceivingDictation -> {now}: session={Id}");
        OnReceivingDictationChanged?.Invoke(now);
    }

    /// <summary>
    /// Send text + Enter through the shared terminal submit protocol. ConPTY sessions use one
    /// echo-verified implementation for every agent and route, with bracketed paste for large or
    /// multi-line blocks when the TUI has requested mode 2004; non-PTY transports keep their
    /// backend-specific whole-turn semantics.
    ///
    /// <paramref name="source"/> names who is sending: a human (<see cref="SendSource.UserInput"/>,
    /// the default), an arriving dictation (<see cref="SendSource.Delivery"/>), or the framework
    /// itself (<see cref="SendSource.Internal"/>). It is diagnostic only - sends are never refused
    /// by source. The old dictation-lock rejection here was removed deliberately: this is a
    /// single-operator tool, and a collision between the operator's own phone dictation and their
    /// own typed send is theirs to make, not the Director's to police.
    /// </summary>
    public async Task SendTextAsync(string text, SendSource source = SendSource.UserInput, InputOrigin? origin = null)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;

        FileLog.Write($"[Session] SendTextAsync: session={Id}, source={source}, driver={Driver.Kind}, text=\"{(text.Length > 60 ? text[..60] + "..." : text)}\", len={text.Length}");
        if (BackendType is SessionBackendType.ConPty)
        {
            await Drivers.TerminalSubmit.SharedSubmitAsync(
                _backend,
                text,
                Driver.Kind.ToString(),
                BracketedPasteEnabled,
                requireEcho: Driver.Kind != Agents.AgentKind.Copilot,
                screenSnapshot: SnapshotScreenRows);
        }
        else
        {
            await _backend.SendTextAsync(text);
        }
        IsBrandNew = false;
        // A SendTextAsync is always a submitted turn -- the user is driving this session again, so both a
        // hold and a not-yet-landed deferral are superseded (issue #470). The HoldState setter no-ops when
        // already None.
        HoldState = HoldState.None;
        SetActivityState(ActivityState.Working);
        // DevThrottle Stats: a SendTextAsync is exactly one submitted turn. Count it (plus its character
        // volume) for the tagged origin. Null origin = framework-internal (handover, queue drain) - not
        // counted, even though it still submits a turn to the agent.
        if (origin is InputOrigin o)
        {
            InputStats.RecordTurn(o, text?.Length ?? 0);
            RecordOrigin(o, text?.Length ?? 0);
        }
    }

    /// <summary>
    /// Note WHERE this submission came from in the <see cref="InputOriginLog"/> (issue #1551). Carries
    /// no text: the conversation ingest reads the text back from the agent's own transcript and joins
    /// this event to it by session + nearest timestamp. This choke point is the only place the origin
    /// is ever known.
    ///
    /// Gated on the same non-null origin as the stats tally, so framework-internal sends (handover
    /// text, queue drain, framing) stay out for the same reason they stay out of the counts: they carry
    /// no human keystrokes and no spoken words.
    /// </summary>
    private void RecordOrigin(InputOrigin origin, int charCount)
    {
        if (charCount <= 0) return;
        InputOriginLog.Write(new InputOriginRecord
        {
            TsUtc = DateTime.UtcNow,
            SessionId = Id.ToString(),
            Modality = origin.ModalityToken,
            Surface = origin.SurfaceToken,
            CharCount = charCount,
        });
    }

    /// <summary>Send text followed by Enter (sync wrapper). See
    /// <see cref="SendTextAsync(string, SendSource, InputOrigin?)"/> for what <paramref name="source"/> and
    /// <paramref name="origin"/> mean.</summary>
    public void SendText(string text, SendSource source = SendSource.UserInput, InputOrigin? origin = null)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        // Fire and forget for sync API
        _ = SendTextAsync(text, source, origin);
    }

    /// <summary>Send just an Enter keystroke to the backend.</summary>
    public async Task SendEnterAsync()
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        await _backend.SendEnterAsync();
    }

    // ===== Agent driver verbs =====
    // The per-CLI interaction protocol (docs/plans/agent-driver.md): tool-specific
    // keystrokes live in one driver class per CLI, resolved from this session's
    // AgentKind. Claude gets ClaudeDriver, pi gets PiDriver, unverified tools get
    // GenericDriver (the pre-driver bytes, minimal declared capabilities). UIs and
    // endpoints read Driver.Capabilities to know which verbs this session supports.

    /// <summary>The interaction driver for this session's CLI. Stateless singleton.</summary>
    public Drivers.IAgentDriver Driver => AgentPlugins.AgentPluginRegistry.Contains(AgentKind)
        ? AgentPlugins.AgentPluginRegistry.Get(AgentKind).Driver
        : Drivers.AgentDrivers.For(AgentKind);

    /// <summary>Soft-stop the current turn (Esc for Claude and pi).</summary>
    public async Task CancelTurnAsync()
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        FileLog.Write($"[Session] CancelTurnAsync: session={Id}, driver={Driver.Kind}");
        await Driver.CancelAsync(_backend);
    }

    /// <summary>Hard interrupt (Ctrl+C where the tool supports it; pi does NOT -
    /// its driver throws because Ctrl+C twice quits pi).</summary>
    public async Task InterruptAsync()
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        FileLog.Write($"[Session] InterruptAsync: session={Id}, driver={Driver.Kind}");
        await Driver.InterruptAsync(_backend);
    }

    /// <summary>Open the tool's in-terminal history picker (Claude's double-Esc).</summary>
    public async Task ShowHistoryAsync()
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        FileLog.Write($"[Session] ShowHistoryAsync: session={Id}, driver={Driver.Kind}");
        await Driver.ShowHistoryAsync(_backend);
    }

    /// <summary>
    /// Reset the conversation context in place via the driver (/clear for Claude,
    /// /new for pi). For drivers with transcript access this also DISCOVERS the new
    /// agent-internal session id (the post-/clear transcript file) - the caller must
    /// then re-link via SessionManager.RelinkClaudeSession so the manager's id map and
    /// metadata follow; this closes the stale-relink gap found in the issue #172 spike.
    /// Returns the new id, or null when the tool has no readable transcripts.
    /// </summary>
    public async Task<string?> ClearContextAsync(CancellationToken ct = default)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed)
            throw new InvalidOperationException($"ClearContextAsync: session {Id} is not running (status={Status})");
        var driver = Driver;
        if (!driver.Capabilities.HasFlag(Drivers.DriverCapabilities.ClearContext))
            throw new NotSupportedException($"ClearContextAsync: the {driver.Kind} driver declares no context clear.");

        var oldId = ClaudeSessionId;
        FileLog.Write($"[Session] ClearContextAsync: session={Id}, driver={driver.Kind}, oldAgentSessionId={oldId ?? "(none)"}");
        var t0 = DateTime.UtcNow;
        await driver.ClearContextAsync(_backend);

        if (!driver.Capabilities.HasFlag(Drivers.DriverCapabilities.TranscriptRead) || oldId is null)
        {
            FileLog.Write($"[Session] ClearContextAsync: no transcript tracking for {driver.Kind}, clear submitted");
            return null;
        }

        // The post-clear transcript is the file that is both new (id differs) and
        // recent; old transcripts in the same working directory must not match.
        var deadline = t0.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = driver.ListTranscripts(WorkingDirectory)
                .FirstOrDefault(s => !string.IsNullOrEmpty(s.AgentSessionId)
                                     && s.AgentSessionId != oldId
                                     && s.LastWriteUtc >= t0.AddSeconds(-10));
            if (!string.IsNullOrEmpty(candidate.AgentSessionId))
            {
                FileLog.Write($"[Session] ClearContextAsync: new transcript {candidate.AgentSessionId} after {(DateTime.UtcNow - t0).TotalSeconds:F1}s");
                return candidate.AgentSessionId;
            }
            await Task.Delay(250, ct);
        }
        throw new InvalidOperationException(
            $"ClearContextAsync: no new transcript appeared within 60s (session={Id}, oldId={oldId})");
    }

    private void SetActivityState(ActivityState newState)
    {
        var old = ActivityState;
        if (old == newState) return;
        ActivityState = newState;
        // ===== The hold state machine's activity edges (docs/architecture/session-state-machine-2026-07-14.html).
        // ActivityState has already been assigned above, so IsWorking below reads the NEW state.
        if (IsWorking)
        {
            // THE RULE: a held session that starts working takes itself off hold, every time, with no
            // condition attached. A session cannot be both held and working - the user would see a parked
            // session quietly doing work, which is exactly the lie this machine exists to make impossible.
            //
            // This is safe against cosmetic repaints - the concern the old turn-latch was built to handle -
            // because the detector no longer reports cosmetic Working. Both of its paths filter noise before
            // they get here: for a byte-silent-idle agent a byte IS real output, and for an agent with an
            // animated idle footer the continuous-idle path diffs the screen BODY and stays silent on
            // footer-only repaints (see TerminalStateDetector). The GitHubActions backend's ActivitySink is
            // authoritative run status. So reaching Working means real work, on every path.
            if (HoldState == HoldState.Held)
            {
                FileLog.Write($"[Session] Session started working - lifting hold: session={Id}");
                HoldState = HoldState.None;
            }
            // A DeferredHold deliberately survives here: it is WAITING for this work to stop.
        }
        else if (newState is ActivityState.Exited)
        {
            // Exited: a deferral can never land - there is no turn to come back to, and parking a dead
            // session would just hide it behind a "Snoozed" label forever.
            if (HoldState == HoldState.DeferredHold)
                HoldState = HoldState.None;
        }
        else
        {
            // Settled (WaitingForInput / WaitingForPerm / Idle): a deferred hold lands DURABLY. There is no
            // turn-end auto-lift any more - the lift edge moved to "starts working" above, which is both
            // earlier (the user sees it go blue immediately) and immune to the quiet-gap problem that made
            // the old turn-end lift unreachable after any slow command.
            if (HoldState == HoldState.DeferredHold)
            {
                FileLog.Write($"[Session] Deferred hold landed at settle ({newState}): session={Id}");
                HoldState = HoldState.Held;
            }
        }
        // A real activity change ends any Wingman "running in the background" overlay: once the
        // terminal produces output again (Working), or the session otherwise leaves the parked
        // turn-end it was judged at, the background-wait verdict is stale. The next turn-end
        // briefing re-evaluates from scratch. Only WaitingForInput/WaitingForPerm preserve it.
        if (newState is not (ActivityState.WaitingForInput or ActivityState.WaitingForPerm))
            IsBackgroundRunning = false;
        // A real state change opens a new "generation". This releases any sticky
        // positive-evidence color from the previous generation (issue #136 option C):
        // e.g. a red pending-question survives cosmetic repaints while the session is
        // idle, but the moment the user answers (-> Working) the badge is free to go
        // blue again.
        Interlocked.Increment(ref _activityGeneration);
        // Log the transition (blue<->red) to the in-memory ring the Wingman tab renders.
        RecordStateChange(old, newState);
        OnActivityStateChanged?.Invoke(old, newState);

        // Auto-drain the prompt queue when the session returns to Idle (ready at the prompt).
        if (newState == ActivityState.Idle)
            TryDrainQueue();
    }

    /// <summary>
    /// When the session goes Idle and isn't on hold, send the next queued prompt - so the
    /// queue means "auto-send when Claude is ready", not a manual holding list. One item per
    /// Idle transition; SendText -> Working -> Idle drains the rest in FIFO order. We never
    /// drain on WaitingForInput/WaitingForPerm: a queued prompt must not answer Claude's own
    /// question. The send is scheduled off the current stack to avoid re-entering
    /// SetActivityState (SendText synchronously flips to Working).
    /// </summary>
    private void TryDrainQueue()
    {
        if (OnHold || !PromptQueue.HasItems) return;
        if (Status is SessionStatus.Exited or SessionStatus.Failed) return;

        var next = PromptQueue.Items[0];
        PromptQueue.Remove(next.Id); // remove first so a double Idle can't send it twice
        FileLog.Write($"[Session] Queue auto-drain: session={Id}, remaining={PromptQueue.Count}");

        _ = Task.Run(async () =>
        {
            try { await SendTextAsync(next.Text); }
            catch (Exception ex) { FileLog.Write($"[Session] Queue auto-drain FAILED: session={Id}: {ex.Message}"); }
        });
    }

    /// <summary>
    /// Set ActivityState from the <c>TerminalStateDetector</c> in terminal-driven mode.
    /// The detector is the single authority for state in that mode; this is its writer.
    /// </summary>
    internal void ApplyTerminalActivityState(ActivityState newState) => SetActivityState(newState);

    /// <summary>
    /// Refresh Claude session metadata from sessions-index.json.
    /// Call this after ClaudeSessionId is set or periodically to update message counts.
    /// </summary>
    public void RefreshClaudeMetadata()
    {
        if (string.IsNullOrEmpty(ClaudeSessionId))
        {
            if (ClaudeMetadata != null)
            {
                ClaudeMetadata = null;
                OnClaudeMetadataChanged?.Invoke(null);
            }
            return;
        }

        var metadata = ClaudeSessionReader.ReadSessionMetadata(ClaudeSessionId, RepoPath);
        ClaudeMetadata = metadata;
        OnClaudeMetadataChanged?.Invoke(metadata);
    }

    /// <summary>
    /// Verify that the Claude session's .jsonl file exists and matches expected content.
    /// Updates VerificationStatus and VerifiedFirstPrompt.
    /// Uses ExpectedFirstPrompt if set, otherwise just verifies file existence.
    /// Requires at least MinVerificationLength characters to verify.
    /// </summary>
    public void VerifyClaudeSession()
    {
        FileLog.Write($"[Session] VerifyClaudeSession: session={Id}, claudeSessionId={ClaudeSessionId ?? "null"}");
        var oldStatus = VerificationStatus;

        // Can't verify without a session ID
        if (string.IsNullOrEmpty(ClaudeSessionId))
        {
            VerificationStatus = SessionVerificationStatus.NotLinked;
            VerifiedFirstPrompt = null;
            if (oldStatus != VerificationStatus)
                OnVerificationStatusChanged?.Invoke(VerificationStatus);
            return;
        }

        // Read the JSONL first prompt to check length
        var jsonlPath = ClaudeSessionReader.GetJsonlPath(ClaudeSessionId, RepoPath);
        var firstPrompt = ClaudeSessionReader.ReadFirstPromptFromJsonl(jsonlPath);

        // Need minimum content to verify (avoid verifying new sessions too early)
        if (string.IsNullOrEmpty(firstPrompt) || firstPrompt.Length < MinVerificationLength)
        {
            // File exists but not enough content yet - stay NotLinked (no badge)
            VerificationStatus = SessionVerificationStatus.NotLinked;
            VerifiedFirstPrompt = firstPrompt;
            if (oldStatus != VerificationStatus)
                OnVerificationStatusChanged?.Invoke(VerificationStatus);
            return;
        }

        // Now do full verification
        var result = ClaudeSessionReader.VerifySessionFile(ClaudeSessionId, RepoPath, ExpectedFirstPrompt);
        VerificationStatus = result.Status;
        VerifiedFirstPrompt = result.FirstPromptSnippet;

        // If verified and we didn't have an expected prompt yet, save the actual one
        if (result.Status == SessionVerificationStatus.Verified && string.IsNullOrEmpty(ExpectedFirstPrompt))
        {
            ExpectedFirstPrompt = result.FirstPromptSnippet;
        }

        if (oldStatus != result.Status)
        {
            OnVerificationStatusChanged?.Invoke(result.Status);
        }
    }

    /// <summary>
    /// Find the matching .jsonl file by comparing terminal content with user prompts.
    /// Starts matching immediately - shows "Potential" for early matches, "Matched" after 50+ lines.
    /// </summary>
    /// <param name="terminalText">Terminal content.</param>
    /// <param name="lineCount">Number of lines in terminal.</param>
    /// <returns>Verification result with matched session ID or error.</returns>
    public TerminalVerificationResult VerifyWithTerminalContent(string terminalText, int lineCount)
    {
        FileLog.Write($"[Session] VerifyWithTerminalContent: session={Id}, lineCount={lineCount}, textLen={terminalText.Length}");
        // Skip if already matched or exhausted all retry attempts
        if (TerminalVerificationStatus == TerminalVerificationStatus.Matched)
        {
            return new TerminalVerificationResult
            {
                IsMatched = true,
                MatchedSessionId = ClaudeSessionId
            };
        }
        if (_confirmationAttempts >= MaxConfirmationAttempts)
        {
            return new TerminalVerificationResult
            {
                IsMatched = false,
                MatchedSessionId = ClaudeSessionId
            };
        }

        // Prevent concurrent verification runs (called from background threads)
        if (Interlocked.CompareExchange(ref _verificationRunning, 1, 0) != 0)
            return new TerminalVerificationResult { IsMatched = false, ErrorMessage = "Verification already running" };

        try
        {
            return VerifyWithTerminalContentCore(terminalText, lineCount);
        }
        finally
        {
            Interlocked.Exchange(ref _verificationRunning, 0);
        }
    }

    private TerminalVerificationResult VerifyWithTerminalContentCore(string terminalText, int lineCount)
    {
        FileLog.Write($"[Session.Verify] START: lineCount={lineCount}, status={TerminalVerificationStatus}, attempts={_confirmationAttempts}, sessionId={Id}");

        bool isConfirmationRun = lineCount >= 50;
        if (isConfirmationRun)
            _confirmationAttempts++;

        var error = LoadJsonlFilesForVerification(isConfirmationRun, out var allFiles);
        if (error != null) return error;

        // Normalize terminal text once for whitespace-insensitive matching
        var normalizedTerminal = ClaudeSessionReader.NormalizeForMatching(terminalText);

        // Score all files and pick the best match
        var bestMatch = FindBestMatch(allFiles, terminalText, normalizedTerminal);

        if (bestMatch != null)
        {
            ClaudeSessionId = bestMatch.Value.SessionId;

            if (isConfirmationRun || bestMatch.Value.MatchCount >= 2)
            {
                SetTerminalVerificationStatus(TerminalVerificationStatus.Matched);
                ExpectedFirstPrompt = ClaudeSessionReader.ReadFirstPromptFromJsonl(bestMatch.Value.FilePath);
                VerifyClaudeSession();
                FileLog.Write($"[Session.Verify] MATCHED: {bestMatch.Value.SessionId} (matches={bestMatch.Value.MatchCount}, prompts={bestMatch.Value.TotalPrompts})");
                return new TerminalVerificationResult { IsMatched = true, MatchedSessionId = bestMatch.Value.SessionId };
            }

            SetTerminalVerificationStatus(TerminalVerificationStatus.Potential);
            FileLog.Write($"[Session.Verify] POTENTIAL: {bestMatch.Value.SessionId} (matches={bestMatch.Value.MatchCount}, prompts={bestMatch.Value.TotalPrompts})");
            return new TerminalVerificationResult { IsPotential = true, MatchedSessionId = bestMatch.Value.SessionId };
        }

        if (isConfirmationRun)
        {
            // Set Failed status immediately, but allow retries up to MaxConfirmationAttempts
            FileLog.Write($"[Session.Verify] NO MATCH FOUND - Setting status=Failed (attempt {_confirmationAttempts}/{MaxConfirmationAttempts}, {allFiles.Count} files checked)");
            SetTerminalVerificationStatus(TerminalVerificationStatus.Failed);
        }
        else
        {
            FileLog.Write($"[Session.Verify] No match found yet, NOT confirmation run - staying in status={TerminalVerificationStatus}");
        }
        return new TerminalVerificationResult { ErrorMessage = "No matching .jsonl file found" };
    }

    /// <summary>
    /// Load and validate .jsonl files for terminal verification.
    /// Returns null on success (files populated), or an error result on failure.
    /// </summary>
    private TerminalVerificationResult? LoadJsonlFilesForVerification(
        bool isConfirmationRun, out List<FileInfo> allFiles)
    {
        allFiles = new List<FileInfo>();

        var projectFolder = ClaudeSessionReader.GetProjectFolderPath(RepoPath);
        if (!Directory.Exists(projectFolder))
        {
            FileLog.Write($"[Session.Verify] Project folder not found: {projectFolder}");
            if (isConfirmationRun)
                SetTerminalVerificationStatus(TerminalVerificationStatus.Failed);
            return new TerminalVerificationResult { ErrorMessage = "Project folder not found" };
        }

        allFiles = Directory.GetFiles(projectFolder, "*.jsonl")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        FileLog.Write($"[Session.Verify] Found {allFiles.Count} .jsonl files in {projectFolder}");

        if (allFiles.Count == 0)
        {
            if (isConfirmationRun)
                SetTerminalVerificationStatus(TerminalVerificationStatus.Failed);
            else
                FileLog.Write($"[Session.Verify] No .jsonl files, NOT confirmation run - staying in current status");
            return new TerminalVerificationResult { ErrorMessage = "No .jsonl files found" };
        }

        return null;
    }

    private readonly record struct MatchResult(string SessionId, string FilePath, int MatchCount, int TotalPrompts);

    /// <summary>
    /// Score all JSONL files against terminal text and return the best match.
    /// Uses both exact and whitespace-normalized matching to handle word wrapping.
    /// Picks the file with the most prompt matches (not ratio), requiring at least 1 match.
    /// </summary>
    private MatchResult? FindBestMatch(
        IReadOnlyList<FileInfo> allFiles, string terminalText, string normalizedTerminal)
    {
        MatchResult? best = null;

        foreach (var file in allFiles)
        {
            var prompts = ClaudeSessionReader.ExtractUserPrompts(file.FullName);
            if (prompts.Count == 0) continue;

            int matchCount = 0;
            foreach (var prompt in prompts)
            {
                // Try exact match first (fast)
                if (terminalText.Contains(prompt, StringComparison.Ordinal))
                {
                    matchCount++;
                    continue;
                }

                // Try whitespace-normalized match (handles word wrapping)
                var normalizedPrompt = ClaudeSessionReader.NormalizeForMatching(prompt);
                if (normalizedPrompt.Length > 10 && normalizedTerminal.Contains(normalizedPrompt, StringComparison.Ordinal))
                {
                    matchCount++;
                }
            }

            var fileName = Path.GetFileNameWithoutExtension(file.Name);
            var shortName = fileName.Length > 8 ? fileName[..8] : fileName;
            FileLog.Write($"[Session.Verify] File={shortName}..., prompts={prompts.Count}, matched={matchCount}");

            if (matchCount > 0 && (best == null || matchCount > best.Value.MatchCount))
            {
                best = new MatchResult(fileName, file.FullName, matchCount, prompts.Count);
            }
        }

        return best;
    }

    private void SetTerminalVerificationStatus(TerminalVerificationStatus status)
    {
        if (TerminalVerificationStatus == status) return;
        TerminalVerificationStatus = status;
        OnTerminalVerificationStatusChanged?.Invoke(status);
    }

    /// <summary>Resize the terminal (only meaningful for ConPty backend).</summary>
    public void Resize(short cols, short rows)
    {
        if (_disposed) return;
        if (cols <= 0 || rows <= 0) return;
        // No-op on an unchanged size. A resize repaints the PTY, which the monitoring loop
        // observes; resizing to the same dimensions would be a pointless write that could
        // feed a repaint storm (the Wingman repaint-loop invariant). Guard against it so a
        // chatty Cockpit (window-drag events) can't hammer the PTY.
        if (cols == CurrentCols && rows == CurrentRows) return;
        // Re-size the live-attach parser to match the new PTY geometry BEFORE the PTY repaints, so
        // the agent's post-resize output is parsed at the correct width/height. Overlapping content
        // is copied so an agent that does not fully repaint on resize (Codex) keeps its screen; any
        // imperfection self-heals on the repaint the resize triggers. The fixed-grid _htmlParser is
        // intentionally left alone (its consumers depend on its stable geometry).
        ResizeStreamParser(cols, rows);
        _backend.Resize(cols, rows);
        CurrentCols = cols;
        CurrentRows = rows;
    }

    // Grow/shrink the live-attach parser's grid, copying the overlapping cells so existing content
    // survives the resize (mirrors the desktop TerminalControl's resize path). Under _htmlParserLock
    // because the buffer feed thread parses into this same parser.
    private void ResizeStreamParser(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        lock (_htmlParserLock)
        {
            if (_streamParser is null || _streamCells is null) return;
            if (cols == _streamGridCols && rows == _streamGridRows) return;
            var newCells = new TerminalCell[cols, rows];
            int copyC = Math.Min(_streamGridCols, cols);
            int copyR = Math.Min(_streamGridRows, rows);
            for (int r = 0; r < copyR; r++)
                for (int c = 0; c < copyC; c++)
                    newCells[c, r] = _streamCells[c, r];
            _streamParser.UpdateGrid(newCells, cols, rows);
            _streamCells = newCells;
            _streamGridCols = cols;
            _streamGridRows = rows;
        }
    }

    /// <summary>
    /// Build a self-contained ANSI "prime" frame that reconstructs the session's CURRENT terminal
    /// screen (scrollback + visible grid + cursor) when replayed into a fresh client terminal. This
    /// is what the WebSocket stream endpoint sends on attach instead of a mid-stream raw-byte replay,
    /// so a browser xterm rebuilds the exact screen regardless of how far back or how incrementally
    /// the agent drew it. Returns an empty array for sessions with no server-side parser (Embedded).
    /// </summary>
    public (byte[] Snapshot, long ReflectedCursor, int Cols, int Rows) GetTerminalSnapshot()
    {
        if (_streamParser is null) return (System.Array.Empty<byte>(), 0, CurrentCols, CurrentRows);
        lock (_htmlParserLock)
        {
            if (_streamParser is null || _streamScrollback is null)
                return (System.Array.Empty<byte>(), _streamBytesReflected, CurrentCols, CurrentRows);
            var cells = _streamParser.ActiveCells;
            int cols = cells.GetLength(0);
            int rows = cells.GetLength(1);
            var (cc, cr) = _streamParser.GetCursorPosition();
            var bytes = TerminalSnapshotSerializer.ToAnsi(
                _streamScrollback, cells, cols, rows, cc, cr,
                _streamParser.IsCursorVisible, _streamParser.IsAlternateScreen);
            return (bytes, _streamBytesReflected, cols, rows);
        }
    }

    /// <summary>Kill the session gracefully, then force if needed.</summary>
    public async Task KillAsync(int timeoutMs = 5000)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        Status = SessionStatus.Exiting;
        await _backend.GracefulShutdownAsync(timeoutMs);
    }

    /// <summary>Mark the session as running (called after backend.Start succeeds).</summary>
    internal void MarkRunning()
    {
        Status = SessionStatus.Running;
    }

    /// <summary>Mark the session as failed.</summary>
    internal void MarkFailed()
    {
        Status = SessionStatus.Failed;
    }

    private void OnBackendProcessExited(int exitCode)
    {
        FileLog.Write($"[Session] ProcessExited: session={Id}, exitCode={exitCode}, pid={ProcessId}, uptime={(DateTimeOffset.UtcNow - CreatedAt).TotalSeconds:F1}s");

        // Decide crash-vs-clean BEFORE we overwrite the activity state: a session that dropped out
        // while it was actively working is a crash even if its exit code is 0 (issue #959).
        var wasWorking = ActivityState == ActivityState.Working;
        var crashed = IsUnexpectedExit(exitCode, wasWorking);

        ExitCode = exitCode;
        Crashed = crashed;
        Status = crashed ? SessionStatus.Failed : SessionStatus.Exited;
        // Process exit is an authoritative, transport-independent signal - drive the
        // state directly so it works in both terminal-driven and hook modes.
        SetActivityState(ActivityState.Exited);

        // A crash keeps the row visible in an Error colour so the user sees work stopped rather than
        // the session silently disappearing. SetActivityState above bumped the colour generation, so
        // this authoritative crash colour is not dropped by the sticky positive-evidence guard.
        if (crashed)
        {
            var reason = exitCode == 0
                ? "crashed: the agent process ended unexpectedly while working"
                : $"crashed: the agent process exited with code {exitCode}";
            SetStatusColor(CcDirector.Core.Wingman.StatusColor.Error, reason);
        }

        // Announce the exit exactly once (a backend could theoretically raise its
        // ProcessExited more than once). This is an event-raise on a backend thread,
        // so guard it: a faulting subscriber must not kill the exit-monitor thread.
        if (!_exitNotified)
        {
            _exitNotified = true;
            try { OnExited?.Invoke(exitCode); }
            catch (Exception ex) { FileLog.Write($"[Session] OnExited handler threw: {ex.Message}"); }
        }
    }

    private bool _exitNotified;

    private void OnBackendStatusChanged(string status)
    {
        FileLog.Write($"[Session] BackendStatus: session={Id}, status={status}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _backend.ProcessExited -= OnBackendProcessExited;
        _backend.StatusChanged -= OnBackendStatusChanged;
        if (_htmlParserFeed is not null && _backend.Buffer is not null)
            _backend.Buffer.OnBytesWritten -= _htmlParserFeed;
        _backend.Dispose();
    }
}
