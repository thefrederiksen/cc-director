using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Voice;

/// <summary>
/// THE GATEWAY'S OWN RECORD OF WHAT IT TRANSCRIBED, so a prompt's claim to be spoken can be VERIFIED rather
/// than trusted (inspection finding I2-03 of the "Clean up Your Throttle" mission, 2026-09-05).
///
/// The rule (ruling R10) is that a turn is spoken only when the words submitted are exactly one untouched
/// transcription. Phase two carried that as an utterance id on the prompt body, and the Director treated
/// any nonblank id as a voice delivery. That made the id a replayable boolean: a caller could attach a
/// made-up id to typed text, attach a real id to different text, or replay one id across several prompts,
/// and every one of those was counted as a voice turn. Nothing looked the id up because nothing had kept
/// what it stood for - the utterance upload is deleted the moment it is transcribed.
///
/// This keeps exactly what the claim needs and nothing else: for one tenant, one upload id produced one
/// transcript at one time, and the claim has or has not been spent. A prompt carrying the id is spoken
/// only if all four hold - the tenant matches, the id is known, the claim is unspent and young, and the
/// submitted words are the transcript - and consuming it spends it. Anything else is a typed turn, which
/// is what the rule says an edited, mixed, or replayed dictation is; it is never an error, because the
/// text is still delivered.
///
/// In memory, on purpose. A claim lives for the seconds between "transcribed" and "sent", so the record
/// needs to survive that and no longer; a Gateway restart in that window costs one turn its voice label
/// and nothing else. Tenant-keyed, because the hosted Gateway serves many accounts and an id one account
/// transcribed must never be spendable by another. The transcript text is held only as long as the claim
/// and is never logged.
/// </summary>
public sealed class SpokenClaimRegistry
{
    /// <summary>How long a transcription stays claimable after it was produced. Long enough for a person to
    /// read the words back and press Send; short enough that a stale id is worthless.</summary>
    public static readonly TimeSpan ClaimLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Why a claim was refused - a closed vocabulary for the log line, never a message a caller
    /// sees, because refusal is silent by design (the turn is delivered, as typed).</summary>
    public enum Refusal
    {
        None,
        BlankId,
        Unknown,
        Expired,
        AlreadySpent,
        TextDiffers,
    }

    private sealed class Claim
    {
        public required string Transcript { get; init; }
        public required DateTime CreatedUtc { get; init; }
        public int Spent;
    }

    private readonly ConcurrentDictionary<(string Tenant, string Id), Claim> _claims = new();
    private readonly Func<DateTime> _clock;

    public SpokenClaimRegistry(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Record that <paramref name="uploadId"/>, for <paramref name="tenant"/>, produced
    /// <paramref name="transcript"/>. Called by the utterance completion route on a successful transcription
    /// and nowhere else - the registry is written by the Gateway about its own work.</summary>
    public void Register(TenantId tenant, string uploadId, string transcript)
    {
        if (!tenant.IsValid) throw new ArgumentException("A valid tenant is required.", nameof(tenant));
        if (string.IsNullOrWhiteSpace(uploadId)) throw new ArgumentException("An upload id is required.", nameof(uploadId));
        var now = _clock();
        Sweep(now);
        _claims[(tenant.Value, uploadId.Trim())] = new Claim { Transcript = Normalize(transcript ?? ""), CreatedUtc = now };
        FileLog.Write($"[SpokenClaimRegistry] Register: tenant={tenant.Value} upload={uploadId} chars={transcript?.Length ?? 0}");
    }

    /// <summary>
    /// Spend the claim for <paramref name="uploadId"/> if, and only if, it belongs to <paramref name="tenant"/>,
    /// is unspent and young, and <paramref name="submittedText"/> is its transcript. True means the prompt is
    /// a voice turn and the claim is now spent; false means the prompt is typed, with the reason for the log.
    /// </summary>
    public bool TryConsume(TenantId tenant, string? uploadId, string submittedText, out Refusal refusal)
    {
        if (string.IsNullOrWhiteSpace(uploadId)) { refusal = Refusal.BlankId; return false; }
        if (!tenant.IsValid) { refusal = Refusal.Unknown; return false; }
        if (!_claims.TryGetValue((tenant.Value, uploadId.Trim()), out var claim)) { refusal = Refusal.Unknown; return false; }
        if (_clock() - claim.CreatedUtc > ClaimLifetime) { refusal = Refusal.Expired; return false; }
        if (!string.Equals(claim.Transcript, Normalize(submittedText ?? ""), StringComparison.Ordinal)) { refusal = Refusal.TextDiffers; return false; }
        // Spend it exactly once, even under two concurrent prompts carrying the same id.
        if (Interlocked.Exchange(ref claim.Spent, 1) != 0) { refusal = Refusal.AlreadySpent; return false; }
        refusal = Refusal.None;
        return true;
    }

    /// <summary>The words, and nothing else: surrounding whitespace and runs of internal whitespace do not
    /// make a transcription a different utterance, but any other character does.</summary>
    internal static string Normalize(string text) => Regex.Replace(text.Trim(), @"\s+", " ");

    private void Sweep(DateTime now)
    {
        foreach (var pair in _claims)
            if (now - pair.Value.CreatedUtc > ClaimLifetime)
                _claims.TryRemove(pair.Key, out _);
    }
}
