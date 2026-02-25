namespace CcDirector.Core.Git;

public class RemoteRepoInfo
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsPrivate { get; set; }
}
