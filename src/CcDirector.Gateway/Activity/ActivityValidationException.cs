namespace CcDirector.Gateway.Activity;

/// <summary>
/// A caller handed the activity ledger an event it must not store: a missing required fact, an event type
/// or cause outside the closed sets, an over-length field, or an over-size batch. Thrown loudly (the
/// repository's no-fallback rule - a malformed event is a producer bug to surface, never something to
/// quietly coerce) and mapped to HTTP 400 at the endpoint boundary.
/// </summary>
public sealed class ActivityValidationException : Exception
{
    public ActivityValidationException(string message) : base(message)
    {
    }
}
