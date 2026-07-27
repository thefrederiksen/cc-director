using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Settings;

/// <summary>
/// The per-tenant memory behind the daily email's suggestion cadence (issue #2074, mockup screen 5): which
/// BATCH of pending suggestions was last mentioned, and how many times it has been mentioned.
///
/// The cadence decision this type exists to hold: a given batch is mentioned AT MOST
/// <see cref="MaxMentionsPerBatch"/> times, then the email stays quiet until new evidence arrives. The durable
/// signal is the red badge on the Dictionary page, which never goes quiet - the email is only the doorbell, so
/// repeating it indefinitely would be nagging about something the user can already see.
///
/// "New evidence arrives" is expressed as a change of <see cref="Batch"/>, a fingerprint of the pending terms.
/// Any change to the set - a term added by fresh mining, a term removed by being applied or dismissed - is a
/// different batch and earns its own mentions. That is deliberately generous in ONE direction only: it can
/// cost an extra mention after the user acts, never silence a genuinely new term.
///
/// Stored as JSON under <see cref="TenantSettingKeys.DictationEmailCadence"/>, so it needs no table of its own.
/// </summary>
/// <param name="Batch">The fingerprint of the batch last mentioned; empty when nothing has been mentioned.</param>
/// <param name="Mentions">How many times that batch has been mentioned.</param>
/// <param name="LastMentionUtc">When the most recent mention was emitted; null when there has been none.</param>
public sealed record DictationEmailCadenceState(
    [property: JsonPropertyName("batch")] string Batch,
    [property: JsonPropertyName("mentions")] int Mentions,
    [property: JsonPropertyName("lastMentionUtc")] DateTime? LastMentionUtc)
{
    /// <summary>How many times one batch may be mentioned in the daily email before it goes quiet.</summary>
    public const int MaxMentionsPerBatch = 2;

    /// <summary>The state of a tenant that has never had a batch mentioned.</summary>
    public static readonly DictationEmailCadenceState None = new("", 0, null);

    /// <summary>
    /// Whether <paramref name="batch"/> may still be mentioned. A batch this state has never seen always may
    /// (its count restarts at zero); the batch it is already tracking may only until it reaches
    /// <see cref="MaxMentionsPerBatch"/>.
    /// </summary>
    public bool MayMention(string batch)
        => !string.Equals(Batch, batch, StringComparison.Ordinal) || Mentions < MaxMentionsPerBatch;

    /// <summary>
    /// This state advanced by one mention of <paramref name="batch"/>. A different batch restarts the count at
    /// one; the same batch increments it.
    /// </summary>
    public DictationEmailCadenceState Mentioned(string batch, DateTime nowUtc)
        => string.Equals(Batch, batch, StringComparison.Ordinal)
            ? this with { Mentions = Mentions + 1, LastMentionUtc = nowUtc.ToUniversalTime() }
            : new DictationEmailCadenceState(batch, 1, nowUtc.ToUniversalTime());
}
