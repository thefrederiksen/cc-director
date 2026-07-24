using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The client error channel's per-device rate gate (client error logging build): a browser error loop
/// must not flood the Gateway log, and one device's flood must never gag another device's reports.
/// </summary>
public sealed class ClientErrorEndpointsTests
{
    [Fact]
    public void AdmitWithinRate_CapsOneDevice_PerMinute()
    {
        var device = "device-" + Guid.NewGuid().ToString("N");
        var admitted = 0;
        for (var i = 0; i < 100; i++)
        {
            if (ClientErrorEndpoints.AdmitWithinRate(device)) admitted++;
        }
        Assert.Equal(30, admitted);
    }

    [Fact]
    public void AdmitWithinRate_DistinctDevices_AreIndependent()
    {
        var deviceA = "device-" + Guid.NewGuid().ToString("N");
        var deviceB = "device-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 100; i++) ClientErrorEndpoints.AdmitWithinRate(deviceA);
        Assert.True(ClientErrorEndpoints.AdmitWithinRate(deviceB));
    }
}
