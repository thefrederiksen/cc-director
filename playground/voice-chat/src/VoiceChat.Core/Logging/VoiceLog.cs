using System.Collections.Concurrent;

namespace VoiceChat.Core.Logging;

/// <summary>
/// File logger for the voice chat pipeline.
/// Writes timestamped entries to %LOCALAPPDATA%\voice-chat\logs\.
/// Thread-safe with background writer.
/// </summary>
public static class VoiceLog
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "voice-chat", "logs");

    private static readonly BlockingCollection<string> Queue = new(1024);
    private static Thread? _writerThread;
    private static StreamWriter? _writer;
    private static string _currentLogPath = string.Empty;
    private static bool _started;

    public static string CurrentLogPath => _currentLogPath;

    public static void Start()
    {
        if (_started) return;
        _started = true;

        Directory.CreateDirectory(LogDir);
        var fileName = $"voicechat-{DateTime.Now:yyyy-MM-dd}-{Environment.ProcessId}.log";
        _currentLogPath = Path.Combine(LogDir, fileName);
        _writer = new StreamWriter(_currentLogPath, append: true) { AutoFlush = false };

        _writerThread = new Thread(WriterLoop)
        {
            Name = "VoiceLog-Writer",
            IsBackground = true,
        };
        _writerThread.Start();

        Write("[VoiceLog] Log started.");
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        Queue.TryAdd(line);
        System.Diagnostics.Debug.WriteLine(line);
    }

    public static void Stop()
    {
        if (!_started) return;
        Write("[VoiceLog] Log stopping.");
        Queue.CompleteAdding();
        _writerThread?.Join(3000);
        _writer?.Flush();
        _writer?.Dispose();
    }

    private static void WriterLoop()
    {
        try
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                _writer?.WriteLine(line);
                if (Queue.Count == 0)
                    _writer?.Flush();
            }
        }
        catch (InvalidOperationException)
        {
            // Queue completed
        }
    }
}
