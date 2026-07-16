namespace CcDirector.Core.HostedAi;

/// <summary>
/// The ONE source of the hosted-AI unavailable copy (issue #938, epic #937). Every voice/Wingman/TTS
/// surface across desktop, web, mobile, and native calls <see cref="For"/> so the message a user sees
/// when they have no credits or account setup is incomplete by construction - not 13 hand-written strings that
/// drift apart.
///
/// Copy rules (from epic #937, enforced by <see cref="HostedAiCopyRules"/> and unit-tested):
/// the hosted-account states (<see cref="HostedAiState.NeedsCredits"/>, <see cref="HostedAiState.CapReached"/>)
/// name NO provider ("OpenAI", "Groq", ...), say nothing about "free credits", and use no
/// subscription/tier words.
/// </summary>
public static class HostedAiMessages
{
    /// <summary>
    /// The single-source copy for a state. <see cref="HostedAiState.Ready"/> returns an empty message
    /// (nothing to show); the three unavailable states return the sentence + call-to-action.
    /// </summary>
    public static HostedAiMessage For(HostedAiState state) => state switch
    {
        HostedAiState.Ready => new HostedAiMessage("", "", HostedAiCtaAction.None, null),

        HostedAiState.NeedsCredits => new HostedAiMessage(
            "Voice needs credit. Add $5 to turn on transcription, voice mode, and Wingman - enough to last most of a month.",
            "Add credits",
            HostedAiCtaAction.OpenBilling,
            HostedAiUrls.Billing),

        HostedAiState.CapReached => new HostedAiMessage(
            "You've hit your monthly spending limit. Raise it in Billing to keep going.",
            "Open Billing",
            HostedAiCtaAction.OpenBilling,
            HostedAiUrls.Billing),

        HostedAiState.NeedsKey => new HostedAiMessage(
            "DevThrottle AI is not configured for this machine yet. Open Settings to finish setup.",
            "Open Settings",
            HostedAiCtaAction.OpenSettings,
            null),

        // The far end is down. Nothing is wrong with the account, the setup, or this machine, so there
        // is NO call to action - offering the user a button here would be a lie, because pressing it
        // fails the same way. The surface retries on its own; the copy's job is to say what is true and
        // that recovery is already happening, so the user stops hunting for a bug that is not theirs.
        // Names no provider, per the copy rules above.
        HostedAiState.ServiceDown => new HostedAiMessage(
            "The voice service is not responding. Retrying automatically - this usually comes back on its own.",
            "",
            HostedAiCtaAction.None,
            null),

        // The service did not answer in time - a timeout, not an outage. Say what is TRUE (the audio is
        // still coming and we are trying again), not "the service is down", which we have no evidence
        // for and which sends the user hunting for a bug that is not theirs. No call to action: the
        // surface retries by itself. Names no provider, per the copy rules above.
        HostedAiState.Retrying => new HostedAiMessage(
            "Voice is taking a moment - retrying automatically. It should come through shortly.",
            "",
            HostedAiCtaAction.None,
            null),

        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown hosted-AI state"),
    };
}

/// <summary>
/// The forbidden-language rules for the hosted-account copy (epic #937). Pure and unit-tested so the
/// copy is provably clean, not clean by reviewer memory.
/// </summary>
public static class HostedAiCopyRules
{
    /// <summary>Provider names that must never appear in hosted-account copy (case-insensitive).</summary>
    public static readonly IReadOnlyList<string> ForbiddenProviderNames = new[]
    {
        "openai", "groq", "whisper", "kokoro", "glm", "gpt", "anthropic", "claude",
    };

    /// <summary>Money/marketing phrases that must never appear in hosted-account copy (case-insensitive).</summary>
    public static readonly IReadOnlyList<string> ForbiddenPhrases = new[]
    {
        "free credit", "subscription", "premium", "tier", "free trial",
    };

    /// <summary>
    /// Return the forbidden terms found in <paramref name="text"/> (empty when the text is clean). The
    /// hosted-account states must produce an empty list.
    /// </summary>
    public static IReadOnlyList<string> FindViolations(string text)
    {
        var hits = new List<string>();
        if (string.IsNullOrEmpty(text)) return hits;
        var lower = text.ToLowerInvariant();
        foreach (var term in ForbiddenProviderNames)
            if (lower.Contains(term)) hits.Add(term);
        foreach (var term in ForbiddenPhrases)
            if (lower.Contains(term)) hits.Add(term);
        return hits;
    }

    /// <summary>True when <paramref name="text"/> contains no forbidden hosted-copy term.</summary>
    public static bool IsClean(string text) => FindViolations(text).Count == 0;
}
