using System.IO.Compression;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Manages JSONL session history snapshots using zip archives.
/// Before each user prompt, the current JSONL file is copied into a zip archive
/// as a numbered entry. This allows rewinding to any previous turn by extracting
/// the corresponding entry and writing it as a new session file for --resume.
///
/// The JSONL content is treated as an opaque blob -- never parsed or modified.
/// </summary>
public sealed class SessionHistory
{
    private readonly string _archivePath;
    private readonly string _jsonlPath;
    private int _nextEntryNumber;
    private readonly object _lock = new();

    /// <summary>
    /// Directory where session history zip files are stored.
    /// </summary>
    public static string HistoryDir => CcStorage.Ensure(
        Path.Combine(CcStorage.Root(), "session-history"));

    /// <summary>
    /// Get the zip archive path for a given Claude session ID.
    /// </summary>
    public static string GetArchivePath(string claudeSessionId)
    {
        return Path.Combine(HistoryDir, $"{claudeSessionId}.zip");
    }

    /// <summary>
    /// Create a SessionHistory instance for the given session.
    /// </summary>
    /// <param name="claudeSessionId">The Claude session ID (matches the .jsonl filename).</param>
    /// <param name="repoPath">The repo path to locate the .jsonl file.</param>
    public SessionHistory(string claudeSessionId, string repoPath)
        : this(claudeSessionId, repoPath, ClaudeSessionReader.GetJsonlPath(claudeSessionId, repoPath))
    {
    }

    /// <summary>
    /// Create a SessionHistory instance with an explicit JSONL path (for testing).
    /// </summary>
    internal SessionHistory(string claudeSessionId, string repoPath, string jsonlPath)
    {
        FileLog.Write($"[SessionHistory] ctor: sessionId={claudeSessionId}, repo={repoPath}");
        _jsonlPath = jsonlPath;
        _archivePath = GetArchivePath(claudeSessionId);
        _nextEntryNumber = DetermineNextEntryNumber();
        FileLog.Write($"[SessionHistory] ctor: jsonl={_jsonlPath}, archive={_archivePath}, nextEntry={_nextEntryNumber}");
    }

    /// <summary>
    /// Number of snapshots currently stored in the archive.
    /// </summary>
    public int SnapshotCount
    {
        get
        {
            lock (_lock)
            {
                return _nextEntryNumber;
            }
        }
    }

    /// <summary>
    /// Snapshot the current JSONL file into the zip archive.
    /// Call this BEFORE sending a new prompt to Claude.
    /// Returns the entry number (0-based turn index), or -1 if the snapshot failed.
    /// </summary>
    public int TakeSnapshot()
    {
        lock (_lock)
        {
            FileLog.Write($"[SessionHistory] TakeSnapshot: entry={_nextEntryNumber}, jsonl={_jsonlPath}");

            if (!File.Exists(_jsonlPath))
            {
                FileLog.Write("[SessionHistory] TakeSnapshot: JSONL file not found, skipping");
                return -1;
            }

            try
            {
                var entryName = $"{_nextEntryNumber:D4}.jsonl";

                // Read the JSONL file content (with ReadWrite sharing since Claude may be writing)
                byte[] content;
                using (var fs = new FileStream(_jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    content = new byte[fs.Length];
                    fs.ReadExactly(content);
                }

                // Open or create the zip archive and add the entry
                using (var archive = ZipFile.Open(_archivePath, ZipArchiveMode.Update))
                {
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    entryStream.Write(content);
                }

                var entryNum = _nextEntryNumber;
                _nextEntryNumber++;

                var archiveSize = new FileInfo(_archivePath).Length;
                FileLog.Write($"[SessionHistory] TakeSnapshot: saved {entryName} ({content.Length} bytes), archive={archiveSize} bytes");
                return entryNum;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionHistory] TakeSnapshot FAILED: {ex.Message}");
                return -1;
            }
        }
    }

    /// <summary>
    /// Restore a snapshot to a new JSONL file for --resume.
    /// Creates a new file in Claude's projects folder with a new session ID.
    /// Returns the new session ID, or null if restore failed.
    /// </summary>
    /// <param name="entryNumber">The snapshot entry number to restore (0-based).</param>
    /// <param name="repoPath">The repo path (to determine the Claude projects folder).</param>
    public string? RestoreSnapshot(int entryNumber, string repoPath)
    {
        lock (_lock)
        {
            FileLog.Write($"[SessionHistory] RestoreSnapshot: entry={entryNumber}");

            if (!File.Exists(_archivePath))
            {
                FileLog.Write("[SessionHistory] RestoreSnapshot FAILED: archive not found");
                return null;
            }

            try
            {
                var entryName = $"{entryNumber:D4}.jsonl";
                var newSessionId = Guid.NewGuid().ToString();
                var projectFolder = ClaudeSessionReader.GetProjectFolderPath(repoPath);
                Directory.CreateDirectory(projectFolder);
                var newJsonlPath = Path.Combine(projectFolder, $"{newSessionId}.jsonl");

                using (var archive = ZipFile.OpenRead(_archivePath))
                {
                    var entry = archive.GetEntry(entryName);
                    if (entry == null)
                    {
                        FileLog.Write($"[SessionHistory] RestoreSnapshot FAILED: entry {entryName} not found in archive");
                        return null;
                    }

                    using var entryStream = entry.Open();
                    using var outFs = new FileStream(newJsonlPath, FileMode.Create, FileAccess.Write);
                    entryStream.CopyTo(outFs);
                }

                var fileSize = new FileInfo(newJsonlPath).Length;
                FileLog.Write($"[SessionHistory] RestoreSnapshot: restored {entryName} -> {newSessionId} ({fileSize} bytes)");
                return newSessionId;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionHistory] RestoreSnapshot FAILED: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Get metadata about all snapshots in the archive.
    /// </summary>
    public List<SnapshotInfo> GetSnapshots()
    {
        lock (_lock)
        {
            var result = new List<SnapshotInfo>();

            if (!File.Exists(_archivePath))
                return result;

            try
            {
                using var archive = ZipFile.OpenRead(_archivePath);
                foreach (var entry in archive.Entries.OrderBy(e => e.Name))
                {
                    if (!entry.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var numStr = Path.GetFileNameWithoutExtension(entry.Name);
                    if (!int.TryParse(numStr, out var entryNum))
                        continue;

                    result.Add(new SnapshotInfo
                    {
                        EntryNumber = entryNum,
                        CompressedSize = entry.CompressedLength,
                        OriginalSize = entry.Length,
                        Timestamp = entry.LastWriteTime.DateTime
                    });
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionHistory] GetSnapshots FAILED: {ex.Message}");
            }

            return result;
        }
    }

    /// <summary>
    /// Delete the archive file. Used when a session is permanently removed.
    /// </summary>
    public void DeleteArchive()
    {
        lock (_lock)
        {
            FileLog.Write($"[SessionHistory] DeleteArchive: {_archivePath}");
            try
            {
                if (File.Exists(_archivePath))
                    File.Delete(_archivePath);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionHistory] DeleteArchive FAILED: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Clean up old session history archives.
    /// Deletes archives older than the specified retention period and archives
    /// whose total size exceeds the max size limit (oldest first).
    /// Call this periodically (e.g., on app startup).
    /// </summary>
    /// <param name="maxAgeDays">Delete archives not modified in this many days. Default 30.</param>
    /// <param name="maxTotalSizeMb">Maximum total size of all archives in MB. Default 500.</param>
    public static void CleanupOldArchives(int maxAgeDays = 30, int maxTotalSizeMb = 500)
    {
        FileLog.Write($"[SessionHistory] CleanupOldArchives: maxAge={maxAgeDays}d, maxSize={maxTotalSizeMb}MB");

        var historyDir = Path.Combine(CcStorage.Root(), "session-history");
        if (!Directory.Exists(historyDir))
            return;

        try
        {
            var files = Directory.GetFiles(historyDir, "*.zip")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
            int deletedAge = 0;
            int deletedSize = 0;

            // Phase 1: Delete archives older than retention period
            for (int i = files.Count - 1; i >= 0; i--)
            {
                if (files[i].LastWriteTimeUtc < cutoff)
                {
                    FileLog.Write($"[SessionHistory] Cleanup: deleting expired {files[i].Name} (age={DateTime.UtcNow - files[i].LastWriteTimeUtc:d\\.hh})");
                    files[i].Delete();
                    files.RemoveAt(i);
                    deletedAge++;
                }
            }

            // Phase 2: Enforce total size limit (delete oldest first)
            long maxBytes = (long)maxTotalSizeMb * 1024 * 1024;
            long totalSize = files.Sum(f => f.Length);

            while (totalSize > maxBytes && files.Count > 0)
            {
                var oldest = files[0];
                FileLog.Write($"[SessionHistory] Cleanup: deleting for size {oldest.Name} ({oldest.Length / 1024}KB)");
                totalSize -= oldest.Length;
                oldest.Delete();
                files.RemoveAt(0);
                deletedSize++;
            }

            FileLog.Write($"[SessionHistory] CleanupOldArchives: deleted {deletedAge} expired + {deletedSize} for size, remaining={files.Count} archives ({totalSize / 1024 / 1024}MB)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionHistory] CleanupOldArchives FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Determine the next entry number by reading existing entries in the archive.
    /// </summary>
    private int DetermineNextEntryNumber()
    {
        if (!File.Exists(_archivePath))
            return 0;

        try
        {
            using var archive = ZipFile.OpenRead(_archivePath);
            int max = -1;
            foreach (var entry in archive.Entries)
            {
                var numStr = Path.GetFileNameWithoutExtension(entry.Name);
                if (int.TryParse(numStr, out var num) && num > max)
                    max = num;
            }
            return max + 1;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionHistory] DetermineNextEntryNumber FAILED: {ex.Message}");
            return 0;
        }
    }
}

/// <summary>
/// Metadata about a single snapshot in the history archive.
/// </summary>
public sealed class SnapshotInfo
{
    /// <summary>0-based entry number (turn index).</summary>
    public int EntryNumber { get; init; }

    /// <summary>Compressed size in the zip archive.</summary>
    public long CompressedSize { get; init; }

    /// <summary>Original uncompressed JSONL size.</summary>
    public long OriginalSize { get; init; }

    /// <summary>When the snapshot was taken.</summary>
    public DateTime Timestamp { get; init; }
}
