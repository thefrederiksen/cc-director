namespace CcDirector.Core.Utilities;

/// <summary>
/// Categorizes file types for the built-in viewer system.
/// </summary>
public enum FileViewerCategory
{
    None,
    Markdown,
    Image,
    Text
}

/// <summary>
/// Shared file extension checks used across controls.
/// Pure logic, no WPF dependencies.
/// </summary>
public static class FileExtensions
{
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".json", ".xml", ".yaml", ".yml",
        ".ini", ".cfg", ".conf", ".toml",
        ".gitignore", ".editorconfig",
        ".cs", ".py", ".js", ".ts", ".ps1", ".bat", ".sh", ".sql",
        ".html", ".css", ".svg", ".tsx", ".jsx",
        ".rs", ".go", ".java", ".cpp", ".c", ".h", ".hpp",
        ".rb", ".php"
    };

    public static bool IsMarkdown(string path)
    {
        var ext = Path.GetExtension(path);
        return MarkdownExtensions.Contains(ext);
    }

    public static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext);
    }

    public static bool IsTextFile(string path)
    {
        var ext = Path.GetExtension(path);
        return TextExtensions.Contains(ext);
    }

    public static bool IsViewable(string path)
    {
        return IsMarkdown(path) || IsImage(path) || IsTextFile(path);
    }

    public static FileViewerCategory GetViewerCategory(string path)
    {
        if (IsMarkdown(path)) return FileViewerCategory.Markdown;
        if (IsImage(path)) return FileViewerCategory.Image;
        if (IsTextFile(path)) return FileViewerCategory.Text;
        return FileViewerCategory.None;
    }
}
