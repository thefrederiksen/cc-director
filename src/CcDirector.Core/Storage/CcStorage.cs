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
///
/// NOTE: CcStorage methods intentionally omit FileLog.Write calls because
/// FileLog.LogDir is initialized from CcStorage.ToolLogs(), creating a
/// circular dependency at static initialization time.
/// </summary>
public static class CcStorage
{
    // -- Root categories --

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

    /// <summary>Generated files: PDFs, reports, transcripts, exports.</summary>
    public static string Output()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "cc-director");
    }

    /// <summary>All application and tool logs.</summary>
    public static string Logs() => Path.Combine(Base(), "logs");

    /// <summary>Installed executables (tool binaries).</summary>
    public static string Bin()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "cc-director", "bin");
    }

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

    /// <summary>Communication queue database: config/comm-queue/communications.db</summary>
    public static string CommQueueDb() => Path.Combine(ToolConfig("comm-queue"), "communications.db");

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

    // -- Utilities --

    /// <summary>Create directory if it doesn't exist and return the path.</summary>
    public static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
