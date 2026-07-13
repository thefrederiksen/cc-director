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
    /// <see cref="DirectorImplSessionDriver"/> over the shared client with NO stream hook (the HTTP fallback
    /// leg only); the Gateway host injects a tunnel-aware factory in production. Tests inject a fake so the
    /// claim + ordered drain through this launcher is verifiable without a live Director.
    /// </param>
    public DirectorWorkListDrainLauncher(
        WorkListStore store,
        DirectorEndpointClient? client,
        Func<string, string, string, IImplSessionDriver>? driverFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (driverFactory is not null)
        {
            _driverFactory = driverFactory;
        }
        else if (client is not null)
        {
            // Flow analysis: client is non-null in this branch, so the captured driver build is safe. No
            // stream hook here (sendCommand = null) - this default is the HTTP-fallback-only path; the
            // Gateway host injects a tunnel-aware factory for production stream mode.
            _driverFactory = (directorId, endpoint, repoPath) =>
                new DirectorImplSessionDriver(client, directorId, endpoint, repoPath, sendCommand: null);
        }
        else
        {
            throw new ArgumentException("a client or a driver factory is required", nameof(client));
        }
    }

    public async Task LaunchAsync(string directorId, string endpoint, string repoPath, string listName, string consumer, CancellationToken ct)
    {
        var driver = _driverFactory(directorId, endpoint, repoPath);
        var runner = new WorkListRunner(_store, driver);
        await runner.DrainAsync(listName, consumer, ct);
    }
}
