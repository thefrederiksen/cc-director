using CcDirector.Gateway.Account;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Proves the Gateway's stable install identity (issue #857): <see cref="GatewayInstallId.LoadOrCreate()"/>
/// mints a GUID once, persists it, and returns the SAME value on every later call - the idempotency anchor
/// that keeps the cloud from creating a new device record on each launch. A malformed file regenerates.
/// </summary>
public sealed class GatewayInstallIdTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "cc-gw-install-id-" + Guid.NewGuid().ToString("N"), GatewayInstallId.FileName);

    [Fact]
    public void LoadOrCreate_MintsOnce_PersistsAndReusesTheSameId()
    {
        var path = TempPath();
        try
        {
            var first = new GatewayInstallId(path).LoadOrCreate();
            Assert.True(Guid.TryParse(first, out _), "the minted id must be a GUID");
            Assert.True(File.Exists(path));

            var second = new GatewayInstallId(path).LoadOrCreate();
            Assert.Equal(first, second);
        }
        finally
        {
            TryDeleteDir(path);
        }
    }

    [Fact]
    public void LoadOrCreate_MalformedFile_RegeneratesAValidId()
    {
        var path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "not-a-guid");

            var id = new GatewayInstallId(path).LoadOrCreate();
            Assert.True(Guid.TryParse(id, out _));
            Assert.Equal(id, File.ReadAllText(path).Trim());
        }
        finally
        {
            TryDeleteDir(path);
        }
    }

    private static void TryDeleteDir(string filePath)
    {
        try { Directory.Delete(Path.GetDirectoryName(filePath)!, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
