using System.Collections.Concurrent;

namespace CcDirector.Core.Storage;

/// <summary>
/// Where each submitted prompt came from - typed or spoken, desktop or cockpit or phone - held in
/// memory just long enough to be joined onto the message and pushed to the Gateway (issue #1551).
///
/// This exists because the two halves of a prompt record are knowable in two different places:
/// - WHAT was said is the agent's business, read back from its transcript at the end of the turn.
/// - WHERE IT CAME FROM is only ever known HERE, at the Session choke points. By the time a prompt
///   reaches a transcript, one spoken on the phone and one typed at the terminal are identical.
///
/// In memory, not on disk: the Director keeps no log of its own - it captures and pushes, and the
/// Gateway holds the single copy. An origin only has to survive the seconds between the operator
/// submitting and the turn ending. If the Director dies in that window the origin is simply gone, and
/// the message is honestly recorded as "unknown" rather than guessed.
///
/// Bounded per session so a long-running session cannot grow this without limit.
/// </summary>
public static class InputOriginBuffer
{
    /// <summary>How many recent submissions to remember per session. A turn ends within seconds of a
    /// submission, so this only has to cover a burst - not a session's history.</summary>
    private const int MaxPerSession = 256;

    private static readonly ConcurrentDictionary<string, LinkedList<InputOriginEvent>> _bySession = new();

    /// <summary>Note that a submission just crossed a choke point.</summary>
    public static void Record(string sessionId, InputOriginEvent origin)
    {
        var list = _bySession.GetOrAdd(sessionId, _ => new LinkedList<InputOriginEvent>());
        lock (list)
        {
            list.AddLast(origin);
            while (list.Count > MaxPerSession) list.RemoveFirst();
        }
    }

    /// <summary>The submissions remembered for this session, oldest first.</summary>
    public static IReadOnlyList<InputOriginEvent> For(string sessionId)
    {
        if (!_bySession.TryGetValue(sessionId, out var list)) return Array.Empty<InputOriginEvent>();
        lock (list) return list.ToArray();
    }

    /// <summary>Drop a session's origins (it ended).</summary>
    public static void Forget(string sessionId) => _bySession.TryRemove(sessionId, out _);

    /// <summary>Drop everything (tests).</summary>
    public static void Clear() => _bySession.Clear();
}

/// <summary>
/// One submission crossing a Session choke point. Carries no prompt text: the text is read back from
/// the agent's own transcript, and this is joined to it by nearest timestamp.
/// </summary>
/// <param name="TsUtc">When the submission crossed the choke point.</param>
/// <param name="Modality">"typed" or "voice".</param>
/// <param name="Surface">"desktop", "cockpit", "phone", or "unknown".</param>
/// <param name="CharCount">Characters submitted. A size hint only.</param>
public readonly record struct InputOriginEvent(DateTime TsUtc, string Modality, string Surface, int CharCount);
