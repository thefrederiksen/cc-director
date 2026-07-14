using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class InstalledStateReaderTests
{
    [Fact]
    public void DefaultExists_File_True()
    {
        var file = Path.Combine(Path.GetTempPath(), $"reader-test-{Guid.NewGuid():N}.bin");
        File.WriteAllText(file, "x");
        try
        {
            Assert.True(InstalledStateReader.DefaultExists(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void DefaultExists_Directory_True()
    {
        // The macOS Director installs as a .app BUNDLE, which is a directory. Presence
        // detection must accept it; File.Exists alone reported it "not installed" (issue #1445).
        var dir = Path.Combine(Path.GetTempPath(), $"reader-test-{Guid.NewGuid():N}.app");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(InstalledStateReader.DefaultExists(dir));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void DefaultExists_Missing_False()
    {
        Assert.False(InstalledStateReader.DefaultExists(
            Path.Combine(Path.GetTempPath(), $"reader-test-{Guid.NewGuid():N}")));
    }
}
