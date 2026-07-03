namespace CcDirector.Core.Utilities;

public static class GitHubRepositoryDefaults
{
    public const string OwnerEnvironmentVariable = "DEVTHROTTLE_GITHUB_OWNER";
    public const string RepositoryEnvironmentVariable = "DEVTHROTTLE_GITHUB_REPO";

    private const string FallbackOwner = "devthrottle";
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
