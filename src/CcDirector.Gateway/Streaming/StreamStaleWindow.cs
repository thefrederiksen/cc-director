namespace CcDirector.Gateway.Streaming;

/// <summary>
/// THE ONE freshness window for "is this Director's pushed snapshot still current", carried as a type so it
/// can be resolved from the service container.
///
/// It exists because a bare <see cref="System.TimeSpan"/> cannot be injected: an optional TimeSpan parameter
/// on a container-constructed type silently takes its default and says nothing about it. That already
/// happened once here - <see cref="DirectorHub"/> took an optional TimeSpan, nothing registered one, and it
/// used the built-in default while the roster used the operator's configured value, so the two disagreed
/// about which Directors were in the fleet on any Gateway not running the default configuration. A constant
/// standing in for a measurement is not a small bug; it is a wrong answer that looks like a right one.
///
/// The hub needs it again for the queued snooze prune, which re-reads the store's CURRENT accepted set when
/// it runs rather than carrying a set captured when it was queued. That read has to use the SAME freshness
/// window the roster serves from, or the prune would act on a fleet the roster does not agree exists.
/// </summary>
/// <param name="Value">How long after its last push a Director's snapshot is still treated as fresh.</param>
public sealed record StreamStaleWindow(System.TimeSpan Value);
