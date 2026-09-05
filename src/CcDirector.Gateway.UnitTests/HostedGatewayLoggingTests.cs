using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The hosted Gateway's process log is diagnostic output, not durable product state. Issue #2678 found that
/// keeping it below the Gateway storage root made it most of the Azure Files traffic and let an unrelated
/// reader break the active file's write lease. These tests pin the location boundary, not a cleanup policy.
/// </summary>
public sealed class HostedGatewayLoggingTests
{
    [Fact]
    public void Hosted_process_log_lives_below_the_container_temporary_root()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "hosted-log-root");

        var directory = GatewayEntryPoint.ResolveHostedLogDirectory(temporaryRoot);

        Assert.Equal(
            Path.Combine(Path.GetFullPath(temporaryRoot), "devthrottle", "logs", "director"),
            directory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Hosted_process_log_refuses_an_absent_temporary_root(string temporaryRoot)
    {
        Assert.Throws<ArgumentException>(() => GatewayEntryPoint.ResolveHostedLogDirectory(temporaryRoot));
    }
}
