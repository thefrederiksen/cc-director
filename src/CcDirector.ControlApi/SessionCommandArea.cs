using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (spine): the context every command area needs to execute a verb -
/// the target <see cref="SessionManager"/>, this Director's id, the optional side-effect services, and the
/// send source. It is passed to <see cref="ISessionCommandArea.ExecuteAsync"/> so the area classes stay
/// stateless singletons (no per-call fields), which is what lets the verb-to-area dictionary be composed
/// exactly once at startup.
/// </summary>
internal readonly record struct SessionCommandContext(
    SessionManager SessionManager,
    string DirectorId,
    SessionCommandServices? Services,
    SendSource Source);

/// <summary>
/// Gateway Cleanup mission, Phase 0 (spine): one AREA of the Director's tunnel command surface (session
/// reads, catalog reads, session writes, queue/git writes, unary byte verbs). Each area declares the verbs
/// it owns and executes them. <see cref="SessionCommandExecutor.DispatchAsync"/> builds a single
/// verb-to-area dictionary from every area's <see cref="Verbs"/> ONCE at startup (throwing loudly if two
/// areas declare the same verb) and routes each command to the owning area.
///
/// This is the structure that removes the merge chokepoint: a worker adds a verb by editing ONLY its own
/// area file - the verb list plus the handling - never a shared central switch. The four connection-bound
/// stream verbs (open-terminal-stream, read-file, screenshot-file, close-stream) are deliberately NOT areas
/// here: they branch earlier, in <c>GatewayStreamClient</c>, because only they need the live connection and
/// the per-stream cancellation registry (Architect ruling A, 2026-07-12).
/// </summary>
internal interface ISessionCommandArea
{
    /// <summary>The verbs this area owns. Each verb must be owned by exactly one area (enforced at composition).</summary>
    IReadOnlyCollection<string> Verbs { get; }

    /// <summary>
    /// Execute one command whose verb this area owns. The dispatcher has already looked the verb up, so an
    /// area only ever receives a verb from its own <see cref="Verbs"/>; a mismatch is a fail-loud BadRequest.
    /// </summary>
    Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken);
}
