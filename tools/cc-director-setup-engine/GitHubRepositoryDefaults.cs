namespace CcDirector.Setup.Engine;

public static class GitHubRepositoryDefaults
{
    public const string OwnerEnvironmentVariable = "DEVTHROTTLE_GITHUB_OWNER";
    public const string RepositoryEnvironmentVariable = "DEVTHROTTLE_GITHUB_REPO";

    private const string FallbackOwner = "devthrottle";
    private const string FallbackRepository = "devthrottle";

    public static string Owner => Resolve(OwnerEnvironmentVariable, FallbackOwner);
    public static string Repository => Resolve(RepositoryEnvironmentVariable, FallbackRepository);
    public static string Slug => $"{Owner}/{Repository}";

    public static string GitHubUrl(string path) => $"https://github.com/{Slug}/{path.TrimStart('/')}";
    public static string RawUrl(string path) => $"https://raw.githubusercontent.com/{Slug}/main/{path.TrimStart('/')}";

    private static string Resolve(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
