namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One (modality, surface) bucket of the DevThrottle Stats input tally: how many submitted TURNS and
/// how many CHARACTERS of user input arrived through this modality/surface pair. A turn is one
/// submitted message; character volume counts every typed or spoken character. The pair is the honest
/// unit the mission measures - "how much of development is spoken vs typed, and from phone vs desktop
/// vs cockpit".
/// </summary>
public sealed class InputStatBucketDto
{
    /// <summary>How the input was produced: "typed" or "voice".</summary>
    public string Modality { get; set; } = "";

    /// <summary>Which surface the input came from: "desktop", "cockpit", "phone", or "unknown".</summary>
    public string Surface { get; set; } = "";

    /// <summary>Count of submitted turns through this bucket (never synthesized from raw keystrokes).</summary>
    public long Turns { get; set; }

    /// <summary>Total character volume of input through this bucket.</summary>
    public long Characters { get; set; }
}

/// <summary>
/// A snapshot of a session's (or an aggregate's) input tally, as a flat list of
/// <see cref="InputStatBucketDto"/> buckets. Additive wire type: rides the existing snapshot/delta path
/// on <see cref="SessionDto.InputStats"/> and defaults to null on Directors that predate it. Only counts
/// and ratios ever travel; the text of what was said or typed never does (mission decision 5).
/// </summary>
public sealed class InputStatsDto
{
    /// <summary>The per-bucket tallies. Empty when the session has taken no counted input yet.</summary>
    public List<InputStatBucketDto> Buckets { get; set; } = new();
}
