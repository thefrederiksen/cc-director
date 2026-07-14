namespace CcDirector.Setup.Engine;

/// <summary>
/// A single installable component. Immutable description used by the registry,
/// the install layout, and the update planner.
/// </summary>
/// <param name="Id">Canonical id (e.g. "director", "gateway", "cc-pdf").</param>
/// <param name="Kind">Category.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="WindowsAsset">
/// The release-asset filename this component ships as, on Windows
/// (e.g. "cc-director-win-x64.exe"). This is the key into the release manifest.
/// </param>
/// <param name="Roles">Which install roles include this component.</param>
/// <param name="MacAsset">
/// The release-asset filename this component ships as, on macOS
/// (e.g. "cc-launcher-mac-arm64"), or null when the component has no macOS build.
/// Like <paramref name="WindowsAsset"/>, this is the key into the release manifest.
/// </param>
public sealed record Component(
    string Id,
    ComponentKind Kind,
    string DisplayName,
    string WindowsAsset,
    IReadOnlySet<InstallRole> Roles,
    string? MacAsset = null)
{
    public bool InRole(InstallRole role) => Roles.Contains(role);

    /// <summary>
    /// The release-asset filename for the requested platform: <see cref="MacAsset"/> on macOS
    /// (null when the component has no macOS build), <see cref="WindowsAsset"/> otherwise.
    /// The platform is a parameter, not read from the environment, so planning stays pure and
    /// testable on any development machine.
    /// </summary>
    public string? AssetFor(bool macOs) => macOs ? MacAsset : WindowsAsset;
}
