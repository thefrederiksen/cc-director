using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// <see cref="HostedInferenceBrain"/> is the stateless hosted wingman: one OpenAI-compatible
/// chat-completions call per ask. These prove the request shape (URL, model, Bearer, single user
/// message), the reply extraction, and the no-fallback error paths - all over a stub handler, no network.
/// </summary>
public sealed class HostedInferenceBrainTests
{
    /// <summary>A canned-response handler that records the last request it saw.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }

    private static string OkBody(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    [Fact]
    public async Task AskAsync_PostsChatCompletions_WithModelBearerAndUserMessage()
    {
        var stub = new StubHandler(HttpStatusCode.OK, OkBody("the spoken summary"));
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc", "glm-5.2", http, _ => { });

        var result = await brain.AskAsync("translate this");

        Assert.Equal("the spoken summary", result.Text);
        Assert.NotNull(stub.LastRequest);
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal("https://devthrottle.com/api/v1/chat/completions", stub.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", stub.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("dt_live_abc", stub.LastRequest.Headers.Authorization.Parameter);

        using var doc = JsonDocument.Parse(stub.LastRequestBody!);
        Assert.Equal("glm-5.2", doc.RootElement.GetProperty("model").GetString());
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("translate this", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task AskAsync_NoKey_ThrowsWithoutCallingModel()
    {
        var stub = new StubHandler(HttpStatusCode.OK, OkBody("x"));
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "", "glm-5.2", http, _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.AskAsync("hi"));
        Assert.Null(stub.LastRequest);   // no-fallback: never called the model without a credential
    }

    [Fact]
    public async Task AskAsync_PaymentRequired_ThrowsSharedNeedsCreditsMessage()
    {
        // Issue #939: the 402 message is now the ONE shared copy (branched by code), not a hand-written
        // string - so it matches every other surface by construction.
        var stub = new StubHandler(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"insufficient_credits\"}}");
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc", "glm-5.2", http, _ => { });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => brain.AskAsync("hi"));
        Assert.Contains(Core.HostedAi.HostedAiMessages.For(Core.HostedAi.HostedAiState.NeedsCredits).Text, ex.Message);
    }

    [Fact]
    public async Task AskAsync_MonthlyLimit402_ThrowsSharedCapReachedMessage()
    {
        // Branch on the code, not the status: a monthly-cap 402 yields the CapReached copy, distinct
        // from the out-of-credits copy.
        var stub = new StubHandler(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"monthly_limit_reached\"}}");
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc", "glm-5.2", http, _ => { });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => brain.AskAsync("hi"));
        Assert.Contains(Core.HostedAi.HostedAiMessages.For(Core.HostedAi.HostedAiState.CapReached).Text, ex.Message);
    }

    [Fact]
    public async Task AskAsync_EmptyContent_Throws()
    {
        var stub = new StubHandler(HttpStatusCode.OK, OkBody(""));
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://api.openai.com/v1", "sk-abc", "gpt-5.5", http, _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.AskAsync("hi"));
    }

    [Theory]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"hello\"}}]}", "hello")]
    [InlineData("{\"choices\":[]}", "")]
    [InlineData("not json", "")]
    [InlineData("{\"unexpected\":true}", "")]
    public void ExtractContent_ParsesOrDegradesToEmpty(string body, string expected)
        => Assert.Equal(expected, HostedInferenceBrain.ExtractContent(body));

    [Fact]
    public async Task ClearAndRestart_AreNoOps()
    {
        var brain = new HostedInferenceBrain("https://api.openai.com/v1", "sk-abc", "gpt-5.5", new HttpClient(new StubHandler(HttpStatusCode.OK, OkBody("x"))), _ => { });
        await brain.ClearAsync();     // must not throw
        await brain.RestartAsync();   // must not throw
        Assert.Null(brain.SessionId);
    }
}
