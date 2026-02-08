using System.Text.Json.Serialization;

namespace CcDirector.Core.Pipes;

/// <summary>
/// Flat model representing a JSON message from a Claude Code hook relay.
/// Property names match the snake_case JSON keys from Claude hooks.
/// </summary>
public sealed class PipeMessage
{
    [JsonPropertyName("hook_event_name")]
    public string? HookEventName { get; set; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("transcript_path")]
    public string? TranscriptPath { get; set; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [JsonPropertyName("notification_type")]
    public string? NotificationType { get; set; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>Timestamp when the Director received this message.</summary>
    [JsonIgnore]
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
