namespace CcDirector.Gateway.Streaming;

/// <summary>
/// THE ONE freshness window for "is this Director's pushed snapshot still current", carried as a type so it
/// can be resolved from the service container.
///
/// It exists because a bare <see cref="System.TimeSpan"/> cannot be injected: an optional TimeSpan parameter
/// on a container-constructed type resolves to its default and says nothing about it. That is exactly what
/// happened here - <see cref="DirectorHub"/> took an optional TimeSpan, nothing registered one, and it
/// silently used the 20-second default while the roster read used the operator's configured value. The two
/// then disagreed about which Directors were in the fleet, so the recorded concurrency peak stopped matching
/// what the roster showed, on any Gateway whose configuration was not the default.
///
/// Wrapping the value makes the registration explicit and the omission a compile-time-visible absence rather
/// than a silent fallback. One window, registered once, read by everything that needs it.
/// </summary>
/// <param name="Value">How long after its last push a Director's snapshot is still treated as fresh.</param>
public sealed record StreamStaleWindow(System.TimeSpan Value);
