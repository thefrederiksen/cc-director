using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Unit coverage for the owner-email cloud client (issue #1318 consumer). Proves the wire body carries
/// the subject/body/attachments and NO recipient (single-recipient by construction lives at the cloud,
/// which resolves the owner from the token), that the Bearer token is sent, and that a cloud failure
/// surfaces as a clear error rather than a fabricated success. A stub handler stands in for the cloud.
/// </summary>
public sealed class AccountNotifyClientTests
{
    [Fact]
    public void BuildBody_CarriesSubjectBodyAttachments_AndNoRecipient()
    {
        var body = AccountNotifyClient.BuildBody(
            "Nightly report", "plain", "<p>html</p>",
            new[] { new NotifyAttachment("report.html", "YWJj", "text/html") });

        var root = (JsonObject)JsonNode.Parse(body)!;
        Assert.Equal("Nightly report", (string?)root["subject"]);
        Assert.Equal("plain", (string?)root["text"]);
        Assert.Equal("<p>html</p>", (string?)root["html"]);
        // No recipient field of any kind is ever emitted.
        Assert.False(root.ContainsKey("to"));
        Assert.False(root.ContainsKey("recipient"));

        var att = (JsonArray)root["attachments"]!;
        Assert.Single(att);
        var a0 = (JsonObject)att[0]!;
        Assert.Equal("report.html", (string?)a0["filename"]);
        Assert.Equal("YWJj", (string?)a0["content"]);
        Assert.Equal("text/html", (string?)a0["contentType"]);
    }

    [Fact]
    public void BuildBody_OmitsEmptyBodyAndContentType()
    {
        var body = AccountNotifyClient.BuildBody(
            "Subj", null, null, new[] { new NotifyAttachment("x.bin", "YWJj", null) });

        var root = (JsonObject)JsonNode.Parse(body)!;
        Assert.False(root.ContainsKey("text"));
        Assert.False(root.ContainsKey("html"));
        var a0 = (JsonObject)((JsonArray)root["attachments"]!)[0]!;
        Assert.False(a0.ContainsKey("contentType"));
    }

    [Fact]
    public async Task SendOwnerAsync_SendsBearerToken_AndPostsToNotifyPath()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler(req => { seen = req; return Json(HttpStatusCode.OK, """{ "data": { "sent": true, "id": "resend-1" } }"""); });
        var client = NewClient(handler);

        var result = await client.SendOwnerAsync("tok-123", "Subj", "body", null, null);

        Assert.True(result.Sent);
        Assert.Equal("resend-1", result.ProviderId);
        Assert.NotNull(seen);
        Assert.EndsWith(AccountNotifyClient.NotifyOwnerPath, seen!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", seen.Headers.Authorization?.Scheme);
        Assert.Equal("tok-123", seen.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task SendOwnerAsync_CloudRejects_ReturnsErrorWithStatus_NotSent()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadRequest, """{ "error": { "message": "subject is required" } }"""));
        var client = NewClient(handler);

        var result = await client.SendOwnerAsync("tok", "s", "b", null, null);

        Assert.False(result.Sent);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("subject is required", result.Error);
    }

    [Fact]
    public async Task SendOwnerAsync_CloudErrorWithoutMessage_HasGenericReason()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadGateway, "not json"));
        var client = NewClient(handler);

        var result = await client.SendOwnerAsync("tok", "s", "b", null, null);

        Assert.False(result.Sent);
        Assert.Equal(502, result.StatusCode);
        Assert.Contains("502", result.Error);
    }

    [Fact]
    public async Task SendOwnerAsync_EmptyToken_Throws()
    {
        var client = NewClient(new StubHandler(_ => Json(HttpStatusCode.OK, "{}")));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendOwnerAsync("", "s", "b", null, null));
    }

    private static AccountNotifyClient NewClient(StubHandler handler)
        => new(new HttpClient(handler), baseUrl: "https://cloud.example.com");

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }
}
