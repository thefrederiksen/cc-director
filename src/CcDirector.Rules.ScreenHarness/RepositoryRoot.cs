namespace CcDirector.Rules.ScreenHarness;

/// <summary>
/// Where the repository is, found by walking up from the running program to the directory that holds
/// <c>cc-director.sln</c>. The harness and the corpus tests both locate the corpus this way, so a run from
/// a build output directory and a run from a test output directory read the same files.
/// </summary>
public static class RepositoryRoot
{
    /// <summary>The solution file that marks the root.</summary>
    public const string Marker = "cc-director.sln";

    /// <summary>The corpus directory, relative to the root.</summary>
    public const string CorpusRelativePath = "src/CcDirector.Rules.ScreenHarness/corpus";

    /// <summary>The default output directory, relative to the root. Git-ignored.</summary>
    public const string OutputRelativePath = "src/CcDirector.Rules.ScreenHarness/harness-out";

    /// <summary>Walk up from <paramref name="start"/> (the program's base directory when null) to the
    /// directory holding the solution file.</summary>
    /// <exception cref="DirectoryNotFoundException">No ancestor holds the solution file.</exception>
    public static string Find(string? start = null)
    {
        var from = start ?? AppContext.BaseDirectory;
        var dir = new DirectoryInfo(from);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Marker)))
            dir = dir.Parent;
        if (dir is null)
            throw new DirectoryNotFoundException(
                "no directory above " + from + " holds " + Marker +
                ", so the corpus cannot be located. Run from a build inside the repository, or pass --corpus.");
        return dir.FullName;
    }

    /// <summary>The corpus directory under the repository root.</summary>
    public static string DefaultCorpus() =>
        Path.Combine(Find(), CorpusRelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The output directory under the repository root.</summary>
    public static string DefaultOutput() =>
        Path.Combine(Find(), OutputRelativePath.Replace('/', Path.DirectorySeparatorChar));
}
