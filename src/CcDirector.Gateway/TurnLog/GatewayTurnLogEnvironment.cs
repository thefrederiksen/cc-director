using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Settings;

namespace CcDirector.Gateway.TurnLog;

/// <summary>
/// The production wiring of <see cref="ITurnLogEnvironment"/>: each of the recorder's reads pointed at
/// machinery that already exists.
///
/// Nothing here is new plumbing. The screen and the scrollback are the same tunnel verbs the voice cluster
/// and the supervisor already call; the session snapshot is the pushed roster, so nothing is established by
/// dialing a Director; the conversation is the Gateway's own turn store, which a Director has already pushed.
/// The one genuinely new cost is that the screen is read a SECOND time at this boundary, and that is
/// deliberate - see <see cref="TurnLogRecorder"/> for why riding the supervisor's read would blind the log
/// to exactly the turns it exists to catch.
///
/// TENANT SCOPE IS ENTERED FOR THE READ THAT NEEDS IT, SYNCHRONOUSLY. The recorder runs on a background task
/// that outlives the turn-end callback, and an ambient scope does not survive into an asynchronous
/// continuation. The conversation read therefore enters the owning tenant's scope itself and hands back a
/// finished snapshot, exactly as the narration path does, rather than relying on a scope somebody else
/// entered.
/// </summary>
internal sealed class GatewayTurnLogEnvironment : ITurnLogEnvironment
{
    private readonly TurnLogSwitchStore _switches;
    private readonly TurnLogWriter _writer;
    private readonly Func<TenantId, string, SessionVerbClient?> _route;
    private readonly Func<TenantId, string, SessionDto?> _locate;
    private readonly Func<TenantId, string, StoredConversationSnapshot?> _conversation;
    private readonly TenantSettingsResolver _settings;
    private readonly Func<TenantId, string, bool> _isVoiceSession;

    public GatewayTurnLogEnvironment(
        TurnLogSwitchStore switches,
        TurnLogWriter writer,
        Func<TenantId, string, SessionVerbClient?> route,
        Func<TenantId, string, SessionDto?> locate,
        Func<TenantId, string, StoredConversationSnapshot?> conversation,
        TenantSettingsResolver settings,
        Func<TenantId, string, bool> isVoiceSession)
    {
        _switches = switches ?? throw new ArgumentNullException(nameof(switches));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _locate = locate ?? throw new ArgumentNullException(nameof(locate));
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _isVoiceSession = isVoiceSession ?? throw new ArgumentNullException(nameof(isVoiceSession));
    }

    /// <inheritdoc />
    public bool IsEnabled(string account, string machine) => _switches.IsEnabled(account, machine);

    /// <inheritdoc />
    public SessionDto? LocateSession(TenantId tenant, string sessionId) => _locate(tenant, sessionId);

    /// <inheritdoc />
    public async Task<ScreenGridResponse?> ReadScreenAsync(
        TenantId tenant, string directorId, string sessionId, CancellationToken ct)
    {
        var route = _route(tenant, directorId);
        if (route is null) return null;   // that computer is not connected - a fact about reach, not a screen
        return await route.GetScreenGridAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<BufferResponse?> ReadScrollbackAsync(
        TenantId tenant, string directorId, string sessionId, int lines, CancellationToken ct)
    {
        var route = _route(tenant, directorId);
        if (route is null) return null;
        // Cleaned rather than raw: the escape codes are presentation, and the grid above already carries the
        // resolved screen. What the scrollback is FOR here is how the turn got to where it ended, which is
        // the text.
        return await route.GetBufferAsync(sessionId, lines, raw: false, since: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public StoredConversationSnapshot? ReadConversation(TenantId tenant, string sessionId)
        => _conversation(tenant, sessionId);

    /// <inheritdoc />
    public bool? SupervisorEnabled(TenantId tenant) => _settings.SessionSupervisor(tenant).Enabled;

    /// <inheritdoc />
    public bool? IsVoiceSession(TenantId tenant, string sessionId) => _isVoiceSession(tenant, sessionId);

    /// <inheritdoc />
    public string? Write(TurnLogRecord record)
    {
        var path = _writer.Append(record);
        if (path is not null)
            FileLog.Write($"[TurnLogRecorder] recorded a turn end sid={record.Glance.SessionId} capture={record.Moment.CaptureMs}ms gaps={record.Gaps.Count}");
        return path;
    }
}
