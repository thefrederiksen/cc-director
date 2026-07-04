namespace CcDirector.Core.Utilities;

public static class GitHubRepositoryDefaults
{
    public const string OwnerEnvironmentVariable = "DEVTHROTTLE_GITHUB_OWNER";
    public const string RepositoryEnvironmentVariable = "DEVTHROTTLE_GITHUB_REPO";

    // The product's release location. This is inherently public (it is where every user downloads
    // releases and where the auto-updater fetches from), not private data - so it must be the REAL
    // repository, or shipped builds cannot self-update or fresh-install. Override with the env vars
    // above when hosting a fork's releases elsewhere.
    private const string FallbackOwner = "thefrederiksen";
    private const string FallbackRepository = "devthrottle";

    public static string Owner => Resolve(OwnerEnvironmentVariable, FallbackOwner);
    public static string Repository => Resolve(RepositoryEnvironmentVariable, FallbackRepository);
    public static string Slug => $"{Owner}/{Repository}";

    private static string Resolve(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
