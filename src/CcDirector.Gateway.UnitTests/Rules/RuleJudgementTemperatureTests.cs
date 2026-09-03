using System.Net;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// ONE SCREEN, ONE VERDICT (Session Rules mission, phase 1). With the provider's default sampling, the fast
/// model answered the same negative screen "decline" on one run and "act" on the next - measured through the
/// screen harness on 3 September 2026. A rule whose judgement about a fixed screen is a dice roll would type
/// on whichever idle transition it happened to land on. So the rules engine asks at temperature zero, and
/// this proves the setting reaches the wire - and that no other wingman call is changed by it.
///
/// Whether the hosted endpoint HONOURS the setting is not provable here; the harness measures it by running
/// every case several times and reporting the flip rate.
/// </summary>
public sealed class RuleJudgementTemperatureTests
{
    /// <summary>Captures the request body and answers like a chat-completions endpoint.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{}\"}}]}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static async Task<string> BodySentBy(double? temperature)
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        using var brain = new HostedInferenceBrain(
            "http://localhost:1/v1", "a-key", IncludedModelId.WingmanFast, http: http, log: _ => { }, temperature: temperature);
        try
        {
            await brain.AskAsync("is this the situation?");
        }
        catch (Exception)
        {
            // The fake answer may not be read as a full answer; the request body is what this test reads.
        }
        Assert.NotNull(handler.Body);
        return handler.Body!;
    }

    [Fact]
    public void The_judgement_temperature_is_zero()
    {
        Assert.Equal(0.0, RuleAgentContract.JudgementTemperature);
    }

    [Fact]
    public async Task The_rules_brain_sends_the_judgement_temperature_on_the_wire()
    {
        var body = await BodySentBy(RuleAgentContract.JudgementTemperature);

        Assert.Contains("\"temperature\":0", body, StringComparison.Ordinal);
    }

    /// <summary>Every other wingman call is exactly what it was: no temperature is sent unless asked for.</summary>
    [Fact]
    public async Task A_brain_asked_for_no_temperature_sends_none()
    {
        var body = await BodySentBy(null);

        Assert.DoesNotContain("temperature", body, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"" + IncludedModelId.WingmanFast.Value + "\"", body, StringComparison.Ordinal);
    }
}
