namespace CcDirector.Gateway.Contracts;

/// <summary>
/// What a prompt's door knew at the moment of entry, on the wire between the Gateway and a Director (owner's
/// ruling, 2026-09-05: source logging). The Gateway's routes build it from what they verified - the route the
/// request came through, the kind of credential behind it, the transcript claim and where its characters
/// stand in the text - and the Director hands it, untouched, to the session's choke point, which records it
/// on the turn-submitted ledger row with the content digest and length. Nothing downstream infers any of it.
/// </summary>
public sealed class SubmissionProvenanceDto
{
    /// <summary>The door: gateway-prompt, gateway-dictation, fleet-message, gateway-terminal.</summary>
    public string Route { get; set; } = "";

    /// <summary>The credential kind behind the caller: device, machine-token, session, unknown.</summary>
    public string IdentityKind { get; set; } = "";

    /// <summary>The transcript's identifier (the upload id the Gateway transcribed), or null.</summary>
    public string? TranscriptId { get; set; }

    /// <summary>The character ranges of the sent text that came from a transcript, in text order.</summary>
    public List<SpokenSpanDto> SpokenSpans { get; set; } = new();
}

/// <summary>One run of spoken characters: the text from <see cref="Start"/> for <see cref="Length"/> characters.</summary>
public sealed class SpokenSpanDto
{
    public int Start { get; set; }
    public int Length { get; set; }
}
