using System.Text.Json;
using CcDirector.Avalonia;
using CcDirector.Gateway.Contracts;

namespace CcDirector.StateAgreementCheck;

/// <summary>
/// THE COMPARISON - the mission's proof, in one testable place (specification section 6).
///
/// It is a library rather than a lump inside Main for one reason: <em>a check nobody has watched fail is
/// worth nothing.</em> This mission's own law is that a green test proves nothing until you have seen it
/// go red with the reported symptom, and the same standard has to apply to the thing doing the proving.
/// So the comparison is callable with a hand-made roster, and
/// <c>AgreementCheckFaultInjectionTests</c> feeds it the SIX ORIGINAL DISAGREEING SESSIONS and watches it
/// report them.
///
/// THE ONE RULE: it calls the REAL fold (<see cref="SessionOrdering"/>) and the REAL palettes
/// (<see cref="StatusPalette"/> for the rail, the parsed shipping table for the clients). It
/// re-implements NOTHING. A check that re-derives the ladder becomes fold number fifteen - internally
/// consistent, consistent with nothing else - which is the exact sin this mission exists to end,
/// committed by the tool built to prove the sin is gone.
/// </summary>
public static class AgreementCheck
{
    /// <summary>One disagreement, named by what it is rather than by a code.</summary>
    /// <param name="Kind">
    /// <c>unstamped</c> - the Gateway sent no answer (defect 15); <c>stamp-not-fold</c> - the stamped
    /// answer is not the shared fold's (defects 6 and 7); <c>law-broken</c> - a working session is not
    /// blue; <c>desktop-vs-gateway</c> - the rail's fold and the Gateway's answer differ (defect 5's
    /// family); <c>two-different-pixels</c> - both surfaces fold to the same NAME and paint different
    /// hexes (defect 18 - invisible to a check that compares the fold's answer);
    /// <c>palette-missing</c> - a surface cannot paint the colour it was given;
    /// <c>indeterminate</c> - the row does not carry enough to judge, so the check refuses to call it
    /// either way (see <see cref="IsIndeterminate"/>). It is REPORTED, never skipped: a row the
    /// instrument cannot read is not a row that agrees, and quietly counting it as agreement is how a
    /// measurement turns into a lie.
    /// </param>
    public sealed record Finding(string? SessionId, string Name, string Kind, string Detail);

    /// <summary>
    /// One check's verdict, and the ONLY honest way to state it: what it found, and how many rows it
    /// never got to look at.
    ///
    /// "FOUND NOTHING" AND "PASSED" ARE DIFFERENT CLAIMS, and collapsing them is the defect this type
    /// exists to make impossible. An unstamped row is terminal - Compare cannot compare a stamp that is
    /// not there, so it stops and the fold, law, desktop and palette checks never run on that row. The
    /// first version of this reported those four as PASS on such a fleet, because no finding came back
    /// from checks that had not executed. Absence of evidence, printed as evidence.
    ///
    /// So a verdict carries <see cref="NotGraded"/> and cannot claim the fleet without it.
    /// </summary>
    public sealed record CheckVerdict(string Name, int Failures, int NotGraded, int LiveSessions)
    {
        /// <summary>Rows this check actually ran on.</summary>
        public int Graded => LiveSessions - NotGraded;

        /// <summary>Found nothing WHERE IT RAN. Not the same as "holds over the fleet" - see Line.</summary>
        public bool Passed => Failures == 0;

        /// <summary>Found nothing AND ran on every live session. The only basis for an unqualified pass.</summary>
        public bool PassedEverywhere => Failures == 0 && NotGraded == 0;

        /// <summary>
        /// The verdict as printed. It states BOTH halves, always: what the check found, and what it never
        /// looked at. Neither half may be dropped because the other is interesting.
        ///
        /// The first version returned early on <c>Failures &gt; 0</c> and silently discarded
        /// <see cref="NotGraded"/>. So a check that failed on one row AND never ran on another printed a
        /// bare "FAIL (1)" beneath a header reading "over 2 live session(s)" - a complete-looking verdict
        /// over a fleet it had only half examined. Not the bare PASS of the previous bug; the same
        /// claim-without-evidence one step sideways, and it survived because the tests covered
        /// failure-only and not-graded-only and never both at once.
        ///
        /// The lesson that finally stuck, on the tenth pass: A PARTIAL TRUTH IS THE FAILURE MODE, not an
        /// acceptable summary of a complicated one. Every version of this bug has been some true half
        /// printed without its qualifier.
        /// </summary>
        public string Line =>
            (Failures, NotGraded) switch
            {
                (0, 0) => "PASS",
                (0, _) => $"pass on {Graded} of {LiveSessions} - NOT GRADED on {NotGraded}",
                (_, 0) => $"FAIL ({Failures})",
                _ => $"FAIL ({Failures}) on {Graded} of {LiveSessions} - NOT GRADED on {NotGraded}",
            };
    }

    /// <summary>
    /// The numbers the run publishes: how many real disagreements, over how many sessions, and each
    /// check's verdict - including the rows it never reached, so the prose reporting them cannot claim a
    /// check passed where it did not run.
    /// </summary>
    /// <param name="IndeterminateRows">
    /// A CAUSE, not a check's scope - the distinction that produced the eleventh finding on pull request
    /// 1606. This counts rows the Gateway made unreadable. It is NOT "the rows the desktop check skipped":
    /// that is <c>DesktopAgreed.NotGraded</c>, which is this PLUS the unstamped rows, because an unstamped
    /// row stops the desktop comparison too.
    ///
    /// The two sat side by side under names close enough to swap, and the explanatory prose duly read the
    /// narrow one under the broad one's meaning - announcing "the desktop comparison was not graded on 1
    /// row" on a fleet where it had not been graded on two, and adding that every other check ran on them,
    /// which was true of one and false of the other.
    ///
    /// THE RULE THIS ENCODES, and it is the eleventh instance of the same lesson: PROSE IS RENDERED FROM
    /// <see cref="AllChecks"/> AND NOTHING ELSE. A cause count is an input to a verdict, never a thing to
    /// narrate from. If you find yourself printing this number, you are about to describe a check's scope
    /// using a cause's count, and they are not the same set.
    /// </param>
    public sealed record Summary(
        int Disagreements,
        int LiveSessions,
        int IndeterminateRows,
        int Unstamped,
        int StampNotFold,
        int LawBroken,
        int DesktopVsGateway,
        int TwoDifferentPixels,
        int PaletteMissing)
    {
        /// <summary>Check 1. It runs on every row - nothing can stop it, so it is never not-graded.</summary>
        public CheckVerdict StampPresent => new("the stamp is present", Unstamped, 0, LiveSessions);

        // Checks 2, 3 and 5 run on every row EXCEPT the ones check 1 stopped: an unstamped row has no
        // stamped answer to compare a fold against, no colour to test the law on, and no colour to look
        // up in a palette. They are not-graded there, never passed.
        public CheckVerdict StampIsFold => new("the stamped answer IS the shared fold's", StampNotFold, Unstamped, LiveSessions);
        public CheckVerdict Law => new("the LAW: working => blue", LawBroken, Unstamped, LiveSessions);
        public CheckVerdict SamePixels => new("every colour is the SAME HEX on both palettes",
            TwoDifferentPixels + PaletteMissing, Unstamped, LiveSessions);

        // Check 4 is stopped by BOTH: an unstamped row (nothing to compare) and an indeterminate one (the
        // Gateway destroyed the fact it needs). The only check with two ways to be ungradeable.
        public CheckVerdict DesktopAgreed => new("the desktop's fold == the Gateway's",
            DesktopVsGateway, Unstamped + IndeterminateRows, LiveSessions);

        public IReadOnlyList<CheckVerdict> AllChecks => new[] { StampPresent, StampIsFold, Law, SamePixels, DesktopAgreed };
    }

    /// <summary>
    /// THE ARITHMETIC OF THE HEADLINE NUMBER, in a function, because it is the sentence people quote and
    /// it had been wrong twice - both times found by a reader reasoning about console output, which is
    /// the least reliable way to find anything.
    ///
    /// This mission's own lesson, applied to itself for the third time: an unbindable seam is an
    /// untestable one. The counting lived inline in Program.Report, so no test could reach it, so it
    /// drifted out of step with Compare twice without a single test going red. Now it is bindable and
    /// AgreementSummaryTests pins it.
    ///
    /// THE RULE IT ENCODES: every live session IS checked. Only ONE of the five checks - the desktop
    /// comparison - can be defeated by an indeterminate row, and only on that row; the stamp, fold, LAW
    /// and palette checks run on everything and stand. So the denominator is every live session, and an
    /// unreadable row's CERTAIN findings are counted like anyone else's.
    ///
    /// The version before this subtracted indeterminate rows from the denominator, counted their certain
    /// findings in the numerator anyway, and then printed "the number above says nothing about them" -
    /// three statements that cannot all be true at once. It was introduced by the fix that let an
    /// unreadable row keep reporting its certain defects, which is the honest behaviour; the arithmetic
    /// simply did not follow it there. Found by inspection of pull request 1606.
    /// </summary>
    public static Summary Summarize(IReadOnlyList<SessionDto> roster, IReadOnlyList<Finding> findings)
    {
        int Count(string kind) => findings.Count(f => f.Kind == kind);
        var indeterminate = Count("indeterminate");
        return new Summary(
            Disagreements: findings.Count - indeterminate,
            LiveSessions: roster.Count,
            IndeterminateRows: indeterminate,
            Unstamped: Count("unstamped"),
            StampNotFold: Count("stamp-not-fold"),
            LawBroken: Count("law-broken"),
            DesktopVsGateway: Count("desktop-vs-gateway"),
            TwoDifferentPixels: Count("two-different-pixels"),
            PaletteMissing: Count("palette-missing"));
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Compare every surface's answer for every session in a roster. <paramref name="clientPalette"/> is
    /// the table the phone and the Cockpit actually ship, read by <see cref="ClientPalette.Read"/> -
    /// never a copy typed here.
    /// </summary>
    public static IEnumerable<Finding> Compare(
        IReadOnlyList<SessionDto> roster, IReadOnlyDictionary<string, string> clientPalette)
    {
        foreach (var row in roster)
        {
            var name = row.Name ?? row.SessionId ?? "(unnamed)";

            // ---- 1. THE STAMP IS PRESENT. Defect 15: GET /sessions/{sid} used to return these null while
            // the DTO documents them "Required on Gateway /sessions responses". A client that cannot get a
            // stamped answer fails loudly (magenta on the rail, a blank page in the Cockpit), so a null
            // stamp is a rendered defect, not a cosmetic one.
            if (string.IsNullOrEmpty(row.EffectiveColor) || string.IsNullOrEmpty(row.StateLabel)
                || string.IsNullOrEmpty(row.TriageBucket))
            {
                yield return new Finding(row.SessionId, name, "unstamped",
                    $"the Gateway returned effectiveColor='{row.EffectiveColor}', stateLabel='{row.StateLabel}', " +
                    $"triageBucket='{row.TriageBucket}' - a client cannot render an answer that is not there.");
                continue;
            }

            // ---- 2. THE STAMPED ANSWER IS THE FOLD'S ANSWER. Catches a row stamped by a different code
            // path than the shared fold - defect 6 (the Exes page folding without the fleet pass) - and the
            // shape of defect 7 (a dot and a label in one row from two authorities).
            var foldColor = SessionOrdering.EffectiveColor(row);
            var foldLabel = SessionOrdering.StateLabel(row);
            var foldBucket = SessionOrdering.Classify(row) switch
            {
                SessionOrdering.TriageBucket.NeedsYou => "needsYou",
                SessionOrdering.TriageBucket.OnHold => "onHold",
                _ => "active",
            };

            if (!string.Equals(foldColor, row.EffectiveColor, StringComparison.OrdinalIgnoreCase))
                yield return new Finding(row.SessionId, name, "stamp-not-fold",
                    $"the Gateway stamped colour '{row.EffectiveColor}' but the shared fold says '{foldColor}'.");
            if (!string.Equals(foldLabel, row.StateLabel, StringComparison.Ordinal))
                yield return new Finding(row.SessionId, name, "stamp-not-fold",
                    $"the Gateway stamped label '{row.StateLabel}' but the shared fold says '{foldLabel}'.");
            if (!string.Equals(foldBucket, row.TriageBucket, StringComparison.OrdinalIgnoreCase))
                yield return new Finding(row.SessionId, name, "stamp-not-fold",
                    $"the Gateway stamped bucket '{row.TriageBucket}' but the shared fold says '{foldBucket}'.");

            // ---- 3. THE LAW. If a session is working, it is BLUE. Always. Nothing outranks working.
            // Measured over the real fleet rather than over a constructed DTO.
            var working = string.Equals(row.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(row.ActivityState, "Starting", StringComparison.OrdinalIgnoreCase);
            if (working && !string.Equals(row.EffectiveColor, "blue", StringComparison.OrdinalIgnoreCase))
                yield return new Finding(row.SessionId, name, "law-broken",
                    $"activityState='{row.ActivityState}' but the fleet shows '{row.EffectiveColor}' " +
                    $"('{row.StateLabel}'). THE LAW: if a session is working it is BLUE, always.");

            // ---- 4. THE DESKTOP'S ANSWER. The real fold over the reconstructed desktop input, and the
            // ONLY check on this row that the destroyed BriefingState can defeat.
            //
            // THE REFUSAL LIVES HERE, NOT AT THE TOP OF THE LOOP. It used to sit above check 1 and
            // `continue` - so an unreadable row skipped the stamp check, the fold check, the LAW check and
            // the palette check as well, every one of which is Gateway-side and CERTAIN regardless of what
            // the Director's briefing label had been. An ambiguous row could therefore hide a definite
            // stamp-not-fold or a broken law behind "I cannot read this one". Refusing to answer the
            // question you cannot answer is honest; refusing to answer the four you can is just silence
            // with better manners. Found by inspection of pull request 1606.
            if (IsIndeterminate(row))
            {
                yield return new Finding(row.SessionId, name, "indeterminate",
                    "VoiceGenerating with BriefingState=\"Briefing\": the Gateway stamps that label only " +
                    "when the Director's own value was null/None/Briefed, but stamps VoiceGenerating " +
                    "unconditionally - so this row cannot say whether the desktop folds yellow (the " +
                    "Director was briefing too) or red (the Gateway overwrote it), and those differ in " +
                    "whether they agree. The desktop comparison is NOT graded for this row; every other " +
                    "check above and below it is, and stands.");
            }
            else
            {
                var desktopInput = ToDesktopInput(row);
                var desktopColor = SessionOrdering.EffectiveColor(desktopInput);
                var desktopLabel = SessionOrdering.StateLabel(desktopInput);

                if (!string.Equals(desktopColor, row.EffectiveColor, StringComparison.OrdinalIgnoreCase))
                    yield return new Finding(row.SessionId, name, "desktop-vs-gateway",
                        $"the phone and the Cockpit show '{row.EffectiveColor}' ({row.StateLabel}); the desktop rail " +
                        $"folds '{desktopColor}' ({desktopLabel}) from the facts it has. {WhyDesktopDiffers(row)}");
                else if (!string.Equals(desktopLabel, row.StateLabel, StringComparison.Ordinal))
                    yield return new Finding(row.SessionId, name, "desktop-vs-gateway",
                        $"same colour, different words: the phone reads '{row.StateLabel}', the desktop reads " +
                        $"'{desktopLabel}'. {WhyDesktopDiffers(row)}");
            }

            // ---- 5. THE RENDERED PIXEL. The hole the Phase 4 Manager found, and it would have made this
            // whole measurement worthless: comparing the fold's ANSWER reports ZERO while two screens paint
            // different colours. Before Phase 4 the rail's red was #EF4444 and the client's #F14C4C, and the
            // rail's yellow #EAB308 against #F59E0B - both surfaces folded to "red", agreed perfectly, and
            // painted different pixels. Law 7 is "every device shows the same thing, always", and the thing
            // the owner sees is a pixel, not a string. So resolve each surface's answer THROUGH ITS OWN
            // PALETTE and compare the hex.
            var desktopHex = StatusPalette.HexFor(row.EffectiveColor).ToUpperInvariant();
            if (!clientPalette.TryGetValue(row.EffectiveColor, out var clientHex))
            {
                yield return new Finding(row.SessionId, name, "palette-missing",
                    $"the Gateway emitted colour '{row.EffectiveColor}' and the client palette " +
                    $"({ClientPalette.RelativePath}) has no entry for it - the phone cannot paint it.");
            }
            else if (!string.Equals(desktopHex, clientHex, StringComparison.OrdinalIgnoreCase))
            {
                yield return new Finding(row.SessionId, name, "two-different-pixels",
                    $"both surfaces fold to '{row.EffectiveColor}' and AGREE - and then paint different colours: " +
                    $"the desktop rail {desktopHex}, the phone and Cockpit {clientHex}. Law 7 is 'every device " +
                    "shows the same thing, always'.");
            }
            else if (!StatusPalette.Knows(row.EffectiveColor))
            {
                yield return new Finding(row.SessionId, name, "palette-missing",
                    $"the desktop palette does not know '{row.EffectiveColor}' - the rail renders the magenta " +
                    "BROKEN sentinel for this session.");
            }
        }
    }

    /// <summary>
    /// Reconstruct the DESKTOP's fold input from a live Gateway row, by removing exactly the facts the
    /// Gateway adds and <c>ControlEndpoints.Map</c> does not carry.
    ///
    /// WHY A RECONSTRUCTION AND NOT A READ. The desktop never uses HTTP: SessionViewModel runs INSIDE the
    /// Director process and folds the in-process Session directly. The Director's own GET /fleet/sessions
    /// RELAYS the Gateway's already-stamped roster when a Gateway is attached (ControlEndpoints.cs:321-330
    /// -> GatewayClient.ListFleetSessionsAsync -> GET /sessions) - verified by probing a live Director, not
    /// by reading the code. So folding THAT and comparing it to the Gateway would be one function agreeing
    /// with itself: a guaranteed, meaningless ZERO. This is the check's one modelled step, so every removal
    /// below is cited and none is a guess.
    ///
    /// Verified by reading ControlEndpoints.Map (ControlEndpoints.cs:1188 onward): it carries ActivityState,
    /// Crashed, BriefingState (the DIRECTOR's own enum), VoiceMode, HoldState/OnHold, IsTranscribing,
    /// IsBrandNew, IsBackgroundRunning, IsAutoExplaining, WingmanEnabled, IsControlled, ControllerSessionId,
    /// StatusColor - and SessionRole, read from Session.GatewayResolvedRole, written ONLY by the Gateway's
    /// set-resolved-role verb (defect 5's fix).
    ///
    /// It does NOT carry these four, which the Gateway stamps in its roster loop (GatewayEndpoints.cs
    /// ~:770-790) and which NOTHING pushes down - the Gateway sends exactly two verbs to a Director,
    /// "launch" and "set-resolved-role". Each is a fold input, so each is a real divergence:
    ///   * DictationStatus - the durable phone-dictation record. Gateway: orange. Desktop: cannot see it.
    ///   * Transcribing - the Gateway's active-run mark. Gateway: orange. Desktop: cannot see it.
    ///   * VoiceGenerating - drives IsVoicePreparing. Gateway: yellow. Desktop: cannot see it.
    ///   * The OnHold expiry overlay - the Gateway owns the snooze clock and overlays OnHold=false BEFORE
    ///     the fold. The desktop reads the Director's raw hold. This one is TRANSIENT, not structural: the
    ///     sweep nudges a live Director off hold within its 15-second interval (SnoozeExpirySweep).
    ///
    /// KNOWN LIMIT, stated rather than papered over: the desktop's OWN SessionRole is not externally
    /// observable, because the Gateway's fleet pass overwrites the inbound role on every read
    /// (FleetRoleResolver.Stamp - every branch assigns). So a role that is STALE on a Director cannot be
    /// seen from here. That push is proved in-process, with nothing hand-set, by DesktopRoleStampWireProofTests.
    /// </summary>
    /// <summary>
    /// TRUE when this row cannot be judged from what it carries, so the check must not call it either
    /// way. Exactly one shape today, and it is the Gateway OVERWRITING a Director fact rather than
    /// adding one:
    ///
    /// <c>VoiceGenerating == true &amp;&amp; BriefingState == "Briefing"</c> has two possible origins and
    /// the row cannot tell them apart. GatewayEndpoints stamps <c>BriefingState = "Briefing"</c> ONLY
    /// when the Director's own value was null/None/Briefed - but it stamps <c>VoiceGenerating</c>
    /// UNCONDITIONALLY. So either:
    ///
    ///   (a) the Gateway overwrote a null/None/Briefed - the desktop never sees the stamp, folds RED,
    ///       and this is a real disagreement; or
    ///   (b) the Director genuinely WAS briefing, the guard was false, nothing was overwritten - the
    ///       desktop has "Briefing" too, folds YELLOW, and this agrees.
    ///
    /// The overwrite destroyed the fact needed to tell (a) from (b). Reporting agreement would be a
    /// guess, and so would reporting a disagreement. This mission's own law: never state what you have
    /// not observed. So it is reported as indeterminate and the number of sessions the instrument could
    /// not read is published beside the number that agreed.
    ///
    /// The DEEPER fix is not here: the Gateway should not overwrite a Director-owned field to carry its
    /// own fact - it should add one, the way VoiceGenerating already does. That is a change to the
    /// Gateway's enrichment, not to this instrument, and it is recorded as a gap rather than smuggled in
    /// here. Until then this check refuses to grade what it cannot see.
    ///
    /// Found by independent inspection of pull request 1606: it ran the tool against the live fleet, saw
    /// a zero with two Gateway-only fold inputs in play, and did not believe it.
    ///
    /// IT ASKS WHETHER THE AMBIGUITY CHANGES THE VERDICT, rather than listing when it might. Three cuts
    /// of this predicate were wrong before this one, each a list, each wrong one rung from the last:
    ///
    ///   1. "VoiceGenerating && Briefing" - refused to grade WORKING sessions. Blue outranks everything,
    ///      so both origins fold blue. An existing control caught it.
    ///   2. "...&& IsRawRed" - still refused rows where a HOLD or a DESKTOP dictation had already won.
    ///      Grey and orange sit above briefing too. The inspector caught it.
    ///   3. "do both plausible desktop rows fold the same colour?" - closer, and still refused a phone
    ///      dictation, because the DESKTOP CANNOT SEE a phone dictation: strip it and the two origins
    ///      fold different colours (yellow or red). My own negative controls caught it.
    ///
    /// Every one of those was a new list, and every list was a new chance to be wrong. So this asks the
    /// only question the check actually answers - IS THERE A FINDING? - and calls the real ladder to
    /// answer it, rather than describing a ladder that can grow a rung tomorrow.
    ///
    /// The subtlety cut 3 missed: two plausible desktop rows can fold DIFFERENT colours and still both
    /// disagree with the Gateway. A phone dictation is Gateway-orange while the desktop folds yellow or
    /// red - we do not know which, and we do not need to: neither is orange, so the disagreement is
    /// certain even though its exact shape is not. That is reportable. The row is only unreadable when
    /// one origin AGREES and the other does not, because then the destroyed fact decides whether there is
    /// anything to report at all.
    ///
    /// Crying wolf costs the same trust as missing one. An instrument that refuses to read rows it can
    /// read perfectly well gets ignored just as fast as one that reads them wrong.
    ///
    /// The enumerating habit is the defect, not any one of its lists.
    /// </summary>
    public static bool IsIndeterminate(SessionDto row)
    {
        // The only shape where a Director fact was destroyed - see above. Everything else is readable.
        if (!row.VoiceGenerating || row.BriefingState != "Briefing")
            return false;

        // (b) the Director genuinely WAS briefing: the label it carries is its own.
        var ifDirectorWasBriefing = ToDesktopInput(row);

        // (a) the Gateway overwrote a null/None/Briefed. "None" stands for all three: none of them is
        // "Briefing", so none folds yellow via IsBriefing, so they are fold-equivalent here.
        var ifGatewayOverwrote = ToDesktopInput(row);
        ifGatewayOverwrote.BriefingState = "None";

        // THE QUESTION IS THE VERDICT, NOT THE COLOUR - and getting that wrong is what made the first
        // version of this refuse rows it could grade.
        //
        // The check reports DISAGREEMENTS. So the destroyed fact only defeats it when it changes the
        // ANSWER TO THAT QUESTION, and it often does not. A phone dictation folds the Gateway orange
        // while the desktop - which cannot see phone dictation at all - folds yellow or red depending on
        // the lost label. Two different desktop colours, and they DISAGREE WITH THE GATEWAY EITHER WAY.
        // We do not know exactly what the desktop shows; we know for certain it does not match. That is
        // a reportable disagreement, so grade it.
        //
        // It is indeterminate only when one plausible origin AGREES and the other does not - when the
        // destroyed fact decides whether there is a finding at all.
        var gatewayAnswer = row.EffectiveColor;
        var briefingAgrees = string.Equals(SessionOrdering.EffectiveColor(ifDirectorWasBriefing), gatewayAnswer, StringComparison.OrdinalIgnoreCase);
        var overwrittenAgrees = string.Equals(SessionOrdering.EffectiveColor(ifGatewayOverwrote), gatewayAnswer, StringComparison.OrdinalIgnoreCase);

        return briefingAgrees != overwrittenAgrees;
    }

    public static SessionDto ToDesktopInput(SessionDto row)
    {
        var copy = JsonSerializer.Deserialize<SessionDto>(JsonSerializer.Serialize(row, Json), Json)!;

        copy.DictationStatus = null;
        copy.Transcribing = false;
        copy.VoiceGenerating = false;

        // Un-apply the Gateway's expiry overlay: it set OnHold=false and flagged SnoozeExpired, so the
        // Director's own hold still reads Held until the sweep's nudge reaches it.
        if (row.SnoozeExpired)
        {
            copy.HoldState = HoldStates.Held;
            copy.SnoozeExpired = false;
        }

        // The desktop computes these itself - it must never read the Gateway's.
        copy.EffectiveColor = null;
        copy.StateLabel = null;
        copy.TriageBucket = null;

        return copy;
    }

    /// <summary>Name, in plain English, which Gateway-only fact produced a desktop/Gateway divergence.
    /// Stated from the row's own fields - never guessed. When no field explains it, say so rather than
    /// inventing a cause: a fabricated cause reads exactly like a finding and sends the next reader to fix
    /// a world that does not exist.</summary>
    public static string WhyDesktopDiffers(SessionDto row)
    {
        var reasons = new List<string>();
        if (row.DictationStatus is not null)
            reasons.Add($"the Gateway can see a phone dictation ('{row.DictationStatus}') and the desktop cannot");
        if (row.Transcribing)
            reasons.Add("the Gateway can see its own transcription mark and the desktop cannot");
        if (row.VoiceGenerating)
            reasons.Add("the Gateway can see voice being prepared and the desktop cannot");
        if (row.SnoozeExpired)
            reasons.Add("the Gateway's snooze clock has expired and the Director has not been nudged off hold yet");
        return reasons.Count == 0
            ? "Cause NOT identified from this row's fields - do not guess one; go and look."
            : "Because " + string.Join("; and ", reasons) + ".";
    }
}
