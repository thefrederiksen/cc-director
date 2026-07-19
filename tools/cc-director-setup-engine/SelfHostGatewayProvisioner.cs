using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Places the Gateway binary so the tray installer has something to start.
///
/// This exists because <see cref="GatewayTrayInstaller"/> deliberately refuses to run when the
/// Gateway executable is not on disk - it assumes the file swap has already happened. The command
/// line installer does that swap through a plan it composes privately, which the Director cannot
/// reach. Rather than move the whole command line composition (a wide change with its own tests),
/// this composes the same engine primitives for exactly ONE component: the Gateway.
///
/// Ownership matters here. If the Gateway is already installed and current, this reports
/// <see cref="SelfHostStepResult.AlreadyThere"/> so a failed provision never rolls back a binary the
/// user already had.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SelfHostGatewayProvisioner
{
    private readonly InstallLayout _layout;
    private readonly ReleaseSource _source;
    private readonly Func<CancellationToken, Task<ResolvedRelease>> _resolveRelease;

    public SelfHostGatewayProvisioner(
        InstallLayout layout,
        ReleaseSource? source = null,
        Func<CancellationToken, Task<ResolvedRelease>>? resolveRelease = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _source = source ?? new ReleaseSource();
        _resolveRelease = resolveRelease
            ?? (ct => _source.FetchReleaseForSetupAsync(ct));
    }

    /// <summary>
    /// Resolve the release, plan the Gateway component alone, and place it. Idempotent: an
    /// already-current Gateway plans to nothing and is reported as pre-existing, not as work done.
    /// </summary>
    public async Task<SelfHostStepResult> PlaceAsync(CancellationToken ct = default)
    {
        var release = await _resolveRelease(ct);

        var components = new[] { ComponentRegistry.Gateway };
        var installed = new InstalledStateReader(_layout).ReadAll(components);
        var pins = PinStore.Load(_layout);
        var plan = UpdatePlanner.Plan(components, installed, release.Manifest, pins);

        if (plan.Items.Count == 0)
            return SelfHostStepResult.AlreadyThere("The Gateway is already installed and current.");

        // Same composition the command line installer uses: download by asset name from the
        // resolved release, verified against the manifest hash inside UpdateRunner.
        var runner = new UpdateRunner(_layout, components,
            (item, token) => _source.DownloadAssetAsync(item.AssetName, release.DownloadUrls, token));
        var result = await runner.ApplyAsync(plan, ct);

        if (result.Failed > 0)
        {
            var why = result.Results.FirstOrDefault(r => r.Status == ApplyStatus.Failed)?.Error
                      ?? "the download or hash check did not succeed";
            return SelfHostStepResult.Failed($"The Gateway could not be installed: {why}");
        }

        // Skipped means the runner had nothing to do for that item. Treat it as pre-existing rather
        // than as work this run owns, so a later failure cannot roll back a Gateway we did not place.
        var placed = result.Installed + result.Updated;
        return placed > 0
            ? SelfHostStepResult.Created("Downloaded and verified the Gateway.")
            : SelfHostStepResult.AlreadyThere("The Gateway was already in place.");
    }
}
