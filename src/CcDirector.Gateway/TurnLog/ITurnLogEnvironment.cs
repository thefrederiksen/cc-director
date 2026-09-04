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

    /// <summary>The session as the Gateway holds it, whole. Null when the session cannot be located.</summary>
    SessionDto? LocateSession(TenantId tenant, string sessionId);

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
public sealed record StoredConversationSnapshot(bool IsSupported, string? Generation, IReadOnlyList<HistoryMessageDto> Messages);
