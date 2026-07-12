using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (spine): the CATALOG and DIRECTOR-LEVEL READ area of the tunnel command
/// surface. It owns the reads that are not addressed to one session (git status is per-session but grouped
/// here with the catalogs) - repos list, facts, coaching categories, claude-sessions, interrupted list,
/// filesystem list, directory list. Empty in the spine; Worker R2 fills it. Each verb it adds is declared in
/// <see cref="Verbs"/> and handled in <see cref="ExecuteAsync"/>, touching only this file.
/// </summary>
internal sealed class CatalogReadExecutor : ISessionCommandArea
{
    public IReadOnlyCollection<string> Verbs { get; } = Array.Empty<string>();

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the catalog read area"));
}
