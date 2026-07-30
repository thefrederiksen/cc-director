namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// Which identity table a display spelling belongs to. This replaced a <c>bool isRepo</c> when the model
/// dimension arrived and made the question three-valued: a boolean cannot name a third kind, and the
/// alternative - a second parallel set of NeedIdentity/Resolve methods for models - would have duplicated the
/// batch-level OrdinalIgnoreCase dedup, which is the subtle part.
///
/// <see cref="Model"/> is a first-class kind here but has NO distinct-session set: nothing asks how many
/// sessions ran a model, so the write path refuses to file a membership row for one rather than carrying a set
/// nothing populates. It is also the only kind that can be ABSENT - an unnamed model is a SQL NULL, never an
/// identity spelled "".
///
/// <see cref="Checkout"/> is the local working-directory path retained beside the repository name. Like
/// <see cref="Model"/> it keeps no distinct-session set (the session count the Repos page shows is per
/// repository, not per checkout). Unlike Model it is never absent - a session always has a working directory.
///
/// It lives in this namespace rather than nested inside the aggregator because the write path
/// (<see cref="GatewayStatsWriter"/>) and the batch it consumes (<see cref="StatsWriteBatch"/>) are now their
/// own types, and all three have to name the same four kinds.
/// </summary>
internal enum IdentityKind
{
    Repo,
    Agent,
    Model,
    Checkout,
}
