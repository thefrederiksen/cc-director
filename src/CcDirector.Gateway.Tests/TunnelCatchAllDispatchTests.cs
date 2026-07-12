using System.Text;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR B1): the catch-all read verbs dispatched over the tunnel must produce
/// the SAME HTTP response the Director REST route (via the HTTP dial) returned - the serialized DTO with 200
/// and application/json on success, and the matching typed error code otherwise. An unmapped path, a non-GET
/// method (writes come later), or no active stream must return false so the caller keeps the HTTP path.
/// </summary>
public sealed class TunnelCatchAllDispatchTests
{
    private static (DefaultHttpContext ctx, MemoryStream body) NewCtx(string method)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        var body = new MemoryStream();
        ctx.Response.Body = body;
        return (ctx, body);
    }

    private static string BodyText(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    [Fact]
    public async Task Turns_read_writes_the_dto_as_200_json_matching_the_http_dial()
    {
        const string dto = "{\"sessionId\":\"s\",\"status\":\"ok\",\"widgets\":[]}";
        var seenVerb = "";
        DirectorCommandRouter.SendDirectorCommandAsync send = (d, c, ct) =>
        {
            seenVerb = c.Verb;
            return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(dto));
        };
        var dispatch = new TunnelCatchAllDispatch(send);
        var (ctx, body) = NewCtx("GET");

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", "turns");

        Assert.True(handled);
        Assert.Equal("turns", seenVerb);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal("application/json", ctx.Response.ContentType);
        Assert.Equal(dto, BodyText(body));
    }

    [Theory]
    [InlineData("turns", "turns")]
    [InlineData("buffer/html", "buffer-html")]
    [InlineData("usage", "usage")]
    [InlineData("context", "context")]
    [InlineData("history", "history")]
    [InlineData("github-urls", "github-urls")]
    public async Task Each_catch_all_read_path_maps_to_its_verb(string rest, string expectedVerb)
    {
        var seenVerb = "";
        DirectorCommandRouter.SendDirectorCommandAsync send = (d, c, ct) =>
        {
            seenVerb = c.Verb;
            return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success("{}"));
        };
        var dispatch = new TunnelCatchAllDispatch(send);
        var (ctx, _) = NewCtx("GET");

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", rest);

        Assert.True(handled);
        Assert.Equal(expectedVerb, seenVerb);
    }

    [Fact]
    public async Task NotFound_maps_to_404_matching_the_http_dial()
    {
        DirectorCommandRouter.SendDirectorCommandAsync send = (d, c, ct) =>
            Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found"));
        var dispatch = new TunnelCatchAllDispatch(send);
        var (ctx, _) = NewCtx("GET");

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", "turns");

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task BadRequest_maps_to_400()
    {
        DirectorCommandRouter.SendDirectorCommandAsync send = (d, c, ct) =>
            Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format"));
        var dispatch = new TunnelCatchAllDispatch(send);
        var (ctx, _) = NewCtx("GET");

        var handled = await dispatch.TryDispatchAsync(ctx, "not-a-guid", "dir1", "turns");

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task An_unmapped_rest_path_falls_through_to_http()
    {
        var called = false;
        DirectorCommandRouter.SendDirectorCommandAsync send = (d, c, ct) => { called = true; return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success("{}")); };
        var dispatch = new TunnelCatchAllDispatch(send);
        var (ctx, _) = NewCtx("GET");

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", "queue");

        Assert.False(handled);   // not a mapped read verb (writes/queue are a later increment)
        Assert.False(called);    // never dialed the Director
    }

    [Fact]
    public async Task A_write_method_falls_through_to_http_in_this_increment()
    {
        DirectorCommandRouter.SendDirectorCommandAsync send = (d, c, ct) => Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success("{}"));
        var dispatch = new TunnelCatchAllDispatch(send);
        var (ctx, _) = NewCtx("POST");

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", "resize");

        Assert.False(handled);
    }

    [Fact]
    public async Task No_active_stream_falls_through_to_http()
    {
        DirectorCommandRouter.SendDirectorCommandAsync send = (d, c, ct) => Task.FromResult<DirectorCommandResult?>(null);
        var dispatch = new TunnelCatchAllDispatch(send);
        var (ctx, _) = NewCtx("GET");

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", "turns");

        Assert.False(handled);   // caller falls back to the HTTP proxy path
    }
}
