using System.Security.Cryptography;
using System.Text;

namespace CcDirector.Gateway.CarMode;

/// <summary>
/// The per-stage timing the Car Mode brain measures while it answers one turn (Car Mode performance round).
/// These are the SERVER stamps: the whole /carmode/turn wall-clock, every hosted-model round trip, and the
/// fleet/roster reads the tools made. They are returned inline in the turn response so the browser can
/// merge them with its own CLIENT stamps (pause-to-transcribe, the brain round trip, text-to-speech, and
/// first-audio) into ONE compact record it posts back to the telemetry store. Milliseconds throughout.
/// </summary>
public sealed record CarModeTurnTiming
{
    /// <summary>The whole brain turn, server side: the first line of RunTurnAsync to the final answer.</summary>
    public double TotalMs { get; init; }

    /// <summary>How many hosted-model round trips this turn took (one per tool-calling round).</summary>
    public int ModelCallCount { get; init; }

    /// <summary>The sum of every model round trip's duration.</summary>
    public double ModelMsTotal { get; init; }

    /// <summary>Each model round trip's duration, in call order, so a slow single call is visible.</summary>
    public IReadOnlyList<double> ModelMs { get; init; } = Array.Empty<double>();

    /// <summary>How many times the tools read the fleet roster (or the directors/repos) this turn. Zero for
    ///  a general question that never touched the fleet - the whole point of the fleet-read suppression.</summary>
    public int FleetReadCount { get; init; }

    /// <summary>The sum of every fleet/roster read's duration this turn.</summary>
    public double FleetReadMsTotal { get; init; }

    /// <summary>How many tool-calling rounds the loop ran before it settled on a final spoken answer.</summary>
    public int Rounds { get; init; }
}

/// <summary>
/// One compact Car Mode turn timing record, the unit the telemetry store keeps. It carries ONLY timings
/// and small counts - never the text of what was said or heard (the command and reply are recorded as
/// character counts only, mirroring the Stats dashboard's counts-only rule). The device is identified by a
/// short one-way hash of its credential, never the raw credential (security rule DT-05).
///
/// The browser fills the client stamps and echoes back the server timing it received in the turn response;
/// the server fills <see cref="ReceivedAtUtc"/>, <see cref="DeviceHash"/>, and <see cref="GatewayVersion"/>
/// from its own side so those cannot be spoofed by the client.
/// </summary>
public sealed record CarModeTelemetryRecord
{
    /// <summary>The turn id the server minted and returned in the turn response, so the client record and
    ///  the server's own log line for the same turn can be lined up.</summary>
    public string TurnId { get; init; } = "";

    /// <summary>When the server received this record (server-stamped, ISO-8601 UTC). The retention sweep
    ///  ages records off by this.</summary>
    public string ReceivedAtUtc { get; init; } = "";

    /// <summary>A short, one-way hash of the caller's device credential (server-stamped). Groups a device's
    ///  turns without ever storing the credential (DT-05).</summary>
    public string DeviceHash { get; init; } = "";

    /// <summary>The Gateway build that served the turn (server-stamped), so a record is tied to a build.</summary>
    public string GatewayVersion { get; init; } = "";

    // ----- Client stamps (milliseconds), measured in the browser turn-taking machine -----

    /// <summary>Pause detected (or "over and out" tapped) to the command transcript in hand: the whole
    ///  command path (client transcode + network + server transcribe).</summary>
    public double PauseToTranscribeMs { get; init; }

    /// <summary>The client-side WebM/Opus to 16k mono WAV transcode alone (phone CPU), so the transcribe
    ///  round trip (network + server) is <see cref="PauseToTranscribeMs"/> minus this.</summary>
    public double TranscodeMs { get; init; }

    /// <summary>The brain round trip as the browser saw it: POST /carmode/turn request to response, network
    ///  included (so it is >= the server TotalMs by the network cost).</summary>
    public double BrainMs { get; init; }

    /// <summary>The text-to-speech round trip for the whole reply (POST /wingman/tts). It is the whole
    ///  reply since the first-sentence split was reverted - the reply is synthesized as one clip.</summary>
    public double TtsMs { get; init; }

    /// <summary>Reply text in hand to audio actually playing: the time the owner waits after the brain
    ///  answers before he hears anything (reply synthesis plus the play call).</summary>
    public double FirstAudioMs { get; init; }

    /// <summary>The whole turn as the owner feels it: pause detected to first audio playing.</summary>
    public double TotalTurnMs { get; init; }

    // ----- "Over and out" finickiness (the finicky-end-phrase diagnostic), measured in the browser -----

    /// <summary>How many pause/forced transcribe probes ran this turn before the turn was taken. 1 means the
    ///  end phrase landed on the first try; a higher count means "over and out" kept being missed and the
    ///  owner had to keep trying - the direct measure of how finicky the end phrase was on this turn.</summary>
    public int TranscribeAttempts { get; init; }

    // ----- Reply-audio lifecycle (the cut-off-reply diagnostic), measured in the browser -----

    /// <summary>How many audio clips the reply played. It is 1 after the first-sentence split was reverted
    ///  (the whole reply is synthesized and played as one clip); a future streaming regression that clobbers
    ///  the reply would show here as a value other than 1.</summary>
    public int Chunks { get; init; }

    /// <summary>How long the reply clip was actually audible: play-started to play-ended (or to a cut-off),
    ///  milliseconds (wall-clock). A value far short of what the reply length implies flags a truncated reply.</summary>
    public double PlayMs { get; init; }

    /// <summary>The synthesized reply clip's media length (audio.duration), milliseconds: the whole reply the
    ///  phone actually received. A value far short of what <see cref="ReplyChars"/> implies flags a TRUNCATED
    ///  SYNTHESIS (the reply came back short), as opposed to a playback cut-off.</summary>
    public double ClipDurationMs { get; init; }

    /// <summary>How far INTO the clip playback reached at end/cutoff (audio.currentTime), milliseconds
    ///  (media-time). A value far below <see cref="ClipDurationMs"/> flags a PLAYBACK cut-off: the whole reply
    ///  was synthesized but playback stopped part way through - the exact distinction the diagnostic needs.</summary>
    public double PlayedToMs { get; init; }

    /// <summary>True when the reply clip played fully to its natural end; false when it was cut off (a voice
    ///  or touch interrupt, or End Car Mode). The cut-off-reply bug this telemetry makes visible reads as
    ///  false: the reply was synthesized but the owner did not hear all of it.</summary>
    public bool Completed { get; init; }

    // ----- The mic-contention hypothesis (does re-opening the mic mid-playback cut the reply on mobile?) -----

    /// <summary>True when the rolling-"stop" watch re-opened the microphone WHILE the reply was playing (the
    ///  current behavior). The strong hypothesis is that on mobile this re-acquisition ducks or interrupts the
    ///  reply. Read together with <see cref="Completed"/>/<see cref="PlayedToMs"/> it proves or kills that.</summary>
    public bool MicReacquiredDuringPlayback { get; init; }

    /// <summary>How many rolling-"stop" transcriptions ran during this reply. Each re-reads the open capture
    ///  stream; more polls mean more mic contention while the reply was supposed to be playing.</summary>
    public int SpeakingPollCount { get; init; }

    // ----- Server stamps (from CarModeTurnTiming, echoed back by the client) -----

    public double ServerTotalMs { get; init; }
    public int ModelCallCount { get; init; }
    public double ModelMsTotal { get; init; }
    public IReadOnlyList<double> ModelMs { get; init; } = Array.Empty<double>();
    public int FleetReadCount { get; init; }
    public double FleetReadMsTotal { get; init; }
    public int Rounds { get; init; }

    // ----- Small, non-text turn facts -----

    /// <summary>The owner's command length in characters (never the text itself).</summary>
    public int CommandChars { get; init; }

    /// <summary>The spoken reply length in characters (never the text itself).</summary>
    public int ReplyChars { get; init; }

    /// <summary>How many fleet actions the brain took this turn.</summary>
    public int ActionsCount { get; init; }

    /// <summary>True when the turn ended holding a destructive action for a spoken confirmation.</summary>
    public bool PendingConfirmation { get; init; }
}

/// <summary>Helpers for the caller-credential hash used as <see cref="CarModeTelemetryRecord.DeviceHash"/>.</summary>
public static class CarModeDeviceHash
{
    /// <summary>A short, stable, one-way hash of a device credential (first 12 hex of its SHA-256), so a
    ///  device's turns group together without the raw credential ever being stored or logged (DT-05). A
    ///  blank credential (auth-off debug) maps to a fixed "anonymous" bucket.</summary>
    public static string Of(string? credential)
    {
        if (string.IsNullOrWhiteSpace(credential)) return "anonymous";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        var sb = new StringBuilder(12);
        for (var i = 0; i < 6; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
