using System.Globalization;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the machine-wide suite lock is actually HELD while this assembly's tests run.
///
/// This exists for the same reason <see cref="TestStorageRootRedirectTests"/> does: a module initializer
/// that silently does not run leaves no trace at all. The suite would go straight back to letting two runs
/// overlap and corrupt each other, and every test here would still pass. A guard whose failure is invisible
/// is not coverage, so the guard gets a guard.
/// </summary>
public sealed class GatewayTestSuiteLockTests
{
    [Fact]
    public void TheLockIsHeldWhileTestsRun()
    {
        Assert.True(
            GatewayTestSuiteLock.IsHeld,
            "This run does not hold the machine-wide Gateway test lock, so nothing is stopping a second "
            + "run of this suite from executing alongside it and corrupting both. See GatewayTestSuiteLock.");
    }

    [Fact]
    public void ASecondExclusiveOpenIsRefused_WhichIsWhatBlocksAConcurrentRun()
    {
        // The exact open a second run would attempt. Share modes are per-handle, not per-process, so this
        // is refused inside the holding process too - which makes it a real proof of exclusivity rather
        // than a restatement of a flag we set ourselves.
        var refused = Assert.ThrowsAny<IOException>(() =>
        {
            using var _ = new FileStream(
                GatewayTestSuiteLock.LockFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        });

        Assert.NotNull(refused);
    }

    [Fact]
    public void TheLockFileNamesThisProcess_SoABlockedRunCanSayWhoIsBlockingIt()
    {
        // Read exactly the way a blocked run reads it: read-only, sharing everything, while the holder
        // still has the file open for writing.
        using var stream = new FileStream(
            GatewayTestSuiteLock.LockFilePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        Assert.Contains(
            "processId=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            text,
            StringComparison.Ordinal);
        Assert.Contains("acquiredUtc=", text, StringComparison.Ordinal);
        Assert.Contains("session=", text, StringComparison.Ordinal);
    }
}
