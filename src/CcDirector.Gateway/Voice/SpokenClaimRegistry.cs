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
/// transcript at one time, and the claim is free, reserved, or spent. A prompt carrying the id is spoken
/// only if all four hold - the tenant matches, the id is known, the claim is free and young, and the
/// submitted words are the transcript. Anything else is a typed turn, which is what the rule says an
/// edited, mixed, or replayed dictation is; it is never an error, because the text is still delivered.
///
/// RESERVED, THEN COMMITTED OR RELEASED (final inspection finding F-07). The claim used to be spent the
/// moment the route looked at it, before the session was even located, so a prompt that never entered a
/// session - a stale session, a menu refusal, a dropped tunnel - burned the only proof, and the person's
/// retry of the same spoken words was filed as typed. Now the route RESERVES the claim, delivers, and
/// COMMITS it only when the Director accepted the prompt; any other outcome RELEASES it so the retry is
/// still spoken. A reserved claim refuses a concurrent second prompt exactly as a spent one does, so a
/// replay in flight cannot be counted twice.
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

    private const int Free = 0;
    private const int Reserved = 1;
    private const int Spent = 2;

    private sealed class Claim
    {
        public required string Transcript { get; init; }
        public required DateTime CreatedUtc { get; init; }
        public int State;
    }

    /// <summary>A reservation the route holds between delivery and its outcome. Committed on an accepted
    /// prompt, released on anything else; never both, never neither.</summary>
    public readonly record struct Reservation(TenantId Tenant, string UploadId);

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
    /// Reserve the claim for <paramref name="uploadId"/> if, and only if, it belongs to <paramref name="tenant"/>,
    /// is free and young, and <paramref name="submittedText"/> is its transcript. True means the prompt may be
    /// delivered as a voice turn and the caller now holds <paramref name="reservation"/>, which it MUST either
    /// <see cref="Commit"/> (the Director accepted the prompt) or <see cref="Release"/> (anything else). False
    /// means the prompt is typed, with the reason for the log; a claim another prompt holds reserved refuses as
    /// <see cref="Refusal.AlreadySpent"/>.
    /// </summary>
    public bool TryReserve(TenantId tenant, string? uploadId, string submittedText, out Refusal refusal, out Reservation reservation)
    {
        reservation = default;
        if (string.IsNullOrWhiteSpace(uploadId)) { refusal = Refusal.BlankId; return false; }
        if (!tenant.IsValid) { refusal = Refusal.Unknown; return false; }
        var key = (tenant.Value, uploadId.Trim());
        if (!_claims.TryGetValue(key, out var claim)) { refusal = Refusal.Unknown; return false; }
        if (_clock() - claim.CreatedUtc > ClaimLifetime) { refusal = Refusal.Expired; return false; }
        if (!string.Equals(claim.Transcript, Normalize(submittedText ?? ""), StringComparison.Ordinal)) { refusal = Refusal.TextDiffers; return false; }
        // Reserve it exactly once, even under two concurrent prompts carrying the same id.
        if (Interlocked.CompareExchange(ref claim.State, Reserved, Free) != Free) { refusal = Refusal.AlreadySpent; return false; }
        refusal = Refusal.None;
        reservation = new Reservation(tenant, key.Item2);
        return true;
    }

    /// <summary>The prompt entered a session: the claim is spent for good. A reservation this registry does
    /// not hold is a programming error and throws, never a silent no-op.</summary>
    /// <summary>
    /// Are these characters the transcript that id registered? A READ-ONLY check for a client's span claim
    /// (source logging, 2026-09-05): it changes no state, spends nothing, and answers only about text the
    /// caller already holds. A SPENT claim still matches - the words in that range did come from that
    /// transcript, which is what a span records; whether the TURN counts as spoken is decided by
    /// <see cref="TryReserve"/> and by nothing else.
    /// </summary>
    public bool Matches(TenantId tenant, string? uploadId, string? text)
    {
        if (string.IsNullOrWhiteSpace(uploadId) || !tenant.IsValid || text is null) return false;
        if (!_claims.TryGetValue((tenant.Value, uploadId.Trim()), out var claim)) return false;
        if (_clock() - claim.CreatedUtc > ClaimLifetime) return false;
        return string.Equals(claim.Transcript, Normalize(text), StringComparison.Ordinal);
    }

    public void Commit(Reservation reservation)
    {
        var claim = Held(reservation);
        if (Interlocked.CompareExchange(ref claim.State, Spent, Reserved) != Reserved)
            throw new InvalidOperationException($"The spoken claim {reservation.UploadId} is not reserved, so it cannot be committed.");
        FileLog.Write($"[SpokenClaimRegistry] Commit: tenant={reservation.Tenant.Value} upload={reservation.UploadId}");
    }

    /// <summary>No prompt entered a session: the claim is free again, so the person's retry of the same spoken
    /// words is still spoken (finding F-07). Throws on a claim that is not reserved.</summary>
    public void Release(Reservation reservation)
    {
        var claim = Held(reservation);
        if (Interlocked.CompareExchange(ref claim.State, Free, Reserved) != Reserved)
            throw new InvalidOperationException($"The spoken claim {reservation.UploadId} is not reserved, so it cannot be released.");
        FileLog.Write($"[SpokenClaimRegistry] Release: tenant={reservation.Tenant.Value} upload={reservation.UploadId} - no turn entered a session; the claim is free for the retry");
    }

    private Claim Held(Reservation reservation)
    {
        if (reservation == default) throw new ArgumentException("An empty reservation cannot be committed or released.", nameof(reservation));
        if (!_claims.TryGetValue((reservation.Tenant.Value, reservation.UploadId), out var claim))
            throw new InvalidOperationException($"The spoken claim {reservation.UploadId} is not held by this registry (it may have expired and been swept).");
        return claim;
    }

    /// <summary>The words, and nothing else: surrounding whitespace and runs of internal whitespace do not
    /// make a transcription a different utterance, but any other character does.</summary>
    internal static string Normalize(string text) => Regex.Replace(text.Trim(), @"\s+", " ");

    private void Sweep(DateTime now)
    {
        foreach (var pair in _claims)
            if (now - pair.Value.CreatedUtc > ClaimLifetime && pair.Value.State != Reserved)
                _claims.TryRemove(pair.Key, out _);
    }
}
