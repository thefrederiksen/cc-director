using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the host-wide auth gate can be turned on without a code change via the CC_GATEWAY_AUTH
/// environment opt-in (issue #908), while the shipped default stays off (the tailnet is the boundary
/// until an operator opts in). The actual enforcement it unlocks - /sessions answering 401 without a
/// credential when auth is on - is proven separately by MobileAuthServingTests.
/// </summary>
public sealed class GatewayAuthToggleTests
{
    [Fact]
    public void Explicit_flag_enables_regardless_of_env()
    {
        Assert.True(GatewayHost.ResolveAuthEnabled(true));
    }

    [Fact]
    public void Env_opt_in_enables_when_the_flag_is_off()
    {
        var previous = Environment.GetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar, "1");
            Assert.True(GatewayHost.ResolveAuthEnabled(false));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar, previous);
        }
    }

    [Fact]
    public void Default_is_off_without_the_flag_or_env()
    {
        var previous = Environment.GetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar, null);
            Assert.False(GatewayHost.ResolveAuthEnabled(false));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar, previous);
        }
    }

    [Fact]
    public void Env_value_other_than_1_does_not_enable()
    {
        var previous = Environment.GetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar, "true");
            Assert.False(GatewayHost.ResolveAuthEnabled(false));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar, previous);
        }
    }
}
