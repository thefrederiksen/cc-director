using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Runs the actual work-list drain once the cron trigger has decided to go (epic #479, #484).
/// Behind an interface so <see cref="DirectorCronWorkListRunner"/>'s decision logic is unit-testable
/// without a live Director; production is <see cref="DirectorWorkListDrainLauncher"/>, which reuses
/// the same <see cref="DirectorImplSessionDriver"/> + <see cref="WorkListRunner"/> path the
/// <c>/lists/{name}/run</c> endpoint uses (#274). The call completes when the whole list is drained.
/// </summary>
public interface ICronWorkListDrainLauncher
{
    /// <summary>Claim and drain <paramref name="listName"/> on the Director identified by
    /// <paramref name="directorId"/> (the tunnel leg) at <paramref name="endpoint"/> (the HTTP-fallback leg)
    /// as <paramref name="consumer"/>, opening sessions in <paramref name="repoPath"/>.</summary>
    Task LaunchAsync(string directorId, string endpoint, string repoPath, string listName, string consumer, CancellationToken ct);
}

/// <summary>Production launcher: the shipped #274 drain path (DirectorImplSessionDriver + WorkListRunner).</summary>
public sealed class DirectorWorkListDrainLauncher : ICronWorkListDrainLauncher
{
    private readonly WorkListStore _store;
    private readonly Func<string, string, string, IImplSessionDriver> _driverFactory;

    /// <param name="driverFactory">
    /// Builds the per-drain session driver from (directorId, endpoint, repoPath). Defaults to the production
    /// <see cref="DirectorImplSessionDriver"/> with NO stream hook (a no-op driver); the Gateway host injects
    /// a tunnel-aware factory in production. Tests inject a fake so the claim + ordered drain through this
    /// launcher is verifiable without a live Director.
    /// </param>
    public DirectorWorkListDrainLauncher(
        WorkListStore store,
        Func<string, string, string, IImplSessionDriver>? driverFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        // The default factory carries NO stream hook (sendCommand = null); the Gateway host injects a
        // tunnel-aware factory for production. The endpoint argument is ignored post-cut (tunnel-only).
        _driverFactory = driverFactory
            ?? ((directorId, endpoint, repoPath) =>
                new DirectorImplSessionDriver(directorId, repoPath, sendCommand: null));
    }

    public async Task LaunchAsync(string directorId, string endpoint, string repoPath, string listName, string consumer, CancellationToken ct)
    {
        var driver = _driverFactory(directorId, endpoint, repoPath);
        var runner = new WorkListRunner(_store, driver);
        await runner.DrainAsync(listName, consumer, ct);
    }
}
