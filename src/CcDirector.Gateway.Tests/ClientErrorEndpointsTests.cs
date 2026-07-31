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

    // Hostile values that fit ANY identifier/route alphabet - the fifth review pass's point: a
    // character filter cannot tell these from genuine route tokens, so nothing client-supplied may
    // be logged verbatim at all.
    private const string SensitiveToken = "hunter2";
    private const string SensitivePath = "/prompt/api_key_sk-abc123/fix-login";

    [Theory]
    [InlineData(Sensitive)]
    [InlineData(SensitiveToken)]
    [InlineData(SensitivePath)]
    public void DurableLine_never_carries_a_client_controlled_value_verbatim(string hostile)
    {
        var record = new ClientErrorEndpoints.ClientErrorRecord(
            AtUtc: DateTime.UtcNow,
            DeviceHash: "abcdef1234567890",
            Surface: hostile,
            Page: hostile,
            Message: hostile,
            Detail: hostile,
            Stack: hostile);

        var line = ClientErrorEndpoints.DurableLine(new TenantId("11111111-1111-1111-1111-111111111111"), record);

        Assert.DoesNotContain(hostile, line);
        // The raw account tenant id must not appear either - only its one-way hash form.
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", line);
        Assert.Contains("t#", line);
        Assert.Contains($"messageLength={hostile.Length}", line);
    }

    /// <summary>The tag must stay CORRELATABLE (same value, same tag; different values, different
    /// tags) or the line stops being useful for debugging and someone reverts the gate.</summary>
    [Fact]
    public void HashTag_is_content_free_but_correlatable()
    {
        var a1 = ClientErrorEndpoints.HashTag("/m/sessions/list");
        var a2 = ClientErrorEndpoints.HashTag("/m/sessions/list");
        var b = ClientErrorEndpoints.HashTag("/m/settings");

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.StartsWith("h#", a1);
        Assert.DoesNotContain("sessions", a1);
        Assert.Equal("(empty)", ClientErrorEndpoints.HashTag(""));
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
            // Surface and Page get the alphabet-fitting hostile values on purpose: they would sail
            // through any character filter, and the promise is that they still never reach the log.
            var resp = await client.PostAsJsonAsync("/client-errors", new ClientErrorEndpoints.ClientErrorPost
            {
                Surface = SensitiveToken,
                Page = SensitivePath,
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
            Assert.DoesNotContain(SensitiveToken, l);
            Assert.DoesNotContain(SensitivePath, l);
            Assert.DoesNotContain("api_key_sk-abc123", l);
            Assert.DoesNotContain("prompt said", l);
        });
        Assert.Contains(durable, l => l.Contains($"messageLength={Sensitive.Length}"));
    }
}
