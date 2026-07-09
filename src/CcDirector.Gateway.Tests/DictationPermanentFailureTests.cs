using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The server half of the interim loop-stop fix (issue #1185): a PERMANENT transcription failure on the
/// dictation complete path is mapped to a parked FAILED record + the HTTP 422 { permanent, reason } client
/// contract, instead of the generic retryable 502 that made the durable queue re-drive forever.
///
/// The transcription service is sealed and constructed inside the running host, so these tests drive the
/// exact mapping seam <see cref="GatewayDictationEndpoint.MapNonOkTranscription"/> with a fabricated
/// <see cref="GatewayTranscriptionResult"/> for each outcome - which is precisely where the classification,
/// the reason translation, the FAILED park (keeping the chunks), and the guard all live.
/// </summary>
public sealed class DictationPermanentFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-perm-" + Guid.NewGuid().ToString("N"));
    private readonly VoiceUploadStore _store;

    public DictationPermanentFailureTests() => _store = new VoiceUploadStore(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* test cleanup */ }
    }

    [Theory]
    [InlineData("audio_too_large", "audio-too-large")]
    [InlineData("unsupported_format", "unsupported-format")]
    [InlineData("non_decodable", "unsupported-format")]
    public async Task PermanentError_MapsTo422_ParksFailed_KeepsChunks(string code, string expectedReason)
    {
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Encoding.UTF8.GetBytes("AAA"), null);
        var result = GatewayTranscriptionResult.PermanentError("mode", "model", code, "the clip cannot transcribe");

        var outcome = GatewayDictationEndpoint.MapNonOkTranscription(result, id, _store);

        // The client contract: HTTP 422 { permanent:true, reason:<translated> }.
        Assert.NotNull(outcome);
        var (status, body) = await ExecuteAsync(outcome!.ToResult());
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, status);
        Assert.True(body.GetProperty("permanent").GetBoolean());
        Assert.Equal(expectedReason, body.GetProperty("reason").GetString());

        // The record is parked FAILED with the reason code, its chunks retained, and it is not pending.
        var record = _store.ReadRecord(id);
        Assert.NotNull(record);
        Assert.Equal(DictationDeliveryState.Failed, record!.State);
        Assert.Equal(code, record.Reason);
        Assert.False(_store.IsPending(id));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, Guid.Parse(id).ToString("N")), "*.part"));
    }

    [Fact]
    public void Guard_ProviderError_IsNotMappedToPermanent_AndDoesNotParkTheRecord()
    {
        // The guard: every non-Ok outcome that is NOT PermanentError keeps its existing behavior. A provider
        // error stays a retryable 502 and must NOT park the record FAILED.
        var id = _store.Register(null);
        var result = GatewayTranscriptionResult.ProviderError("mode", "model", "provider rejected the key");

        var outcome = GatewayDictationEndpoint.MapNonOkTranscription(result, id, _store);

        Assert.NotNull(outcome);
        Assert.False(outcome!.IsIncomplete);
        Assert.Null(_store.ReadRecord(id)); // not parked FAILED - still PENDING, so a retry re-runs
    }

    [Fact]
    public void Guard_OutOfCredits_IsNotMappedToPermanent_AndDoesNotParkTheRecord()
    {
        var id = _store.Register(null);
        var result = GatewayTranscriptionResult.OutOfCredits("mode", "model", "insufficient_credits", "no credits");

        var outcome = GatewayDictationEndpoint.MapNonOkTranscription(result, id, _store);

        Assert.NotNull(outcome);
        Assert.Null(_store.ReadRecord(id)); // out-of-credits keeps the recording, never permanent
    }

    [Fact]
    public void Ok_ReturnsNull_SoTheCallerContinuesToInject()
    {
        var id = _store.Register(null);
        var result = GatewayTranscriptionResult.Ok("hello there", "mode", "model");

        Assert.Null(GatewayDictationEndpoint.MapNonOkTranscription(result, id, _store));
        Assert.Null(_store.ReadRecord(id));
    }

    [Theory]
    [InlineData("audio_too_large", "audio-too-large")]
    [InlineData("unsupported_format", "unsupported-format")]
    [InlineData("non_decodable", "unsupported-format")]
    [InlineData("something_unexpected", "unsupported-format")]
    public void TranslatePermanentReason_MapsEveryCode(string code, string expected)
    {
        Assert.Equal(expected, GatewayDictationEndpoint.TranslatePermanentReason(code));
    }

    [Fact]
    public void Permanent_Outcome_IsNotTerminal_AndDoesNotKeepTheOrangeMark()
    {
        // Permanent is terminal-for-this-attempt (clears the orange mark: not IsIncomplete) but
        // retryable-for-the-record (not Terminal: the always-remove drops it from the single-flight cache so
        // a retry re-runs, and the FAILED record can re-enter PENDING).
        var outcome = DictationOutcome.Permanent("audio-too-large");
        Assert.False(outcome.IsIncomplete, "a permanent failure clears the transcribing mark");
        Assert.False(outcome.Terminal, "a permanent failure is user-retryable, not a cached terminal");
    }

    // Execute a minimal-API IResult against an in-memory context and read back the status + JSON body. The
    // JSON result resolves logging + JSON options from the request services, so a minimal provider is wired.
    private static async Task<(int status, JsonElement body)> ExecuteAsync(IResult result)
    {
        var provider = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = provider };
        using var ms = new MemoryStream();
        ctx.Response.Body = ms;
        await result.ExecuteAsync(ctx);
        ms.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ms);
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }
}
