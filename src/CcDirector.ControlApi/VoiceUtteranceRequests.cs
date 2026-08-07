namespace CcDirector.ControlApi;

/// <summary>Body of POST /voice/utterance. Optional client-supplied id (must be a GUID).</summary>
internal sealed record VoiceUtteranceRegisterRequest(string? UtteranceId);

/// <summary>
/// Body of POST /voice/utterance/{id}/complete. TotalChunks is how many chunks the
/// client uploaded (indices 0..TotalChunks-1). SessionId picks which repo dictionary
/// the server's transcript CLEANUP uses - the pass that runs over the finished
/// transcript. Nothing is sent to the speech-to-text provider (issue 2481).
/// </summary>
internal sealed record VoiceUtteranceCompleteRequest(int TotalChunks, string? Mime, string? SessionId);
