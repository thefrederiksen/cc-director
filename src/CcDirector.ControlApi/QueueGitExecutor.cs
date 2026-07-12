using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (spine): the QUEUE and GIT WRITE area of the tunnel command surface. It
/// owns the voice-queue mutation group (add, patch, remove, move-up, move-down, clear, send) and the git
/// working-tree writes (stage, unstage, discard, commit), plus create-from-github. Empty in the spine;
/// Worker W2 fills it. Each verb it adds is declared in <see cref="Verbs"/> and handled in
/// <see cref="ExecuteAsync"/>, touching only this file.
/// </summary>
internal sealed class QueueGitExecutor : ISessionCommandArea
{
    public IReadOnlyCollection<string> Verbs { get; } = Array.Empty<string>();

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the queue/git area"));
}
