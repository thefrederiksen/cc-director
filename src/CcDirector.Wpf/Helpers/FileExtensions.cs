namespace CcDirector.Wpf.Helpers;

/// <summary>
/// Shared file extension checks used across controls.
/// </summary>
public static class FileExtensions
{
    public static bool IsMarkdown(string path)
    {
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
    }
}
