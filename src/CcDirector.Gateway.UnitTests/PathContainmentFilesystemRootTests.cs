using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tenant-boundary hardening, Phase 5b, independent inspection finding M03-I2B-04: the Phase 5a
/// containment fix introduced a REGRESSION for a session whose working directory is a filesystem
/// root.
///
/// <see cref="ControlEndpoints.ResolveSessionFile"/> trimmed the trailing separator off the root
/// before resolving it. That is harmless for an ordinary directory and destructive for a root: on
/// Windows <c>D:\</c> trimmed becomes <c>D:</c>, which is DRIVE-RELATIVE and resolves to that
/// drive's current directory rather than to the drive's root; on Unix <c>/</c> trimmed becomes the
/// empty string, which resolves to the process current directory. Either way the real-identity
/// comparison then ran against a completely different directory, and every file under the session's
/// actual working directory was refused.
///
/// The failure direction was fail-CLOSED, so nothing was disclosed - but a session with a
/// filesystem-root working directory served its files before this branch and could not after it,
/// and no test covered the case. These are that missing case.
///
/// The decisive test uses the root of the drive the TEST PROCESS runs on, because that drive is
/// guaranteed to have a per-drive current directory that is not the root (the test host's current
/// directory is the test binary's own folder). That is what makes the pre-fix behaviour observably
/// wrong rather than accidentally right: on a drive whose current directory happens to BE the root,
/// the trimmed form resolves back to the root and the defect hides.
/// </summary>
public sealed class PathContainmentFilesystemRootTests
{
    /// <summary>
    /// The root of the filesystem the test process is running on - "D:\" on Windows, "/" on Unix.
    /// </summary>
    private static string ProcessDriveRoot =>
        Path.GetPathRoot(Environment.CurrentDirectory)
        ?? throw new InvalidOperationException("the current directory has no path root");

    [Fact]
    public void ResolveSessionFile_workingDirectoryIsAFilesystemRoot_servesAPathBeneathIt()
    {
        var root = ProcessDriveRoot;

        // The premise this test rests on, asserted rather than assumed: the process current
        // directory is NOT the filesystem root, so the drive-relative spelling of the root
        // ("D:" on Windows, "" on Unix) resolves somewhere else entirely. Without this, the
        // pre-fix code would resolve the root correctly by accident and the test would prove
        // nothing about the defect it is named for.
        Assert.NotEqual(
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Environment.CurrentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // A path directly beneath the root and outside the current directory. It deliberately does
        // NOT exist: ResolveSessionFile decides containment, and the caller decides existence, so
        // creating a file at a drive root (which needs privilege the test host may not have) would
        // add a dependency that proves nothing extra.
        var candidate = Path.Combine(root, "ccd-fsroot-" + Guid.NewGuid().ToString("N"), "notes.txt");

        var resolved = ControlEndpoints.ResolveSessionFile(root, candidate);

        Assert.Equal(candidate, resolved);
    }

    [Fact]
    public void ResolveSessionFile_workingDirectoryIsAFilesystemRoot_stillRefusesAnotherFilesystem()
    {
        // The fix must widen the root back to the whole filesystem it names - not to EVERYTHING.
        //
        // Windows has one root per volume, so a path on another volume is genuinely outside a
        // filesystem-root working directory and must still be refused. Unix has exactly ONE root, so
        // there is no "other filesystem" to name in a path and every absolute path really is beneath
        // "/" - a session whose working directory is "/" is a session over the whole machine, which
        // is what its owner asked for. Both statements are asserted; neither host silently skips.
        var root = ProcessDriveRoot;
        var candidate = Path.Combine(
            OperatingSystem.IsWindows()
                ? (string.Equals(root, @"C:\", StringComparison.OrdinalIgnoreCase) ? @"Z:\" : @"C:\")
                : root,
            "ccd-fsroot-" + Guid.NewGuid().ToString("N"),
            "notes.txt");

        var resolved = ControlEndpoints.ResolveSessionFile(root, candidate);

        if (OperatingSystem.IsWindows())
            Assert.Null(resolved);
        else
            Assert.Equal(candidate, resolved);
    }

    [Fact]
    public void ResolveSessionFile_realFileBeneathAFilesystemRootWorkingDirectory_isServed()
    {
        // The same case with a file that genuinely exists on disk, so the resolution runs through
        // real filesystem components rather than the non-existent-suffix path.
        var temp = Path.Combine(Path.GetTempPath(), "ccd-fsroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var file = Path.Combine(temp, "notes.txt");
            File.WriteAllText(file, "beneath a filesystem-root working directory");

            var root = Path.GetPathRoot(file)
                       ?? throw new InvalidOperationException("the temp file has no path root");

            Assert.Equal(file, ControlEndpoints.ResolveSessionFile(root, file));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ListDirectory_allowedRootIsAFilesystemRoot_listsADirectoryBeneathIt()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ccd-fsroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "child"));
        try
        {
            var root = Path.GetPathRoot(temp)
                       ?? throw new InvalidOperationException("the temp directory has no path root");

            var listing = ControlEndpoints.ListDirectory(temp, new[] { root });

            Assert.Equal(temp, listing.CurrentPath);
            Assert.Contains(listing.Entries, e => e.Name == "child");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---------------------------------------------------------------- the pure decisions ----

    [Fact]
    public void NormalizeDirectoryPath_keepsTheSeparatorOnAFilesystemRoot()
    {
        var root = ProcessDriveRoot;
        Assert.Equal(root, ControlEndpoints.NormalizeDirectoryPath(root));
    }

    [Fact]
    public void NormalizeDirectoryPath_trimsAnOrdinaryDirectory()
    {
        var withSeparator = Path.Combine(ProcessDriveRoot, "repo") + Path.DirectorySeparatorChar;
        var expected = Path.Combine(ProcessDriveRoot, "repo");

        Assert.Equal(expected, ControlEndpoints.NormalizeDirectoryPath(withSeparator));
    }

    [Fact]
    public void ContainmentPrefix_doesNotDoubleTheSeparatorOnAFilesystemRoot()
    {
        var root = ProcessDriveRoot;

        var prefix = ControlEndpoints.ContainmentPrefix(root);

        Assert.Equal(root, prefix);
        Assert.True(Path.Combine(root, "anything").StartsWith(prefix, StringComparison.Ordinal),
            $"a path beneath the root must begin with the containment prefix; prefix was '{prefix}'");
    }

    [Fact]
    public void ContainmentPrefix_addsExactlyOneSeparatorToAnOrdinaryDirectory()
    {
        var dir = Path.Combine(ProcessDriveRoot, "repo");

        Assert.Equal(dir + Path.DirectorySeparatorChar, ControlEndpoints.ContainmentPrefix(dir));
    }
}
