namespace CcDirector.Core.Utilities;

/// <summary>
/// Categorizes file types for the built-in viewer system.
/// </summary>
public enum FileViewerCategory
{
    None,
    Markdown,
    Image,
    Text,
    Code,
    Pdf
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

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".tsx", ".jsx",
        ".json", ".xml", ".xaml", ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".sln",
        ".html", ".css", ".svg",
        ".sql",
        ".ps1", ".bat", ".sh",
        ".yaml", ".yml", ".toml",
        ".rs", ".go", ".java", ".cpp", ".c", ".h", ".hpp",
        ".rb", ".php", ".swift", ".kt"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv",
        ".ini", ".cfg", ".conf",
        ".gitignore", ".editorconfig",
        ".dockerignore", ".env", ".gitattributes", ".prettierrc", ".eslintrc"
    };

    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile"
    };

    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
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

    public static bool IsCodeFile(string path)
    {
        var ext = Path.GetExtension(path);
        return CodeExtensions.Contains(ext);
    }

    public static bool IsTextFile(string path)
    {
        var ext = Path.GetExtension(path);
        if (TextExtensions.Contains(ext)) return true;
        if (string.IsNullOrEmpty(ext))
        {
            var fileName = Path.GetFileName(path);
            return TextFileNames.Contains(fileName);
        }
        return false;
    }

    public static bool IsPdf(string path)
    {
        var ext = Path.GetExtension(path);
        return PdfExtensions.Contains(ext);
    }

    public static bool IsViewable(string path)
    {
        return IsMarkdown(path) || IsImage(path) || IsCodeFile(path) || IsTextFile(path) || IsPdf(path);
    }

    public static FileViewerCategory GetViewerCategory(string path)
    {
        if (IsMarkdown(path)) return FileViewerCategory.Markdown;
        if (IsImage(path)) return FileViewerCategory.Image;
        if (IsCodeFile(path)) return FileViewerCategory.Code;
        if (IsTextFile(path)) return FileViewerCategory.Text;
        if (IsPdf(path)) return FileViewerCategory.Pdf;
        return FileViewerCategory.None;
    }
}
