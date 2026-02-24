using System.Diagnostics;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Core.Voice.Services;

/// <summary>
/// Speech-to-text service using Whisper.cpp CLI.
/// Expects whisper.exe and a model file to be available.
/// </summary>
public class WhisperSttService : ISpeechToText
{
    private const int TimeoutSeconds = 60;

    private readonly string _executablePath;
    private readonly string _modelPath;
    private bool? _isAvailable;
    private string? _unavailableReason;

    /// <summary>
    /// Create a WhisperSttService with default paths.
    /// Looks for whisper.exe in PATH or common locations.
    /// </summary>
    public WhisperSttService()
    {
        // Try common locations
        _executablePath = FindExecutable("whisper.exe") ?? "whisper.exe";
        _modelPath = FindModelFile() ?? "";
    }

    /// <summary>
    /// Create a WhisperSttService with specific paths.
    /// </summary>
    /// <param name="executablePath">Path to whisper.exe.</param>
    /// <param name="modelPath">Path to the model file (.bin).</param>
    public WhisperSttService(string executablePath, string modelPath)
    {
        _executablePath = executablePath;
        _modelPath = modelPath;
    }

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            if (_isAvailable == null)
                CheckAvailability();
            return _isAvailable!.Value;
        }
    }

    /// <inheritdoc />
    public string? UnavailableReason
    {
        get
        {
            if (_isAvailable == null)
                CheckAvailability();
            return _unavailableReason;
        }
    }

    /// <inheritdoc />
    public async Task<string> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        FileLog.Write($"[WhisperSttService] TranscribeAsync: {audioPath}");

        if (!IsAvailable)
        {
            throw new InvalidOperationException($"Whisper not available: {UnavailableReason}");
        }

        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException("Audio file not found", audioPath);
        }

        // Output path for the transcription
        var outputBase = Path.Combine(
            Path.GetDirectoryName(audioPath) ?? Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(audioPath));

        try
        {
            // Run whisper CLI
            // whisper.exe -m model.bin -f audio.wav -otxt
            var psi = new ProcessStartInfo
            {
                FileName = _executablePath,
                Arguments = $"-m \"{_modelPath}\" -f \"{audioPath}\" -otxt",
                WorkingDirectory = Path.GetDirectoryName(audioPath),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0)
            {
                var error = await errorTask;
                FileLog.Write($"[WhisperSttService] Whisper exited with code {process.ExitCode}: {error}");
                throw new InvalidOperationException($"Whisper failed: {error}");
            }

            // Read the output text file
            var txtPath = outputBase + ".txt";
            if (File.Exists(txtPath))
            {
                var text = await File.ReadAllTextAsync(txtPath, cancellationToken);
                FileLog.Write($"[WhisperSttService] Transcription: {text.Length} chars");

                // Clean up temp file
                try { File.Delete(txtPath); } catch { }

                return text.Trim();
            }

            // Fallback: try to get from stdout
            var stdout = await outputTask;
            FileLog.Write($"[WhisperSttService] Stdout transcription: {stdout.Length} chars");
            return stdout.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WhisperSttService] TranscribeAsync FAILED: {ex.Message}");
            throw;
        }
    }

    private void CheckAvailability()
    {
        // Check executable
        if (!File.Exists(_executablePath))
        {
            // Try to find in PATH
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "whisper.exe",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    process.WaitForExit(5000);
                    if (process.ExitCode != 0)
                    {
                        _isAvailable = false;
                        _unavailableReason = "whisper.exe not found in PATH. Install Whisper.cpp from https://github.com/ggerganov/whisper.cpp";
                        return;
                    }
                }
            }
            catch
            {
                _isAvailable = false;
                _unavailableReason = "whisper.exe not found. Install Whisper.cpp from https://github.com/ggerganov/whisper.cpp";
                return;
            }
        }

        // Check model file
        if (string.IsNullOrEmpty(_modelPath) || !File.Exists(_modelPath))
        {
            _isAvailable = false;
            _unavailableReason = "Whisper model file not found. Download a model from https://github.com/ggerganov/whisper.cpp/tree/master/models";
            return;
        }

        _isAvailable = true;
        _unavailableReason = null;
        FileLog.Write("[WhisperSttService] Whisper is available");
    }

    private static string? FindExecutable(string name)
    {
        // Check common locations
        var locations = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "whisper.cpp", name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "whisper.cpp", name),
            Path.Combine("C:\\whisper", name),
        };

        foreach (var loc in locations)
        {
            if (File.Exists(loc))
                return loc;
        }

        return null;
    }

    private static string? FindModelFile()
    {
        // Look for common model files
        var modelNames = new[] { "ggml-base.en.bin", "ggml-small.en.bin", "ggml-tiny.en.bin", "ggml-base.bin" };
        var locations = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "whisper.cpp", "models"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "whisper.cpp", "models"),
            "C:\\whisper\\models",
        };

        foreach (var loc in locations)
        {
            if (!Directory.Exists(loc)) continue;

            foreach (var model in modelNames)
            {
                var path = Path.Combine(loc, model);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }
}
