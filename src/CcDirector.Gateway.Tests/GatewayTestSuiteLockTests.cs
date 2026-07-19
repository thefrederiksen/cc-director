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

    /// <summary>
    /// The regression test for a DEMONSTRATED defect, not a hypothesis. The lock path was originally derived
    /// from <see cref="Path.GetTempPath"/>, which reads TEMP and TMP from the process environment. Two runs
    /// launched with different TEMP values computed two different lock files, so neither could see the
    /// other: both reported acquiring the lock in the same second and both ran concurrently. The whole
    /// mechanism was inert one environment variable away from its intended use.
    ///
    /// This asserts the property that closes it - the lock's identity does not move when the environment
    /// moves - on whatever platform the tests run on, including platforms the author could not reach.
    /// </summary>
    [Fact]
    public void TheLockPathDoesNotMoveWhenTheTemporaryDirectoryEnvironmentMoves()
    {
        var original = GatewayTestSuiteLock.ComputeLockFilePath();
        var names = new[] { "TEMP", "TMP", "TMPDIR" };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);

        try
        {
            foreach (var name in names)
                Environment.SetEnvironmentVariable(name, Path.Combine(Path.GetTempPath(), "moved-" + name));

            Assert.Equal(original, GatewayTestSuiteLock.ComputeLockFilePath());
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>
    /// The end-to-end half of the same defect, against the REAL lock this run is really holding.
    ///
    /// A second run launched with a different TEMP would compute its lock path, then open it. This does
    /// exactly that - recomputes the path under a changed environment, then attempts the same exclusive
    /// open a second run would attempt - and requires it to be REFUSED. Under the defect the recomputed
    /// path was a different file and the open succeeded, which is precisely how two suites ran side by
    /// side.
    /// </summary>
    [Fact]
    public void ARunWithADifferentTemporaryDirectoryStillCollidesWithThisHeldLock()
    {
        var names = new[] { "TEMP", "TMP", "TMPDIR" };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        var elsewhere = Path.Combine(Path.GetTempPath(), "another-runs-temp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);

        try
        {
            foreach (var name in names)
                Environment.SetEnvironmentVariable(name, elsewhere);

            var asAnotherRunWouldComputeIt = GatewayTestSuiteLock.ComputeLockFilePath();

            // Same file this run holds - that is the property being pinned.
            Assert.Equal(GatewayTestSuiteLock.LockFilePath, asAnotherRunWouldComputeIt);

            Assert.ThrowsAny<IOException>(() =>
            {
                using var _ = new FileStream(
                    asAnotherRunWouldComputeIt, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            });
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(elsewhere, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void TheLockDoesNotLiveUnderTheEnvironmentSettableTemporaryDirectory()
    {
        // Stated as its own fact because it is the shape of the defect: anything under the temporary
        // directory is relocatable by whoever launches the run, and a relocatable lock is not a lock.
        Assert.False(
            GatewayTestSuiteLock.LockFilePath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            $"The lock lives at {GatewayTestSuiteLock.LockFilePath}, under the environment-settable "
            + "temporary directory, so two runs with different TEMP values would not see each other.");
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
