namespace CcDirector.Gateway.CarMode;

/// <summary>
/// The fleet operations the Car Mode brain's tools call (Car Mode mission, New build A). Each method is
/// backed by an existing Gateway or Director endpoint - no new fleet mechanics, just a wrapper the model
/// can call in the tool-calling loop. Phase 2 is read-only (list + activity); Phase 3 adds the act tools
/// (start / message / approve) and the confirmed-destructive tools (delete).
///
/// This is an interface so the brain's tool loop is unit-tested against a fake fleet (no network), and
/// the production implementation talks to the Gateway's own endpoints.
/// </summary>
public interface ICarModeFleet
{
    /// <summary>The whole fleet roster as compact, speakable session views. Answers count / list /
    ///  "who needs me" / "the latest one". Ordered newest-created first so "the latest one" is index 0.</summary>
    Task<IReadOnlyList<CarModeSessionInfo>> ListSessionsAsync(CancellationToken ct);

    /// <summary>Resolve a fuzzy human reference (a name, a repo, or a number) to one session and return
    ///  what it is doing. Returns null when nothing matches, so the brain can say it plainly rather than
    ///  guess.</summary>
    Task<CarModeActivity?> GetSessionActivityAsync(string sessionReference, CancellationToken ct);
}
