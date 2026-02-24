using System.Diagnostics;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Core.Voice.Services;

/// <summary>
/// Text-to-speech service using Piper TTS CLI.
/// Expects piper.exe and a voice model to be available.
/// </summary>
public class PiperTtsService : ITextToSpeech
{
    private const int TimeoutSeconds = 30;

    private readonly string _executablePath;
    private readonly string _modelPath;
    private bool? _isAvailable;
    private string? _unavailableReason;

    /// <summary>
    /// Create a PiperTtsService with default paths.
    /// Looks for piper.exe in PATH or common locations.
    /// </summary>
    public PiperTtsService()
    {
        _executablePath = FindExecutable("piper.exe") ?? "piper.exe";
        _modelPath = FindModelFile() ?? "";
    }

    /// <summary>
    /// Create a PiperTtsService with specific paths.
    /// </summary>
    /// <param name="executablePath">Path to piper.exe.</param>
    /// <param name="modelPath">Path to the voice model file (.onnx).</param>
    public PiperTtsService(string executablePath, string modelPath)
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
    public async Task SynthesizeAsync(string text, string outputPath, CancellationToken cancellationToken = default)
    {
        FileLog.Write($"[PiperTtsService] SynthesizeAsync: text={text.Length} chars, output={outputPath}");

        if (!IsAvailable)
        {
            throw new InvalidOperationException($"Piper not available: {UnavailableReason}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text cannot be empty", nameof(text));
        }

        try
        {
            // Run piper CLI
            // echo "text" | piper --model voice.onnx --output_file out.wav
            var psi = new ProcessStartInfo
            {
                FileName = _executablePath,
                Arguments = $"--model \"{_modelPath}\" --output_file \"{outputPath}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Write text to stdin
            await process.StandardInput.WriteAsync(text);
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0)
            {
                var error = await errorTask;
                FileLog.Write($"[PiperTtsService] Piper exited with code {process.ExitCode}: {error}");
                throw new InvalidOperationException($"Piper failed: {error}");
            }

            if (!File.Exists(outputPath))
            {
                throw new InvalidOperationException("Piper did not create output file");
            }

            FileLog.Write($"[PiperTtsService] Created: {outputPath}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PiperTtsService] SynthesizeAsync FAILED: {ex.Message}");
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
                    Arguments = "piper.exe",
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
                        _unavailableReason = "piper.exe not found in PATH. Install Piper TTS from https://github.com/rhasspy/piper";
                        return;
                    }
                }
            }
            catch
            {
                _isAvailable = false;
                _unavailableReason = "piper.exe not found. Install Piper TTS from https://github.com/rhasspy/piper";
                return;
            }
        }

        // Check model file
        if (string.IsNullOrEmpty(_modelPath) || !File.Exists(_modelPath))
        {
            _isAvailable = false;
            _unavailableReason = "Piper voice model not found. Download a voice from https://github.com/rhasspy/piper/releases";
            return;
        }

        _isAvailable = true;
        _unavailableReason = null;
        FileLog.Write("[PiperTtsService] Piper is available");
    }

    private static string? FindExecutable(string name)
    {
        var locations = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "piper", name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "piper", name),
            Path.Combine("C:\\piper", name),
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
        // Look for common voice models
        var locations = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "piper", "voices"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "piper", "voices"),
            Path.Combine("C:\\piper", "voices"),
        };

        foreach (var loc in locations)
        {
            if (!Directory.Exists(loc)) continue;

            // Find any .onnx file
            var models = Directory.GetFiles(loc, "*.onnx", SearchOption.AllDirectories);
            if (models.Length > 0)
                return models[0];
        }

        return null;
    }
}
