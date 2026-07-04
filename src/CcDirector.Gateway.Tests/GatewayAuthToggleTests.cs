using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the host-wide auth gate now enforces BY DEFAULT (issue #917, Phase 1 of the security epic
/// #916): with no explicit constructor choice and no disable override, the gate is ON. A disable
/// override (CC_GATEWAY_NO_AUTH=1 or CC_GATEWAY_AUTH=0) turns it off for debugging, and an explicit
/// constructor choice always wins so tests can force the gate on or off deterministically. The actual
/// enforcement it unlocks - /sessions answering 401 without a credential when auth is on - is proven
/// separately by MobileAuthServingTests.
/// </summary>
public sealed class GatewayAuthToggleTests
{
    [Fact]
    public void Default_is_on_without_any_override()
    {
        var previousDisabled = Environment.GetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar);
        var previousEnabled = Environment.GetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar, null);
            Environment.SetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar, null);
            Assert.True(GatewayHost.ResolveAuthEnabled(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar, previousDisabled);
            Environment.SetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar, previousEnabled);
        }
    }

    [Fact]
    public void Explicit_false_forces_off_regardless_of_env()
    {
        var previousDisabled = Environment.GetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar);
        try
        {
            // Even with no disable override, an explicit false wins - this is how a test boots a Gateway
            // with the gate deliberately off.
            Environment.SetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar, null);
            Assert.False(GatewayHost.ResolveAuthEnabled(false));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar, previousDisabled);
        }
    }

    [Fact]
    public void Explicit_true_forces_on_regardless_of_disable_override()
    {
        var previousDisabled = Environment.GetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar);
        try
        {
            // An explicit true wins even when the disable override is set - the constructor choice is
            // authoritative so tests are not perturbed by a stray env var on the build machine.
            Environment.SetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar, "1");
            Assert.True(GatewayHost.ResolveAuthEnabled(true));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar, previousDisabled);
        }
    }

    [Fact]
    public void Disable_override_no_auth_turns_the_default_off()
    {
        var previousDisabled = Environment.GetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar, "1");
            Assert.False(GatewayHost.ResolveAuthEnabled(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar, previousDisabled);
        }
    }

    [Fact]
    public void Disable_override_auth_zero_turns_the_default_off()
    {
        var previousDisabled = Environment.GetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar);
        var previousEnabled = Environment.GetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar, null);
            Environment.SetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar, "0");
            Assert.False(GatewayHost.ResolveAuthEnabled(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar, previousDisabled);
            Environment.SetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar, previousEnabled);
        }
    }

    [Fact]
    public void Disable_override_value_other_than_1_does_not_disable()
    {
        var previousDisabled = Environment.GetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar);
        var previousEnabled = Environment.GetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar);
        try
        {
            // Only the exact disable tokens turn the gate off; anything else leaves the default (ON).
            Environment.SetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar, "true");
            Environment.SetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar, "1");
            Assert.True(GatewayHost.ResolveAuthEnabled(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayHost.AuthDisabledEnvVar, previousDisabled);
            Environment.SetEnvironmentVariable(GatewayHost.AuthEnabledEnvVar, previousEnabled);
        }
    }
}
