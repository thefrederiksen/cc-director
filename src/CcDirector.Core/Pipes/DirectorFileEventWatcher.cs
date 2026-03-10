using System.Text.Json;
using CcDirector.Core.Storage;

namespace CcDirector.Core.Pipes;

/// <summary>
/// Watches a shared event directory for JSON files from hook relays.
/// Unlike named pipes, file-based events are naturally broadcast to ALL
/// CC Director instances watching the same directory.
/// </summary>
public sealed class DirectorFileEventWatcher : IDirectorServer
{
    private static readonly string EventDir = Path.Combine(CcStorage.Config(), "director", "events");

    private FileSystemWatcher? _watcher;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private Timer? _cleanupTimer;

    /// <summary>Raised when a complete message is received and deserialized.</summary>
    public event Action<PipeMessage>? OnMessageReceived;

    public DirectorFileEventWatcher(Action<string>? log = null)
    {
        _log = log;
    }

    /// <summary>The shared event directory path. Relay scripts write here.</summary>
    public static string EventDirectory => EventDir;

    public void Start()
    {
        Directory.CreateDirectory(EventDir);

        // Process any files already in the directory (from before we started)
        ProcessExistingFiles();

        _watcher = new FileSystemWatcher(EventDir, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnFileCreated;

        // Periodic cleanup of old event files (older than 30 seconds)
        _cleanupTimer = new Timer(CleanupOldFiles, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        _log?.Invoke($"FileEventWatcher started, watching: {EventDir}");
    }

    public void Stop()
    {
        _cts.Cancel();
        _cleanupTimer?.Dispose();
        _cleanupTimer = null;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileCreated;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (_cts.IsCancellationRequested) return;

        // Small delay to ensure the file is fully written
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, _cts.Token);
                ProcessFile(e.FullPath);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log?.Invoke($"FileEventWatcher error processing {Path.GetFileName(e.FullPath)}: {ex.Message}");
            }
        });
    }

    private void ProcessExistingFiles()
    {
        try
        {
            foreach (var file in Directory.GetFiles(EventDir, "*.json"))
            {
                ProcessFile(file);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FileEventWatcher error processing existing files: {ex.Message}");
        }
    }

    private void ProcessFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;

            string json;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
            {
                json = reader.ReadToEnd();
            }

            if (string.IsNullOrWhiteSpace(json)) return;

            var msg = JsonSerializer.Deserialize<PipeMessage>(json);
            if (msg != null)
            {
                msg.ReceivedAt = DateTimeOffset.UtcNow;
                OnMessageReceived?.Invoke(msg);
            }
        }
        catch (IOException)
        {
            // File may still be written or already deleted by another instance
        }
        catch (JsonException ex)
        {
            _log?.Invoke($"FileEventWatcher JSON parse error for {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    private void CleanupOldFiles(object? state)
    {
        try
        {
            if (!Directory.Exists(EventDir)) return;

            var cutoff = DateTime.UtcNow.AddSeconds(-30);
            foreach (var file in Directory.GetFiles(EventDir, "*.json"))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.CreationTimeUtc < cutoff)
                    {
                        info.Delete();
                    }
                }
                catch (IOException) { } // File locked by another instance
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FileEventWatcher cleanup error: {ex.Message}");
        }
    }
}
