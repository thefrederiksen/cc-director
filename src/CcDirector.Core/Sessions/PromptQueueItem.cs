namespace CcDirector.Core.Sessions;

public sealed class PromptQueueItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
