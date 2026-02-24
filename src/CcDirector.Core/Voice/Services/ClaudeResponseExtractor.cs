using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Voice.Services;

/// <summary>
/// Extracts Claude assistant responses from .jsonl session files.
/// Used by voice mode to get the response text for summarization and TTS.
/// </summary>
public static class ClaudeResponseExtractor
{
    /// <summary>
    /// Extract the last assistant response from a Claude session .jsonl file.
    /// Returns null if file doesn't exist or no assistant response found.
    /// </summary>
    /// <param name="jsonlPath">Full path to the .jsonl session file.</param>
    /// <returns>The last assistant response text, or null if not found.</returns>
    public static string? ExtractLastResponse(string jsonlPath)
    {
        FileLog.Write($"[ClaudeResponseExtractor] ExtractLastResponse: {jsonlPath}");

        if (!File.Exists(jsonlPath))
        {
            FileLog.Write("[ClaudeResponseExtractor] File not found");
            return null;
        }

        try
        {
            // Use FileShare.ReadWrite to allow reading while Claude writes
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            string? lastResponse = null;
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    // Look for assistant messages
                    if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "assistant")
                        continue;

                    var content = ExtractTextContent(root);
                    if (!string.IsNullOrEmpty(content))
                    {
                        lastResponse = content;
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed lines
                }
            }

            if (lastResponse != null)
            {
                FileLog.Write($"[ClaudeResponseExtractor] Found response: {lastResponse.Length} chars");
            }
            else
            {
                FileLog.Write("[ClaudeResponseExtractor] No assistant response found");
            }

            return lastResponse;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeResponseExtractor] ExtractLastResponse FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extract all assistant responses from a Claude session .jsonl file.
    /// Returns empty list if file doesn't exist or no responses found.
    /// </summary>
    /// <param name="jsonlPath">Full path to the .jsonl session file.</param>
    /// <returns>List of assistant response texts in order.</returns>
    public static List<string> ExtractAllResponses(string jsonlPath)
    {
        FileLog.Write($"[ClaudeResponseExtractor] ExtractAllResponses: {jsonlPath}");
        var responses = new List<string>();

        if (!File.Exists(jsonlPath))
        {
            FileLog.Write("[ClaudeResponseExtractor] File not found");
            return responses;
        }

        try
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "assistant")
                        continue;

                    var content = ExtractTextContent(root);
                    if (!string.IsNullOrEmpty(content))
                    {
                        responses.Add(content);
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed lines
                }
            }

            FileLog.Write($"[ClaudeResponseExtractor] Found {responses.Count} response(s)");
            return responses;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeResponseExtractor] ExtractAllResponses FAILED: {ex.Message}");
            return responses;
        }
    }

    /// <summary>
    /// Extract text content from an assistant message element.
    /// Handles both simple string content and array content with text blocks.
    /// </summary>
    private static string? ExtractTextContent(JsonElement root)
    {
        // Try to get message property first
        if (!root.TryGetProperty("message", out var messageEl))
            return null;

        // Message can be a simple string
        if (messageEl.ValueKind == JsonValueKind.String)
        {
            return messageEl.GetString();
        }

        // Or an object with content property
        if (!messageEl.TryGetProperty("content", out var contentEl))
            return null;

        // Content can be a simple string
        if (contentEl.ValueKind == JsonValueKind.String)
        {
            return contentEl.GetString();
        }

        // Or an array of content blocks
        if (contentEl.ValueKind == JsonValueKind.Array)
        {
            var textParts = new List<string>();

            foreach (var item in contentEl.EnumerateArray())
            {
                // Look for text content blocks
                if (item.TryGetProperty("type", out var itemType) &&
                    itemType.GetString() == "text" &&
                    item.TryGetProperty("text", out var textProp))
                {
                    var text = textProp.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        textParts.Add(text);
                    }
                }
            }

            return textParts.Count > 0 ? string.Join("\n", textParts) : null;
        }

        return null;
    }
}
