using CcDirector.Core.HostedAi;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.HostedAi;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// Folds one session's voice facts into the single <see cref="VoiceDisplay"/> verdict the Voice screen
/// renders verbatim. This is THE place the voice screen is ruled - the job that used to be spread across
/// the client (voiceAvailability.ts's nine-input guess plus the view's retrying / service-down / reason
/// branching). Pure and total so it is unit-tested directly, and so "add a voice state" is one edit here
/// instead of a new branch in every client.
///
/// The law it serves (docs/new_architecture/session-state.html): the client is dumb; all ruling - state,
/// colors, labels, and which actions are offered - is computed on the Gateway and pushed. The credit /
/// key / service-down / retrying copy is reused from the single-source <see cref="HostedAiMessages"/> so
/// the voice screen says exactly what every other surface says for the same condition.
/// </summary>
public static class VoiceDisplayFold
{
    /// <param name="voiceMode">The session is in voice mode (the Director's authoritative flag).</param>
    /// <param name="agentWorking">The agent is mid-turn (a blue / working activity state). The
    /// finished-turn narration is stale while it works, so this dominates: no play, no Generate button,
    /// just "the agent is working; the next completed turn will be narrated". Matches the pre-fold client
    /// rule where agent-working suppressed every other voice affordance.</param>
    /// <param name="hasAudio">The Gateway holds fetchable, playable audio for this turn (HasVoice).</param>
    /// <param name="generating">The wingman is producing this turn's narration right now (IsGenerating).</param>
    /// <param name="unavailable">Why the hosted leg could not make audio, or null - the recorded
    /// <see cref="HostedAiState"/> (Retrying / ServiceDown / NeedsCredits / CapReached / NeedsKey).</param>
    /// <param name="nothingToNarrate">The last turn has no text reply to read aloud - the session is
    /// waiting on a prompt / menu, so there is genuinely nothing to narrate (a NON-failure, distinct from
    /// "not made yet"). This is the fact that used to reach the client as a bare null and got rendered as
    /// a dead-end Generate button.</param>
    public static VoiceDisplay Fold(bool voiceMode, bool agentWorking, bool hasAudio, bool generating, HostedAiState? unavailable, bool nothingToNarrate)
    {
        // Not a voice session: the screen shows its own "off" card; there is no verdict to render.
        if (!voiceMode)
            return new VoiceDisplay { Kind = "off", Tone = "neutral", Label = "Voice off", Message = "" };

        // The agent is working: the finished-turn narration is stale, so offer nothing (no play, no
        // Generate) and say what is true - the next completed turn will be narrated. Dominates the rest.
        if (agentWorking)
            return new VoiceDisplay
            {
                Kind = "working",
                Tone = "yellow",
                Label = "Agent is working",
                Message = "The agent is working on the next step. The next completed turn will be narrated.",
            };

        // Playable audio wins over everything below. Even mid-regeneration we keep offering the existing
        // clip (issue #1322: never pull the rug on a listener), so has-audio is checked before generating.
        if (hasAudio)
            return new VoiceDisplay { Kind = "ready", Tone = "green", Label = "Voice ready", Message = "", CanPlay = true };

        // Being made right now: a calm "on its way", no button (it is already happening).
        if (generating)
            return new VoiceDisplay
            {
                Kind = "preparing",
                Tone = "yellow",
                Label = "Voice on its way",
                Message = "The wingman is preparing this turn's narration.",
            };

        // An ANSWERED or in-progress hosted-AI condition. Reuse the single-source copy so the voice screen
        // matches the roster and every other surface for the same state - never a hand-written string here.
        switch (unavailable)
        {
            case HostedAiState.Retrying:
                return new VoiceDisplay
                {
                    Kind = "retrying",
                    Tone = "yellow",
                    Label = "Voice on its way",
                    Message = HostedAiMessages.For(HostedAiState.Retrying).Text,
                };
            case HostedAiState.ServiceDown:
                return new VoiceDisplay
                {
                    Kind = "serviceDown",
                    Tone = "red",
                    Label = "Voice service down",
                    Message = HostedAiMessages.For(HostedAiState.ServiceDown).Text,
                };
            case HostedAiState.NeedsCredits:
            case HostedAiState.CapReached:
            case HostedAiState.NeedsKey:
                // Carries the shared call-to-action (add credit / raise the cap / finish setup). The CTA
                // IS a real action, so it rides in Reason; a generate button is still wrong (it would hit
                // the same wall), so CanGenerate stays false.
                return new VoiceDisplay
                {
                    Kind = "blocked",
                    Tone = "red",
                    Label = BlockedLabel(unavailable.Value),
                    Message = HostedAiMessages.For(unavailable.Value).Text,
                    Reason = HostedAiHttp.Dto(unavailable.Value),
                };
        }

        // Nothing to read aloud: the session needs the user, but on a prompt / menu, not a text reply. This
        // is the honest state that replaces the old "red badge next to a Generate button that can never
        // work". No button - generating cannot narrate a text reply that does not exist.
        if (nothingToNarrate)
            return new VoiceDisplay
            {
                Kind = "nothingToNarrate",
                Tone = "neutral",
                Label = "Nothing to read aloud",
                Message = "This session is waiting for you on a prompt, not a text answer, so there is nothing to read aloud yet.",
            };

        // Voice on, no audio, no reason, not being made, and not known-empty: a genuine "not made yet"
        // window (just entered voice, or a fresh turn before the sweep runs). Here - and ONLY here -
        // offering "Generate narration now" is honest, because there may be a text reply waiting to narrate.
        return new VoiceDisplay
        {
            Kind = "notReady",
            Tone = "neutral",
            Label = "No narration yet",
            Message = "There is no spoken summary for this turn yet.",
            CanGenerate = true,
        };
    }

    /// <summary>The short badge headline for a blocked (account) state - the body text and the call-to-
    /// action still come from the shared <see cref="HostedAiMessages"/> / <see cref="HostedAiHttp.Dto"/>.</summary>
    private static string BlockedLabel(HostedAiState state) => state switch
    {
        HostedAiState.NeedsCredits => "Voice needs credit",
        HostedAiState.CapReached => "Monthly limit reached",
        HostedAiState.NeedsKey => "Finish setup",
        _ => "Voice unavailable",
    };
}
