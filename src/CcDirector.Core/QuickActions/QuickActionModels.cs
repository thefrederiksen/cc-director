namespace CcDirector.Core.QuickActions;

/// <summary>
/// A conversation thread in Quick Actions.
/// </summary>
public sealed class QuickActionThread
{
    public required string Id { get; init; }
    public required string Title { get; set; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A single message in a Quick Actions thread.
/// </summary>
public sealed class QuickActionMessage
{
    public int Id { get; init; }
    public required string ThreadId { get; init; }
    public required string Role { get; init; }
    public required string Content { get; init; }
    public required DateTime CreatedAt { get; init; }
}
