using System.Text.Json.Serialization;

namespace CcDirector.DocumentLibrary.Models;

/// <summary>
/// Deserialized JSON line from cc-vault stdout streaming.
/// </summary>
public class StreamEvent
{
    public string Event { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;

    // Progress events
    public int Processed { get; set; }
    public int Total { get; set; }
    public string? File { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }

    // Complete events
    public int New { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Missing { get; set; }
    public int Errors { get; set; }
    public int Summarized { get; set; }
    public int Deduped { get; set; }
}
