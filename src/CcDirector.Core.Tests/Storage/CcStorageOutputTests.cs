using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// Tests for <see cref="CcStorage.Output"/> honoring CC_DIRECTOR_ROOT. Output() resolved the user's real
/// Documents\cc-director and ignored the override - the last path in CcStorage that did, after Bin() and
/// Screenshots() were fixed the same way. No test reached it (only QuickActionService calls it, and
/// nothing tests that), so it was latent rather than leaking; these tests keep it that way.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class CcStorageOutputTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public CcStorageOutputTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-output-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Output_withRedirectedRoot_staysInsideTheRoot()
    {
        var dir = CcStorage.Output();

        Assert.StartsWith(_root, dir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Output_withRedirectedRoot_neverReturnsTheUsersDocumentsFolder()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "cc-director");

        var dir = CcStorage.Output();

        Assert.False(
            string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(real), StringComparison.OrdinalIgnoreCase),
            $"a redirected root must not resolve to the user's real Documents folder, but got {dir}");
    }

    [Fact]
    public void ToolOutput_withRedirectedRoot_staysInsideTheRoot()
    {
        // The reachable one: QuickActionService's constructor calls CcStorage.Ensure(ToolOutput(...)),
        // so the first test to touch that class would otherwise create a folder in the real Documents.
        var dir = CcStorage.ToolOutput("quick-actions");

        Assert.StartsWith(_root, dir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Output_withNoRoot_isTheUsersDocumentsFolder()
    {
        // The product never sets CC_DIRECTOR_ROOT, so its location must not move.
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", null);
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "cc-director");

        var dir = CcStorage.Output();

        Assert.Equal(Path.TrimEndingDirectorySeparator(expected), Path.TrimEndingDirectorySeparator(dir));
    }
}
