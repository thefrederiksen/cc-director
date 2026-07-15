namespace CcDirector.Core.Storage;

/// <summary>
/// Single source of truth for all cc-director storage paths.
/// Mirrors the Python cc_storage.CcStorage API.
///
/// Storage categories:
///   Vault  - Personal data: contacts, docs, tasks, goals, health, vectors
///   Config - Tool settings, OAuth tokens, credentials, app state
///   Output - Generated files: PDFs, reports, transcripts, exports
///   Logs   - All application and tool logs
///   Bin    - Installed executables (tool binaries)
///
/// Environment variable overrides:
///   CC_DIRECTOR_ROOT - Override the base directory (default: %LOCALAPPDATA%\cc-director)
///   CC_VAULT_PATH    - Override the vault directory specifically
///   CC_DIRECTOR_INSTANCES_DIR - Override the Director instance-discovery directory specifically
///
/// NOTE: CcStorage methods intentionally omit FileLog.Write calls because
/// FileLog.LogDir is initialized from CcStorage.ToolLogs(), creating a
/// circular dependency at static initialization time.
/// </summary>
public static class CcStorage
{
    // -- Root categories --

    /// <summary>Root directory for all cc-director storage.</summary>
    public static string Root() => Base();

    private static string Base()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        if (!string.IsNullOrEmpty(overrideRoot))
            return overrideRoot;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "cc-director");
    }

    /// <summary>Personal data: vault.db, vectors, documents, health, media.</summary>
    public static string Vault()
    {
        var overridePath = Environment.GetEnvironmentVariable("CC_VAULT_PATH");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;

        return Path.Combine(Base(), "vault");
    }

    /// <summary>Tool settings, OAuth tokens, credentials, app state.</summary>
    public static string Config() => Path.Combine(Base(), "config");

    /// <summary>
    /// Director instance-discovery directory: config/director/instances/. Each running Director writes
    /// a <c>{directorId}.json</c> here and the Gateway watches it. Honors the
    /// <c>CC_DIRECTOR_INSTANCES_DIR</c> override (issue #322) so tests can pin JUST this directory to a
    /// throwaway location - keeping a stray test Director's instance file out of the real directory,
    /// where the live Gateway's file watcher would otherwise discover it, probe its dead ephemeral port,
    /// and paint a phantom "unreachable" Director - without redirecting the whole storage root.
    /// </summary>
    public static string DirectorInstances()
    {
        var overridePath = Environment.GetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;

        return Path.Combine(Config(), "director", "instances");
    }

    /// <summary>
    /// Generated files: PDFs, reports, transcripts, exports. Lives in the user's Documents folder by
    /// design - these are files the user opens - but honors CC_DIRECTOR_ROOT when set so a test that
    /// pins the root cannot write into the real Documents\cc-director. The product never sets the root,
    /// so its location is unchanged. Same fix already applied to <see cref="Bin"/> and
    /// <see cref="Screenshots"/>; this was the last path here that ignored the override.
    /// </summary>
    public static string Output()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        if (!string.IsNullOrEmpty(overrideRoot))
            return Path.Combine(overrideRoot, "output");

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "cc-director");
    }

    /// <summary>All application and tool logs.</summary>
    public static string Logs() => Path.Combine(Base(), "logs");

    /// <summary>
    /// Durable activity-state transition logs: base/state-changes/&lt;sessionId&gt;.jsonl. The caller creates
    /// the directory, so this composes the path without touching disk. Resolved here rather than in
    /// <see cref="Wingman.StateChangeLog"/> so it honors CC_DIRECTOR_ROOT like every other path: that class
    /// baked %LOCALAPPDATA% into a static readonly field, which no test could redirect - so its test wrote
    /// into the real running Director's data directory.
    /// </summary>
    public static string StateChanges() => Path.Combine(Base(), "state-changes");

    /// <summary>
    /// Per-utterance resumable voice-chunk staging: base/voice-utterances/&lt;uploadId&gt;/. Transient - the
    /// service deletes each utterance directory after transcription, and creates them itself, so this
    /// composes the path without touching disk. Resolved here for the same reason as
    /// <see cref="StateChanges"/>: <see cref="Voice.VoiceUtteranceService"/> baked %LOCALAPPDATA% into a
    /// static readonly field that no test could redirect.
    /// </summary>
    public static string VoiceUtterances() => Path.Combine(Base(), "voice-utterances");

    /// <summary>
    /// Per-turn voice comparison logs (short retention, auto-purged): base/voice-turn-logs/.
    /// Holds the audio, user transcript, agent reply, and wingman spoken reply for
    /// each voice turn so a meaning divergence can be flagged and compared later.
    /// </summary>
    public static string VoiceTurnLogs() => Ensure(Path.Combine(Base(), "voice-turn-logs"));

    /// <summary>
    /// Per-turn review logs (7-day retention, auto-purged): base/turn-review/&lt;date&gt;/&lt;sessionId&gt;/.
    /// One record per turn-end (Working -&gt; needs-you), holding the terminal screen + transcript
    /// for that turn plus whatever the Wingman said/did, so any turn can be reviewed later.
    /// </summary>
    public static string TurnReviewLogs() => Ensure(Path.Combine(Base(), "turn-review"));

    /// <summary>
    /// Durable voice-turn archive (issue: guaranteed audio-turn): base/voice-turn-archive/&lt;turnId&gt;/.
    /// Each completed async voice turn writes its result here - meta.json (session id, upload id,
    /// stage, summary) plus reply.mp3 - so the reply "sits in the session" and is retrievable long
    /// after the in-memory job cache TTL and across a Gateway restart. Owned by the Gateway.
    /// </summary>
    public static string VoiceTurnArchive() => Ensure(Path.Combine(Base(), "voice-turn-archive"));

    /// <summary>
    /// Resumable upload staging for the Gateway voice-turn front door: base/voice-turn-uploads/&lt;uploadId&gt;/.
    /// Each chunk lands here as it arrives (SHA-checked, idempotent) and the dir is deleted once the
    /// chunks are assembled and the turn has been started. Owned by the Gateway.
    /// </summary>
    public static string VoiceTurnUploads() => Ensure(Path.Combine(Base(), "voice-turn-uploads"));

    /// <summary>
    /// Resumable upload staging for durable dictation (issue #1006): base/dictation-uploads/&lt;uploadId&gt;/.
    /// The mobile app persists the raw audio locally the instant Send is pressed and streams it here in
    /// SHA-checked chunks; once assembled the Gateway transcribes it and injects the turn into the owning
    /// session itself, so a dead tab or a dropped connection cannot lose a recorded utterance. Abandoned
    /// staging dirs are swept after ~1 hour. Owned by the Gateway.
    /// </summary>
    public static string DictationUploads() => Ensure(Path.Combine(Base(), "dictation-uploads"));

    /// <summary>
    /// Wingman training data (issue #531 follow-up): base/wingman-training/. When the
    /// "wingman_training_capture" setting is on, every wingman summary appends one JSON-lines record
    /// here holding up to 20,000 characters of the session terminal, the agent reply + context the
    /// wingman saw, and the wingman's spoken response - a labeled dataset for testing and improving
    /// the wingman. Owned by the Gateway.
    /// </summary>
    public static string WingmanTrainingData() => Ensure(Path.Combine(Base(), "wingman-training"));

    /// <summary>
    /// "This brief is wrong" reports (TURN_BRIEFING.md D7): base/brief-feedback/. Each report
    /// stores the brief + the user's note as a labeled example that drives wingman prompt
    /// iteration. Written by the GATEWAY's feedback endpoint since issue #187. (The old
    /// Director-side turn-briefs ring at base/turn-briefs/ is dead data, left on disk.)
    /// </summary>
    public static string BriefFeedback() => Ensure(Path.Combine(Base(), "brief-feedback"));

    /// <summary>Installed executables (tool binaries). Honors the CC_DIRECTOR_ROOT override via
    /// <see cref="Base"/> like every other path here, so an isolated root redirects the tool bin too
    /// (previously this hardcoded %LOCALAPPDATA%\cc-director\bin and silently ignored the override).</summary>
    public static string Bin() => Path.Combine(Base(), "bin");

    // -- Tool-specific shortcuts --

    /// <summary>Config directory for a specific tool: config/{tool}/</summary>
    public static string ToolConfig(string tool) => Path.Combine(Config(), tool);

    /// <summary>Output directory for a specific tool: output/{tool}/</summary>
    public static string ToolOutput(string tool) => Path.Combine(Output(), tool);

    /// <summary>Log directory for a specific tool: logs/{tool}/</summary>
    public static string ToolLogs(string tool) => Path.Combine(Logs(), tool);

    // -- Vault subdirectories --

    /// <summary>Main personal data database: vault/vault.db</summary>
    public static string VaultDb() => Path.Combine(Vault(), "vault.db");

    /// <summary>Job scheduler state database: vault/engine.db</summary>
    public static string EngineDb() => Path.Combine(Vault(), "engine.db");

    /// <summary>Quick Actions chat database: vault/quick_actions.db</summary>
    public static string QuickActionsDb() => Path.Combine(Vault(), "quick_actions.db");

    /// <summary>Imported files: vault/documents/</summary>
    public static string VaultDocuments() => Path.Combine(Vault(), "documents");

    /// <summary>Embeddings: vault/vectors/</summary>
    public static string VaultVectors() => Path.Combine(Vault(), "vectors");

    /// <summary>Media files: vault/media/</summary>
    public static string VaultMedia() => Path.Combine(Vault(), "media");

    /// <summary>Health data: vault/health/</summary>
    public static string VaultHealth() => Path.Combine(Vault(), "health");

    /// <summary>Backup files: vault/backups/</summary>
    public static string VaultBackups() => Path.Combine(Vault(), "backups");

    /// <summary>Session handover documents: vault/handovers/</summary>
    public static string VaultHandovers() => Ensure(Path.Combine(Vault(), "handovers"));

    // -- Config shortcuts --

    /// <summary>Shared settings file: config/config.json</summary>
    public static string ConfigJson() => Path.Combine(Config(), "config.json");

    /// <summary>
    /// User's screenshots directory, where phone-uploaded images are filed so the
    /// owning session can read them by absolute path. Resolution order:
    ///   1. config.json -> screenshots.source_directory (honored when explicitly set).
    ///   2. Platform default:
    ///      - macOS: the Desktop (where macOS drops screenshots by default).
    ///      - Windows/Linux: the "Pictures" known folder + \Screenshots. GetFolderPath
    ///        follows a OneDrive redirect, so on a machine with Pictures backed up to
    ///        OneDrive this yields e.g. D:\...\OneDrive\Pictures\Screenshots.
    /// The directory is created if it does not exist. On a Mac neither default may match
    /// where the user actually keeps screenshots, so the explicit config override (set via
    /// the Settings page) is the reliable path - see CcDirectorConfigService.
    /// </summary>
    public static string Screenshots()
    {
        var configured = TryReadConfigString("screenshots", "source_directory");
        if (!string.IsNullOrWhiteSpace(configured))
            return Ensure(configured);

        // No configured folder. When CC_DIRECTOR_ROOT is set the caller has pinned storage to a throwaway
        // location (only tests do this - the product never sets it), so keep the screenshots folder inside
        // that root rather than falling through to the user's REAL Pictures\Screenshots. Without this a test
        // that redirects the root still wrote into the user's own screenshots folder, where the gallery listed
        // the leftovers as undrawable date-only entries; the leak shipped because setting the root LOOKS like
        // it sandboxes everything. Mirrors CC_DIRECTOR_INSTANCES_DIR (issue #322) in keeping test artefacts out
        // of the user's real environment.
        var overrideRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        if (!string.IsNullOrEmpty(overrideRoot))
            return Ensure(Path.Combine(overrideRoot, "screenshots"));

        if (OperatingSystem.IsMacOS())
            return Ensure(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Ensure(Path.Combine(pictures, "Screenshots"));
    }

    /// <summary>
    /// Read a nested string value from config.json (config[section][key]). Returns null
    /// when the file, section, or key is absent. Used for optional path overrides.
    /// </summary>
    private static string? TryReadConfigString(string section, string key)
    {
        var path = ConfigJson();
        if (!File.Exists(path)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty(section, out var sectionEl)) return null;
        if (!sectionEl.TryGetProperty(key, out var valueEl)) return null;
        return valueEl.ValueKind == System.Text.Json.JsonValueKind.String ? valueEl.GetString() : null;
    }

    /// <summary>Communication queue database: config/comm-queue/communications.db</summary>
    public static string CommQueueDb() => Path.Combine(ToolConfig("comm-queue"), "communications.db");

    /// <summary>
    /// Encrypted DevThrottle account credential blob: config/director/devthrottle-credential.bin.
    /// The access-plus-refresh token pair is written here encrypted at rest by the operating system
    /// credential store (Windows Data Protection on Windows), never as plain text. Distinct from the
    /// Claude sign-in account store (accounts.json) - this is the DevThrottle account.
    /// </summary>
    public static string DevThrottleCredentialBlob() =>
        Path.Combine(ToolConfig("director"), "devthrottle-credential.bin");

    /// <summary>
    /// DevThrottle authentication-floor event log: config/director/devthrottle-auth-events.jsonl.
    /// Append-only JSON-lines record of "logged-in" / "logout" events. Records only the event kind
    /// and timestamp - never the token - so it can never leak a credential.
    /// </summary>
    public static string DevThrottleAuthEventsLog() =>
        Path.Combine(ToolConfig("director"), "devthrottle-auth-events.jsonl");

    /// <summary>
    /// DevThrottle richer usage-telemetry sink: config/director/devthrottle-usage-events.jsonl.
    /// Append-only JSON-lines record of the user-controllable usage events (issue #582), written only
    /// while the usage-telemetry toggle is on. Distinct from the always-on authentication-floor log
    /// (<see cref="DevThrottleAuthEventsLog"/>): when the toggle is off, nothing is written here while
    /// the authentication events still flow. Records only an event name and timestamp - never the
    /// token or the user's work.
    /// </summary>
    public static string DevThrottleUsageEventsLog() =>
        Path.Combine(ToolConfig("director"), "devthrottle-usage-events.jsonl");

    /// <summary>
    /// The Director's last-known cache of the Gateway's fleet-wide telemetry-consent setting:
    /// config/director/telemetry-consent-cache.json (issue #649). The Director reads the authoritative
    /// value from the Gateway and caches it here; when the Gateway is unreachable it falls back to this
    /// last-known value (degraded, decision #3) rather than guessing. Holds only the boolean consent and
    /// the time it was cached - no token, no user data.
    /// </summary>
    public static string TelemetryConsentCache() =>
        Path.Combine(ToolConfig("director"), "telemetry-consent-cache.json");

    /// <summary>
    /// Encrypted DevThrottle account credential blob on the Gateway: config/gateway/devthrottle-credential.bin.
    /// The Gateway-Centralization Phase 2 foundation (issue #636): the access-plus-refresh token pair is
    /// written here encrypted at rest by the operating system credential store (Windows Data Protection on
    /// Windows), never as plain text. Distinct from the per-Director credential blob
    /// (<see cref="DevThrottleCredentialBlob"/>) under config/director - the account moves onto the Gateway
    /// so each Director no longer holds its own copy.
    /// </summary>
    public static string GatewayDevThrottleCredentialBlob() =>
        Path.Combine(ToolConfig("gateway"), "devthrottle-credential.bin");

    /// <summary>
    /// DevThrottle authentication-floor event log on the Gateway: config/gateway/devthrottle-auth-events.jsonl.
    /// Append-only JSON-lines record of "logged-in" / "logout" events for the Gateway-hosted account
    /// (issue #636). Records only the event kind and timestamp - never the token - so it can never leak a
    /// credential. Distinct from the per-Director authentication log
    /// (<see cref="DevThrottleAuthEventsLog"/>) under config/director.
    /// </summary>
    public static string GatewayDevThrottleAuthEventsLog() =>
        Path.Combine(ToolConfig("gateway"), "devthrottle-auth-events.jsonl");

    // -- Life Operating System coaching directories --

    /// <summary>Life OS coaching root: vault/life/</summary>
    public static string VaultLife() => Path.Combine(Vault(), "life");

    /// <summary>
    /// Life OS coaching category directory: vault/life/{category}/
    /// Valid categories: assistant, health, business, personal, growth.
    /// Creates the directory if it doesn't exist.
    /// </summary>
    public static string CoachingCategory(string category)
    {
        return Ensure(Path.Combine(VaultLife(), category));
    }

    // -- Workspaces --

    /// <summary>Workspace definitions directory: config/director/workspaces/</summary>
    public static string Workspaces() => Path.Combine(ToolConfig("director"), "workspaces");

    /// <summary>Named-session definitions directory: config/director/named-sessions/</summary>
    public static string NamedSessions() => Path.Combine(ToolConfig("director"), "named-sessions");

    // -- Browser Connections --

    /// <summary>Browser connections directory: base/connections/</summary>
    public static string Connections() => Path.Combine(Base(), "connections");

    /// <summary>Connection registry file: connections/connections.json</summary>
    public static string ConnectionsRegistry() => Path.Combine(Connections(), "connections.json");

    /// <summary>Chrome profile directory for a specific connection: connections/{name}/</summary>
    public static string ConnectionProfile(string name) => Path.Combine(Connections(), name);

    /// <summary>Workflow storage for a connection: connections/{name}/workflows/</summary>
    public static string ConnectionWorkflows(string name) =>
        Ensure(Path.Combine(ConnectionProfile(name), "workflows"));

    /// <summary>Workflow data directory: connections/{name}/workflows/{workflow}/</summary>
    public static string ConnectionWorkflowDir(string connectionName, string workflowName) =>
        Ensure(Path.Combine(ConnectionWorkflows(connectionName), SafeFileName(workflowName)));

    /// <summary>Workflow runs directory: connections/{name}/workflows/{workflow}/runs/</summary>
    public static string ConnectionWorkflowRuns(string connectionName, string workflowName) =>
        Ensure(Path.Combine(ConnectionWorkflowDir(connectionName, workflowName), "runs"));

    /// <summary>Workflow run screenshot directory: .../{workflow}/runs/{runId}/</summary>
    public static string ConnectionWorkflowRunDir(string connectionName, string workflowName, string runId) =>
        Ensure(Path.Combine(ConnectionWorkflowRuns(connectionName, workflowName), runId));

    // -- Utilities --

    /// <summary>Create directory if it doesn't exist and return the path.</summary>
    public static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Sanitize a name for use as a file/directory name.</summary>
    internal static string SafeFileName(string name)
    {
        return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
    }
}
