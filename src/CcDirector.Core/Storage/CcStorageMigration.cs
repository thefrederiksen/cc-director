using CcDirector.Core.Utilities;

namespace CcDirector.Core.Storage;

/// <summary>
/// Migrates data from legacy storage locations to the unified cc-director structure.
/// Called on first launch of C# apps. Non-destructive: copies data, does not delete old locations.
/// </summary>
public static class CcStorageMigration
{
    private static int _migrated;

    /// <summary>
    /// Run migration if not already done. Safe to call multiple times.
    /// </summary>
    public static void EnsureMigrated()
    {
        if (Interlocked.CompareExchange(ref _migrated, 1, 0) != 0)
            return;

        FileLog.Write("[CcStorageMigration] EnsureMigrated: checking for legacy data");

        MigrateDirectorConfig();
        MigrateDirectorDocuments();
        MigrateVaultData();
        MigrateDirectorLogs();
        MigrateCommQueue();
        FileLog.Write("[CcStorageMigration] EnsureMigrated: migration check complete");
    }

    /// <summary>
    /// Migrate %LOCALAPPDATA%\CcDirector\ -> config\director\
    /// Files: accounts.json, root-directories.json
    /// </summary>
    private static void MigrateDirectorConfig()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var oldDir = Path.Combine(localAppData, "CcDirector");
        var newDir = CcStorage.ToolConfig("director");

        if (!Directory.Exists(oldDir))
            return;

        CopyFileIfMissing(Path.Combine(oldDir, "accounts.json"), Path.Combine(newDir, "accounts.json"));
        CopyFileIfMissing(Path.Combine(oldDir, "root-directories.json"), Path.Combine(newDir, "root-directories.json"));
    }

    /// <summary>
    /// Migrate %USERPROFILE%\Documents\CcDirector\ -> config\director\
    /// Files: sessions.json, recent-sessions.json, repositories.json
    /// Folders: sessions\
    /// </summary>
    private static void MigrateDirectorDocuments()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var oldDir = Path.Combine(docs, "CcDirector");
        var newDir = CcStorage.ToolConfig("director");

        if (!Directory.Exists(oldDir))
            return;

        CopyFileIfMissing(Path.Combine(oldDir, "sessions.json"), Path.Combine(newDir, "sessions.json"));
        CopyFileIfMissing(Path.Combine(oldDir, "recent-sessions.json"), Path.Combine(newDir, "recent-sessions.json"));
        CopyFileIfMissing(Path.Combine(oldDir, "repositories.json"), Path.Combine(newDir, "repositories.json"));

        // Migrate sessions folder (individual history files)
        var oldSessions = Path.Combine(oldDir, "sessions");
        var newSessions = Path.Combine(newDir, "sessions");
        CopyDirectoryIfMissing(oldSessions, newSessions);
    }

    /// <summary>
    /// Migrate %LOCALAPPDATA%\cc-myvault\ -> vault\
    /// Files: vault.db, engine.db
    /// Folders: vectors\, documents\, health\, media\, backups\
    /// </summary>
    private static void MigrateVaultData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var oldDir = Path.Combine(localAppData, "cc-myvault");
        var newDir = CcStorage.Vault();

        if (!Directory.Exists(oldDir))
            return;

        CopyFileIfMissing(Path.Combine(oldDir, "vault.db"), Path.Combine(newDir, "vault.db"));
        CopyFileIfMissing(Path.Combine(oldDir, "engine.db"), Path.Combine(newDir, "engine.db"));
        CopyDirectoryIfMissing(Path.Combine(oldDir, "vectors"), Path.Combine(newDir, "vectors"));
        CopyDirectoryIfMissing(Path.Combine(oldDir, "documents"), Path.Combine(newDir, "documents"));
        CopyDirectoryIfMissing(Path.Combine(oldDir, "health"), Path.Combine(newDir, "health"));
        CopyDirectoryIfMissing(Path.Combine(oldDir, "media"), Path.Combine(newDir, "media"));
        CopyDirectoryIfMissing(Path.Combine(oldDir, "backups"), Path.Combine(newDir, "backups"));
    }

    /// <summary>
    /// Migrate %LOCALAPPDATA%\CcDirector\logs\ -> logs\director\
    /// </summary>
    private static void MigrateDirectorLogs()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var oldDir = Path.Combine(localAppData, "CcDirector", "logs");
        var newDir = CcStorage.ToolLogs("director");

        CopyDirectoryIfMissing(oldDir, newDir);
    }

    /// <summary>
    /// Migrate %LOCALAPPDATA%\cc-tools\data\comm_manager\content\ -> config\comm-queue\
    /// </summary>
    private static void MigrateCommQueue()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var oldDir = Path.Combine(localAppData, "cc-tools", "data", "comm_manager", "content");
        var newDir = CcStorage.ToolConfig("comm-queue");

        CopyDirectoryIfMissing(oldDir, newDir);
    }

    private static void CopyFileIfMissing(string source, string destination)
    {
        if (!File.Exists(source))
            return;

        if (File.Exists(destination))
            return;

        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.Copy(source, destination);
        FileLog.Write($"[CcStorageMigration] Copied: {source} -> {destination}");
    }

    private static void CopyDirectoryIfMissing(string source, string destination)
    {
        if (!Directory.Exists(source))
            return;

        if (Directory.Exists(destination) && Directory.GetFileSystemEntries(destination).Length > 0)
            return;

        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            var destFile = Path.Combine(destination, Path.GetFileName(file));
            if (!File.Exists(destFile))
                File.Copy(file, destFile);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var destDir = Path.Combine(destination, Path.GetFileName(dir));
            CopyDirectoryIfMissing(dir, destDir);
        }

        FileLog.Write($"[CcStorageMigration] Copied directory: {source} -> {destination}");
    }
}
