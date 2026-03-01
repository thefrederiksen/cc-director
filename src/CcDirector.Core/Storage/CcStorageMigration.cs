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

        CopyFileIfNewer(Path.Combine(oldDir, "accounts.json"), Path.Combine(newDir, "accounts.json"));
        CopyFileIfNewer(Path.Combine(oldDir, "root-directories.json"), Path.Combine(newDir, "root-directories.json"));
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

        CopyFileIfNewer(Path.Combine(oldDir, "sessions.json"), Path.Combine(newDir, "sessions.json"));
        CopyFileIfNewer(Path.Combine(oldDir, "recent-sessions.json"), Path.Combine(newDir, "recent-sessions.json"));
        CopyFileIfNewer(Path.Combine(oldDir, "repositories.json"), Path.Combine(newDir, "repositories.json"));

        // Migrate sessions folder (individual history files)
        var oldSessions = Path.Combine(oldDir, "sessions");
        var newSessions = Path.Combine(newDir, "sessions");
        CopyDirectoryIfNewer(oldSessions, newSessions);
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

        CopyFileIfNewer(Path.Combine(oldDir, "vault.db"), Path.Combine(newDir, "vault.db"));
        CopyFileIfNewer(Path.Combine(oldDir, "engine.db"), Path.Combine(newDir, "engine.db"));
        CopyDirectoryIfNewer(Path.Combine(oldDir, "vectors"), Path.Combine(newDir, "vectors"));
        CopyDirectoryIfNewer(Path.Combine(oldDir, "documents"), Path.Combine(newDir, "documents"));
        CopyDirectoryIfNewer(Path.Combine(oldDir, "health"), Path.Combine(newDir, "health"));
        CopyDirectoryIfNewer(Path.Combine(oldDir, "media"), Path.Combine(newDir, "media"));
        CopyDirectoryIfNewer(Path.Combine(oldDir, "backups"), Path.Combine(newDir, "backups"));
    }

    /// <summary>
    /// Migrate %LOCALAPPDATA%\CcDirector\logs\ -> logs\director\
    /// </summary>
    private static void MigrateDirectorLogs()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var oldDir = Path.Combine(localAppData, "CcDirector", "logs");
        var newDir = CcStorage.ToolLogs("director");

        CopyDirectoryIfNewer(oldDir, newDir);
    }

    /// <summary>
    /// Migrate %LOCALAPPDATA%\cc-tools\data\comm_manager\content\ -> config\comm-queue\
    /// </summary>
    private static void MigrateCommQueue()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var oldDir = Path.Combine(localAppData, "cc-tools", "data", "comm_manager", "content");
        var newDir = CcStorage.ToolConfig("comm-queue");

        CopyDirectoryIfNewer(oldDir, newDir);
    }

    /// <summary>
    /// Copy file using "newer wins" strategy:
    /// - Copy if destination doesn't exist
    /// - Copy if source is newer (by last-write-time)
    /// - Copy if destination is suspiciously small (&lt;10 bytes) and source is larger
    /// </summary>
    private static void CopyFileIfNewer(string source, string destination)
    {
        if (!File.Exists(source))
            return;

        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(destination))
        {
            File.Copy(source, destination);
            FileLog.Write($"[CcStorageMigration] Copied (missing): {source} -> {destination}");
            return;
        }

        var srcInfo = new FileInfo(source);
        var dstInfo = new FileInfo(destination);

        // Destination is suspiciously small (empty/stale) and source has real data
        if (dstInfo.Length < 10 && srcInfo.Length >= 10)
        {
            File.Copy(source, destination, overwrite: true);
            FileLog.Write($"[CcStorageMigration] Copied (dest empty, src={srcInfo.Length}b): {source} -> {destination}");
            return;
        }

        // Source is newer
        if (srcInfo.LastWriteTimeUtc > dstInfo.LastWriteTimeUtc)
        {
            File.Copy(source, destination, overwrite: true);
            FileLog.Write($"[CcStorageMigration] Copied (newer): {source} -> {destination}");
        }
    }

    /// <summary>
    /// Copy directory recursively using "newer wins" strategy for each file.
    /// </summary>
    private static void CopyDirectoryIfNewer(string source, string destination)
    {
        if (!Directory.Exists(source))
            return;

        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            var destFile = Path.Combine(destination, Path.GetFileName(file));
            CopyFileIfNewer(file, destFile);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var destDir = Path.Combine(destination, Path.GetFileName(dir));
            CopyDirectoryIfNewer(dir, destDir);
        }
    }
}
