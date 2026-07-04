using System;
using CcDirector.Gateway.Tray;
using Xunit;

namespace CcDirector.Gateway.Tests.Tray;

/// <summary>
/// Tests the Gateway tray restart-feedback string builder (issue #927): the pure wording the tray
/// tooltip and flyout show after a "Restart Gateway" click so the user can tell the in-process
/// restart took effect (the PID never changes). Proves the SUCCESS strings include the running
/// version and the time, and the FAILURE strings are distinct and carry the reason - all without an
/// Avalonia UI thread.
/// </summary>
public sealed class GatewayRestartFeedbackTests
{
    private static readonly DateTime SampleTime = new(2026, 7, 4, 14, 32, 5, DateTimeKind.Local);

    [Fact]
    public void SuccessStatus_IncludesVersionAndTime()
    {
        // Act
        var status = GatewayRestartFeedback.SuccessStatus("0.9.32", SampleTime);

        // Assert - the user must see it succeeded, which build is live, and when.
        Assert.Equal("Restarted OK - v0.9.32 at 14:32:05", status);
    }

    [Fact]
    public void SuccessTooltip_IsDistinctAndIncludesVersion()
    {
        // Act
        var tooltip = GatewayRestartFeedback.SuccessTooltip("0.9.32", SampleTime);

        // Assert - distinct from the normal "running on :port" tooltip, and names the running version.
        Assert.Equal("DevThrottle Gateway - restarted OK (v0.9.32) at 14:32:05", tooltip);
        Assert.Contains("v0.9.32", tooltip);
    }

    [Fact]
    public void FailureStatus_CarriesReason_AndIsNotSuccess()
    {
        // Act
        var status = GatewayRestartFeedback.FailureStatus("Port 7900 in use by another app");

        // Assert - never a silent no-op: the failure and its reason are surfaced.
        Assert.Equal("Restart FAILED - Port 7900 in use by another app", status);
        Assert.DoesNotContain("OK", status);
    }

    [Fact]
    public void FailureTooltip_IsDistinctFromSuccessTooltip()
    {
        // Act
        var failure = GatewayRestartFeedback.FailureTooltip("Port 7900 in use by another app");
        var success = GatewayRestartFeedback.SuccessTooltip("0.9.32", SampleTime);

        // Assert - the success and failure tooltips can never be confused for one another.
        Assert.Equal("DevThrottle Gateway - RESTART FAILED: Port 7900 in use by another app", failure);
        Assert.NotEqual(success, failure);
    }

    [Fact]
    public void SuccessStatus_WithPrereleaseVersion_KeepsFullVersion()
    {
        // Act - a semver with a prerelease suffix must survive verbatim (no truncation).
        var status = GatewayRestartFeedback.SuccessStatus("1.0.0-rc1", SampleTime);

        // Assert
        Assert.Equal("Restarted OK - v1.0.0-rc1 at 14:32:05", status);
    }
}
