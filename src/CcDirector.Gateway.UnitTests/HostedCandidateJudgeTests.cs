using System.Net;
using System.Text;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// The judge as it actually talks to the inference proxy.
///
/// Every test here is about a backend behaving badly, because the design's whole claim is that the
/// user's words survive whatever comes back. A judge that answers well is the easy case; a judge that
/// 500s, stalls, returns prose, or returns nothing at all is the case that decides whether dictation
/// stays trustworthy.
/// </summary>
public sealed class HostedCandidateJudgeTests
{
    private static readonly IReadOnlyList<JudgeCandidate> TwoCandidates = new[]
    {
        new JudgeCandidate(0, "sure", "Soren", 8),
        new JudgeCandidate(1, "Terascale", "Tailscale", 20),
    };

    private static HostedCandidateJudge Judge(
        HttpMessageHandler handler, TimeSpan? timeout = null, Action<string>? log = null)
        => new("https://example.invalid/v1", "dt_live_secret", IncludedModelId.DictationCleanup,
            new HttpClient(handler), log ?? (_ => { }), timeout ?? TimeSpan.FromSeconds(5));

    private static string ChatBody(string content)
        => "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":"
           + System.Text.Json.JsonSerializer.Serialize(content) + "}}]}";

    // ===== the happy path =====================================================

    [Fact]
    public async Task AWellFormedRuling_IsReturned()
    {
        var judge = Judge(new StubHandler(HttpStatusCode.OK, ChatBody("{\"acceptedCandidateIds\":[1]}")));

        var accepted = await judge.AcceptAsync("make sure we ship Terascale", TwoCandidates);

        Assert.Equal(new[] { 1 }, accepted);
    }

    [Fact]
    public async Task AnEmptyRuling_IsARuling()
    {
        var judge = Judge(new StubHandler(HttpStatusCode.OK, ChatBody("{\"acceptedCandidateIds\":[]}")));

        var accepted = await judge.AcceptAsync("make sure we ship Terascale", TwoCandidates);

        Assert.NotNull(accepted);
        Assert.Empty(accepted);
    }

    /// <summary>No candidates is answered locally - there is no question to ask, so no call and no
    /// cost. Most utterances take this path.</summary>
    [Fact]
    public async Task NoCandidates_MakesNoCallAtAll()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ChatBody("{\"acceptedCandidateIds\":[0]}"));
        var judge = Judge(handler);

        var accepted = await judge.AcceptAsync("nothing to see here", Array.Empty<JudgeCandidate>());

        Assert.Empty(accepted!);
        Assert.Equal(0, handler.Calls);
    }

    // ===== every unhappy path means "no ruling" ===============================

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    public async Task ANonSuccessStatus_IsNoRuling(HttpStatusCode status)
    {
        var judge = Judge(new StubHandler(status, "{}"));

        Assert.Null(await judge.AcceptAsync("make sure we ship Terascale", TwoCandidates));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{}]}")]
    [InlineData("{\"choices\":[{\"message\":{}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":42}}]}")]
    public async Task AReplyThatIsNotAChatCompletion_IsNoRuling(string body)
    {
        var judge = Judge(new StubHandler(HttpStatusCode.OK, body));

        Assert.Null(await judge.AcceptAsync("make sure we ship Terascale", TwoCandidates));
    }

    [Theory]
    [InlineData("I'm sorry, I can't help with that.")]
    [InlineData("Sure! I would correct candidate 1.")]
    [InlineData("```json\n{\"acceptedCandidateIds\":[1]}\n```")]
    [InlineData("{\"acceptedCandidateIds\":[99]}")]
    [InlineData("make sure we ship Tailscale")]
    public async Task AModelThatAnswersInAnyOtherShape_IsNoRuling(string content)
    {
        var judge = Judge(new StubHandler(HttpStatusCode.OK, ChatBody(content)));

        Assert.Null(await judge.AcceptAsync("make sure we ship Terascale", TwoCandidates));
    }

    /// <summary>A stall must fail fast at the deadline, not hang the dictation turn. This is the
    /// regression the removed model pass never had.</summary>
    [Fact]
    public async Task AStall_IsAbandonedAtTheDeadline()
    {
        var judge = Judge(new SlowHandler(TimeSpan.FromSeconds(30)), TimeSpan.FromMilliseconds(80));

        var started = DateTimeOffset.UtcNow;
        var accepted = await judge.AcceptAsync("make sure we ship Terascale", TwoCandidates);

        Assert.Null(accepted);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5),
            "the judge must abandon a stalled backend at its own deadline, not wait it out");
    }

    [Fact]
    public async Task ACancelledTurn_IsNoRuling_AndDoesNotThrow()
    {
        var judge = Judge(new SlowHandler(TimeSpan.FromSeconds(30)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.Null(await judge.AcceptAsync("make sure we ship Terascale", TwoCandidates, cts.Token));
    }

    [Fact]
    public async Task ATransportFailure_IsNoRuling_AndDoesNotThrow()
    {
        var judge = Judge(new ThrowingHandler());

        Assert.Null(await judge.AcceptAsync("make sure we ship Terascale", TwoCandidates));
    }

    // ===== what goes on the wire, and what goes in the log ====================

    [Fact]
    public async Task TheCredentialIsPresentedAsBearer_AndTheIncludedModelIsRequested()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ChatBody("{\"acceptedCandidateIds\":[]}"));
        await Judge(handler).AcceptAsync("make sure we ship Terascale", TwoCandidates);

        Assert.Equal("Bearer", handler.LastAuthScheme);
        Assert.Equal("dt_live_secret", handler.LastAuthParameter);
        Assert.Contains(IncludedModelId.DictationCleanup.Value, handler.LastBody);
        Assert.Contains("\"temperature\":0", handler.LastBody);
    }

    /// <summary>Security rule DT-05, and the transcript belongs to the user. The log carries counts and
    /// timings; it must never carry the credential or the words.</summary>
    [Fact]
    public async Task NeitherTheCredentialNorTheTranscriptIsEverLogged()
    {
        var written = new List<string>();
        var judge = Judge(
            new StubHandler(HttpStatusCode.OK, ChatBody("{\"acceptedCandidateIds\":[1]}")),
            log: written.Add);

        await judge.AcceptAsync("my banking password is hunter2", TwoCandidates);

        var all = string.Join("\n", written);
        Assert.NotEmpty(written);
        Assert.DoesNotContain("dt_live_secret", all);
        Assert.DoesNotContain("hunter2", all);
        Assert.DoesNotContain("banking", all);
    }

    // ===== the deployment switch =============================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("shadow")]
    [InlineData("off")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("enforced")]
    [InlineData("en force")]
    public void AnythingButTheExactWord_LeavesTheJudgeShadowing(string? raw)
        => Assert.Equal(UnlistedCorrectionMode.Shadow, DictationJudgeMode.Parse(raw));

    [Theory]
    [InlineData("enforce")]
    [InlineData("ENFORCE")]
    [InlineData("  Enforce  ")]
    public void OnlyTheExactWordPromotesTheJudge(string raw)
        => Assert.Equal(UnlistedCorrectionMode.Enforce, DictationJudgeMode.Parse(raw));

    // ===== handlers ==========================================================

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string LastBody { get; private set; } = "";
        public string? LastAuthScheme { get; private set; }
        public string? LastAuthParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastAuthScheme = request.Headers.Authorization?.Scheme;
            LastAuthParameter = request.Headers.Authorization?.Parameter;
            LastBody = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}
