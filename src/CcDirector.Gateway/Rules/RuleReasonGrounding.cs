namespace CcDirector.Gateway.Rules;

/// <summary>
/// What checking the agent's citation against the screen it was given produced. <see cref="Statement"/> is
/// never empty - that is the point of it. A firing must always be able to say what the grounding check
/// found, including that there was nothing to check, so that a run in which the check NEVER RAN cannot
/// look identical to a run in which it ran and found nothing wrong.
/// </summary>
public sealed record RuleGrounding(
    bool IsGrounded, string Statement, IReadOnlyList<string> NotOnTheScreen, bool HasCitation)
{
    /// <summary>
    /// Whether this answer may carry an ACT. It must CITE something - a line long enough to be a claim
    /// about the screen - and what it cites must be on the screen.
    ///
    /// The two halves are separate on purpose. <see cref="IsGrounded"/> answers "is anything it said about
    /// the screen wrong", which is the right question for a DECLINE: declining does nothing, so an
    /// unfaithful decline is recorded as it happened with the mismatch noted. Acting is the direction that
    /// touches the world, and for that "nothing it said was wrong" is not enough, because an answer that
    /// cites nothing checkable is never wrong.
    /// </summary>
    public bool CanCarryAnAct => HasCitation && IsGrounded;
}

/// <summary>
/// AN ACT MUST BE GROUNDED IN THE SCREEN IT WAS GIVEN (Architect ruling A12).
///
/// This exists because of one live run. A rule declined, and its recorded reason quoted a sentence that was
/// not on the screen the firing record stores - the words had been on that session's screen twelve minutes
/// earlier, in an unrelated run. The decline was safe, because declining is the direction that does
/// nothing. THE SAME UNFAITHFULNESS IN THE OTHER DIRECTION IS A RULE ACTING ON EVIDENCE THAT WAS NOT
/// THERE, and that is the sharpest thing this mission has learned about its own design.
///
/// So: an ACT whose citation is not on the screen is REFUSED, and so is an act with no citation at all -
/// an answer that cites nothing avoids the whole bound by saying nothing checkable, which is an ABSENCE
/// being read as a positive result. A DECLINE needs no citation, and one whose citation is not on the
/// screen is recorded as it happened with the mismatch NOTED, so the unfaithfulness is visible rather than
/// smoothed over.
///
/// THE CITATION IS A FIELD, NOT A HOPE (phase 1, ruling P1-A). The first version of this scanned a
/// free-text reason for quotation marks and hoped the model had put some there. The phase 0 harness proved
/// it does not: on twelve real limit screens neither model quoted the screen once, so this check refused
/// every act, and the engine never acted on the case it was built for. The question now asks for ONE line
/// copied from the screen as its own named field, and this checks that field - against the same excerpt
/// the model was shown, with the same normaliser and the same comparison the authoring path uses for a
/// trigger word (<see cref="RuleTriggerWords"/>, fix round D ruling D2). One normaliser, one comparison.
///
/// WHAT IT CHECKS, AND WHAT IT DOES NOT. It checks that the cited line is on the screen. It does not check
/// that the conclusion drawn from it is right - judging the judgement is what the model was asked for in
/// the first place. So this is a floor, not a proof of faithfulness: a citation that IS on the screen makes
/// the conclusion checkable by a person reading the record, not correct.
/// </summary>
public static class RuleReasonGrounding
{
    /// <summary>A citation shorter than this is not treated as a claim about the screen. The word 'limit'
    /// on its own would match forty lines of terminal output in both directions and prove nothing.</summary>
    public const int ShortestCheckablePassage = 8;

    /// <summary>The statement a firing carries when its reason is this Gateway's own words rather than the
    /// agent's - a refusal, or an abandonment. There is nothing to check, and saying so is not the same as
    /// saying nothing.</summary>
    public const string NotTheAgentsReason =
        "grounding: not applicable - this reason is the Gateway's own, not the agent's, so there is nothing " +
        "of the agent's to cite or to check.";

    /// <summary>
    /// Check the one line the agent copied from the screen against the excerpt it was shown.
    /// </summary>
    /// <param name="quote">The line the answer cited, as written. Empty means it cited nothing.</param>
    /// <param name="screenExcerpt">THE EXACT TEXT the question carried - <see cref="RuleScreenExcerpt.Of"/>
    /// of the screen. Never a longer or shorter reading of the same screen.</param>
    public static RuleGrounding CheckQuote(string? quote, string? screenExcerpt)
    {
        var line = RuleTriggerWords.Normalise(quote);
        var none = Array.Empty<string>();

        if (line.Length == 0)
            return new RuleGrounding(
                true,
                "grounding: the answer cites no line from the screen, so there is nothing on it that can be " +
                "checked. Nothing it said was contradicted; nothing it said was verifiable either.",
                none,
                HasCitation: false);

        if (line.Length < ShortestCheckablePassage)
            return new RuleGrounding(
                true,
                $"grounding: the answer's citation '{line}' is too short to be a claim about the screen " +
                $"({ShortestCheckablePassage} characters is the least that counts), so it was not checked " +
                "and cannot carry an act.",
                none,
                HasCitation: false);

        // THE SAME COMPARISON A TRIGGER WORD GETS AT AUTHORING (ruling D2): normalised once, looked for
        // on the excerpt, case ignored because the matching it guards ignores case.
        var missing = RuleTriggerWords.NotOn(new[] { line }, screenExcerpt ?? "");
        if (missing.Count == 0)
            return new RuleGrounding(
                true,
                $"grounding: the answer cites this line from the screen, and it is on it: '{line}'.",
                none,
                HasCitation: true);

        return new RuleGrounding(
            false,
            $"grounding: the answer cites a line this screen does not contain: '{line}'.",
            missing,
            HasCitation: true);
    }
}
