using System.Net;
using System.Text;
using CcDirector.Core.Configuration;
using CcDirector.Core.Voice;
using CcDirector.Gateway.Speech;
using Xunit;

namespace CcDirector.Core.Tests.Voice;

/// <summary>
/// THE DESKTOP IS A SINK TOO (issue #1031).
///
/// It was the last place in the product that decided for itself how it sounded: it resolved its voice from the
/// process-global configuration and took no account at all, so an account set to French was read aloud by
/// whatever voice the machine held - in practice the English default. The words were right. The voice was wrong.
/// Nothing errored, which is why it survived two rounds of fixes.
///
/// It does not decide any more. It ASKS the Gateway, which owns the one language-and-voice decision, and packages
/// the answer into the SAME utterance type the Gateway's own sinks take.
/// </summary>
public sealed class AccountUtteranceTests
{
    private const string GatewayUrl = "http://gateway.example:7878";

    /// <summary>
    /// It reads the account's decision and packages it - the language AND the voice, from the one route that
    /// carries both.
    ///
    /// The URL is asserted because it is the point: that route is served by the Gateway's single resolver, so the
    /// desktop is reading the same answer every Gateway speech path reads. Computing either half here would be a
    /// second decider, which is the defect this issue exists to remove.
    /// </summary>
    [Fact]
    public async Task It_packages_the_accounts_language_and_voice_from_the_gateway()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"language":"fr","voice":"ff_siwis"}""");
        var utterances = new AccountUtterance(() => new GatewayConfig { Url = GatewayUrl }, new HttpClient(handler));

        var lookup = await utterances.ForAsync("Trois sessions vous attendent.");

        Assert.True(lookup.HasAccount);
        var utterance = lookup.Utterance;
        Assert.NotNull(utterance);
        Assert.Equal(SpokenLanguages.French, utterance!.Language);
        Assert.Equal("ff_siwis", utterance.Voice);
        Assert.Equal("Trois sessions vous attendent.", utterance.Text);
        Assert.Equal($"{GatewayUrl}/gateway/spoken-language", handler.Request!.RequestUri!.ToString());
    }

    /// <summary>The Gateway's own credential authenticates the ask, which is what makes the answer PER ACCOUNT:
    ///  the route resolves the caller's tenant from it.</summary>
    [Fact]
    public async Task It_authenticates_the_ask_so_the_answer_is_this_accounts()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"language":"es","voice":"ef_dora"}""");
        var utterances = new AccountUtterance(
            () => new GatewayConfig { Url = GatewayUrl, Token = "device-token" }, new HttpClient(handler));

        await utterances.ForAsync("Hola.");

        Assert.Equal("device-token", handler.Request!.Headers.Authorization!.Parameter);
    }

    /// <summary>
    /// STANDALONE ASKS NOTHING. A Director with no Gateway has no account, so there is no per-account language to
    /// have and the machine's own configuration is the only truth. Null means "speak as you always did".
    ///
    /// That no REQUEST was made matters as much as the null: a resolver that asked an empty address and swallowed
    /// the failure would return the same null while adding a timeout to every sentence the desktop speaks.
    /// </summary>
    [Fact]
    public async Task With_no_gateway_it_asks_nothing()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"language":"fr","voice":"ff_siwis"}""");
        var utterances = new AccountUtterance(() => new GatewayConfig(), new HttpClient(handler));

        var standalone = await utterances.ForAsync("some words");
        Assert.False(standalone.HasAccount);
        Assert.Null(standalone.Utterance);
        Assert.Null(handler.Request);
    }

    /// <summary>It re-reads the configuration on every call, so a Director that booted standalone and later had a
    ///  gateway address written in starts speaking the account's language without a restart. Caching the mode at
    ///  construction has already been a real bug in the sibling key resolver.</summary>
    [Fact]
    public async Task It_re_reads_the_gateway_configuration_every_time()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"language":"es","voice":"em_alex"}""");
        var attached = false;
        var utterances = new AccountUtterance(
            () => attached ? new GatewayConfig { Url = GatewayUrl } : new GatewayConfig(), new HttpClient(handler));

        Assert.False((await utterances.ForAsync("some words")).HasAccount);
        attached = true;
        Assert.Equal("em_alex", (await utterances.ForAsync("unas palabras")).Utterance!.Voice);
    }

    /// <summary>
    /// An unknown language code reads as English rather than taking the voice away. A code from a NEWER Gateway
    /// that this build does not know must degrade to speech, not to silence - the direction
    /// <see cref="SpokenLanguages.Resolve"/> documents, and what makes a rollback safe.
    /// </summary>
    [Fact]
    public async Task An_unknown_language_code_degrades_to_English_rather_than_silence()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"language":"de","voice":"af_bella"}""");
        var utterances = new AccountUtterance(() => new GatewayConfig { Url = GatewayUrl }, new HttpClient(handler));

        Assert.Equal(SpokenLanguages.English, (await utterances.ForAsync("words")).Utterance!.Language);
    }

    /// <summary>
    /// A refusal, an error, an unreachable Gateway or an answer missing either half means "no account answer" -
    /// never a guess, and never half a decision.
    ///
    /// This is the case that looks dangerous and is not: desktop speech also needs the account KEY, which comes
    /// from the same Gateway through the same configuration, so an unreachable Gateway yields no audio at all.
    /// There is no state where this returns null quietly and a sentence is still spoken in the wrong voice.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"error":"no tenant"}""")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.OK, """{"language":"fr"}""")]
    [InlineData(HttpStatusCode.OK, """{"voice":"ff_siwis"}""")]
    public async Task Anything_but_a_whole_answer_means_no_account_answer(HttpStatusCode status, string body)
    {
        var utterances = new AccountUtterance(
            () => new GatewayConfig { Url = GatewayUrl }, new HttpClient(new CapturingHandler(status, body)));

        var lookup = await utterances.ForAsync("words");
        Assert.Null(lookup.Utterance);
        Assert.True(lookup.HasAccount, "an attached Gateway that failed is NOT the same as no account");
        Assert.False(string.IsNullOrWhiteSpace(lookup.Reason));
    }

    /// <summary>An unreachable Gateway does not throw into the caller's turn. Desktop speech is something a person
    ///  is waiting on, and a failed lookup must never be the thing that breaks it.</summary>
    [Fact]
    public async Task An_unreachable_gateway_does_not_throw()
    {
        var utterances = new AccountUtterance(
            () => new GatewayConfig { Url = GatewayUrl }, new HttpClient(new ThrowingHandler()));

        var failed = await utterances.ForAsync("words");
        Assert.Null(failed.Utterance);
        Assert.True(failed.HasAccount);
    }

    /// <summary>
    /// NOTHING ON THIS PATH CARRIES A MODEL OR AN ENGINE.
    ///
    /// The desktop's engine is resolved separately, exactly as it always was, with no knowledge of any language.
    /// A language selecting an ENGINE is what got this feature reverted (devthrottle_internal#547), and the
    /// desktop's only route to one would be a model handed to it from here.
    /// </summary>
    [Fact]
    public void The_desktop_utterance_path_offers_no_model_or_engine()
    {
        var names = typeof(AccountUtterance).GetMembers().Select(m => m.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("Model", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Engine", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Captures the outgoing request and returns a configured status + body, so no real network call is
    ///  made. Request stays null when nothing was sent, which one test asserts.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseBody;

        public CapturingHandler(HttpStatusCode status, string responseBody)
        {
            _status = status;
            _responseBody = responseBody;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Simulates an unreachable Gateway by throwing on send.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
