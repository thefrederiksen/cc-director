namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body for <c>POST /sessions/{sid}/transcribing</c>: mark or clear a session as transcribing a
/// dictated utterance in the background (mobile Speak -> Send). Gateway-owned transient flag only;
/// it is not forwarded to the Director. An empty body defaults to <see cref="Transcribing"/> = true
/// (the common case is "start transcribing"); the phone sends false once the background
/// upload/transcribe/submit finishes or fails.
/// </summary>
public sealed class TranscribingRequest
{
    public bool Transcribing { get; set; } = true;
}
