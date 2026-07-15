namespace CcDirector.Gateway.Voice;

/// <summary>
/// The size bounds for audio arriving at the Gateway's voice front doors: the resumable chunk upload
/// (<c>PUT /wingman/utterance/{id}/chunk/{i}</c>) and the one-shot multipart post
/// (<c>POST /wingman/transcribe</c>).
///
/// Why this exists: both routes used to copy the whole request body into a <c>MemoryStream</c> with no
/// ceiling of any kind - the store's only rule was "not empty". The cloud audio endpoint has always had
/// a 4 MB body cap; the local Gateway had none, so the LOCAL leg was the softer target of the two. That
/// matters more than it looks: these are the mobile paths, they are latency-sensitive, and clients are
/// built to RETRY them - so a bad recording does not arrive once, it arrives repeatedly, and every
/// attempt is held in the Gateway's memory in full. The Gateway shares a machine with the user's
/// editors, agents, and builds; memory it wastes is memory they do not get.
///
/// These are ceilings, not targets. Every number here is far above any real recording:
/// a MediaRecorder timeslice fragment is measured in KILObytes, and an hour of the opus the phone
/// actually sends is a few megabytes. If a request trips one of these, something is wrong with it -
/// which is exactly when we want a clear 413 instead of a silent allocation.
/// </summary>
internal static class VoiceUploadLimits
{
    /// <summary>
    /// The most one resumable chunk may carry. This is the number that bounds PER-REQUEST MEMORY, since
    /// a chunk is buffered whole before it is stored.
    ///
    /// 8 MB is roughly a thousand times a real timeslice fragment. It is set that high on purpose: this
    /// is a safety rail against a broken or hostile client, NOT a tuning knob for the honest one, and a
    /// rail that a real user can touch is a bug report waiting to happen.
    /// </summary>
    public const long MaxChunkBytes = 8L * 1024 * 1024;

    /// <summary>
    /// The most one upload may total across all its chunks.
    ///
    /// This bounds two things at once: the disk a single upload id can occupy while it is staged, and -
    /// the reason it is not larger - the assembled clip, which <c>AssembleAsync</c> returns as one
    /// <c>byte[]</c> held in memory. 64 MB is many hours of the opus the phone sends and about an hour
    /// of far denser audio, so no honest dictation reaches it; a byte[] much past this would be
    /// large-object-heap pressure on a machine that is also running the user's real work.
    /// </summary>
    public const long MaxTotalUploadBytes = 64L * 1024 * 1024;

    /// <summary>
    /// The most the one-shot multipart path may accept in its audio file.
    ///
    /// Lower than <see cref="MaxTotalUploadBytes"/> deliberately: the one-shot route has no resume and
    /// buffers the file whole, so it is the wrong door for a long recording - that is what the
    /// resumable chunk path exists for. 25 MB matches the ceiling hosted speech-to-text APIs commonly
    /// enforce, so a clip this route accepts is one the upstream will accept too, rather than one we
    /// carry all the way there to have rejected.
    /// </summary>
    public const long MaxOneShotFileBytes = 25L * 1024 * 1024;
}
