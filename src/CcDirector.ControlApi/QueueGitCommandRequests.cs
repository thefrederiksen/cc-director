namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (Worker W2): the payload for the voice-queue mutation verbs moved onto
/// the tunnel dispatch. The REST routes address a queue item through the URL (<c>{itemId}</c>) and carry the
/// text in a <see cref="CcDirector.Gateway.Contracts.PromptRequest"/> body; the tunnel has only the single
/// <c>PayloadJson</c> envelope, so this record carries both pieces. <see cref="ItemId"/> is the target queue
/// item (null for the whole-queue verbs queue-add / queue-clear / queue-read); <see cref="Text"/> is the
/// prompt text (set for queue-add and queue-update, null otherwise). This keeps the REST route and the tunnel
/// verb calling the exact same core so they cannot drift.
/// </summary>
internal sealed record QueueItemCommand(string? ItemId = null, string? Text = null);

/// <summary>
/// Gateway Cleanup mission, Phase 0 (Worker W2): the minimal shape the git-write REST routes deserialize from
/// a git verb's success/failure body just to read the <see cref="Accepted"/> flag, so they can pick the same
/// HTTP status the original lambdas did (200 on success, 409 Conflict on a non-zero git exit). The full body
/// the client renders (<c>{ accepted, output }</c> or <c>{ accepted, error, exitCode }</c>) is shipped back
/// verbatim from the command result, so the REST response stays byte-identical; this envelope only inspects
/// the flag. Mirrors the "outcome rides inside the body" pattern the execute-action verb established.
/// </summary>
internal sealed class GitWriteEnvelope
{
    /// <summary>True when the git command exited zero; false on a non-zero exit (which the REST layer maps to 409).</summary>
    public bool Accepted { get; set; }
}
