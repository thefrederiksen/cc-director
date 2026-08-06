namespace CcDirector.Core.Configuration;

/// <summary>
/// A chat model id PROVEN to be one of DevThrottle's internal included-service ids (issue #1360,
/// Included AI). This type is the class-wide guard on the meeting point of a chat model id and the
/// DevThrottle deployment credential, made structural: every constructor and resolver tuple that puts
/// a chat model on a request authenticated by the deployment credential takes THIS type, not a raw
/// string, so an unvalidated string cannot reach the wire from any call site - present or future.
///
/// The two earlier runtime guards this replaces were both bypassed by construction in the phase-2
/// inspection: a base-URL string-equality check was defeated by the equivalent
/// <c>https://devthrottle.com:443/api/v1</c> spelling, and the Car Mode transports publicly accepted
/// raw resolver tuples around their guarded default resolver. A type cannot be spelled around: the
/// only way to obtain an instance is the mint on this class, and the mint validates.
///
/// THE ONLY MINT PATH IS HERE. The constructor is private; <see cref="TryMint"/> is the single
/// validation gate, <see cref="MintOrFallForward"/> is the fall-forward resolution every settings leg
/// uses (a catalog id saved by an older release degrades to the included default instead of billing
/// credits), and the three named statics are the known included ids pre-minted from their constants.
/// A reflection test pins this producer surface so a new public way to conjure an instance fails the
/// build's tests, not an inspection.
///
/// Deliberately NOT used by BYO/self-configured provider paths that present the MEMBER's own key -
/// those legitimately run catalog models on the member's own account and keep raw strings.
/// </summary>
public sealed class IncludedModelId : IEquatable<IncludedModelId>
{
    /// <summary>The proven included model id, trimmed. Always carries the
    /// <see cref="TranscriptionEndpointResolver.DevThrottleIncludedModelPrefix"/>.</summary>
    public string Value { get; }

    private IncludedModelId(string value) => Value = value;

    /// <summary>The wingman thinking tier (<see cref="TranscriptionEndpointResolver.DevThrottleWingmanModel"/>).</summary>
    public static IncludedModelId Wingman { get; } = new(TranscriptionEndpointResolver.DevThrottleWingmanModel);

    /// <summary>The wingman fast tier (<see cref="TranscriptionEndpointResolver.DevThrottleWingmanFastModel"/>).</summary>
    public static IncludedModelId WingmanFast { get; } = new(TranscriptionEndpointResolver.DevThrottleWingmanFastModel);

    /// <summary>The dictation-cleanup id (<see cref="TranscriptionEndpointResolver.DevThrottleDictationCleanupModel"/>).</summary>
    public static IncludedModelId DictationCleanup { get; } = new(TranscriptionEndpointResolver.DevThrottleDictationCleanupModel);

    /// <summary>
    /// The single validation gate: mints <paramref name="candidate"/> when it is a DevThrottle internal
    /// included id, else null. Callers that must REFUSE a non-included id (the settings write endpoints,
    /// test-chat) branch on the null; callers that must FALL FORWARD use
    /// <see cref="MintOrFallForward"/> instead.
    /// </summary>
    public static IncludedModelId? TryMint(string? candidate)
        => TranscriptionEndpointResolver.IsDevThrottleIncludedModel(candidate)
            ? new IncludedModelId(candidate!.Trim())
            : null;

    /// <summary>
    /// The fall-forward resolution rule (issue #1360): <paramref name="candidate"/> when it is an
    /// included id, else <paramref name="fallForward"/>. A catalog id - saved by an older release,
    /// set in an environment override, or stored as a tenant override - degrades to the included
    /// default instead of billing credits on an internal feature.
    /// </summary>
    public static IncludedModelId MintOrFallForward(string? candidate, IncludedModelId fallForward)
    {
        ArgumentNullException.ThrowIfNull(fallForward);
        return TryMint(candidate) ?? fallForward;
    }

    public bool Equals(IncludedModelId? other)
        => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as IncludedModelId);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}
