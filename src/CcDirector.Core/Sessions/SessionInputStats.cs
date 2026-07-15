using System.Collections.Concurrent;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Per-session tally of how the operator drove this session: submitted TURNS and CHARACTER volume, split
/// by (<see cref="InputModality"/>, <see cref="InputSurface"/>). This is the honest instrumentation heart
/// of the DevThrottle Stats mission: the count is taken at the Session choke point
/// (<see cref="Session.SendInput(byte[], InputOrigin?)"/> / <see cref="Session.SendTextAsync(string, SendSource, InputOrigin?)"/>),
/// the one place that sees desktop-local input too, so a published phone/voice share is never silently
/// inflated by a surface the Gateway cannot see.
///
/// Counting rules (mission "unit of how much"):
/// - A submitted turn (a whole message that ends with Enter, or a dictation/voice-turn delivery) is one
///   turn via <see cref="RecordTurn"/>. One spoken utterance and one typed message each count as one turn,
///   so neither modality is inflated by its mechanics.
/// - Raw keystrokes (a <see cref="Session.SendInput(byte[], InputOrigin?)"/> that is terminal typing, not a
///   prompt submission) are counted as typed CHARACTER volume only via <see cref="RecordCharacters"/> -
///   NEVER synthesized into turns.
///
/// Thread-safe: several PTY/stream callers can record concurrently.
/// </summary>
public sealed class SessionInputStats
{
    private sealed class Counters
    {
        public long Turns;
        public long Characters;
    }

    private readonly ConcurrentDictionary<(InputModality Modality, InputSurface Surface), Counters> _buckets = new();

    // Turns this session was driven by ANOTHER AGENT (issue #1636), on their own lane - never in _buckets.
    // See InputStatsDto.AgentDrivenTurns for why they are kept apart rather than given a modality.
    private readonly Counters _agentDriven = new();

    /// <summary>Raised after any recorded change, so the host can persist and push a delta (debounced by
    /// the host - this fires per keystroke). Carries no data; readers call <see cref="Snapshot"/>.</summary>
    public event Action? Changed;

    /// <summary>
    /// Record one submitted turn from <paramref name="origin"/>, plus its <paramref name="characters"/> of
    /// text volume. Used by <see cref="Session.SendTextAsync(string, SendSource, InputOrigin?)"/> and by a
    /// dictation/voice-turn delivery.
    /// </summary>
    public void RecordTurn(InputOrigin origin, int characters)
    {
        var c = _buckets.GetOrAdd((origin.Modality, origin.Surface), static _ => new Counters());
        Interlocked.Increment(ref c.Turns);
        if (characters > 0)
            Interlocked.Add(ref c.Characters, characters);
        Changed?.Invoke();
    }

    /// <summary>
    /// Record <paramref name="characters"/> of raw typed keystrokes from <paramref name="origin"/> with NO
    /// turn (mission rule: a bare keystroke is the user composing, not a submitted turn). Used by
    /// <see cref="Session.SendInput(byte[], InputOrigin?)"/> for terminal typing.
    /// </summary>
    public void RecordCharacters(InputOrigin origin, int characters)
    {
        if (characters <= 0) return;
        var c = _buckets.GetOrAdd((origin.Modality, origin.Surface), static _ => new Counters());
        Interlocked.Add(ref c.Characters, characters);
        Changed?.Invoke();
    }

    /// <summary>
    /// Record one turn that ANOTHER AGENT drove into this session (issue #1636) - a fleet message, ask, or
    /// broadcast. A real turn: the sending agent decided to send it. Counted on its own lane, never into
    /// the human buckets, so the voice-versus-typed and phone-versus-desktop numbers stay about the human.
    ///
    /// Framework-authored text (handover, queue drain, pre-prompt) is NOT this: it carries nobody's
    /// decision and is not counted at all, by anything.
    /// </summary>
    public void RecordAgentTurn(int characters)
    {
        Interlocked.Increment(ref _agentDriven.Turns);
        if (characters > 0)
            Interlocked.Add(ref _agentDriven.Characters, characters);
        Changed?.Invoke();
    }

    /// <summary>An immutable snapshot of the tally as the shared wire DTO, buckets in a stable order.</summary>
    public InputStatsDto Snapshot()
    {
        var dto = new InputStatsDto
        {
            AgentDrivenTurns = Interlocked.Read(ref _agentDriven.Turns),
            AgentDrivenCharacters = Interlocked.Read(ref _agentDriven.Characters),
        };
        foreach (var kvp in _buckets
                     .OrderBy(b => b.Key.Modality)
                     .ThenBy(b => b.Key.Surface))
        {
            var origin = new InputOrigin(kvp.Key.Modality, kvp.Key.Surface);
            dto.Buckets.Add(new InputStatBucketDto
            {
                Modality = origin.ModalityToken,
                Surface = origin.SurfaceToken,
                Turns = Interlocked.Read(ref kvp.Value.Turns),
                Characters = Interlocked.Read(ref kvp.Value.Characters),
            });
        }
        return dto;
    }

    /// <summary>True when nothing has been counted yet (used to skip pushing an empty tally). A session
    /// driven ONLY by other agents has no buckets but is not empty - it must still reach the wire, or the
    /// agent-to-agent tally would silently miss exactly the sessions it is about.</summary>
    public bool IsEmpty => _buckets.IsEmpty && Interlocked.Read(ref _agentDriven.Turns) == 0;

    /// <summary>
    /// Replace the tally with a persisted snapshot (Director restart restore). Buckets not present in
    /// <paramref name="dto"/> are cleared; a null or empty dto leaves the tally empty. Does NOT raise
    /// <see cref="Changed"/> - seeding is a load, not new user activity.
    /// </summary>
    public void Seed(InputStatsDto? dto)
    {
        _buckets.Clear();
        Interlocked.Exchange(ref _agentDriven.Turns, dto?.AgentDrivenTurns ?? 0);
        Interlocked.Exchange(ref _agentDriven.Characters, dto?.AgentDrivenCharacters ?? 0);
        if (dto?.Buckets is null) return;
        foreach (var b in dto.Buckets)
        {
            var modality = string.Equals(b.Modality, "voice", StringComparison.OrdinalIgnoreCase)
                ? InputModality.Voice
                : InputModality.Typed;
            var surface = (b.Surface ?? "").Trim().ToLowerInvariant() switch
            {
                "desktop" => InputSurface.Desktop,
                "cockpit" => InputSurface.Cockpit,
                "phone" => InputSurface.Phone,
                _ => InputSurface.Unknown,
            };
            _buckets[(modality, surface)] = new Counters { Turns = b.Turns, Characters = b.Characters };
        }
    }
}
