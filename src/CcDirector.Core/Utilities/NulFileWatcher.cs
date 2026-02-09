namespace CcDirector.Core.Utilities;

/// <summary>
/// Monitors a drive for files named "NUL" and deletes them.
/// Windows reserves the NUL device name, but actual files can be created via \\?\ prefix paths.
/// These files are hard to delete normally and clutter the filesystem.
/// </summary>
public sealed class NulFileWatcher : IDisposable
{
    private readonly string _drivePath;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _watcher;
    private Task? _scanTask;
    private bool _disposed;

    /// <summary>Raised when a NUL file is successfully deleted.</summary>
    public Action<string>? OnNulFileDeleted;

    /// <summary>Raised when deletion of a NUL file fails.</summary>
    public Action<string, Exception>? OnDeletionFailed;

    public NulFileWatcher(string? drivePath = null, Action<string>? log = null)
    {
        _drivePath = drivePath ?? Path.GetPathRoot(AppContext.BaseDirectory)!;
        _log = log;
    }

    public void Start()
    {
        _watcher = new FileSystemWatcher(_drivePath)
        {
            Filter = "NUL",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnFileCreated;

        _scanTask = ScanDriveAsync(_cts.Token);
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        _log?.Invoke($"NUL file detected by watcher: {e.FullPath}");
        TryDeleteAndRaiseEvents(e.FullPath);
    }

    internal Task ScanDriveAsync(CancellationToken ct)
    {
        return Task.Run(() => ScanDirectory(_drivePath, ct), ct);
    }

    private void ScanDirectory(string directory, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        var dirName = Path.GetFileName(directory);
        if (dirName is "$Recycle.Bin" or "System Volume Information")
            return;

        try
        {
            var nulPath = Path.Combine(directory, "NUL");
            var extendedPath = ToExtendedLengthPath(nulPath);
            if (File.Exists(extendedPath))
            {
                _log?.Invoke($"NUL file found by scan: {nulPath}");
                TryDeleteAndRaiseEvents(nulPath);
            }

            foreach (var subDir in Directory.EnumerateDirectories(directory))
            {
                if (ct.IsCancellationRequested) return;
                ScanDirectory(subDir, ct);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
        catch (IOException)
        {
            // Skip directories with I/O errors
        }
    }

    private void TryDeleteAndRaiseEvents(string path)
    {
        try
        {
            if (TryDeleteNulFile(path))
            {
                _log?.Invoke($"Deleted NUL file: {path}");
                OnNulFileDeleted?.Invoke(path);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Failed to delete NUL file {path}: {ex.Message}");
            OnDeletionFailed?.Invoke(path, ex);
        }
    }

    /// <summary>
    /// Attempts to delete a NUL file using the \\?\ extended-length path prefix.
    /// Returns true if the file existed and was deleted.
    /// </summary>
    internal static bool TryDeleteNulFile(string path)
    {
        var extendedPath = ToExtendedLengthPath(path);
        if (!File.Exists(extendedPath))
            return false;

        File.Delete(extendedPath);
        return true;
    }

    /// <summary>
    /// Adds the \\?\ extended-length path prefix if not already present.
    /// This is required to interact with files named NUL, CON, PRN, etc.
    /// on Windows — without it, the OS interprets these as device names.
    /// </summary>
    internal static string ToExtendedLengthPath(string path)
    {
        if (path.StartsWith(@"\\?\"))
            return path;

        return @"\\?\" + path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        _cts.Dispose();
    }
}
