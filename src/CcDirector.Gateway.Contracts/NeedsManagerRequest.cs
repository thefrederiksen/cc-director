namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of <c>POST /sessions/{sid}/needs-manager</c> (issue #2662) - a supervised session putting its hand
/// up to the session that is driving it, or taking it back down.
/// </summary>
public sealed class NeedsManagerRequest
{
    /// <summary>True to raise the hand, false to lower it. Lowering ignores <see cref="Reason"/>.</summary>
    public bool Raised { get; set; }

    /// <summary>
    /// The worker's OWN WORDS for what it is blocked on. REQUIRED when raising - the endpoint refuses a
    /// blank one, because a hand up with no words is the "notice me" ping the roles design rejects and
    /// leaves the supervisor no better off than not being told.
    /// </summary>
    public string? Reason { get; set; }
}
