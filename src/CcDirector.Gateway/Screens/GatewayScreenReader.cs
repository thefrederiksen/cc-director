using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Screens;

/// <summary>Where a screen a reader was handed actually came from.</summary>
public enum ScreenSource
{
    /// <summary>No screen at all. The owning Director did not answer - the screen is UNREADABLE, which is
    /// a real answer and not an empty one.</summary>
    Unreadable = 0,

    /// <summary>Served from the Gateway's own store. HISTORY ONLY - see
    /// <see cref="GatewayScreenReader.ReadStored"/>. A live read is never answered this way; the reason is
    /// in the reader's own comment.</summary>
    Store = 1,

    /// <summary>Pulled live from the owning Director over the tunnel.</summary>
    Tunnel = 2,
}

/// <summary>What one live-screen read produced, and why.</summary>
/// <param name="Grid">The screen, or null when <paramref name="Source"/> is
/// <see cref="ScreenSource.Unreadable"/>. Null carries exactly the meaning the tunnel pull's null carried
/// before this type existed, so every caller's fail-closed branch is unchanged.</param>
/// <param name="Source">Where it came from. Always <see cref="ScreenSource.Tunnel"/> or
/// <see cref="ScreenSource.Unreadable"/> for a live read.</param>
/// <param name="Why">One short phrase naming the deciding fact, for the log line. Never null.</param>
public readonly record struct LiveScreenRead(ScreenGridResponse? Grid, ScreenSource Source, string Why);

/// <summary>
/// The ONE place the Gateway asks "what is on this session's screen?" (the Terminal Rules mission,
/// <c>docs/missions/terminal-rules-2026-09-02/brief.md</c>, rulings 1, 12 and 13).
///
/// TWO QUESTIONS, TWO METHODS, AND THEY ARE NOT INTERCHANGEABLE.
///
/// <b>Question A - "what was on screen at the end of that turn?"</b> History.
/// <see cref="ReadStored"/> / <see cref="ReadStoredRecent"/> answer it from the store with no freshness
/// test at all. Staleness is irrelevant to a history read, and the owning machine being offline is the
/// entire point of having stored it. Consumers: the Cockpit screen view, a rule's evaluation record,
/// "make a rule from this screen" - anything reviewing the past.
///
/// <b>Question B - "what is on screen RIGHT NOW?"</b> Live truth, and a keystroke may be pressed on the
/// answer. <see cref="ReadLiveAsync"/> answers it by asking the owning Director, ALWAYS. The store is
/// never consulted, and a Director that cannot answer is <see cref="ScreenSource.Unreadable"/> - returned
/// AS unreadable, never as a stored screen.
///
/// WHY THE STORE MAY NOT ANSWER THIS QUESTION, since an earlier version of this class let it.
///
/// The rule used to be that a stored screen could answer a live read while three facts held: the byte
/// mark taken at capture equalled the session's pushed <c>TotalBufferBytes</c>, the owning Director's
/// tunnel was connected, and the pushed snapshot was recent. The first of those three cannot do the job
/// its name claims. <c>TotalBufferBytes</c> reaches the Gateway on the session snapshot, which is
/// refreshed by a ten-second timer and by some activity transitions - NEVER by the terminal being
/// written to. So after a fresh snapshot at N the real terminal can move on while the Gateway still
/// holds N, is connected, and is well inside any age budget: all three facts pass, and the reader hands
/// back a screen the terminal has already scrolled past.
///
/// THE PRINCIPLE: a certification may only rest on a signal that is refreshed by the event it claims to
/// detect. The byte count claims "the terminal has not moved since capture" and is not refreshed when
/// the terminal moves, so it cannot establish that - and connection state and snapshot age do not repair
/// it, because they answer different questions.
///
/// AND THE OPTIMISATION WAS WORTH NOTHING ANYWAY, which is what settled it. The store could only ever
/// answer while the owning Director's tunnel was CONNECTED - which is exactly the condition under which
/// the tunnel could have answered the question itself. So the live half never bought availability, not
/// once, by construction; it bought latency on a connection that was already up. Making the byte count
/// current would not rescue it either: a coalesced push says "the terminal has not moved RECENTLY", and
/// a keystroke follows this answer.
///
/// The half the mission was actually for is untouched: a session's turn-end screen, stored per account
/// for seven days, readable from anywhere INCLUDING while the owning machine is offline. That is
/// <see cref="ReadStored"/>, and it is the only thing the store is for.
/// </summary>
public sealed class GatewayScreenReader
{
    private readonly SessionScreenStore _store;

    public GatewayScreenReader(SessionScreenStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
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
    /// QUESTION B. What is on the session's screen right now, read from the owning Director over the
    /// tunnel. A Director that does not answer, or that throws, is <see cref="ScreenSource.Unreadable"/>
    /// with a null grid - the same null <c>GetScreenGridAsync</c> returned before this class existed, so a
    /// caller's fail-closed branch needs no change.
    /// </summary>
    /// <remarks>INTERNAL because <see cref="SessionVerbClient"/> is: the tunnel route is a Gateway-internal
    /// seam and there is no caller outside this assembly. The history reads above are public, because a
    /// stored screen is ordinary data.</remarks>
    internal async Task<LiveScreenRead> ReadLiveAsync(
        SessionVerbClient route, string sessionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        ScreenGridResponse? grid;
        try { grid = await route.GetScreenGridAsync(sessionId, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayScreenReader] sid={sessionId}: live read threw ({ex.Message}) - UNREADABLE");
            return new LiveScreenRead(null, ScreenSource.Unreadable, "tunnel read threw; no tunnel answer");
        }
        if (grid is null)
        {
            FileLog.Write($"[GatewayScreenReader] sid={sessionId}: the owning Director did not answer - UNREADABLE");
            return new LiveScreenRead(null, ScreenSource.Unreadable, "no tunnel answer");
        }
        FileLog.Write($"[GatewayScreenReader] sid={sessionId}: pulled the screen over the TUNNEL");
        return new LiveScreenRead(grid, ScreenSource.Tunnel, "live truth is always read from the owning Director");
    }
}
