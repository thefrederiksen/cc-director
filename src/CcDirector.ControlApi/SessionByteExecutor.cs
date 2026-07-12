using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (spine): the UNARY BYTE area of the tunnel command surface. It owns the
/// byte verbs that are plain unary commands (the small-payload ones), NOT the up-stream byte streams: the
/// screenshots list (a directory read) and upload-image (bytes DOWN in the payload, chunked if large). The
/// three connection-bound byte STREAMS - read-file, screenshot-file, and the terminal stream - are NOT here:
/// they are handled at the connection layer in GatewayStreamClient with the up-stream primitive, because
/// only they need the live connection (Architect ruling A). Empty in the spine; Worker S1 fills it once the
/// spine's up-stream producers are in place. Each verb it adds is declared in <see cref="Verbs"/> and handled
/// in <see cref="ExecuteAsync"/>, touching only this file.
/// </summary>
internal sealed class SessionByteExecutor : ISessionCommandArea
{
    public IReadOnlyCollection<string> Verbs { get; } = Array.Empty<string>();

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the byte area"));
}
