namespace CcDirector.Core.Git;

/// <summary>One line of a parsed diff hunk.</summary>
public sealed record DiffLine
{
    public DiffLineKind Kind { get; init; }
    public string Text { get; init; } = "";

    /// <summary>1-based line number in the OLD file, or null for an added line.</summary>
    public int? OldNumber { get; init; }

    /// <summary>1-based line number in the NEW file, or null for a deleted line.</summary>
    public int? NewNumber { get; init; }
}

public enum DiffLineKind { Context, Added, Removed }

/// <summary>A contiguous hunk of a file diff, with its header.</summary>
public sealed record DiffHunk
{
    public string Header { get; init; } = "";
    public IReadOnlyList<DiffLine> Lines { get; init; } = Array.Empty<DiffLine>();
}

/// <summary>The parsed diff of one file.</summary>
public sealed record FileDiff
{
    public string OldPath { get; init; } = "";
    public string NewPath { get; init; } = "";
    public bool IsBinary { get; init; }
    public bool IsRename => OldPath.Length > 0 && NewPath.Length > 0
                            && !string.Equals(OldPath, NewPath, StringComparison.Ordinal);
    public IReadOnlyList<DiffHunk> Hunks { get; init; } = Array.Empty<DiffHunk>();
    public int Added { get; init; }
    public int Removed { get; init; }
}

/// <summary>
/// Parses unified diff output (<c>git diff</c>) into file/hunk/line records the diff viewer renders.
/// Pure string logic - no git, fully unit-testable. Parsing IS an assertion about the format: any
/// line that does not fit the unified-diff shape inside a hunk ends that hunk rather than being
/// silently mis-rendered.
/// </summary>
public static class DiffParser
{
    public static IReadOnlyList<FileDiff> Parse(string? diffText)
    {
        var files = new List<FileDiff>();
        if (string.IsNullOrEmpty(diffText))
            return files;

        var lines = diffText.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            string oldPath = "", newPath = "";
            bool binary = false;
            var hunks = new List<DiffHunk>();
            int added = 0, removed = 0;
            i++;

            // File header lines until the first hunk or the next file.
            while (i < lines.Length
                   && !lines[i].StartsWith("@@", StringComparison.Ordinal)
                   && !lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var l = lines[i];
                if (l.StartsWith("--- ", StringComparison.Ordinal))
                    oldPath = StripPathPrefix(l[4..]);
                else if (l.StartsWith("+++ ", StringComparison.Ordinal))
                    newPath = StripPathPrefix(l[4..]);
                else if (l.StartsWith("rename from ", StringComparison.Ordinal))
                    oldPath = l["rename from ".Length..].Trim();
                else if (l.StartsWith("rename to ", StringComparison.Ordinal))
                    newPath = l["rename to ".Length..].Trim();
                else if (l.StartsWith("Binary files ", StringComparison.Ordinal) || l.StartsWith("GIT binary patch", StringComparison.Ordinal))
                    binary = true;
                i++;
            }

            // Hunks.
            while (i < lines.Length && lines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                var header = lines[i];
                var (oldStart, newStart) = ParseHunkHeader(header);
                int oldNo = oldStart, newNo = newStart;
                var hunkLines = new List<DiffLine>();
                i++;

                while (i < lines.Length)
                {
                    var l = lines[i];
                    if (l.StartsWith("+", StringComparison.Ordinal))
                    {
                        hunkLines.Add(new DiffLine { Kind = DiffLineKind.Added, Text = l.Length > 1 ? l[1..] : "", NewNumber = newNo++ });
                        added++;
                    }
                    else if (l.StartsWith("-", StringComparison.Ordinal))
                    {
                        hunkLines.Add(new DiffLine { Kind = DiffLineKind.Removed, Text = l.Length > 1 ? l[1..] : "", OldNumber = oldNo++ });
                        removed++;
                    }
                    else if (l.StartsWith(" ", StringComparison.Ordinal) || l.Length == 0)
                    {
                        hunkLines.Add(new DiffLine { Kind = DiffLineKind.Context, Text = l.Length > 1 ? l[1..] : "", OldNumber = oldNo++, NewNumber = newNo++ });
                    }
                    else if (l.StartsWith("\\", StringComparison.Ordinal))
                    {
                        i++; // "\ No newline at end of file" - metadata, not content
                        continue;
                    }
                    else
                    {
                        break; // next hunk, next file, or something that is not diff content
                    }
                    i++;
                }

                hunks.Add(new DiffHunk { Header = header, Lines = hunkLines });
            }

            files.Add(new FileDiff
            {
                OldPath = oldPath,
                NewPath = newPath,
                IsBinary = binary,
                Hunks = hunks,
                Added = added,
                Removed = removed,
            });
        }

        return files;
    }

    /// <summary>Strips the a/ b/ prefixes and maps /dev/null to empty.</summary>
    private static string StripPathPrefix(string path)
    {
        var p = path.Trim();
        if (p == "/dev/null")
            return "";
        if (p.StartsWith("a/", StringComparison.Ordinal) || p.StartsWith("b/", StringComparison.Ordinal))
            return p[2..];
        return p;
    }

    /// <summary>Parses "@@ -oldStart[,n] +newStart[,n] @@ ..." into the two start line numbers.</summary>
    internal static (int OldStart, int NewStart) ParseHunkHeader(string header)
    {
        int oldStart = 1, newStart = 1;
        try
        {
            var parts = header.Split(' ');
            foreach (var part in parts)
            {
                if (part.StartsWith("-", StringComparison.Ordinal))
                    oldStart = int.Parse(part[1..].Split(',')[0]);
                else if (part.StartsWith("+", StringComparison.Ordinal))
                    newStart = int.Parse(part[1..].Split(',')[0]);
            }
        }
        catch
        {
            // A malformed header renders from line 1 rather than crashing the viewer.
        }
        return (oldStart, newStart);
    }
}
