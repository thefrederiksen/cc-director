using System;
using System.IO;
using CcDirector.Gateway.Diagnostics;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway's reader for the build stamp a served web bundle ships with (wwwroot/{c,mobile}/build.json).
/// Deterministic: every case writes its own temporary bundle root, so none of these depends on whether the
/// build configuration happened to stage a real bundle.
///
/// This is the only server-side source for "which Cockpit / mobile app am I serving": each bundle's commit
/// is otherwise compiled into JavaScript the Gateway never executes.
/// </summary>
public sealed class BundleStampTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bundle-stamp-" + Guid.NewGuid().ToString("N"));

    public BundleStampTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void WriteStamp(string json) => File.WriteAllText(Path.Combine(_root, BundleStamp.FileName), json);

    [Fact]
    public void Read_StampedBundle_ReturnsCommitAndBuildTime()
    {
        WriteStamp("""{ "commit": "a1b2c3d", "buildTime": "2026-07-26T09:38:49.000Z" }""");

        var stamp = BundleStamp.Read(_root);

        Assert.NotNull(stamp);
        Assert.Equal("a1b2c3d", stamp!.Commit);
        Assert.Equal(new DateTime(2026, 7, 26, 9, 38, 49, DateTimeKind.Utc), stamp.BuildTime!.Value.ToUniversalTime());
    }

    [Fact]
    public void Read_NoBundleStaged_ReturnsNull()
    {
        // A routine Debug build does not build the web apps at all, so there is no bundle and no stamp. Null is
        // the honest answer; the About page renders it as "(not built into this Gateway)".
        Assert.Null(BundleStamp.Read(_root));
    }

    [Fact]
    public void Read_MissingWebRoot_ReturnsNull()
    {
        Assert.Null(BundleStamp.Read(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void Read_CorruptStamp_ReturnsNull()
    {
        // A truncated / non-JSON stamp is a broken deploy. It must not throw (that would take down the whole
        // About payload, hiding the Gateway version too) and must not invent a commit.
        WriteStamp("{ this is not json");

        Assert.Null(BundleStamp.Read(_root));
    }

    [Fact]
    public void Read_StampWithoutCommit_ReturnsNull()
    {
        // The quiet failure this guards: a stamp object whose commit is empty would otherwise surface as a
        // stamp that names nothing, and the page would print a blank build as though it were a real one.
        WriteStamp("""{ "commit": "   ", "buildTime": "2026-07-26T09:38:49.000Z" }""");

        Assert.Null(BundleStamp.Read(_root));
    }

    [Fact]
    public void Read_StampWithoutBuildTime_ReturnsCommitOnly()
    {
        // The commit alone still identifies the build, so it serves with a null time rather than being
        // discarded wholesale.
        WriteStamp("""{ "commit": "deadbee" }""");

        var stamp = BundleStamp.Read(_root);

        Assert.NotNull(stamp);
        Assert.Equal("deadbee", stamp!.Commit);
        Assert.Null(stamp.BuildTime);
    }
}
