using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;

namespace CcDirector.Gateway.Screens;

/// <summary>Where a screen a reader was handed actually came from.</summary>
public enum ScreenSource
{
    /// <summary>No screen at all. The store could not vouch for one and the tunnel did not answer - the
    /// screen is UNREADABLE, which is a real answer and not an empty one.</summary>
    Unreadable = 0,

    /// <summary>Served from the Gateway's own store, with all three freshness facts established.</summary>
    Store = 1,

    /// <summary>Pulled live from the owning Director over the tunnel.</summary>
    Tunnel = 2,
}

/// <summary>What one live-screen read produced, and why.</summary>
/// <param name="Grid">The screen, or null when <paramref name="Source"/> is
/// <see cref="ScreenSource.Unreadable"/>. Null carries exactly the meaning the tunnel pull's null carried
/// before this type existed, so every caller's fail-closed branch is unchanged.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Why">One short phrase naming the deciding fact, for the log line. Never null.</param>
public readonly record struct LiveScreenRead(ScreenGridResponse? Grid, ScreenSource Source, string Why);

/// <summary>
/// The ONE place the Gateway asks "what is on this session's screen?" (the Terminal Rules mission,
/// <c>docs/missions/terminal-rules-2026-09-02/brief.md</c>, ruling 1 in
/// <c>rulings/r1-store-freshness.md</c>).
///
/// TWO QUESTIONS, TWO METHODS, AND THEY ARE NOT INTERCHANGEABLE.
///
/// <b>Question A - "what was on screen at the end of that turn?"</b> History.
/// <see cref="ReadStored"/> / <see cref="ReadStoredRecent"/> answer it from the store with NO freshness
/// test at all. Staleness is irrelevant to a history read, and the owning machine being offline is the
/// entire point of having stored it. Consumers: the Cockpit screen view, a rule's evaluation record,
/// "make a rule from this screen" - anything reviewing the past.
///
/// <b>Question B - "what is on screen RIGHT NOW?"</b> Live truth, and a keystroke may be pressed on the
/// answer. <see cref="ReadLiveAsync"/> answers it, and the store may answer only when all THREE of these
/// are established - not any one of them:
///
///  1. the byte mark taken WITH the capture equals the session's currently pushed
///     <see cref="SessionDto.TotalBufferBytes"/>, AND
///  2. the owning Director's tunnel is CONNECTED at this instant - a positive liveness fact read off the
///     connection registry, never inferred from the absence of a newer push, AND
///  3. that pushed snapshot is younger than <see cref="LiveSnapshotBudget"/>.
///
/// Any one unestablished and the read goes to the tunnel; a tunnel that cannot answer is
/// <see cref="ScreenSource.Unreadable"/>, and unreadable is returned AS unreadable - never as a stored
/// screen.
///
/// WHY ALL THREE, WHEN BYTE EQUALITY LOOKS SUFFICIENT. The pushed byte count is the LAST VALUE THE
/// OWNING DIRECTOR SENT, not a live reading. When the push stream freezes - machine asleep, tunnel down,
/// or plain lag - the mark and the current value are equal BECAUSE NOTHING IS ARRIVING, not because
/// nothing changed. On its own, condition 1 is a check whose pass condition is an absence, and it fails
/// open in precisely the case this feature was built for: offline becomes indistinguishable from quiet,
/// and a session that went silent starts getting keystrokes pressed at it on the strength of a screen
/// from before it went silent. Conditions 2 and 3 are what make the pass condition a presence: a live
/// connection and a recent push, both positively observed.
///
/// WHY STRICT EQUALITY AND NOT A THRESHOLD. The nearest precedent in this repository is the dictation
/// moved-on guard (<c>GatewayDictationEndpoint</c>, issue #1006), which compares
/// <c>TotalBufferBytes &gt; baseline + MovedOnBufferGrowthBytes</c>. Do not copy that across. It is
/// deciding whether to DROP a person's words, so a false drop costs them their sentence and it is right
/// to allow a little noise. Here the conservative direction is the opposite: any byte at all means the
/// screen may have moved, so it falls to the tunnel, which is cheap and correct.
/// </summary>
internal sealed class GatewayScreenReader
{
    /// <summary>
    /// How old the pushed snapshot backing a live verdict may be. Twenty seconds: a Director pushes its
    /// session snapshot every ten, so this allows exactly one missed tick of slack and no more. It is
    /// deliberately the same window <see cref="PushedSessionStore.TryGetFresh"/> applies to "reads that
    /// ACT" - and this is such a read, because a keystroke can follow it.
    /// </summary>
    public static readonly TimeSpan LiveSnapshotBudget = TimeSpan.FromSeconds(20);

    private readonly SessionScreenStore _store;
    private readonly PushedSessionStore _pushed;
    private readonly Func<DateTime> _nowUtc;

    public GatewayScreenReader(SessionScreenStore store, PushedSessionStore pushed, Func<DateTime>? nowUtc = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _pushed = pushed ?? throw new ArgumentNullException(nameof(pushed));
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// QUESTION A. The session's newest stored turn-end screen, whatever its age, or null when nothing has
    /// been pushed for it. No freshness test: this is the history read, and it is the one that works while
    /// the owning machine is offline. Never crosses a tenant - the store's global query filter answers only
    /// the ambient tenant's rows.
    /// </summary>
    public StoredScreen? ReadStored(string sessionId) => _store.ReadLatest(sessionId);

    /// <summary>QUESTION A, more than one: the session's newest stored screens, newest first.</summary>
    public IReadOnlyList<StoredScreen> ReadStoredRecent(string sessionId, int limit)
        => _store.ReadRecent(sessionId, limit);

    /// <summary>
    /// QUESTION B. What is on the session's screen right now: the stored screen when all three freshness
    /// facts hold, otherwise a live tunnel pull, otherwise <see cref="ScreenSource.Unreadable"/> with a
    /// null grid. The null is the same null <c>GetScreenGridAsync</c> returned before this existed, so a
    /// caller's fail-closed branch needs no change.
    /// </summary>
    public async Task<LiveScreenRead> ReadLiveAsync(
        TenantId tenant, SessionVerbClient route, string sessionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        var certified = CertifyStored(tenant, route.Director.DirectorId, sessionId, out var why);
        if (certified is not null)
        {
            FileLog.Write($"[GatewayScreenReader] sid={sessionId}: served the STORED screen captured {certified.CapturedAtUtc:O} ({why}) - no tunnel read");
            return new LiveScreenRead(certified.Grid, ScreenSource.Store, why);
        }

        ScreenGridResponse? grid;
        try { grid = await route.GetScreenGridAsync(sessionId, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayScreenReader] sid={sessionId}: store could not vouch ({why}) and the tunnel read threw ({ex.Message}) - UNREADABLE");
            return new LiveScreenRead(null, ScreenSource.Unreadable, $"{why}; tunnel read threw");
        }
        if (grid is null)
        {
            FileLog.Write($"[GatewayScreenReader] sid={sessionId}: store could not vouch ({why}) and the owning Director did not answer - UNREADABLE");
            return new LiveScreenRead(null, ScreenSource.Unreadable, $"{why}; no tunnel answer");
        }
        FileLog.Write($"[GatewayScreenReader] sid={sessionId}: pulled the screen over the TUNNEL ({why})");
        return new LiveScreenRead(grid, ScreenSource.Tunnel, why);
    }

    /// <summary>
    /// The three-fact test. Returns the stored screen only when every one of them is positively
    /// established, and always sets <paramref name="why"/> to the deciding fact so the caller's log line
    /// says which one it was. Order is cheapest-first, but each is stated separately rather than
    /// short-circuited into one boolean, because a reader of the log has to be able to tell "the Director
    /// is gone" from "the screen moved" - they are different situations with the same outcome.
    /// </summary>
    private StoredScreen? CertifyStored(TenantId tenant, string directorId, string sessionId, out string why)
    {
        if (string.IsNullOrEmpty(directorId))
        {
            why = "no owning Director resolved";
            return null;
        }

        var stored = _store.ReadLatest(sessionId);
        if (stored is null)
        {
            why = "no screen stored for this session";
            return null;
        }

        var known = _pushed.GetLastKnown(tenant, directorId);

        // FACT 2 first, because it is the one an absence-shaped check gets wrong. A connected tunnel is
        // observed, not deduced: PushedSessionStore reports it from the live connection id, so a Director
        // that has gone quiet reads as false here rather than as "nothing new has arrived".
        if (!known.Connected)
        {
            why = "owning Director's tunnel is not connected";
            return null;
        }

        // FACT 3. A connected tunnel that has not pushed recently is still not evidence about the screen.
        if (known.AsOfUtc is not { } asOf)
        {
            why = "owning Director has never pushed a snapshot";
            return null;
        }
        var age = _nowUtc() - asOf;
        if (age > LiveSnapshotBudget)
        {
            why = $"pushed snapshot is {age.TotalSeconds:0.0}s old, past the {LiveSnapshotBudget.TotalSeconds:0}s budget";
            return null;
        }

        // FACT 1. The session must be IN that snapshot - a session the Director no longer reports is not
        // one we know anything current about - and its byte count must be exactly the mark taken with the
        // capture.
        SessionDto? live = null;
        foreach (var s in known.Sessions)
        {
            if (string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) { live = s; break; }
        }
        if (live is null)
        {
            why = "the session is not in the Director's current snapshot";
            return null;
        }
        if (live.TotalBufferBytes != stored.BufferBytes)
        {
            why = $"the terminal has moved ({stored.BufferBytes} bytes at capture, {live.TotalBufferBytes} now)";
            return null;
        }

        why = $"tunnel connected, snapshot {age.TotalSeconds:0.0}s old, terminal unchanged at {stored.BufferBytes} bytes";
        return stored;
    }
}
