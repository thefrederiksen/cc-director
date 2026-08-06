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
/// raw resolver tuples around their guarded default resolver. A type cannot be spelled around: in
/// ordinary (non-reflective) code the mint on this class is the only producer of instances, and it
/// normalizes (trims) its input before validating and storing. The exact enforced invariant is on
/// <see cref="Value"/>: no instance whose stored value is not a devthrottle/-prefixed id (verbatim,
/// ordinal) is usable. So an instance forged by invoking the private constructor through reflection
/// (phase-2 inspection rounds 3 and 4) throws at its first use of <see cref="Value"/> unless the
/// forged value already names an included id verbatim - the harmless case, because this guard's job
/// is credential/model separation, not provenance. Deliberate reflection beyond that is outside the
/// threat model: code already running in this process with reflection rights could read the
/// deployment credential directly.
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
    private readonly string _value;

    /// <summary>The proven included model id. The getter enforces the exact invariant on every
    /// read: the STORED string must carry the
    /// <see cref="TranscriptionEndpointResolver.DevThrottleIncludedModelPrefix"/> verbatim
    /// (ordinal, no trimming or other normalization at read time), else it throws. The mint trims
    /// its input before validating and storing, so no legitimate instance can carry whitespace and
    /// no legitimate read can throw. A forged instance (private constructor invoked through
    /// reflection, bypassing the mint) is therefore usable only if it already stores a
    /// devthrottle/-prefixed id verbatim - the harmless case, because this guard exists to keep
    /// non-included model ids off the deployment credential, not to prove provenance.</summary>
    public string Value
    {
        get
        {
            if (_value is null
                || !_value.StartsWith(
                    TranscriptionEndpointResolver.DevThrottleIncludedModelPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Forged {nameof(IncludedModelId)}: the stored value does not carry the " +
                    $"'{TranscriptionEndpointResolver.DevThrottleIncludedModelPrefix}' prefix, so this " +
                    "instance was constructed outside the mint (for example by invoking the private " +
                    "constructor through reflection). The mint is the only producer of usable instances.");
            }

            return _value;
        }
    }

    private IncludedModelId(string value) => _value = value;

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
    {
        var normalized = candidate?.Trim();
        return TranscriptionEndpointResolver.IsDevThrottleIncludedModel(normalized)
            ? new IncludedModelId(normalized!)
            : null;
    }

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
