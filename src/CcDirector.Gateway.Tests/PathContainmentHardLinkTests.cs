using System.Runtime.InteropServices;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tenant-boundary hardening, Phase 5b, inspection finding M03-I2-01: HARD LINKS defeated the
/// Phase 5a containment fix completely.
///
/// Phase 5a resolved reparse points - symbolic links and directory junctions - and reported that the
/// containment decision was now made on "the real filesystem identity". It was not. A hard link is a
/// second directory entry for the SAME file object; it carries ordinary file attributes and no
/// reparse-point attribute at all. So an in-root name for an outside file resolved to itself, passed
/// both containment comparisons, and the read served the outside file. The independent inspector
/// executed this against the branch build and read a seeded secret through an in-root alias.
///
/// These tests create REAL hard links on disk - never a simulation - and prove refusal at BOTH
/// surfaces the inspection named: the session file read and the screenshot read. Each one first
/// proves the escape is genuine (the alias really does read the outside file's contents), so a
/// refusal cannot be mistaken for the link having failed to be created. A host that cannot create a
/// hard link FAILS these tests loudly with the reason; it never skips into a false green.
///
/// The false-positive direction is covered too: an ordinary single-named file inside the root must
/// still be served, or the fix would have closed the boundary by deleting the feature.
///
/// In the "DirectorRoot" collection: the screenshot tests redirect CC_DIRECTOR_ROOT to an isolated
/// temp root, which is process-global state.
/// </summary>
[Collection("DirectorRoot")]
public sealed class PathContainmentHardLinkTests : IDisposable
{
    private const string Secret = "hardlink-secret-only-reachable-outside-the-root";

    private readonly string _base;
    private readonly string? _prevRoot;
    private readonly string _root;     // the allowed root (a session working directory stand-in)
    private readonly string _outside;  // a sibling OUTSIDE the allowed root
    private readonly string _secret;   // the outside file a hard link will alias into the root

    public PathContainmentHardLinkTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _base = Path.Combine(Path.GetTempPath(), "ccd-hardlink-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _base);

        _root = Path.Combine(_base, "session-repo");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        _secret = Path.Combine(_outside, "gateway-token.txt");
        File.WriteAllText(_secret, Secret);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------- real hard-link creation ----

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr attributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int UnixLink(string existingPath, string newPath);

    /// <summary>
    /// Create a REAL hard link at <paramref name="alias"/> naming the same file object as
    /// <paramref name="target"/>. Needs no privilege on any supported platform, as long as both
    /// names are on one volume - which they are here, both being under one temp directory. If the
    /// link cannot be created the test FAILS with the reason; a security regression test that
    /// silently skips reads as coverage it does not provide.
    /// </summary>
    private static void CreateHardLink(string alias, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkW(alias, target, IntPtr.Zero))
            {
                Assert.Fail($"test setup: CreateHardLink('{alias}' -> '{target}') failed with Windows error " +
                            $"{Marshal.GetLastWin32Error()}. This test cannot run without a real hard link, " +
                            "and must not be skipped: the defect it covers serves an outside file through an " +
                            "in-root name.");
            }
        }
        else if (UnixLink(target, alias) != 0)
        {
            Assert.Fail($"test setup: link('{target}' -> '{alias}') failed with errno " +
                        $"{Marshal.GetLastWin32Error()}. This test cannot run without a real hard link, " +
                        "and must not be skipped.");
        }

        Assert.True(File.Exists(alias), $"test setup: the hard link '{alias}' was not created");
    }

    /// <summary>
    /// Prove the escape is REAL before asserting it is refused. Without this, a refusal could equally
    /// well mean the link was never created, which would make the whole test a false green.
    /// </summary>
    private void AssertTheAliasReallyReachesTheOutsideFile(string alias)
    {
        Assert.Equal(Secret, File.ReadAllText(alias));
        Assert.False(File.GetAttributes(alias).HasFlag(FileAttributes.ReparsePoint),
            "the point of this test is that a hard link carries NO reparse-point attribute - if it does, " +
            "the platform made a symbolic link and this test is covering the wrong mechanism");
    }

    // ----------------------------------------- ResolveSessionFile: hard links must not escape ----

    [Fact]
    public void ResolveSessionFile_hardLinkInsideTheRootNamingAnOutsideFile_isRefused()
    {
        var alias = Path.Combine(_root, "innocent.txt");
        CreateHardLink(alias, _secret);
        AssertTheAliasReallyReachesTheOutsideFile(alias);

        Assert.Null(ControlEndpoints.ResolveSessionFile(_root, alias));
    }

    [Fact]
    public void ResolveSessionFile_ordinaryFileInsideTheRoot_isStillServed()
    {
        // The other failure direction. Refusing every file would also refuse the hard link, and would
        // be a boundary closed by deleting the feature rather than by containing it.
        var ordinary = Path.Combine(_root, "notes.txt");
        File.WriteAllText(ordinary, "an ordinary file with exactly one name");

        var resolved = ControlEndpoints.ResolveSessionFile(_root, ordinary);

        Assert.NotNull(resolved);
        Assert.Equal("an ordinary file with exactly one name", File.ReadAllText(resolved!));
    }

    [Fact]
    public void ResolveSessionFile_hardLinkInsideTheRootNamingAnotherFileInsideTheRoot_isAlsoRefused()
    {
        // A hard link whose other name happens to be inside the root is still refused, and that is
        // deliberate rather than an oversight: nothing at this decision point can enumerate a file's
        // other names, so "the other name is also inside" is not a fact the code can establish. The
        // honest answer to a file with two names is that its containment cannot be proven.
        var real = Path.Combine(_root, "real.txt");
        File.WriteAllText(real, "inside the root");
        var alias = Path.Combine(_root, "alias.txt");
        CreateHardLink(alias, real);

        Assert.Null(ControlEndpoints.ResolveSessionFile(_root, alias));
        Assert.Null(ControlEndpoints.ResolveSessionFile(_root, real));
    }

    // ------------------------------------------- ResolveScreenshot: hard links must not escape ----

    private void PinScreenshotsFolder(string shotsDir)
    {
        Directory.CreateDirectory(shotsDir);
        Directory.CreateDirectory(CcStorage.Config());
        File.WriteAllText(CcStorage.ConfigJson(), JsonSerializer.Serialize(new
        {
            screenshots = new { source_directory = shotsDir },
        }));
    }

    [Fact]
    public void ResolveScreenshot_hardLinkPlantedInsideTheScreenshotsFolder_isRefused()
    {
        var shots = Path.Combine(_base, "shots");
        PinScreenshotsFolder(shots);
        var outsideImage = Path.Combine(_outside, "private.png");
        File.WriteAllText(outsideImage, Secret);

        var alias = Path.Combine(shots, "innocent.png");
        CreateHardLink(alias, outsideImage);
        Assert.Equal(Secret, File.ReadAllText(alias));

        // Bare name, allowed extension, legal lexical prefix, no reparse point to follow - every
        // check Phase 5a added passes, and the file is somewhere else entirely.
        Assert.Null(ControlEndpoints.ResolveScreenshot("innocent.png"));
    }

    [Fact]
    public void ResolveScreenshot_ordinaryImageInsideTheScreenshotsFolder_isStillServed()
    {
        var shots = Path.Combine(_base, "shots-ok");
        PinScreenshotsFolder(shots);
        var image = Path.Combine(shots, "capture.png");
        File.WriteAllText(image, "an ordinary screenshot");

        var resolved = ControlEndpoints.ResolveScreenshot("capture.png");

        Assert.NotNull(resolved);
        Assert.Equal(image, resolved);
    }

    // ------------------------------------------------------ the identity primitive itself ----

    [Fact]
    public void NameCount_ordinaryFile_isOne()
    {
        var file = Path.Combine(_root, "one-name.txt");
        File.WriteAllText(file, "x");

        Assert.Equal(1, FilesystemIdentity.NameCount(file));
        Assert.True(FilesystemIdentity.HasExactlyOneName(file));
    }

    [Fact]
    public void NameCount_hardLinkedFile_isTwo()
    {
        var file = Path.Combine(_root, "two-names.txt");
        File.WriteAllText(file, "x");
        CreateHardLink(Path.Combine(_root, "the-other-name.txt"), file);

        Assert.Equal(2, FilesystemIdentity.NameCount(file));
        Assert.False(FilesystemIdentity.HasExactlyOneName(file));
    }

    [Fact]
    public void NameCount_fileThatDoesNotExist_isUndeterminableRatherThanOne()
    {
        // The contract callers depend on: "I could not tell" is null, never a confident 1.
        Assert.Null(FilesystemIdentity.NameCount(Path.Combine(_root, "no-such-file.txt")));
        Assert.Null(FilesystemIdentity.HasExactlyOneName(Path.Combine(_root, "no-such-file.txt")));
    }

    // -------------------------------------------------------- canonical spelling and case ----

    [Fact]
    public void CanonicalPath_foldsARequestedSpellingOntoTheRealOnDiskName()
    {
        // The second half of M03-I2-01. On Windows the containment comparison used to be
        // unconditionally case-insensitive, which is wrong on a directory carrying the NTFS
        // per-directory case-sensitive flag. The fix folds both sides onto the filesystem's own
        // spelling and compares ordinally, so neither case rule has to be guessed at.
        var real = Path.Combine(_root, "MixedCaseDir");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "File.txt"), "x");

        var shouted = Path.Combine(_root, "MIXEDCASEDIR", "FILE.TXT");
        var canonical = FilesystemIdentity.CanonicalPath(shouted);

        if (OperatingSystem.IsWindows())
        {
            // Compare the tail rather than the whole path: the canonical form of the temp directory
            // above it is the filesystem's business, and the property under test is that the two
            // shouted components fold back onto their real spelling.
            Assert.NotNull(canonical);
            Assert.EndsWith(Path.Combine("MixedCaseDir", "File.txt"), canonical!, StringComparison.Ordinal);
        }
        else
        {
            // On Unix the path is already the identity - there is no case folding to do, and the
            // shouted spelling simply names nothing.
            Assert.Equal(shouted, canonical);
        }
    }

    [Fact]
    public void ResolveSessionFile_aDifferentlyCasedSpellingOfALegalFile_isStillServedOnACaseInsensitiveHost()
    {
        // The fix must not refuse a legal request just because the caller shouted it. On a
        // case-INSENSITIVE filesystem the shouted spelling folds onto the one real name and resolves;
        // on a case-SENSITIVE one it names nothing and the caller gets an ordinary not-found.
        var real = Path.Combine(_root, "CasedFile.txt");
        File.WriteAllText(real, "cased-content");
        var shouted = Path.Combine(_root, "CASEDFILE.TXT");

        var resolved = ControlEndpoints.ResolveSessionFile(_root, shouted);

        if (File.Exists(shouted))
        {
            Assert.NotNull(resolved);
            Assert.Equal("cased-content", File.ReadAllText(resolved!));
        }
        else
        {
            // A case-sensitive host: nothing is there, and containment answers on the name alone.
            Assert.Equal(shouted, resolved);
        }
    }

    [Fact]
    public void CanonicalPath_pathWhoseTailDoesNotExist_keepsTheTailAndFoldsTheRest()
    {
        var real = Path.Combine(_root, "RealDir");
        Directory.CreateDirectory(real);

        var canonical = FilesystemIdentity.CanonicalPath(Path.Combine(_root, "RealDir", "missing", "x.txt"));

        Assert.NotNull(canonical);
        Assert.EndsWith(Path.Combine("RealDir", "missing", "x.txt"), canonical!, StringComparison.Ordinal);
    }

    // ------------------------------------- the Unix field search, exercised on any machine ----

    [Fact]
    public void LocateCountingField_findsThePositionThatCountsOneTwoThree()
    {
        // The Unix half of NameCount cannot hard-code a struct offset: libc's "struct stat" differs by
        // operating system, architecture and entry point, and a wrong offset would silently read some
        // other field. It locates the link count empirically instead - stat, add a name, stat, add
        // another, stat - and keeps every buffer position that counted 1, 2, 3. That search is the
        // part that decides, so it is exercised here on synthetic buffers, on every build machine,
        // including this Windows one where the interop around it never runs.
        var first = new byte[64];
        var second = new byte[64];
        var third = new byte[64];
        // Noise everywhere else, so a position only survives by genuinely counting.
        for (var i = 0; i < 64; i++) { first[i] = 0xAB; second[i] = 0xCD; third[i] = 0xEF; }
        BitConverter.GetBytes((ulong)1).CopyTo(first, 16);
        BitConverter.GetBytes((ulong)2).CopyTo(second, 16);
        BitConverter.GetBytes((ulong)3).CopyTo(third, 16);

        var found = FilesystemIdentity.LocateCountingField(first, second, third);

        Assert.Contains((16, 8), found);
        // Every surviving position must agree with the real one - that is the property UnixNameCount
        // relies on when it refuses on disagreement.
        Assert.All(found, f => Assert.Equal(16, f.Offset));
    }

    [Fact]
    public void LocateCountingField_findsNothingWhenNoPositionCounts()
    {
        var first = new byte[64];
        var second = new byte[64];
        var third = new byte[64];
        for (var i = 0; i < 64; i++) { first[i] = 0x11; second[i] = 0x22; third[i] = 0x33; }

        Assert.Empty(FilesystemIdentity.LocateCountingField(first, second, third));
    }
}
