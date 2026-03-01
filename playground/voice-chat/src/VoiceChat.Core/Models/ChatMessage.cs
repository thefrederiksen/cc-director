namespace VoiceChat.Core.Models;

public sealed class ChatMessage
{
    public required string Role { get; init; }
    public required string Text { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LatencyInfo? Latency { get; init; }
}

public sealed class LatencyInfo
{
    public int SttMs { get; set; }
    public int LlmMs { get; set; }
    public int TtsMs { get; set; }
    public int TotalMs => SttMs + LlmMs + TtsMs;

    public override string ToString() =>
        $"STT: {SttMs}ms | LLM: {LlmMs}ms | TTS: {TtsMs}ms | Total: {TotalMs}ms";
}
