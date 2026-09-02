using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR E-B): the SessionVerbClient is the ONE choke point the voice /
/// dictation cluster reaches the owning Director through. These tests prove the tunnel-vs-HTTP decision
/// and the per-verb marshaling directly, without a live Director:
///  - when a sendCommand hook is present (stream mode), each read/write maps to the right verb and payload
///    and NEVER touches the HTTP client;
///  - the dictation delivery marker (PromptRequest.DeliveryUploadId) rides the prompt payload, so the tunnel
///    prompt verb carries the Delivery signal with no HTTP header;
///  - a failed tunnel result maps to the same null / (false, ...) shape the HTTP dial produced.
/// The HTTP fallback path (null sendCommand) is exercised end to end by WingmanVoiceServiceTests over a
/// LoopbackDirector; here we only assert the tunnel branch, so no real endpoint is dialed.
/// </summary>
public sealed class SessionVerbClientTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // A DirectorDto whose control endpoint would refuse a connection, so a test that succeeds proves the
    // tunnel branch ran (the HTTP fallback would have failed).
    private static DirectorDto UnreachableDirector() =>
        new() { DirectorId = "dir-1", ControlEndpoint = "http://127.0.0.1:59921/" };

    // Records the last command and returns a caller-supplied result, standing in for the Director stream.
    private sealed class RecordingHub
    {
        public DirectorCommand? Last;
        public DirectorCommandResult? Next;

        public DirectorCommandRouter.SendDirectorCommandAsync Send => (directorId, command, ct) =>
        {
            Last = command;
            return Task.FromResult<DirectorCommandResult?>(Next);
        };
    }

    private static SessionVerbClient Client(RecordingHub hub) =>
        new(UnreachableDirector(), hub.Send);

    [Fact]
    public async Task GetBuffer_ridesTheBufferVerb_andCarriesTheQueryArgs()
    {
        var hub = new RecordingHub
        {
            Next = DirectorCommandResult.Success(JsonSerializer.Serialize(new BufferResponse { Text = "term" }, Web)),
        };

        var buffer = await Client(hub).GetBufferAsync("sid-1", lines: 42, raw: true, since: 7);

        Assert.Equal("buffer", hub.Last!.Verb);
        var payload = JsonNode.Parse(hub.Last.PayloadJson)!.AsObject();
        Assert.Equal(42, (int?)payload["lines"]);
        Assert.True((bool?)payload["raw"]);
        Assert.Equal(7, (long?)payload["since"]);
        Assert.Equal("term", buffer?.Text);
    }

    [Fact]
    public async Task PostPrompt_ridesThePromptVerb_andCarriesTheDeliveryMarker()
    {
        var hub = new RecordingHub
        {
            Next = DirectorCommandResult.Success(JsonSerializer.Serialize(new PromptResponse { Accepted = true }, Web)),
        };

        var (ok, body, error) = await Client(hub).PostPromptAsync(
            "sid-1", new PromptRequest { Text = "hello", DeliveryUploadId = "up-1", Surface = "phone" });

        Assert.True(ok);
        Assert.Null(error);
        Assert.True(body?.Accepted);
        Assert.Equal("prompt", hub.Last!.Verb);
        // The dictation delivery marker rides the prompt payload, so the tunnel carries it with no HTTP header.
        var sent = JsonSerializer.Deserialize<PromptRequest>(hub.Last.PayloadJson, Web);
        Assert.Equal("up-1", sent?.DeliveryUploadId);
        Assert.Equal("phone", sent?.Surface);
        Assert.Equal("hello", sent?.Text);
    }

    [Fact]
    public async Task CreateSession_ridesTheCreateVerb_directorLevel_andMapsSessionDto()
    {
        // Gateway Cleanup Phase 2 (PR E-B2): the director-level create - no target session id (the command
        // carries an EMPTY SessionId exactly like the /directors reads), the NewSessionRequest as payload,
        // and the SessionDto mapped back.
        var hub = new RecordingHub
        {
            Next = DirectorCommandResult.Success(JsonSerializer.Serialize(new SessionDto { SessionId = "sid-new" }, Web)),
        };

        var (ok, body, error) = await Client(hub).CreateSessionAsync(
            new NewSessionRequest { RepoPath = @"C:\repo", Agent = "ClaudeCode", PrePrompt = "seed" });

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("sid-new", body?.SessionId);
        Assert.Equal("create", hub.Last!.Verb);
        Assert.Equal("", hub.Last.SessionId);   // director-level: no target session
        var sent = JsonSerializer.Deserialize<NewSessionRequest>(hub.Last.PayloadJson, Web);
        Assert.Equal(@"C:\repo", sent?.RepoPath);
        Assert.Equal("seed", sent?.PrePrompt);
    }

    [Fact]
    public async Task CreateSession_failedTunnelResult_mapsToFalseTuple()
    {
        var hub = new RecordingHub { Next = DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "boom") };

        var (ok, body, error) = await Client(hub).CreateSessionAsync(new NewSessionRequest { RepoPath = @"C:\repo" });

        Assert.False(ok);
        Assert.Null(body);
        Assert.Contains("Conflict", error);
    }

    [Fact]
    public async Task FailedTunnelResult_mapsToNull_andToFalseTuple()
    {
        // A NotFound from the Director is authoritative (the endpoint must not also HTTP-dial): a read maps
        // to null and a write maps to the (false, null, error) shape the HTTP path returned on a non-200.
        var hub = new RecordingHub { Next = DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "no such session") };

        // Any read will do to make this claim; the turns read is gone (turn-push mission, phase 4) and the
        // claim is about how a failed tunnel result maps, not about which verb asked.
        Assert.Null(await Client(hub).GetBufferAsync("sid-1", lines: null, raw: false, since: null));
        var (ok, body, error) = await Client(hub).PostPromptAsync("sid-1", new PromptRequest { Text = "x" });
        Assert.False(ok);
        Assert.Null(body);
        Assert.Contains("NotFound", error);
    }
}
