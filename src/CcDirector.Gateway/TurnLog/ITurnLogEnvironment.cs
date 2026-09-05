using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.TurnLog;

/// <summary>
/// Everything <see cref="TurnLogRecorder"/> needs from the running Gateway, named as an interface so the
/// recorder can be driven in a test without a Gateway, a Director, a tunnel or a database.
///
/// EVERY READ ANSWERS "CANNOT TELL" RATHER THAN GUESSING. A screen that cannot be read comes back null and
/// is written into the record as a named gap; it never comes back as an empty screen, because an empty
/// screen is a fact about a session and an unreadable one is a fact about us, and a corpus that confuses
/// them will be mined for conclusions that are not there.
/// </summary>
public interface ITurnLogEnvironment
{
    /// <summary>Whether capture is switched on for this account and machine.</summary>
    bool IsEnabled(string account, string machine);

    /// <summary>
    /// The session as the Gateway holds it, whole. Null when the session cannot be located.
    ///
    /// The returned object is the CALLER'S to modify - implementations hand back a snapshot, never the
    /// stored instance - so the recorder may fill in fields the raw push leaves empty without disturbing
    /// anything else reading the roster.
    /// </summary>
    SessionDto? LocateSession(TenantId tenant, string sessionId);

    /// <summary>
    /// The name of the computer a Director runs on, from that Director's own registration. Null when the
    /// Director is not currently registered.
    ///
    /// It exists because a Director pushes its sessions with an EMPTY machine name and the Gateway fills it
    /// in from the registration when it SERVES the session list. Anything reading the raw pushed snapshot -
    /// which is what a turn-end capture does - is one layer earlier than that, and sees the blank.
    /// </summary>
    string? ResolveMachineName(TenantId tenant, string directorId);

    /// <summary>The live terminal grid. Null when it cannot be read.</summary>
    Task<ScreenGridResponse?> ReadScreenAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct);

    /// <summary>The scrollback behind the screen, at most <paramref name="lines"/> lines. Null when it
    /// cannot be read.</summary>
    Task<BufferResponse?> ReadScrollbackAsync(TenantId tenant, string directorId, string sessionId, int lines, CancellationToken ct);

    /// <summary>The session's stored conversation - both sides. Null when nothing has been stored for it,
    /// which for a supported agent means the push has not arrived yet rather than that there is nothing to
    /// say.</summary>
    StoredConversationSnapshot? ReadConversation(TenantId tenant, string sessionId);

    /// <summary>Whether the session supervisor is switched on for this account.</summary>
    bool? SupervisorEnabled(TenantId tenant);

    /// <summary>Whether this is a voice session, and so whether a spoken summary was due this turn.</summary>
    bool? IsVoiceSession(TenantId tenant, string sessionId);

    /// <summary>Persist the finished record. Answers where it went, or null when nothing was written.</summary>
    string? Write(TurnLogRecord record);
}

/// <summary>
/// One session's stored conversation as the log takes it: the messages, plus the two facts that make them
/// interpretable later - which transcript generation they belong to, and whether the agent supports
/// supplying a conversation at all.
/// </summary>
/// <param name="IsSupported">False is a fact about the AGENT, not an empty conversation.</param>
/// <param name="Generation">Which transcript the session is on. Turns either side of a change here are not
/// one conversation, and a corpus that cannot see the boundary will read a cleared session as a confused
/// one.</param>
/// <param name="Messages">Every message the store held for that generation, oldest first. The recorder does
/// the cutting, so the cut is one decision in one place.</param>
/// <param name="LastPushedUtc">When the computer that owns this session last pushed its conversation. The
/// Director announces a state change BEFORE it pushes the turn that caused it, so a capture can arrive
/// between the two and read a conversation that stops one turn short of the screen it is stored beside.
/// Carrying the push time makes that detectable instead of invisible - without it a stale conversation is
/// indistinguishable from a complete one, and the record would quietly claim to describe a turn it does not
/// contain.</param>
public sealed record StoredConversationSnapshot(
    bool IsSupported,
    string? Generation,
    IReadOnlyList<HistoryMessageDto> Messages,
    DateTime? LastPushedUtc = null);
