namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One installed application the launcher found on its machine.
///
/// <see cref="Path"/> is what a launch actually uses, and on Windows it is normally the Start Menu shortcut
/// rather than the program's own executable. That is deliberate: a shortcut already carries the working
/// directory, the icon and the arguments the vendor intended, and starting one through the shell is the same
/// thing that happens when a person clicks it in the Start Menu. Resolving the shortcut down to its target
/// executable would throw all of that away and would need Windows-only component object model interoperation
/// in a launcher that also runs on macOS.
/// </summary>
public sealed class InstalledAppDto
{
    /// <summary>The display name, taken from the shortcut or application bundle filename.</summary>
    public string Name { get; set; } = "";

    /// <summary>The absolute path a launch should start: a shortcut on Windows, an application bundle on
    /// macOS, a desktop entry on Linux.</summary>
    public string Path { get; set; } = "";

    /// <summary>Where the entry was found - for example "start-menu-user", "start-menu-machine",
    /// "applications", "desktop-entries". Lets a caller tell a per-user install from a machine-wide one.</summary>
    public string Source { get; set; } = "";
}

/// <summary>
/// The answer to an "apps" query: the catalogue, plus an honest statement of whether it is complete.
/// </summary>
public sealed class AppSearchResultDto
{
    /// <summary>The machine the catalogue came from.</summary>
    public string Machine { get; set; } = "";

    /// <summary>The matching applications, ordered by name.</summary>
    public List<InstalledAppDto> Apps { get; set; } = new();

    /// <summary>How many matches existed before the limit was applied.</summary>
    public int TotalMatches { get; set; }

    /// <summary>True when <see cref="TotalMatches"/> exceeded the limit, so <see cref="Apps"/> is a prefix of
    /// the real answer rather than all of it.</summary>
    public bool Truncated { get; set; }

    /// <summary>Directories that could not be read, with the reason. A catalogue that quietly skipped half the
    /// Start Menu would look identical to a machine with half as many programs installed, so the skips are
    /// reported rather than swallowed.</summary>
    public List<string> Skipped { get; set; } = new();
}

/// <summary>One file matched by a "files" query.</summary>
public sealed class FileHitDto
{
    /// <summary>The absolute path to the file.</summary>
    public string Path { get; set; } = "";

    /// <summary>The filename on its own, without the directory.</summary>
    public string Name { get; set; } = "";

    /// <summary>The file size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>When the file was last written, in coordinated universal time.</summary>
    public DateTime ModifiedUtc { get; set; }
}

/// <summary>
/// The answer to a "files" query.
///
/// A whole-machine search is bounded by a result ceiling AND by a deadline, so a partial answer is a normal
/// outcome rather than a failure. <see cref="Truncated"/> and <see cref="TruncationReason"/> exist so a
/// partial answer can never be mistaken for a complete one: "no more results" and "I ran out of time" are
/// different facts, and a caller deciding whether to search again needs to know which one it got.
/// </summary>
public sealed class FileSearchResultDto
{
    /// <summary>The machine that was searched.</summary>
    public string Machine { get; set; } = "";

    /// <summary>The query that was run, echoed back.</summary>
    public string Query { get; set; } = "";

    /// <summary>The matching files.</summary>
    public List<FileHitDto> Files { get; set; } = new();

    /// <summary>The roots that were searched - the fixed drives on Windows, the home and system roots
    /// elsewhere.</summary>
    public List<string> Roots { get; set; } = new();

    /// <summary>How many directories were visited before the search ended.</summary>
    public int DirectoriesVisited { get; set; }

    /// <summary>How long the search actually ran.</summary>
    public long ElapsedMilliseconds { get; set; }

    /// <summary>True when the search stopped before exhausting the roots.</summary>
    public bool Truncated { get; set; }

    /// <summary>Why the search stopped early: "limit", "timeout", or null when it finished the whole walk.</summary>
    public string? TruncationReason { get; set; }

    /// <summary>How many directories were unreadable, almost always because of permissions. Counted rather
    /// than listed, because a whole-machine walk hits thousands of them and the list would dwarf the
    /// results.</summary>
    public int UnreadableDirectories { get; set; }
}
