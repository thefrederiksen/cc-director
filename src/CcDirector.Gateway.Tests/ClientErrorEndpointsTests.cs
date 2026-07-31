using System.Net.Http.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The client error channel's per-device rate gate (client error logging build): a browser error loop
/// must not flood the Gateway log, and one device's flood must never gag another device's reports.
///
/// Also the durable-line content gate (CR-3b review, third and fourth passes): every field of the POST
/// body is client-supplied free text, and a browser error can carry anything on the page - including
/// prompt or dictation content - so the data map's promise that service logs never carry customer
/// content is only true if NO client-controlled field reaches the durable log verbatim. The tests here
/// post hostile content into every field and assert none of it lands in FileLog.
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

    // ---- The durable line never carries client-supplied free text ---------------------------------

    private const string Sensitive = "the password is hunter2 and the prompt said fix login";

    [Fact]
    public void DurableLine_reduces_every_client_controlled_field_to_structure()
    {
        var record = new ClientErrorEndpoints.ClientErrorRecord(
            AtUtc: DateTime.UtcNow,
            DeviceHash: "abcdef1234567890",
            Surface: Sensitive,
            Page: Sensitive,
            Message: Sensitive,
            Detail: Sensitive,
            Stack: Sensitive);

        var line = ClientErrorEndpoints.DurableLine(new TenantId("11111111-1111-1111-1111-111111111111"), record);

        Assert.DoesNotContain("hunter2", line);
        Assert.DoesNotContain("prompt said", line);
        // The raw account tenant id must not appear either - only its one-way hash form.
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", line);
        Assert.Contains("t#", line);
        Assert.Contains($"messageLength={Sensitive.Length}", line);
    }

    /// <summary>The gate has two failure directions: a genuinely structural value must still be logged,
    /// or the line stops being useful and someone reverts the gate.</summary>
    [Fact]
    public void StructuralToken_admits_route_shapes_and_refuses_prose()
    {
        Assert.Equal("/m/sessions/list", ClientErrorEndpoints.StructuralToken("/m/sessions/list", 200));
        Assert.Equal("cockpit", ClientErrorEndpoints.StructuralToken("cockpit", 40));
        Assert.Equal("(empty)", ClientErrorEndpoints.StructuralToken("", 40));
        Assert.Equal($"(nonstructural:{Sensitive.Length})", ClientErrorEndpoints.StructuralToken(Sensitive, 200));
        Assert.Equal("(nonstructural:9)", ClientErrorEndpoints.StructuralToken("a\"quote\"b", 40));
        Assert.StartsWith("(overlong:", ClientErrorEndpoints.StructuralToken(new string('a', 300), 200));
    }

    /// <summary>
    /// The reviewer's exact demand (CR-3b, fourth pass): post sensitive strings in EVERY
    /// client-controlled field over the real HTTP pipeline and assert none reaches FileLog.
    /// </summary>
    [Fact]
    public async Task A_hostile_report_leaves_no_customer_content_in_the_durable_log()
    {
        using var logScope = FileLog.RedirectForTests();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        ClientErrorEndpoints.Map(app, tenantBoundary: null!);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var resp = await client.PostAsJsonAsync("/client-errors", new ClientErrorEndpoints.ClientErrorPost
            {
                Surface = Sensitive,
                Page = Sensitive,
                Message = Sensitive,
                Detail = Sensitive,
                Stack = Sensitive,
            });
            resp.EnsureSuccessStatusCode();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        var lines = logScope.DrainAndReadLines();
        var durable = lines.Where(l => l.Contains("[ClientError]")).ToList();
        Assert.NotEmpty(durable);
        Assert.All(lines, l =>
        {
            Assert.DoesNotContain("hunter2", l);
            Assert.DoesNotContain("prompt said", l);
        });
        Assert.Contains(durable, l => l.Contains($"messageLength={Sensitive.Length}"));
    }
}
