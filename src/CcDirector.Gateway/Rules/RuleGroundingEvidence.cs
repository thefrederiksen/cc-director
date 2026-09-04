using System.Collections.Immutable;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// THE EVIDENCE THAT A RULE'S TRIGGER WORDS WERE CHECKED AGAINST THE SESSION'S SCREEN, minted only by a
/// fresh Gateway screen read (fix round E, ruling E1).
///
/// Fix round D made grounding unbypassable on the create ROUTE: the route re-read the screen and ran the
/// check before calling the store. An independent inspection then persisted five trigger strings straight
/// through <see cref="SessionRuleStore.Create"/> with no screen read anywhere in the call path, as an
/// executed positive control - because the store is public, took no session and no screen, and the
/// database gate checked only that a trigger word existed. Grounding was an invariant of one caller, by
/// convention, and the comments called that caller "the one door". A claim that holds only for the
/// callers that exist today stops being true the first time somebody adds a caller, and nothing fails
/// when it does.
///
/// So the invariant moved to the persistence boundary, IN THE SAME SHAPE AS PROMOTION. A rule moving to
/// live needs a <see cref="RulePromotionGrant"/> that only the promote route can mint, and the context
/// refuses the save unless it carries <c>PromotionInEffect</c>. A rule's trigger words now need THIS,
/// which only <see cref="RuleAuthor"/> can mint - after it has read the session's screen through the
/// Gateway and found every word on it - and the context refuses a new rule, or a change to a stored
/// rule's words, unless it carries <c>GroundingInEffect</c>. One mechanism a reader learns once, not two
/// that resemble each other.
///
/// WHAT IT ENFORCES, STATED EXACTLY:
///
///  - It cannot be constructed: the constructor is private, and a structural test over the built
///    assembly asserts that nothing but this type calls it.
///  - THE WORDS IT HOLDS ARE AN IMMUTABLE SNAPSHOT TAKEN AT MINTING (fix round F, ruling F1). They used
///    to be a <c>List</c> handed out behind <c>IReadOnlyList</c>, which is a read-only VIEW and not an
///    immutable collection: an inspection cast the exposed collection back to its list, replaced a word
///    with one that was never on the screen, and the row was stored - every structural guard staying
///    green, because the caller neither constructed nor minted a second token, it edited a valid one.
///    The snapshot is private, the comparison is made against it, and what is exposed cannot be
///    written to.
///  - The only way to obtain one is <see cref="Minted"/>, which is INTERNAL and which the same structural
///    test asserts is called by <see cref="RuleAuthor"/> and nothing else in production code.
///  - It names the EXACT words that were checked, in their stored form, and the store refuses it for any
///    other set of words - evidence for "usage limit" cannot be spent on "rm -rf".
///  - It is SINGLE USE: spent by the write it was minted for, so it cannot be captured and replayed.
///
/// WHAT IT DOES NOT ENFORCE: that the screen read was honest. It certifies that the Gateway's own reader
/// answered a screen and that the check ran over it, which is the property the store can hold; whether
/// that reader is wired to the real roster and tunnel is a property of the host, tested separately.
/// </summary>
public sealed class RuleGroundingEvidence
{
    private int _used;

    /// <summary>
    /// THE SNAPSHOT. Private, immutable, and taken at minting from the words that were just checked
    /// against the screen. Nothing a caller holds reaches it: <see cref="Covers"/> compares against this
    /// field and never against anything that was handed out.
    /// </summary>
    private readonly ImmutableArray<string> _words;

    private RuleGroundingEvidence(string sessionId, IEnumerable<string> words)
    {
        SessionId = sessionId;
        _words = words.ToImmutableArray();
    }

    /// <summary>The session whose screen the words were found on.</summary>
    public string SessionId { get; }

    /// <summary>The words that were checked, in the form the store keeps them - every one was on the
    /// screen at the moment of the read. IMMUTABLE: a caller that casts this gets a collection which
    /// refuses to be written to, never the store of words this evidence reasons about.</summary>
    public IReadOnlyList<string> Words => _words;

    /// <summary>Whether this evidence is for exactly these words - the same set, in stored form, and no
    /// other. Order does not matter; a word more or a word fewer does.</summary>
    public bool Covers(IEnumerable<string>? words)
    {
        var asked = new HashSet<string>(RuleTriggerWords.NormaliseAll(words), StringComparer.Ordinal);
        // AGAINST THE SNAPSHOT, never against the exposed property. Comparing the words to be stored
        // against something a caller can reach is comparing them against the caller's own current
        // opinion of what the screen said.
        var held = new HashSet<string>(_words, StringComparer.Ordinal);
        return asked.SetEquals(held);
    }

    /// <summary>Spend this evidence. True exactly once.</summary>
    internal bool TryConsume() => Interlocked.Exchange(ref _used, 1) == 0;

    /// <summary>
    /// The ONLY way to obtain evidence: from a screen the Gateway read, for words that were all found on
    /// it. Internal, and structurally asserted to be called only by <see cref="RuleAuthor"/>, which is the
    /// one type that reads a screen through the Gateway and runs the check.
    ///
    /// IT RUNS THE ONE GROUNDING CHECK, NOT A SECOND COPY OF IT (fix round F). This used to call
    /// <see cref="RuleTriggerWords.NotOn"/> itself, so the feature's central invariant was checked in two
    /// places - here, and in <see cref="RuleTriggerWords.WhyNotGrounded"/>, which the draft route and the
    /// write gate both call. Two copies of one check that agree today is exactly the shape ruling D2 was
    /// written to remove: the draft reader and the store once had two functions for normalising a trigger
    /// word that happened to agree on every word with no padding, and they disagreed on the first word
    /// that had some. It showed here immediately - when the definition learned that a rule watching for
    /// nothing is not grounded, this copy did not, and went on minting evidence for an empty set. That it
    /// could not be REACHED with one was a fact about the callers of the day, not about this type.
    /// </summary>
    internal static RuleGroundingEvidence Minted(RuleScreenReading screen, IEnumerable<string> words)
    {
        if (screen is null) throw new ArgumentNullException(nameof(screen));
        var normalised = RuleTriggerWords.NormaliseAll(words);
        var notOn = RuleTriggerWords.NotOn(normalised, screen.Excerpt);
        if (notOn.Count > 0)
            throw new InvalidOperationException(
                "evidence was asked for words that are not on the screen: " + string.Join(", ", notOn) +
                ". The check has to run before evidence is minted, never after.");
        return new RuleGroundingEvidence(screen.SessionId, normalised);
    }
}
