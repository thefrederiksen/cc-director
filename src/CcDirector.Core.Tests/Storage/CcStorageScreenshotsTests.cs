using System.Text.Json;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// Tests for <see cref="CcStorage.Screenshots"/> - specifically that a test which redirects
/// CC_DIRECTOR_ROOT cannot write into the user's REAL Pictures\Screenshots folder.
///
/// The regression: the chunked upload-image proof set CC_DIRECTOR_ROOT to a temp dir and trusted that to
/// sandbox its 50 KB fake "photo.png". Screenshots() ignored the root, fell through to
/// MyPictures\Screenshots, and every run of that suite left an undrawable date-only entry in the owner's
/// screenshots gallery. Setting the root LOOKS like it sandboxes everything, so the next author will assume
/// it too - these tests make the assumption true and keep it true.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class CcStorageScreenshotsTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public CcStorageScreenshotsTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-shots-storage-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Screenshots_withRedirectedRoot_andNoConfig_staysInsideTheRoot()
    {
        var dir = CcStorage.Screenshots();

        Assert.StartsWith(_root, dir, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(dir), "Screenshots() must create the folder it returns");
    }

    [Fact]
    public void Screenshots_withRedirectedRoot_andNoConfig_neverReturnsTheUsersPicturesFolder()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var real = Path.Combine(pictures, "Screenshots");

        var dir = CcStorage.Screenshots();

        Assert.False(
            string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(real), StringComparison.OrdinalIgnoreCase),
            $"a redirected root must not resolve to the user's real screenshots folder, but got {dir}");
    }

    [Fact]
    public void Screenshots_configuredSourceDirectory_stillWins_overTheRoot()
    {
        // The product's own override: a user who points the gallery at a real folder keeps it, root or not.
        var configured = Path.Combine(_root, "a-configured-folder");
        WriteConfig(new { screenshots = new { source_directory = configured } });

        var dir = CcStorage.Screenshots();

        Assert.Equal(Path.TrimEndingDirectorySeparator(configured), Path.TrimEndingDirectorySeparator(dir));
    }

    private void WriteConfig(object config)
    {
        var configDir = Path.Combine(_root, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"), JsonSerializer.Serialize(config));
    }
}
