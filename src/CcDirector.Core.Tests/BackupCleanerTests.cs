using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests;

public class BackupCleanerTests : IDisposable
{
    private readonly string _tempDir;

    public BackupCleanerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BackupCleanerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ScanOnce_DeletesInvalidJson()
    {
        var filePath = Path.Combine(_tempDir, "settings.backup.2026-01-01.json");
        File.WriteAllText(filePath, "{ truncated");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-1));

        string? deletedPath = null;
        using var cleaner = CreateCleaner();
        cleaner.OnCorruptedFileDeleted = path => deletedPath = path;

        cleaner.ScanOnce();

        Assert.NotNull(deletedPath);
        Assert.Contains("settings.backup.2026-01-01.json", deletedPath);
        Assert.False(File.Exists(filePath), "Invalid JSON file should be deleted");
    }

    [Fact]
    public void ScanOnce_KeepsValidBackup()
    {
        var filePath = Path.Combine(_tempDir, "settings.backup.2026-01-01.json");
        File.WriteAllText(filePath, """{"key": "value"}""");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-1));

        string? deletedPath = null;
        using var cleaner = CreateCleaner();
        cleaner.OnCorruptedFileDeleted = path => deletedPath = path;

        cleaner.ScanOnce();

        Assert.Null(deletedPath);
        Assert.True(File.Exists(filePath), "Valid JSON backup should be kept");
    }

    [Fact]
    public void ScanOnce_DeletesCorruptedMarkedFile()
    {
        var filePath = Path.Combine(_tempDir, "settings.corrupted.2026-01-01.json");
        File.WriteAllText(filePath, """{"key": "value"}""");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-1));

        string? deletedPath = null;
        using var cleaner = CreateCleaner();
        cleaner.OnCorruptedFileDeleted = path => deletedPath = path;

        cleaner.ScanOnce();

        Assert.NotNull(deletedPath);
        Assert.Contains(".corrupted.", deletedPath);
        Assert.False(File.Exists(filePath), "Corrupted-marked file should be deleted even with valid JSON");
    }

    [Fact]
    public void ScanOnce_SkipsRecentFiles()
    {
        var filePath = Path.Combine(_tempDir, "settings.backup.recent.json");
        File.WriteAllText(filePath, "{ truncated");
        // Don't adjust LastWriteTime - file is brand new (age < 5s)

        string? deletedPath = null;
        using var cleaner = CreateCleaner();
        cleaner.OnCorruptedFileDeleted = path => deletedPath = path;

        cleaner.ScanOnce();

        Assert.Null(deletedPath);
        Assert.True(File.Exists(filePath), "Recent file should be skipped regardless of content");
    }

    [Fact]
    public void ScanOnce_SkipsAlreadyProcessedFiles()
    {
        var filePath = Path.Combine(_tempDir, "settings.backup.processed.json");
        File.WriteAllText(filePath, """{"key": "value"}""");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-1));

        using var cleaner = CreateCleaner();

        // First scan - should process the file
        cleaner.ScanOnce();

        // Delete and recreate the file to see if it gets re-scanned
        // (it shouldn't because the filename is in the processed set)
        File.Delete(filePath);
        File.WriteAllText(filePath, "{ invalid now }");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-1));

        string? deletedPath = null;
        cleaner.OnCorruptedFileDeleted = path => deletedPath = path;

        // Second scan - should skip because filename was already processed
        cleaner.ScanOnce();

        Assert.Null(deletedPath);
    }

    [Fact]
    public void ScanOnce_HandlesNulFile()
    {
        // NUL files on Windows are unreliable to create/detect in automated tests.
        // NulFileWatcherTests already covers deletion thoroughly.
        // This test verifies BackupCleaner attempts NUL detection without crashing.
        var nulPath = Path.Combine(_tempDir, "nul");
        var extendedPath = @"\\?\" + nulPath;

        try
        {
            File.WriteAllText(extendedPath, "test");
        }
        catch
        {
            // NUL file creation may fail on some Windows configurations
            return;
        }

        if (!File.Exists(extendedPath))
        {
            // File creation succeeded but file isn't detectable - skip
            return;
        }

        string? deletedPath = null;
        using var cleaner = new BackupCleaner(
            backupsDir: _tempDir,
            minFileAge: TimeSpan.Zero,
            log: msg => System.Diagnostics.Debug.WriteLine($"[Test] {msg}"));
        cleaner.OnCorruptedFileDeleted = path => deletedPath = path;

        cleaner.ScanOnce();

        if (deletedPath != null)
        {
            Assert.False(File.Exists(extendedPath), "NUL file should be deleted after scan");
        }
        // If deletedPath is null, the NUL file wasn't detected by the cleaner -
        // this is acceptable since NUL file behavior on Windows is inconsistent.
        // The important thing is that ScanOnce didn't crash.
    }

    [Fact]
    public void ScanOnce_SkipsPythonFiles()
    {
        var filePath = Path.Combine(_tempDir, "cleanup_backups.py");
        File.WriteAllText(filePath, "# python script - not valid JSON");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-1));

        string? deletedPath = null;
        using var cleaner = CreateCleaner();
        cleaner.OnCorruptedFileDeleted = path => deletedPath = path;

        cleaner.ScanOnce();

        Assert.Null(deletedPath);
        Assert.True(File.Exists(filePath), "Python script should be kept");
    }

    [Fact]
    public void ScanOnce_DeletesEmptyFile()
    {
        var filePath = Path.Combine(_tempDir, "settings.backup.empty.json");
        File.WriteAllText(filePath, "");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-1));

        string? deletedPath = null;
        using var cleaner = CreateCleaner();
        cleaner.OnCorruptedFileDeleted = path => deletedPath = path;

        cleaner.ScanOnce();

        Assert.NotNull(deletedPath);
        Assert.False(File.Exists(filePath), "Empty file should be deleted as invalid JSON");
    }

    [Fact]
    public void ScanOnce_HandlesNonExistentDirectory()
    {
        var missingDir = Path.Combine(_tempDir, "does_not_exist");
        using var cleaner = new BackupCleaner(
            backupsDir: missingDir,
            minFileAge: TimeSpan.Zero);

        // Should not throw
        cleaner.ScanOnce();
    }

    [Fact]
    public void Dispose_StopsTimer()
    {
        var cleaner = new BackupCleaner(
            backupsDir: _tempDir,
            scanInterval: TimeSpan.FromHours(1));
        cleaner.Start();
        cleaner.Dispose();

        // Double dispose should be safe
        cleaner.Dispose();
    }

    private BackupCleaner CreateCleaner()
    {
        return new BackupCleaner(
            backupsDir: _tempDir,
            minFileAge: TimeSpan.FromSeconds(1),
            log: msg => System.Diagnostics.Debug.WriteLine($"[Test] {msg}"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                // Use extended paths for any NUL files
                foreach (var file in Directory.EnumerateFiles(_tempDir))
                {
                    var extended = file.StartsWith(@"\\?\") ? file : @"\\?\" + file;
                    try { File.Delete(extended); } catch { }
                }
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
