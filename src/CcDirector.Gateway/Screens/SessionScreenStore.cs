using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Screens;

/// <summary>
/// THE terminal-screen store (the Terminal Rules mission,
/// <c>docs/missions/terminal-rules-2026-09-02/brief.md</c>). A Director pushes each session's turn-end
/// screen here; the wingman's screen readers, the supervisor, and later the rules engine read it instead
/// of pulling a fresh grid down the tunnel every time. Tenant-scoped through the context's global filter
/// like every other store, so one account's rows can never answer another account's read.
///
/// Deliberately SEPARATE from the conversation store the turn-push mission owns. A screen is bulky, it
/// is captured from the terminal rather than from a transcript, and it is worthless after a few days -
/// so it gets its own table, its own push, and its own retention (seven days, run by
/// <see cref="SessionScreenSweep"/>).
///
/// The properties the readers lean on:
///
///  - IDEMPOTENT, PER DIRECTOR. A screen is keyed by (tenant, session, captured-at, director); a capture
///    the SAME Director re-sends after a reconnect is stored once, and two Directors that captured the
///    same session id in the same millisecond keep both rows rather than one swallowing the other.
///    Capture times are pinned to whole milliseconds on both sides so a row cannot be written at one
///    precision and looked up at another.
///  - BOUNDED PER SESSION. A session keeps at most <see cref="MaxScreensPerSession"/> screens; the
///    oldest are trimmed at write time. Retention alone is not a bound - a session that ends a hundred
///    turns an hour would otherwise hold seven days of them - and an unbounded table is a slow read for
///    every reader that follows.
///  - THE LATEST IS ONE INDEXED READ. Readers overwhelmingly want "the newest screen for this session",
///    which the key's (tenant, session, captured-at) prefix answers directly.
///  - IT IS HISTORY, AND ONLY HISTORY. This store never claims a screen is live and is never consulted
///    for a live read. An earlier design let a stored screen answer "what is on screen right now?" while
///    its byte mark still matched the session's pushed total; that mark is not refreshed when the terminal
///    is written to, so it could not establish what its name claimed. See <see cref="GatewayScreenReader"/>
///    for the whole argument, and ruling 13 for why the mechanism was removed rather than repaired.
/// </summary>
public sealed class SessionScreenStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly Func<GatewayDbContext> _context;

    /// <summary>The most turn-end screens one session keeps. Beyond this the oldest are trimmed at write
    /// time, so the table is bounded by (sessions * this) and not by how busy a session is.</summary>
    public const int MaxScreensPerSession = 200;

    /// <summary>The most rows one pushed screen may carry. A terminal grid is tens of rows; a push
    /// claiming thousands is a bug in the sender and is refused rather than stored.</summary>
    public const int MaxRows = 500;

    /// <summary>The longest single row accepted. A very wide terminal is a few hundred columns.</summary>
    public const int MaxRowLength = 4000;

    public SessionScreenStore(GatewayDatabase db)
        : this(() => (db ?? throw new ArgumentNullException(nameof(db))).CreateContext())
    {
        ArgumentNullException.ThrowIfNull(db);
    }

    /// <summary>
    /// Build the store over an arbitrary context source. INTERNAL, and it exists for one reason: the
    /// mission's migration is not written yet (the fleet-wide slot is held), and a real
    /// <see cref="GatewayDatabase"/> creates its schema with <c>Database.Migrate()</c>, so it cannot produce
    /// a database containing <c>session_screens</c> until that lands. This lets the tests build the schema
    /// from the mapped MODEL instead - see <c>ScreenStoreTestDb</c>, which also states the limit that comes
    /// with it. Nothing about the production path changes: the public constructor above supplies
    /// <c>db.CreateContext</c>, which is exactly what this class called before.
    /// </summary>
    internal SessionScreenStore(Func<GatewayDbContext> context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Store one pushed screen. Throws <see cref="ArgumentException"/> for a push that disagrees with
    /// itself (see <see cref="Validate"/>) - the caller logs and refuses it, and nothing is written.
    /// Returns true when a row was added, false when this exact capture was already held.
    /// </summary>
    public bool Append(string directorId, ScreenPush push, DateTime nowUtc)
    {
        Validate(directorId, push);
        var now = Utc(nowUtc);
        var capturedAt = CapturePrecision(Utc(push.CapturedAtUtc));

        lock (_gate)
        {
            // One retry when another writer got there first. Two Gateway processes overlap for a moment
            // during a deploy swap, and both may be handed the same reconnect re-send; the key makes the
            // second one a duplicate, and re-reading lets it see that and say so instead of throwing.
            try { return AppendOnce(directorId, push, capturedAt, now); }
            catch (DbUpdateException ex)
            {
                FileLog.Write($"[SessionScreenStore] session={push.SessionId}: write lost a race with another writer ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}); re-reading and retrying once");
                return AppendOnce(directorId, push, capturedAt, now);
            }
        }
    }

    private bool AppendOnce(string directorId, ScreenPush push, DateTime capturedAt, DateTime now)
    {
        using var ctx = _context();
        using var tx = ctx.Database.BeginTransaction();

        // The DIRECTOR is part of this test because it is part of the key. Without it a second Director's
        // distinct capture of the same session at the same millisecond read as the first one's duplicate and
        // was dropped (inspection 01, finding 3).
        var already = ctx.SessionScreens.Any(s =>
            s.SessionId == push.SessionId && s.CapturedAtUtc == capturedAt && s.DirectorId == directorId);
        if (already)
        {
            tx.Commit();
            FileLog.Write($"[SessionScreenStore] session={push.SessionId}: screen captured {capturedAt:O} already stored; push ignored");
            return false;
        }

        ctx.SessionScreens.Add(new SessionScreenEntity
        {
            TenantId = ctx.ActiveTenant!,
            SessionId = push.SessionId,
            CapturedAtUtc = capturedAt,
            DirectorId = directorId,
            RowsJson = JsonSerializer.Serialize(push.Rows, Json),
            CursorRow = push.CursorRow,
            CursorCol = push.CursorCol,
            CursorVisible = push.CursorVisible,
            IsAlternateScreen = push.IsAlternateScreen,
            HasGrid = push.HasGrid,
            BufferBytes = push.BufferBytes,
            ActivityState = push.ActivityState ?? "",
            Agent = push.Agent ?? "",
            ReceivedAtUtc = now,
        });
        ctx.SaveChanges();

        var trimmed = TrimToCap(ctx, push.SessionId);
        tx.Commit();

        var trimNote = trimmed > 0 ? $"; trimmed {trimmed} older screen(s) over the per-session cap" : "";
        FileLog.Write($"[SessionScreenStore] session={push.SessionId}: stored screen captured {capturedAt:O} rows={push.Rows.Count} bufferBytes={push.BufferBytes} hasGrid={push.HasGrid} alt={push.IsAlternateScreen}{trimNote}");
        return true;
    }

    /// <summary>Keep only the newest <see cref="MaxScreensPerSession"/> screens for the session. Runs
    /// inside the push transaction, so the cap holds even under a burst.</summary>
    private static int TrimToCap(GatewayDbContext ctx, string sessionId)
    {
        var count = ctx.SessionScreens.Count(s => s.SessionId == sessionId);
        if (count <= MaxScreensPerSession) return 0;
        // The capture time of the OLDEST screen that is still inside the cap. Everything strictly older
        // goes. Taken as a value rather than as a Skip/Take delete because ExecuteDelete cannot carry an
        // ordering, and a delete that guessed at the boundary would cut the wrong rows.
        var cutoff = ctx.SessionScreens
            .Where(s => s.SessionId == sessionId)
            .OrderByDescending(s => s.CapturedAtUtc)
            .Select(s => s.CapturedAtUtc)
            .Skip(MaxScreensPerSession - 1)
            .First();
        return ctx.SessionScreens
            .Where(s => s.SessionId == sessionId && s.CapturedAtUtc < cutoff)
            .ExecuteDelete();
    }

    /// <summary>
    /// The session's newest stored screen, or null when nothing has been pushed for it. Never crosses a
    /// tenant: the context's global filter answers only rows of the ambient tenant, so another account's
    /// session id reads as "nothing stored" and not as a screen.
    /// </summary>
    public StoredScreen? ReadLatest(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        lock (_gate)
        {
            using var ctx = _context();
            // The Director breaks a tie on capture time, so "the newest" is a deterministic row rather than
            // whichever of two same-millisecond captures the provider happened to return first.
            var row = ctx.SessionScreens.AsNoTracking()
                .Where(s => s.SessionId == sessionId)
                .OrderByDescending(s => s.CapturedAtUtc)
                .ThenByDescending(s => s.DirectorId)
                .FirstOrDefault();
            return row is null ? null : ToStoredScreen(row);
        }
    }

    /// <summary>The session's newest <paramref name="limit"/> stored screens, newest first - the read
    /// behind "show me this session's recent screens".</summary>
    public IReadOnlyList<StoredScreen> ReadRecent(string sessionId, int limit)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "limit must be positive");
        lock (_gate)
        {
            using var ctx = _context();
            return ctx.SessionScreens.AsNoTracking()
                .Where(s => s.SessionId == sessionId)
                .OrderByDescending(s => s.CapturedAtUtc)
                .ThenByDescending(s => s.DirectorId)
                .Take(Math.Min(limit, MaxScreensPerSession))
                .ToList()
                .Select(ToStoredScreen)
                .ToList();
        }
    }

    /// <summary>
    /// Retention: every screen received before <paramref name="cutoffUtc"/> is removed. Screens are
    /// independent rows - there is no prefix to keep whole and no head row to judge on - so this is the
    /// whole of it. Seven days, applied by <see cref="SessionScreenSweep"/>.
    /// </summary>
    public int PurgeOlderThan(DateTime cutoffUtc)
    {
        var cutoff = Utc(cutoffUtc);
        lock (_gate)
        {
            using var ctx = _context();
            var removed = ctx.SessionScreens.Where(s => s.ReceivedAtUtc < cutoff).ExecuteDelete();
            if (removed > 0)
                FileLog.Write($"[SessionScreenStore] PurgeOlderThan: removed {removed} screen(s) received before {cutoff:O}");
            return removed;
        }
    }

    private static StoredScreen ToStoredScreen(SessionScreenEntity row) => new()
    {
        SessionId = row.SessionId,
        CapturedAtUtc = DateTime.SpecifyKind(row.CapturedAtUtc, DateTimeKind.Utc),
        BufferBytes = row.BufferBytes,
        ActivityState = row.ActivityState,
        Agent = row.Agent,
        DirectorId = row.DirectorId,
        Grid = new ScreenGridResponse
        {
            SessionId = row.SessionId,
            Rows = JsonSerializer.Deserialize<List<string>>(row.RowsJson, Json) ?? new List<string>(),
            CursorRow = row.CursorRow,
            CursorCol = row.CursorCol,
            CursorVisible = row.CursorVisible,
            IsAlternateScreen = row.IsAlternateScreen,
            HasGrid = row.HasGrid,
        },
    };

    /// <summary>Refuse a push that disagrees with itself before anything is written. A malformed push is
    /// a bug in the Director that sent it, and the honest answer is an error it can log, not a stored row
    /// a reader will later be asked to act on. Covers the whole object graph, because a push arrives
    /// deserialized and a null where a list belongs must read as "malformed", not as a crash.</summary>
    internal static void Validate(string directorId, ScreenPush push)
    {
        ArgumentNullException.ThrowIfNull(push);
        ArgumentException.ThrowIfNullOrEmpty(directorId);
        ArgumentException.ThrowIfNullOrEmpty(push.SessionId);
        if (push.Rows is null) throw new ArgumentException("Rows is null", nameof(push));
        if (push.SessionId.Length > 64) throw new ArgumentException("session id longer than 64 characters", nameof(push));
        if (directorId.Length > 64) throw new ArgumentException("director id longer than 64 characters", nameof(push));
        if (push.CapturedAtUtc == default) throw new ArgumentException("CapturedAtUtc is not set", nameof(push));
        if (push.BufferBytes < 0) throw new ArgumentException("BufferBytes is negative", nameof(push));
        if ((push.Agent?.Length ?? 0) > 32) throw new ArgumentException("agent name longer than 32 characters", nameof(push));
        if ((push.ActivityState?.Length ?? 0) > 32) throw new ArgumentException("activity state longer than 32 characters", nameof(push));
        if (push.Rows.Count > MaxRows)
            throw new ArgumentException($"a screen push may carry at most {MaxRows} rows; this one carries {push.Rows.Count}", nameof(push));
        // HasGrid is the readability flag every reader fails closed on, so it must agree with the rows it
        // arrived with: claiming a grid while sending none would store a screen that reads as "nothing on
        // screen" to a caller that is about to type into it.
        if (push.HasGrid && push.Rows.Count == 0)
            throw new ArgumentException("HasGrid is true but no rows were sent", nameof(push));
        for (var i = 0; i < push.Rows.Count; i++)
        {
            var row = push.Rows[i];
            if (row is null) throw new ArgumentException($"row {i} is null", nameof(push));
            if (row.Length > MaxRowLength)
                throw new ArgumentException($"row {i} is {row.Length} characters; the limit is {MaxRowLength}", nameof(push));
        }
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    /// <summary>The precision a capture time is compared AND stored at: whole milliseconds. Postgres keeps
    /// microseconds and .NET keeps hundred-nanosecond ticks, so a time written at full precision could not
    /// be found again by the exact-match duplicate check on one provider and could on the other. One
    /// precision on both sides, chosen coarser than either store, removes the disagreement.</summary>
    internal static DateTime CapturePrecision(DateTime utc)
        => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
}
