using System.Diagnostics;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Core.Voice.Services;

/// <summary>
/// Summarizes Claude responses using the Claude CLI with haiku model.
/// Produces short, conversational summaries suitable for TTS.
/// </summary>
public class ClaudeSummarizer : IResponseSummarizer
{
    private const string ClaudeExecutable = "claude";
    private const string Model = "haiku";
    private const int TimeoutSeconds = 30;

    private bool? _isAvailable;
    private string? _unavailableReason;

    /// <summary>
    /// The prompt template for summarization.
    /// </summary>
    private const string SummarizationPrompt = """
        Read this Claude response and summarize it in 2-3 casual sentences,
        as if telling a friend what happened. Skip code details, file paths,
        and technical specifics. Focus on what was accomplished or what
        the answer was. Output ONLY the summary text, nothing else.
        """;

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
    public async Task<string> SummarizeAsync(string response, CancellationToken cancellationToken = default)
    {
        FileLog.Write($"[ClaudeSummarizer] SummarizeAsync: response length={response.Length}");

        if (!IsAvailable)
        {
            throw new InvalidOperationException($"Claude CLI not available: {UnavailableReason}");
        }

        if (string.IsNullOrWhiteSpace(response))
        {
            return "No response to summarize.";
        }

        // If the response is already short, return it as-is
        if (response.Length < 200)
        {
            FileLog.Write("[ClaudeSummarizer] Response already short, returning as-is");
            return CleanupForSpeech(response);
        }

        try
        {
            var summary = await RunClaudeSummarizationAsync(response, cancellationToken);
            FileLog.Write($"[ClaudeSummarizer] Summary: {summary.Length} chars");
            return summary;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeSummarizer] SummarizeAsync FAILED: {ex.Message}");
            // Fallback: return truncated original
            return TruncateForSpeech(response);
        }
    }

    private async Task<string> RunClaudeSummarizationAsync(string response, CancellationToken cancellationToken)
    {
        // Write response to temp file to avoid command line escaping issues
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, response, cancellationToken);

            // Build the command with piped input
            var psi = new ProcessStartInfo
            {
                FileName = ClaudeExecutable,
                Arguments = $"-p \"{SummarizationPrompt}\" --model {Model} --output-format text",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Write response to stdin
            await process.StandardInput.WriteAsync(response);
            process.StandardInput.Close();

            // Wait for completion with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(timeoutCts.Token);

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                FileLog.Write($"[ClaudeSummarizer] Claude exited with code {process.ExitCode}: {error}");
                throw new InvalidOperationException($"Claude CLI failed: {error}");
            }

            var summary = output.Trim();
            if (string.IsNullOrEmpty(summary))
            {
                FileLog.Write("[ClaudeSummarizer] Empty output from Claude");
                return TruncateForSpeech(response);
            }

            return CleanupForSpeech(summary);
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private void CheckAvailability()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ClaudeExecutable,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                _isAvailable = false;
                _unavailableReason = "Failed to start Claude CLI";
                return;
            }

            process.WaitForExit(5000);

            if (process.ExitCode == 0)
            {
                _isAvailable = true;
                _unavailableReason = null;
                FileLog.Write("[ClaudeSummarizer] Claude CLI is available");
            }
            else
            {
                _isAvailable = false;
                _unavailableReason = "Claude CLI returned error";
            }
        }
        catch (Exception ex)
        {
            _isAvailable = false;
            _unavailableReason = $"Claude CLI not found: {ex.Message}";
            FileLog.Write($"[ClaudeSummarizer] CheckAvailability FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Clean up text for speech synthesis.
    /// Removes markdown formatting, code blocks, etc.
    /// </summary>
    private static string CleanupForSpeech(string text)
    {
        // Remove code blocks
        text = System.Text.RegularExpressions.Regex.Replace(text, @"```[\s\S]*?```", "");

        // Remove inline code
        text = System.Text.RegularExpressions.Regex.Replace(text, @"`[^`]+`", "");

        // Remove markdown links, keeping text
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");

        // Remove bullet points
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[\s]*[-*+]\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);

        // Remove numbered lists
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[\s]*\d+\.\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);

        // Remove markdown headers
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^#+\s*", "", System.Text.RegularExpressions.RegexOptions.Multiline);

        // Remove bold/italic
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*([^*]+)\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"__([^_]+)__", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"_([^_]+)_", "$1");

        // Collapse multiple newlines
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");

        // Collapse multiple spaces
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ ]{2,}", " ");

        return text.Trim();
    }

    /// <summary>
    /// Truncate text for speech when summarization fails.
    /// </summary>
    private static string TruncateForSpeech(string text)
    {
        text = CleanupForSpeech(text);

        if (text.Length <= 300)
            return text;

        // Find a sentence break near the cutoff
        var cutoff = 300;
        var sentenceEnd = text.LastIndexOf('.', cutoff);
        if (sentenceEnd > 100)
            return text[..(sentenceEnd + 1)];

        // No good sentence break, just truncate
        return text[..cutoff] + "...";
    }
}
