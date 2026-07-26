using System.Net;
using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The recipient list for the daily report.
///
/// This endpoint returns EVERY account's email address, which makes it more sensitive than any single
/// report, so the tests that matter are the refusals. It must be impossible to reach it more easily
/// than the report itself: unconfigured token is 503 and never an open door, wrong token is 401, and
/// neither leaks a single address on the way out.
/// </summary>
[Collection("ReportRecipients")] // serial: mutates the REPORT_SERVICE_TOKEN process env var
public sealed class ReportRecipientsEndpointTests : IDisposable
{
    private const string Token = "test-report-token";
    private readonly string? _previousToken;
    private readonly GatewayDbTestHarness _h = new();

    public ReportRecipientsEndpointTests()
    {
        _previousToken = Environment.GetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar);
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, Token);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, _previousToken);
        _h.Dispose();
    }

    private TenantRegistry Registry() => new(_h.Open());

    /// <summary>An IResult needs a service provider to execute, exactly as the report endpoint's own
    /// tests build one.</summary>
    private static readonly IServiceProvider Services =
        new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();

    private static HttpContext Ctx(string? bearer = Token)
    {
        var ctx = new DefaultHttpContext { RequestServices = Services, Response = { Body = new MemoryStream() } };
        if (bearer is not null) ctx.Request.Headers.Authorization = $"Bearer {bearer}";
        return ctx;
    }

    private static async Task<(int Status, JsonElement Body)> ExecuteAsync(IResult result, HttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }

    [Fact]
    public async Task WithNoTokenConfigured_ItRefuses_RatherThanServingUnguarded()
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, null);

        var ctx = Ctx();
        var (status, _) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, Registry()), ctx);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
    }

    [Fact]
    public async Task WithTheWrongToken_ItRefuses()
    {
        var ctx = Ctx("not-the-token");
        var (status, _) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, Registry()), ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task WithNoAuthorizationHeaderAtAll_ItRefuses()
    {
        var ctx = Ctx(bearer: null);
        var (status, _) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, Registry()), ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task ARefusalLeaksNoAddresses()
    {
        // The failure path must not be a quieter way to read the list.
        var registry = Registry();
        registry.MintOrLookupBySubject("subject-1", "leaked@example.com");

        var ctx = Ctx("wrong");
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry), ctx);

        Assert.DoesNotContain("leaked@example.com", body.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ItReturnsEveryAccountThatHasAnEmail()
    {
        var registry = Registry();
        registry.MintOrLookupBySubject("subject-1", "alice@example.com");
        registry.MintOrLookupBySubject("subject-2", "bob@example.com");

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry), ctx);
        var emails = body.GetProperty("recipients").EnumerateArray()
            .Select(r => r.GetProperty("email").GetString()).ToList();

        Assert.Equal(new[] { "alice@example.com", "bob@example.com" }, emails);
    }

    [Fact]
    public async Task AnAccountWithNoEmailIsOmitted_NotReturnedBlank()
    {
        // A recipient with nothing to send to is not a recipient. Returning it blank would invite the
        // sender to try anyway and fail once per morning, forever.
        var registry = Registry();
        registry.MintOrLookupBySubject("subject-no-email", null);
        registry.MintOrLookupBySubject("subject-with", "real@example.com");

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry), ctx);
        var recipients = body.GetProperty("recipients").EnumerateArray().ToList();

        Assert.Equal("real@example.com", Assert.Single(recipients).GetProperty("email").GetString());
    }

    [Fact]
    public async Task TheSameAddressOnTwoAccountsIsSentToOnce()
    {
        // Two tenants can legitimately carry one address. Mailing it twice each morning would read as
        // a bug to the person receiving it, and would be one.
        var registry = Registry();
        registry.MintOrLookupBySubject("subject-1", "same@example.com");
        registry.MintOrLookupBySubject("subject-2", "SAME@example.com");

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry), ctx);

        Assert.Single(body.GetProperty("recipients").EnumerateArray());
    }

    [Fact]
    public async Task WithNoAccountsAtAll_ItReturnsAnEmptyListRatherThanFailing()
    {
        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, Registry()), ctx);

        Assert.Empty(body.GetProperty("recipients").EnumerateArray());
    }

    [Fact]
    public async Task TheOrderIsStable_SoASendCanBeComparedBetweenMornings()
    {
        var registry = Registry();
        registry.MintOrLookupBySubject("s3", "carol@example.com");
        registry.MintOrLookupBySubject("s1", "alice@example.com");
        registry.MintOrLookupBySubject("s2", "bob@example.com");

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry), ctx);
        var emails = body.GetProperty("recipients").EnumerateArray()
            .Select(r => r.GetProperty("email").GetString()).ToList();

        Assert.Equal(new[] { "alice@example.com", "bob@example.com", "carol@example.com" }, emails);
    }
}
