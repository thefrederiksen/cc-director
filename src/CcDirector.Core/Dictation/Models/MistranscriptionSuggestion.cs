namespace CcDirector.Core.Dictation.Models;

/// <summary>
/// One evidence row for a mistranscription suggestion: a wrong spelling the speech model produced for a
/// term, and how many times it was seen across the tenant's stored transcripts. This is the "heard as X
/// (n times)" the customer sees, and - when a suggestion is applied - the exact wrong form written into the
/// glossary's Common mistranscriptions so the cleanup pass corrects it.
/// </summary>
public sealed record MistranscriptionVariant(string Heard, int Count);

/// <summary>
/// A ranked, evidence-carrying suggestion that a term be added to the dictation dictionary (devthrottle
/// issue #2075). The <see cref="MistranscriptionMiner"/> produces these by comparing what the speech model
/// heard across a tenant's own transcripts, so every suggestion is grounded in that tenant's history:
///
///   * <see cref="Term"/> is the canonical spelling to add to Vocabulary - the most-frequent spelling the
///     model produced for the cluster (the form it gets right when the audio is clear).
///   * <see cref="Variants"/> are the wrong spellings observed for that term, with counts, newest-highest
///     first - both the evidence shown to the customer AND the Common-mistranscription entries written on
///     apply.
///   * <see cref="WrongCount"/> is how many times the term was heard wrong (the sum of the variant counts),
///     and <see cref="TotalCount"/> how many times it was said at all, so the page can render
///     "wrong 53 of 97 times".
///
/// Pure data: the miner ranks and filters; this record only carries the result. It is never persisted - it
/// is recomputed from the transcripts on demand - so it holds no id and no tenant (the tenant is the scope
/// the miner was run under).
/// </summary>
public sealed record MistranscriptionSuggestion(
    string Term,
    IReadOnlyList<MistranscriptionVariant> Variants,
    int WrongCount,
    int TotalCount);
