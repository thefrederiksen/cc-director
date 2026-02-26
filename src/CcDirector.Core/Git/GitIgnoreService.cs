using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

public static class GitIgnoreService
{
    /// <summary>
    /// Checks whether an entry already exists in the .gitignore file.
    /// </summary>
    public static bool EntryExists(string repoPath, string entry)
    {
        FileLog.Write($"[GitIgnoreService] EntryExists: repoPath={repoPath}, entry={entry}");

        var normalized = NormalizeEntry(entry);
        var gitignorePath = Path.Combine(repoPath, ".gitignore");

        if (!File.Exists(gitignorePath))
        {
            FileLog.Write("[GitIgnoreService] EntryExists: .gitignore does not exist");
            return false;
        }

        var lines = File.ReadAllLines(gitignorePath);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && trimmed == normalized)
            {
                FileLog.Write($"[GitIgnoreService] EntryExists: found match for '{normalized}'");
                return true;
            }
        }

        FileLog.Write($"[GitIgnoreService] EntryExists: no match for '{normalized}'");
        return false;
    }

    /// <summary>
    /// Appends an entry to .gitignore at the repo root.
    /// Returns false if the entry already exists (duplicate).
    /// Creates the .gitignore file if it doesn't exist.
    /// </summary>
    public static async Task<bool> AddEntryAsync(string repoPath, string entry)
    {
        FileLog.Write($"[GitIgnoreService] AddEntryAsync: repoPath={repoPath}, entry={entry}");

        var normalized = NormalizeEntry(entry);
        var gitignorePath = Path.Combine(repoPath, ".gitignore");

        if (EntryExists(repoPath, normalized))
        {
            FileLog.Write($"[GitIgnoreService] AddEntryAsync: duplicate entry '{normalized}', skipping");
            return false;
        }

        await Task.Run(() =>
        {
            // Ensure the file ends with a newline before appending
            if (File.Exists(gitignorePath))
            {
                var content = File.ReadAllText(gitignorePath);
                if (content.Length > 0 && !content.EndsWith('\n'))
                {
                    File.AppendAllText(gitignorePath, "\n");
                }
            }

            File.AppendAllText(gitignorePath, normalized + "\n");
        });

        FileLog.Write($"[GitIgnoreService] AddEntryAsync: added '{normalized}' to .gitignore");
        return true;
    }

    /// <summary>
    /// Normalizes an entry for .gitignore: converts backslashes to forward slashes.
    /// </summary>
    internal static string NormalizeEntry(string entry)
    {
        return entry.Replace('\\', '/');
    }
}
