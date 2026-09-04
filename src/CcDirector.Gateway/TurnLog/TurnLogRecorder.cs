using System.Diagnostics;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.TurnLog;

/// <summary>
/// The turn log: at the end of every turn on a machine where capture is switched on, write one
/// self-contained record of what just happened.
///
/// WHY IT IS NOT PART OF THE SUPERVISOR, WHICH ALREADY READS THE SCREEN AT THIS EXACT MOMENT. The single
/// most valuable thing this instrument can capture is a turn end the supervisor did NOT act on - the misses,
/// which today write no line anywhere and are the reason our measurement said the supervisor works while the
/// owner's experience said it works only sometimes. A log living inside the supervisor writes nothing
/// precisely when the supervisor does nothing, which reproduces the blindness it exists to cure. So it hangs
/// off the same turn-end boundary independently, and it costs a second screen read to stay honest.
///
/// IT MUST NOT CHANGE WHAT THE PRODUCT DOES. <see cref="OnTurnEnd"/> returns immediately and the whole
/// capture runs on its own task, so a turn ends identically whether capture is on or off. Nothing here can
/// gate, delay or veto anything: it holds no lock the product waits on, it types nothing, and it never
/// throws into the caller.
///
/// A FAILURE TO LOG IS NOT A FAILURE OF THE TURN - BUT IT IS RECORDED. A part that could not be collected
/// is named in the record's gaps rather than quietly left out, because a corpus that silently drops what it
/// could not read acquires holes exactly where the interesting cases are, and nobody can tell afterwards
/// whether a missing screen was a quiet session or a broken instrument.
/// </summary>
public sealed class TurnLogRecorder : IDisposable
{
    /// <summary>
    /// How many FULL turns of conversation go into a record - a full turn being the user's message and the
    /// agent's reply together. Ten, on the owner's instruction, because a screen alone often cannot say
    /// whether a session is stuck, waiting or finished and what came before it can. Erring long is
    /// deliberate: an over-long conversation costs bytes, and a short one costs a question we cannot ask.
    /// </summary>
    public const int FullTurnsCaptured = 10;

    /// <summary>How many scrollback lines are asked for. Well past what any judgement reads, for the same
    /// reason: the window is a decision we will want to re-take.</summary>
    public const int ScrollbackLinesCaptured = 2000;

    /// <summary>
    /// The ceiling on one capture. Not a correctness bound - nothing downstream waits on this - but an
    /// unreachable Director must not leave a capture task hanging on to a session's state for the rest of
    /// the day, and a fleet's worth of those would be a leak rather than a log.
    /// </summary>
    public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(30);

    private readonly ITurnLogEnvironment _env;
    private readonly Func<TenantId, IDisposable>? _enterTenantScope;
    private readonly Func<DateTime> _nowUtc;
    private readonly CancellationTokenSource _stopping = new();
    private bool _disposed;

    /// <param name="enterTenantScope">Enters the owning account's storage scope for the duration of a
    /// capture. Required on the hosted Gateway and inert on a self-hosted one, which has a single partition.
    /// Without it every read the capture makes runs with no tenant in scope and is DENIED, and because a
    /// denied read is written down as a gap rather than thrown, the corpus would fill with records whose
    /// screen and conversation were always missing - a feature that looks switched on and captures nothing.</param>
    public TurnLogRecorder(
        ITurnLogEnvironment env,
        Func<TenantId, IDisposable>? enterTenantScope = null,
        Func<DateTime>? nowUtc = null)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _enterTenantScope = enterTenantScope;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// A turn just ended. Returns immediately, having started the capture on its own task - the product's
    /// turn-end path continues with nothing waiting on this.
    ///
    /// The switch is read here, synchronously and cheaply, so a Gateway with capture off does not even spawn
    /// a task per turn end.
    /// </summary>
    public void OnTurnEnd(TurnEndSignal signal)
    {
        if (_disposed || signal is null) return;
        if (!signal.Tenant.IsValid || string.IsNullOrEmpty(signal.SessionId)) return;

        // The machine is the Director's identity; the account is the tenant. Both are needed before the
        // switch can be asked, and neither costs a round trip.
        if (!_env.IsEnabled(signal.Tenant.Value, signal.DirectorId)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                timeout.CancelAfter(CaptureTimeout);
                // The scope is entered HERE, inside the task, and not by the caller. It is an async-local,
                // so a scope the turn-end callback entered would not survive into this continuation - the
                // same reason the supervisor enters its own.
                using (_enterTenantScope?.Invoke(signal.Tenant))
                    await CaptureAsync(signal, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                FileLog.Write($"[TurnLogRecorder] capture timed out sid={signal.SessionId} - no record written");
            }
            catch (Exception ex)
            {
                // Loud, and it goes no further. The turn is long over.
                FileLog.Write($"[TurnLogRecorder] capture FAILED sid={signal.SessionId}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Gather one record and write it. The test entry point; production reaches it through
    /// <see cref="OnTurnEnd"/>. Answers the path written, or null.
    /// </summary>
    public async Task<string?> CaptureAsync(TurnEndSignal signal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var startedAt = _nowUtc();
        var overall = Stopwatch.StartNew();
        var gaps = new List<TurnLogGap>();

        var session = _env.LocateSession(signal.Tenant, signal.SessionId);
        if (session is null)
        {
            // We keep going deliberately. A session that has already gone - deleted, or its Director dropped
            // between the boundary and this task - still produced a turn end, and a record saying so with a
            // named gap is worth more than no record at all, which would look like a turn that never happened.
            gaps.Add(new TurnLogGap
            {
                Part = "session",
                Reason = "the session was not in the Gateway's snapshot at capture time",
            });
        }

        var screenTimer = Stopwatch.StartNew();
        ScreenGridResponse? grid = null;
        try
        {
            grid = await _env.ReadScreenAsync(signal.Tenant, signal.DirectorId, signal.SessionId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            gaps.Add(new TurnLogGap { Part = "terminal", Reason = ex.Message });
        }
        screenTimer.Stop();
        if (grid is null)
            gaps.Add(new TurnLogGap { Part = "terminal", Reason = "the live screen could not be read - unreadable, not empty" });

        var scrollbackTimer = Stopwatch.StartNew();
        BufferResponse? scrollback = null;
        try
        {
            scrollback = await _env.ReadScrollbackAsync(
                signal.Tenant, signal.DirectorId, signal.SessionId, ScrollbackLinesCaptured, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            gaps.Add(new TurnLogGap { Part = "scrollback", Reason = ex.Message });
        }
        scrollbackTimer.Stop();
        if (scrollback is null)
            gaps.Add(new TurnLogGap { Part = "scrollback", Reason = "the scrollback could not be read" });

        StoredConversationSnapshot? stored = null;
        try
        {
            stored = _env.ReadConversation(signal.Tenant, signal.SessionId);
        }
        catch (Exception ex)
        {
            gaps.Add(new TurnLogGap { Part = "conversation", Reason = ex.Message });
        }
        if (stored is null)
            gaps.Add(new TurnLogGap { Part = "conversation", Reason = "nothing has been stored for this session yet" });

        var conversation = BuildConversation(stored);

        // A CONVERSATION OLDER THAN THE TURN IS A GAP - but the comparison has to be against the TURN, not
        // against this capture, and getting that backwards was the first thing the live corpus caught.
        //
        // The Director announces the state change before it pushes the turn that caused it, so the risk is a
        // conversation that stops one turn short of the screen stored beside it. The evidence for that is a
        // last push older than the session's last activity - the turn ending. Comparing against the capture
        // instead flags the HEALTHY case, because a push that landed before we read is exactly what we want,
        // and it fired on every record in the first minutes of real capture. A gap that marks good records
        // as suspect is worse than no gap: it teaches everyone reading the corpus to ignore the field.
        if (stored is not null && stored.LastPushedUtc is { } pushed && session?.LastActivityAt is { } lastActivity)
        {
            var turnEnded = DateTime.SpecifyKind(lastActivity, DateTimeKind.Utc);
            if (pushed < turnEnded)
            {
                gaps.Add(new TurnLogGap
                {
                    Part = "conversation",
                    Reason = $"the conversation was last pushed at {pushed:O}, before this session's last activity at "
                           + $"{turnEnded:O} - it may stop one turn short of the screen stored beside it",
                });
            }
        }
        overall.Stop();

        var record = new TurnLogRecord
        {
            CapturedAtUtc = startedAt,
            Glance = new TurnLogGlance
            {
                SessionId = signal.SessionId,
                SessionName = session?.Name,
                Computer = string.IsNullOrWhiteSpace(session?.MachineName) ? null : session!.MachineName,
                Agent = string.IsNullOrWhiteSpace(session?.Agent) ? null : session!.Agent,
                Repository = string.IsNullOrWhiteSpace(session?.RepoPath) ? null : session!.RepoPath,
                DirectorId = signal.DirectorId,
                Account = signal.Tenant.Value,
            },
            Moment = new TurnLogMoment
            {
                ActivityStateBefore = signal.PreviousActivityState,
                ActivityStateAfter = session?.ActivityState,
                IsNewTurn = signal.IsNewTurn,
                IdleSeconds = session?.IdleSeconds,
                QuietThresholdSeconds = session?.QuietThresholdSeconds,
                LastActivityAtUtc = session?.LastActivityAt,
                LastOwnerTurnAtUtc = session?.LastOwnerTurnAtUtc,
                ScreenReadMs = screenTimer.ElapsedMilliseconds,
                ScrollbackReadMs = scrollbackTimer.ElapsedMilliseconds,
                GatherMs = overall.ElapsedMilliseconds,
            },
            Session = session,
            Terminal = BuildTerminal(grid, scrollback),
            Conversation = conversation,
            Observed = new TurnLogObserved
            {
                SupervisorEnabled = Safely(() => _env.SupervisorEnabled(signal.Tenant), "supervisor-enabled", gaps),
                VoiceSession = Safely(() => _env.IsVoiceSession(signal.Tenant, signal.SessionId), "voice-session", gaps),
                StateLabel = session?.StateLabel,
                TriageBucket = session?.TriageBucket,
                NeedsYouSinceUtc = session?.NeedsYouSince,
            },
            Verdict = null,
            Gaps = gaps,
        };

        var path = _env.Write(record);
        if (path is null)
            FileLog.Write($"[TurnLogRecorder] record NOT written sid={signal.SessionId} - the writer refused it");
        return path;
    }

    private static TurnLogTerminal BuildTerminal(ScreenGridResponse? grid, BufferResponse? scrollback)
    {
        var lines = SplitScrollback(scrollback?.Text);
        return new TurnLogTerminal
        {
            HasGrid = grid?.HasGrid ?? false,
            Rows = grid?.Rows ?? new List<string>(),
            RowCount = grid?.Rows.Count ?? 0,
            CursorRow = grid?.CursorRow ?? -1,
            CursorCol = grid?.CursorCol ?? -1,
            CursorVisible = grid?.CursorVisible ?? false,
            IsAlternateScreen = grid?.IsAlternateScreen ?? false,
            Scrollback = lines,
            ScrollbackLineCount = lines.Count,
            ScrollbackLinesRequested = ScrollbackLinesCaptured,
        };
    }

    private static List<string> SplitScrollback(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        return text.Replace("\r\n", "\n").Split('\n').ToList();
    }

    /// <summary>
    /// Cut the stored conversation down to the last <see cref="FullTurnsCaptured"/> full turns.
    ///
    /// A FULL TURN STARTS AT A HUMAN MESSAGE, on the owner's definition that a turn is both sides together.
    /// So the cut walks back counting those and keeps everything from the tenth one onward, which keeps each
    /// kept turn WHOLE rather than slicing a fixed number of messages and handing the corpus an agent reply
    /// whose prompt was cut off.
    ///
    /// A TOOL RESULT IS NOT A HUMAN TURN, even though it arrives with the user's role. Codex records every
    /// <c>function_call_output</c> as a User message carrying a single ToolResult part
    /// (<c>CodexTranscriptReader</c>), so counting bare roles would let ten tool calls inside ONE human turn
    /// consume the whole window - and the conversation we stored would begin in the middle of a turn, with
    /// the prompt that started it cut off, which is the exact failure keeping whole turns exists to prevent.
    /// A human turn is a user message carrying something a person actually wrote.
    /// </summary>
    internal static TurnLogConversation BuildConversation(StoredConversationSnapshot? stored)
    {
        if (stored is null) return new TurnLogConversation();

        var all = stored.Messages;
        var startIndex = 0;
        var humanTurns = 0;
        for (var i = all.Count - 1; i >= 0; i--)
        {
            if (!IsHumanTurn(all[i])) continue;
            humanTurns++;
            if (humanTurns < FullTurnsCaptured) continue;
            startIndex = i;
            break;
        }

        return new TurnLogConversation
        {
            IsSupported = stored.IsSupported,
            Generation = stored.Generation,
            TotalMessageCount = all.Count,
            FullTurnsRequested = FullTurnsCaptured,
            Truncated = startIndex > 0,
            LastPushedAtUtc = stored.LastPushedUtc,
            Messages = all.Skip(startIndex).ToList(),
        };
    }

    /// <summary>
    /// Whether this message is a person taking a turn, as opposed to the transcript's own bookkeeping
    /// wearing the user's role.
    ///
    /// A user message counts when it carries at least one part that is not a tool result. That admits an
    /// ordinary prompt and excludes a bare <c>function_call_output</c>; a message with no parts at all is
    /// not a turn either, because nothing was said.
    /// </summary>
    private static bool IsHumanTurn(HistoryMessageDto message)
    {
        if (!string.Equals(message.Role, "User", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = message.Parts;
        if (parts is null || parts.Count == 0) return false;
        return parts.Any(p => !string.Equals(p.Kind, "ToolResult", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A read whose failure must not cost the whole record. Answers null AND writes a gap.
    ///
    /// The gap is the point. A null with no gap is indistinguishable from a value that is genuinely unknown,
    /// so a record could report "we do not know whether the supervisor was on" for a fault that was ours -
    /// and a corpus mined later would read those as sessions with no supervisor rather than as sessions we
    /// failed to ask about.
    /// </summary>
    private static bool? Safely(Func<bool?> read, string part, List<TurnLogGap> gaps)
    {
        try { return read(); }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnLogRecorder] an observed-state read FAILED ({part}): {ex.Message}");
            gaps.Add(new TurnLogGap { Part = part, Reason = ex.Message });
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stopping.Cancel(); } catch (ObjectDisposedException) { }
        _stopping.Dispose();
    }
}
