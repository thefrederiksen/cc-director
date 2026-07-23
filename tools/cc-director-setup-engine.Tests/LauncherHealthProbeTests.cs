using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>The health answer's IDENTITY rules (issue #2042): liveness alone never certifies an
/// install when the expected version is known.</summary>
public class LauncherHealthProbeTests
{
    [Fact]
    public void Parse_WellFormedAnswer_ReadsIdentity()
    {
        var h = LauncherHealthProbe.Parse("""{"ok":true,"version":"1.7.4+abc","pid":77,"uptimeS":3}""");
        Assert.True(h.Ok);
        Assert.Equal("1.7.4+abc", h.Version);
        Assert.Equal(77, h.Pid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"ok\":false}")]
    public void Parse_GarbageOrNotOk_NeverOk(string body) =>
        Assert.False(LauncherHealthProbe.Parse(body).Ok);

    [Theory]
    [InlineData("1.7.4", "1.7.4", true)]
    [InlineData("1.7.4", "1.7.4+d26a09", true)]     // build metadata ignored
    [InlineData("v1.7.4", "1.7.4.0", true)]          // normalized forms match
    [InlineData("1.7.4", "1.7.1", false)]            // the issue #2042 incident shape
    [InlineData("1.7.4", null, false)]               // expected but nothing reported
    [InlineData(null, "9.9.9", true)]                // nothing expected: cannot check, accept
    public void VersionMatches_ComparesNormalizedAndIgnoresBuildMetadata(string? expected, string? reported, bool matches) =>
        Assert.Equal(matches, LauncherHealthProbe.VersionMatches(expected, reported));

    [Fact]
    public void Certifies_RequiresOkAndIdentity()
    {
        Assert.True(LauncherHealthProbe.Certifies(new LauncherHealth(true, "1.7.4", 1), "1.7.4"));
        Assert.False(LauncherHealthProbe.Certifies(new LauncherHealth(true, "1.7.1", 1), "1.7.4"));
        Assert.False(LauncherHealthProbe.Certifies(new LauncherHealth(false, "1.7.4", 1), "1.7.4"));
        Assert.False(LauncherHealthProbe.Certifies(null, "1.7.4"));
    }
}
