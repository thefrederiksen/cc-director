using System.Text.Json.Serialization;

namespace CcDirector.Core.Configuration;

public enum GitProvider
{
    GitHub,
    AzureDevOps,
    LocalOnly
}

public class RootDirectoryConfig
{
    public string Label { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public GitProvider Provider { get; set; } = GitProvider.LocalOnly;
    public string? AzureOrg { get; set; }
    public string? AzureProject { get; set; }

    [JsonIgnore]
    public string ProviderDisplayName => Provider switch
    {
        GitProvider.GitHub => "GitHub",
        GitProvider.AzureDevOps => "Azure DevOps",
        GitProvider.LocalOnly => "Local Only",
        _ => "Unknown"
    };
}
