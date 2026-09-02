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
    /// <summary>
    /// The GENERIC, provider-neutral heads-up shown when a ready clip was served by the BACKUP voice
    /// provider (the TTS-fallback mission). Single-source and rendered verbatim by every client. It
    /// deliberately names NO provider (the host non-disclosure rule has no carve-out) and states there is
    /// no extra charge, because the member is billed the normal rate on a fallback. Owner-approved wording.
    /// </summary>
    /// <summary>
    /// How long a session may wait for its voice before the screen stops promising one and says it did
    /// not arrive.
    ///
    /// Three minutes because that is the number this product already committed to: SessionOrdering's
    /// note on IsVoicePreparing says voice generation should average under a minute and that "anything
    /// over three minutes is an exception to be flagged and fixed". This is that flag, finally built -
    /// it does not invent a new standard, it renders the one already written down.
    ///
    /// Erring long on purpose. A false "did not arrive" on a narration that lands at three minutes and
    /// one second costs a moment of doubt; a promise that never ends cost forty-eight minutes of a
    /// person believing the product was working on something.
    /// </summary>
    public static readonly TimeSpan GaveUpAfter = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The wait, in whole minutes and hours, or null under a minute. ONE ladder, matching the one the
    /// roster already uses for needs-you waits (client-core sessions/waiting.ts durationFromMs) so the two
    /// elapsed times on a card cannot describe the same kind of span two different ways.
    ///
    /// Null under a minute rather than "0m": the healthy case is a two-second synthesis, and a card that
    /// announces "0m" on every ordinary turn trains the reader to ignore the number that matters.
    /// </summary>
    internal static string? WaitedLabelFor(DateTime? waitingSince, DateTime utcNow)
    {
        if (waitingSince is not { } since) return null;
        var elapsed = utcNow - since;
        if (elapsed < TimeSpan.FromMinutes(1)) return null;
        var days = (int)elapsed.TotalDays;
        if (days >= 1) return $"{days}d {elapsed.Hours}h";
        if (elapsed.TotalHours >= 1) return $"{elapsed.Hours}h {elapsed.Minutes}m";
        return $"{(int)elapsed.TotalMinutes}m";
    }

    public const string BackupVoiceNotice =
        "Some voice providers are overloaded right now, so we switched you to a backup voice. No extra charge.";

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
    /// <param name="servedViaFallback">This turn's ready clip was made by the BACKUP voice provider (the
    /// primary was overloaded and the cloud proxy quietly failed over). A SUCCESS-with-a-note: it only ever
    /// rides the green <c>ready</c> verdict and adds the generic <see cref="BackupVoiceNotice"/> - it is not
    /// an unavailable/outage state and changes nothing else. Ignored unless there is playable audio.</param>
    /// <param name="waitingSince">When this session's wait for voice began (SessionDto.VoiceWaitingSince),
    /// or null when it is not waiting. Past <see cref="GaveUpAfter"/> the verdict becomes a terminal
    /// "gave up" instead of another calm "on its way" - see that field for why a promise with no end is
    /// worse than an admission.</param>
    /// <param name="utcNow">Now, injected so the give-up boundary is testable without waiting for it.</param>
    /// <param name="automaticAttempts">How many AUTOMATIC narration attempts for this turn have ended with no
    /// audio (the Gateway's own count, see <see cref="VoiceRetryPolicy"/>). It words the gave-up verdict
    /// ("2 of 5 tries") and, once the schedule is used up, turns the Generate button ON: the Gateway has had
    /// its turns, and the one honest thing left to offer is a way to try again on purpose.</param>
    public static VoiceDisplay Fold(bool voiceMode, bool agentWorking, bool hasAudio, bool generating, HostedAiState? unavailable, bool nothingToNarrate, bool servedViaFallback = false, DateTime? waitingSince = null, DateTime? utcNow = null, int automaticAttempts = 0)
    {
        // Computed once, up front, because more than one verdict below carries it: the calm "on its way"
        // wants it so a healthy wait can be seen climbing, and the give-up verdict wants it in its own
        // sentence. Null under a minute - see WaitedLabelFor.
        var waited = WaitedLabelFor(waitingSince, utcNow ?? DateTime.UtcNow);

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
            return new VoiceDisplay
            {
                Kind = "ready",
                Tone = "green",
                Label = "Voice ready",
                Message = "",
                CanPlay = true,
                // A backup-served clip is still a normal, playable "ready" - just with a generic heads-up.
                // The failover is never surfaced as an outage; it only adds this one verbatim line.
                VoiceFallbackNotice = servedViaFallback ? BackupVoiceNotice : null,
            };

        // Being made right now: a calm "on its way", no button (it is already happening).
        if (generating)
            return new VoiceDisplay
            {
                Kind = "preparing",
                Tone = "yellow",
                Label = "Voice on its way",
                Message = "The wingman is preparing this turn's narration.",
                WaitedLabel = waited,
            };

        // GAVE UP. The one state the voice screen could never reach before, and the reason a session
        // could sit on "voice on its way" for forty-eight minutes: every arm below is either a calm
        // "still coming" or a specific fault, and a narration that simply never arrives matches the
        // calm one forever. IsVoicePreparing's own comment named a terminal gave-up state as the
        // correct answer to that wedge; this is it.
        //
        // Keyed on ELAPSED TIME first, because a clock means the same thing to the reader on a busy Gateway
        // and a quiet one: "nothing has arrived in three minutes". The COUNT of automatic attempts is the
        // second key, and it became a meaningful one on 1 September 2026, when the sweep stopped retrying
        // every 45 seconds and started running the schedule in VoiceRetryPolicy - a fixed number of tries,
        // a fixed number of minutes apart, per turn. The count is what the verdict WORDS ("2 of 5 tries")
        // and what decides the button: while tries remain, the Gateway is genuinely still trying and the
        // button stays off; once they are spent, the Gateway has stopped and the button is the only way
        // forward, so it comes on. An exhausted schedule gives up even if the clock somehow has not.
        //
        // WHERE IT SITS, and the two versions of this that were wrong before review caught them:
        //
        //   above it  - off, working, hasAudio, generating. A playable clip or a live attempt must never
        //               be hidden behind a verdict about how long the wait has been.
        //   also above - EVERY SPECIFIC, ACTIONABLE REASON: service down, out of credits, cap reached,
        //               finish setup. The first draft put gaveUp ahead of these, so a member who needed
        //               to add credit was told "voice did not arrive" instead - the actionable sentence
        //               replaced by a symptom. That is the same defect as a terminal read hiding a
        //               standing account condition, which this codebase fixed once already today.
        //   also above - nothingToNarrate. A session parked on a menu was never going to be narrated, so
        //               "it did not arrive" reports a failure that never happened.
        //
        // What gaveUp DOES displace is Retrying and notReady - the two states whose whole content is "be
        // patient". Those are the sentences that were still being said at forty-eight minutes.
        //
        // While automatic tries remain the sweep keeps trying and a success replaces this on the next
        // fold - what ends is the PROMISE, not the effort. Once the tries are spent the effort ends too,
        // and the verdict says so and offers the button.
        var clockRanOut = waitingSince is { } since
                          && (utcNow ?? DateTime.UtcNow) - since >= GaveUpAfter;
        var gaveUp = !nothingToNarrate
                     && (clockRanOut || VoiceRetryPolicy.IsExhausted(automaticAttempts));

        // An ANSWERED or in-progress hosted-AI condition. Reuse the single-source copy so the voice screen
        // matches the roster and every other surface for the same state - never a hand-written string here.
        switch (unavailable)
        {
            case HostedAiState.Retrying:
                // "Voice on its way" is true for a minute and a lie at forty-eight. Past the threshold the
                // retry stops being news and becomes the thing being reported.
                if (gaveUp) return GaveUpDisplay(waited, automaticAttempts);
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
            case HostedAiState.SubscriptionRequired:
            case HostedAiState.FairUseLimitReached:
            case HostedAiState.Unavailable:
                // Carries the shared call-to-action where one exists (add credit / raise the cap /
                // finish setup / view plans - the fair-use and unknown states have none). The CTA
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
        // The same rule for the plain "not made yet" window: honest for a moment, misleading for an hour.
        if (gaveUp) return GaveUpDisplay(waited, automaticAttempts);

        return new VoiceDisplay
        {
            Kind = "notReady",
            Tone = "neutral",
            Label = "No narration yet",
            Message = "There is no spoken summary for this turn yet.",
            CanGenerate = true,
            WaitedLabel = waited,
        };
    }

    /// <summary>
    /// THE ONE definition of "this session is waiting for its voice", used by every caller that stamps the
    /// clock AND readable beside the fold that consumes it.
    ///
    /// It existed twice, hand-written, before review: once in the roster aggregation and once in the
    /// display-push enrichment. They happened to agree character for character, which is exactly the
    /// condition under which two copies stay wrong together and then quietly diverge - nothing bound them,
    /// and no test would have noticed the day one of them changed.
    ///
    /// Deliberately NARROWER than "the fold shows a no-audio verdict". A session that is out of credits or
    /// whose speech service is down is not waiting for a narration that is coming - it is blocked, and
    /// counting that as waiting would start a clock whose only use is to declare a give-up that the
    /// blocked verdict already explains better. So the clock runs for voice sessions with no audio that
    /// are not mid-turn, and the fold decides what to SAY about that; the two questions are related but
    /// they are not the same question.
    /// </summary>
    public static bool IsWaitingForVoice(bool voiceMode, bool hasAudio, bool agentWorking)
        => voiceMode && !hasAudio && !agentWorking;

    /// <summary>
    /// The terminal verdict, in one place so the two arms that reach it cannot word it differently. It has
    /// two faces, decided by whether the Gateway's own retry schedule (<see cref="VoiceRetryPolicy"/>) is
    /// used up:
    ///
    ///   still trying - the Gateway has automatic tries left. The message says how many it has used and
    ///                  that it tries again in a few minutes, and there is NO button: a press would race
    ///                  the very retry that is about to happen, and the reader was promised the Gateway
    ///                  would try first.
    ///   stopped      - every automatic try is spent. The message says the Gateway has stopped trying on
    ///                  its own, and the Generate button comes ON. This is the face the owner asked for on
    ///                  1 September 2026, looking at "did not arrive after 19m" with nothing to press.
    ///
    /// The numbers in the words come from the same policy the sweep runs, never typed here, so the
    /// schedule the screen describes cannot drift from the schedule being kept.
    /// </summary>
    private static VoiceDisplay GaveUpDisplay(string? waited, int automaticAttempts)
    {
        var max = VoiceRetryPolicy.MaxAutomaticAttempts;
        var minutes = (int)VoiceRetryPolicy.RetryEvery.TotalMinutes;
        var stopped = VoiceRetryPolicy.IsExhausted(automaticAttempts);
        var message = stopped
            ? $"The Gateway tried {max} times and could not produce this turn's narration, so it has stopped "
              + "trying on its own. Generate it now to try again, or read the turn instead."
            : automaticAttempts <= 0
                ? $"This turn's narration has not been produced. The Gateway will try up to {max} times, "
                  + $"at least {minutes} minutes apart, and you can read the turn instead."
                : $"This turn's narration has not been produced. The Gateway has tried {automaticAttempts} of {max} times "
                  + $"and tries again after at least {minutes} minutes; you can read the turn instead.";
        return new VoiceDisplay
        {
            Kind = "gaveUp",
            Tone = "red",
            Label = waited is null ? "Voice did not arrive" : $"Voice did not arrive after {waited}",
            Message = message,
            // The button only once the Gateway has stopped: while it is still on the schedule a press would
            // re-run what is about to be re-run anyway; after it, the press is the only remaining way.
            CanGenerate = stopped,
            WaitedLabel = waited,
        };
    }

    /// <summary>The short badge headline for a blocked (account) state - the body text and the call-to-
    /// action still come from the shared <see cref="HostedAiMessages"/> / <see cref="HostedAiHttp.Dto"/>.</summary>
    private static string BlockedLabel(HostedAiState state) => state switch
    {
        HostedAiState.NeedsCredits => "Voice needs credit",
        HostedAiState.CapReached => "Monthly limit reached",
        HostedAiState.NeedsKey => "Finish setup",
        // The two Included AI refusals (issue #1360): no cost words, no numbers.
        HostedAiState.SubscriptionRequired => "Not included with this account",
        HostedAiState.FairUseLimitReached => "Monthly fair-use limit reached",
        _ => "Voice unavailable",
    };
}
