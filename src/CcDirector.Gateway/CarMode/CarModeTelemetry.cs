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

    /// <summary>Pause detected (or "over and out" tapped) to the command transcript in hand: the Gateway
    ///  transcription round trip for the command.</summary>
    public double PauseToTranscribeMs { get; init; }

    /// <summary>The brain round trip as the browser saw it: POST /carmode/turn request to response, network
    ///  included (so it is >= the server TotalMs by the network cost).</summary>
    public double BrainMs { get; init; }

    /// <summary>The text-to-speech round trip for the FIRST spoken chunk (POST /wingman/tts).</summary>
    public double TtsMs { get; init; }

    /// <summary>Reply text in hand to audio actually playing: the time the owner waits after the brain
    ///  answers before he hears anything (first-chunk synthesis plus the play call).</summary>
    public double FirstAudioMs { get; init; }

    /// <summary>The whole turn as the owner feels it: pause detected to first audio playing.</summary>
    public double TotalTurnMs { get; init; }

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
