namespace CcDirector.Gateway.Util;

/// <summary>The verdict on one request from a session key, and the sentence explaining it. A refusal always
/// names its reason so an agent whose command breaks is debuggable from one log line.</summary>
public readonly record struct SessionKeyVerdict(bool Allowed, string Reason)
{
    public static readonly SessionKeyVerdict Allow = new(true, "");

    public static SessionKeyVerdict Refuse(string reason) => new(false, reason);
}

/// <summary>
/// What a SESSION KEY may call on the Gateway (Remove-the-network-port mission, phase 1b).
///
/// This began as the Gateway twin of the Director's ControlApiGuard.CheckSessionChild (deleted with the Director's listener; this guard is the surviving one), and it is written the same way and
/// for the same reason: it is an ALLOW LIST. Anything the product grows later is DENIED to an agent until
/// somebody deliberately adds it here - the opposite of a deny list, where every new dangerous route is open
/// until someone remembers to close it. A pure function on the method and path, so there is exactly one place
/// this rule lives and it is unit-testable without a server.
///
/// THE LINE IT DRAWS: AN AGENT MAY CHANGE HOW THE PRODUCT BEHAVES. IT MAY NOT CHANGE WHO IS ALLOWED IN.
/// That is the owner's ruling of 2026-08-03, and it is the single sentence to reason from when a new route
/// has to be classified. The point of running agents is not to have to use the interface for most things:
/// once the agents are set up, the owner maintains and CONFIGURES through them. So configuration is an
/// agent's work, and admission is not.
///
/// BEHAVES - allowed. The FLEET WORK an agent's command line does: see the roster, find repositories and
/// worktrees and machines, read a session's terminal, message/prompt/interrupt/hold/rename another session,
/// spawn one, take a mission or a role, mark itself done, and read and publish the fleet's shared skills and
/// workflows. Plus CONFIGURATION, in both directions: a Director's settings, the application's own settings
/// (the closed <c>/gateway</c> set in <see cref="IsApplicationSetting"/>), and handovers - which are content
/// agents produce, and which moving a session needs.
///
/// WHO IS ALLOWED IN - refused. Device registration, enrollment and revocation, because a credential that
/// can admit a NEW device is not configuring the product, it IS the boundary, and an agent holding one could
/// mint itself an account-wide credential and step straight out of this guard. Account-level identity:
/// sign-in and sign-out, ownership, billing, credits and subscription. Director registration and
/// de-registration. Force-killing a Director or shutting the Gateway down - agents already have a clean way
/// to end a session (<c>request-deletion</c>), and this one is flagged to the owner as a line he may want
/// moved. Also still refused, for the separate reason that they are not configuration at all: the
/// diagnostics surface, and voice, dictation and transcription DATA - note the distinction from the voice
/// SETTINGS above, which say how the product should behave and are therefore allowed. There is exactly ONE
/// opening in that dictation surface, <c>POST /ingest/dictionary/terms</c>, and it is an ADD ONLY grant -
/// see <see cref="IsDictionaryAdd"/>.
///
/// WHAT CHANGED, AND WHY THIS PARAGRAPH WAS REWRITTEN RATHER THAN AMENDED. Phase 1b refused the whole
/// <c>/directors</c> surface bar two sub-paths, and said so here in prose. The owner's ruling reverses that
/// for settings and handovers. The old sentence was not edited around, because an allow list whose stated
/// reasoning contradicts its contents is worse than a wrong entry: a wrong entry is visible in the code,
/// whereas prose that no longer describes the list is trusted by the next reader and quietly propagated.
///
/// A NOTE ON WHAT IS DELIBERATELY IN. Spawning a session and launching an application on a machine are both
/// CODE EXECUTION on a computer, and both are allowed here - because both are what the fleet's agents do all
/// day through the command line today, and phase 1b is a credential change, not a capability change.
/// Narrowing them is a product decision for the owner, not a decision to smuggle in behind a refactor. What
/// this guard DOES add over today is a boundary: the key is bound to one session and one tenant, so those
/// verbs can only ever act inside the account that issued it.
/// </summary>
public static class SessionKeyGuard
{
    /// <summary>
    /// Decide whether a session key may make this request. <paramref name="path"/> is the request path with
    /// no query string; the query is deliberately not consulted, because a rule that depends on a query
    /// parameter is a rule a caller can move.
    /// </summary>
    public static SessionKeyVerdict Check(string? method, string? path)
    {
        var verb = (method ?? "").ToUpperInvariant();
        var p = (path ?? "").TrimEnd('/');
        if (p.Length == 0) p = "/";

        // Matched lower-cased, because ASP.NET routing matches a path case-insensitively and a guard that
        // did not would be bypassed by /Sessions. Only the STRUCTURE is compared - the identifier segments
        // ({sid}, {id}) are never read here - so folding their case cannot change a decision.
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
            segments[i] = segments[i].ToLowerInvariant();

        if (IsAllowed(verb, segments))
            return SessionKeyVerdict.Allow;

        // A refusal on the dictation surface gets its OWN sentence, because the general one would send the
        // reader somewhere the truth is not. An agent that tried to replace the glossary has not run into
        // the admission boundary, and telling it about device enrollment and billing would have it hunting a
        // credential problem instead of reading the one line that answers it: the grant here is add only.
        if (segments.Length >= 2 && segments[0] == "ingest" && segments[1] == "dictionary")
            return SessionKeyVerdict.Refuse(
                $"a session key may not call {verb} {p}; the dictation dictionary is ADD ONLY for an agent - " +
                "POST /ingest/dictionary/terms with a 'terms' list adds words, and GET " +
                "/ingest/dictionary/additions reads back what agents added. Nothing else on this surface is " +
                "open to a session key, so an agent can never delete, rename or overwrite a term the person " +
                "relies on. Prune in the Cockpit dictionary editor");

        return SessionKeyVerdict.Refuse(
            $"a session key may not call {verb} {p}; it may run the fleet's agent routes and configure the " +
            "product - settings and handovers - but never the admission surface: device enrollment, account " +
            "identity, Director registration, or force-killing a Director");
    }

    private static bool IsAllowed(string verb, string[] s)
    {
        // ---------- Reads ----------
        if (verb is "GET" or "HEAD")
        {
            switch (Join(s))
            {
                // Liveness, and the discovery set the fleet preamble and the read verbs need to orient an
                // agent: who is running, where, in which repositories and worktrees, on which machines.
                case "healthz":
                case "sessions":
                case "repositories":
                case "worktrees":
                case "directors":
                case "launchers":
                case "machines":
                case "missions":
                case "gateway/about":
                case "gateway/snooze-presets":
                case "gateway/skills":
                case "gateway/workflows":
                case "gateway/workflow-runs":
                case "cron/jobs":
                    return true;
            }

            // One session (the roster row) and its terminal scrollback. The buffer carries whatever any agent
            // typed, so it is account-scoped by the key's tenant - a session key can only ever read a session
            // inside its own account, which the tenant binding enforces before the handler runs.
            if (s.Length == 2 && s[0] == "sessions") return true;
            if (s.Length == 3 && s[0] == "sessions" && s[2] == "buffer") return true;

            // A session's parsed conversation history - what `cc-history` reads. Exactly the same class of
            // read as the buffer beside it: one session's own output, inside the caller's own account, and
            // bounded by the same tenant binding. It is listed separately rather than folded in because an
            // allow list that widens by pattern stops being an allow list.
            if (s.Length == 3 && s[0] == "sessions" && s[2] == "history") return true;

            // One mission, one workflow run. Scheduled jobs are handled by IsScheduleRoute below, which
            // owns every /cron shape in one place rather than splitting the reads away from the writes.
            if (s.Length == 2 && s[0] == "missions") return true;
            if (s.Length == 3 && s[0] == "gateway" && s[1] == "workflow-runs") return true;
            if (IsScheduleRoute(verb, s)) return true;

            // What is installed on another machine, and which files it can see - the "start something over
            // there" discovery pair. Reads only; the start itself is a POST below.
            if (s.Length == 3 && s[0] == "machines" && (s[2] == "apps" || s[2] == "files")) return true;

            // The fleet's shared skills and workflows: the catalogue entry, its body/instructions, and its
            // version history. This is how an agent reads a capability the fleet holds centrally.
            if (IsCatalogueRead(s)) return true;

            // The automation browsers on one Director's machine. See IsBrowserRoute for how the /directors
            // surface is split between configuration and admission.
            if (IsBrowserRoute(verb, s)) return true;

            // What agents added to the dictation dictionary, and which one added it. See
            // IsDictionaryAdditionTrail for why this ONE read is open while the glossary itself is not.
            if (IsDictionaryAdditionTrail(verb, s)) return true;

            // Configuration, read side. A Director's settings, the application's own settings, and the
            // handovers on a Director - all three by the owner's ruling that an agent configures the product.
            if (IsDirectorSettings(s)) return true;
            if (IsApplicationSetting(s)) return true;
            if (IsHandoverRead(s)) return true;

            return false;
        }

        // ---------- Fleet actions ----------
        if (verb == "POST")
        {
            // Talk to a session: prompt it, interrupt it, park it, give it a role or a mission, ask it to
            // compact, or flag it finished. Every one of these names a session in the path, and the tenant
            // binding keeps it inside the calling account.
            if (s.Length == 3 && s[0] == "sessions")
            {
                switch (s[2])
                {
                    case "prompt":
                    // An agent-to-agent message: a prompt the Gateway frames with the CALLING session's own
                    // name, so the recipient knows who sent it. Allowed for the same reason "prompt" is, and
                    // it is strictly the narrower of the two - the sender cannot be chosen by the caller.
                    case "message":
                    case "interrupt":
                    case "escape":
                    case "hold":
                    case "role":
                    case "mission":
                    case "request-deletion":
                    case "compact-context":
                        return true;
                }
                return false;
            }

            // A message to the agent's own team (the fanout the fleet's "message send all" uses), and the
            // team-resolving front door onto it - which is what the command line actually calls, because
            // working out who is on the team is the Gateway's ruling to make, not the caller's.
            if (Join(s) == "fanout") return true;
            if (Join(s) == "fleet/broadcast") return true;

            // Create a mission - the unit of work sessions attach to.
            if (Join(s) == "missions") return true;

            // Start a session, or an application, on a machine in this account.
            if (s.Length == 3 && s[0] == "machines" && (s[2] == "sessions" || s[2] == "launch")) return true;

            // Start a session on ONE named Director. The same verb as the machine route above, addressed
            // precisely rather than to "some Director over there" - which is the only way to be specific on
            // a machine running several instances, and how a session spawns on its OWN Director now that
            // there is no loopback port meaning "here". No wider than what is already allowed: the machine
            // route would reach the same Director, it just would not let the caller say which.
            if (s.Length == 3 && s[0] == "directors" && s[2] == "sessions") return true;

            // Contribute to the fleet's shared skills and workflows: create one, publish it, clone one.
            if (IsCatalogueWrite(verb, s)) return true;

            // Create a scheduled job, or run one now.
            if (IsScheduleRoute(verb, s)) return true;

            // Create, start, stop, sign in to or rename an automation browser.
            if (IsBrowserRoute(verb, s)) return true;

            // Write a handover. This is the one that makes moving a session possible without the owner
            // opening the interface, which is the whole point of the ruling.
            if (IsHandoverWrite(s)) return true;

            // Add a word to the dictation dictionary.
            if (IsDictionaryAdd(verb, s)) return true;

            return false;
        }

        // ---------- Configuration and update writes ----------
        // Every PUT is listed explicitly. Opening the verb, or opening /gateway by prefix, is what would stop
        // this being an allow list: a PUT arriving on a route added next year must be refused until somebody
        // classifies it, and a prefix rule would have handed over /gateway/skills/{id}/disable on day one.
        if (verb == "PUT")
        {
            if (IsDirectorSettings(s)) return true;
            if (IsApplicationSetting(s)) return true;

            // Update a skill or workflow draft, and update a scheduled job. Both were refused until the
            // inspection found that the shipped command line has always sent PUT here.
            if (IsCatalogueWrite(verb, s)) return true;
            if (IsScheduleRoute(verb, s)) return true;
            return false;
        }

        // Rename a session (PATCH /sessions/{sid}).
        if (verb == "PATCH" && s.Length == 2 && s[0] == "sessions") return true;

        if (verb == "DELETE")
        {
            // Delete an automation browser.
            if (IsBrowserRoute(verb, s)) return true;

            // Delete a skill, a workflow, or a scheduled job - the same contribute-and-maintain surface as
            // the creates above.
            if (IsCatalogueWrite(verb, s)) return true;
            if (IsScheduleRoute(verb, s)) return true;

            // Delete a handover. Beyond the literal words of the owner's ruling, which named handovers as
            // content an agent produces without saying who may remove one - and recorded as a judgement call
            // in PHASE-2B-REPORT.md rather than slipped in. It is allowed because it is the same class as the
            // POST that created it: removing a handover changes neither who is admitted nor what identity the
            // account has, and an agent that can write handovers but must ask the owner to tidy them up is
            // exactly the trip back to the interface the ruling exists to remove.
            if (IsHandoverWrite(s)) return true;

            return false;
        }

        return false;
    }

    /// <summary>
    /// The automation-browser shapes under <c>/directors/{id}/browsers</c>.
    ///
    /// HOW <c>/directors</c> IS SPLIT. Four shapes are open to a session key - browsers (here), <c>POST
    /// /directors/{id}/sessions</c>, <c>/directors/{id}/settings</c>, and <c>/directors/{id}/handovers</c> -
    /// and they are open because each is the product BEHAVING, which the owner's ruling puts in an agent's
    /// hands. What stays refused on this surface is admission and lifecycle: <c>POST /directors/register</c>,
    /// <c>DELETE /directors/{id}/registration</c>, and <c>DELETE /directors/{id}</c>, which force-kills the
    /// Director. Registering or de-registering a Director decides who is in the account; force-killing one is
    /// the blunt instrument agents already have a clean alternative to in <c>request-deletion</c>.
    /// This sub-path is not Director administration at all: an automation browser is a tool an agent uses,
    /// it was reachable by every agent on the machine before this mission (over the Director's loopback
    /// port, with no credential narrower than the machine secret), and routing it through the Gateway
    /// NARROWS it, because the key is bound to one session inside one account.
    ///
    /// Matched by structure so a new sibling under /directors cannot be reached by accident: the id segment
    /// is never read here, only counted.
    /// </summary>
    private static bool IsBrowserRoute(string verb, string[] s)
    {
        if (s.Length < 3 || s[0] != "directors" || s[2] != "browsers") return false;

        // /directors/{id}/browsers - list, or create.
        if (s.Length == 3) return verb is "GET" or "HEAD" or "POST";

        // /directors/{id}/browsers/{browserId} - DELETE only. There is no GET of a single browser and no
        // POST to the item itself, so neither is authorized.
        if (s.Length == 4) return verb == "DELETE";

        // /directors/{id}/browsers/{browserId}/{verb}. `attach` is a GET that prints the connection lines;
        // the four actions are POSTs.
        if (s.Length == 5)
        {
            if (s[4] == "attach") return verb is "GET" or "HEAD";
            if (s[4] is "start" or "stop" or "signin" or "rename") return verb == "POST";
        }

        return false;
    }

    /// <summary>
    /// One Director's settings: <c>/directors/{id}/settings</c>, read with GET and written with PUT.
    ///
    /// Matched by structure, and by length exactly 3, so that a sub-path somebody hangs off settings later -
    /// <c>/directors/{id}/settings/credentials</c>, say - is refused until it is classified here on purpose.
    /// </summary>
    private static bool IsDirectorSettings(string[] s)
        => s.Length == 3 && s[0] == "directors" && s[2] == "settings";

    /// <summary>
    /// The application's own settings - the closed <c>/gateway</c> set the Settings page writes.
    ///
    /// Every one of these says how the product should BEHAVE: when it reports, how long a snooze lasts, which
    /// time zone and which model, which voice and which language it speaks, what text it injects, how it
    /// transcribes. None of them says who may sign in or which devices are admitted, which is why the whole
    /// set moves together under the owner's ruling.
    ///
    /// NOTE THE DISTINCTION THAT MATTERS: the voice, language and transcription entries here are SETTINGS,
    /// not DATA. Recordings, dictation audio and transcripts remain refused - a knob saying how to transcribe
    /// is configuration, whereas what was actually said into the microphone is the owner's.
    ///
    /// Written as a literal list rather than a <c>/gateway</c> prefix on purpose. A prefix would have opened
    /// the catalogue enable/disable routes that sit under the same segment and are deliberately refused, and
    /// it would silently swallow every future <c>/gateway</c> route on the day it was added.
    /// </summary>
    private static bool IsApplicationSetting(string[] s) => Join(s) switch
    {
        "gateway/settings" => true,
        "gateway/daily-report" => true,
        "gateway/snooze-default" => true,
        "gateway/snooze-presets" => true,
        "gateway/time-zone" => true,
        "gateway/ai-provider" => true,
        "gateway/tts-voice" => true,
        "gateway/spoken-language" => true,
        "gateway/spoken-language/voice" => true,
        "gateway/injected-text" => true,
        "gateway/transcription-mode" => true,
        _ => false,
    };

    /// <summary>
    /// Reading handovers on a Director: the list at <c>/directors/{id}/handovers</c>, and one document's text
    /// at <c>/directors/{id}/handovers/content</c>. The document is named by query string, which this guard
    /// deliberately never consults - see <see cref="Check"/>.
    /// </summary>
    private static bool IsHandoverRead(string[] s)
    {
        if (s.Length < 3 || s[0] != "directors" || s[2] != "handovers") return false;
        return s.Length == 3 || (s.Length == 4 && s[3] == "content");
    }

    /// <summary>Creating or removing a handover, both at <c>/directors/{id}/handovers</c> exactly.</summary>
    private static bool IsHandoverWrite(string[] s)
        => s.Length == 3 && s[0] == "directors" && s[2] == "handovers";

    /// <summary>
    /// ADDING A WORD TO THE DICTATION DICTIONARY - <c>POST /ingest/dictionary/terms</c>, and nothing else
    /// anywhere under <c>/ingest</c>. The owner's ruling of 2026-08-07 (issue #2484).
    ///
    /// WHY THIS ONE ROUTE AND NOT THE SURFACE IT SITS ON. The rest of <c>/ingest</c> is the owner's dictation
    /// audio and his transcripts, which stay refused for the reason the class comment gives. The glossary is
    /// different in kind: it says how the product should hear a word, which is configuration, and an agent
    /// that has just read a product name off the repository in front of it is the best-placed thing on the
    /// machine to teach the transcriber that word. The owner ruled that asking him to confirm every such
    /// addition is worse than the occasional stray one.
    ///
    /// ADD ONLY, AND WHY THAT IS THE WHOLE OF THE GRANT. A session key may add a term. It may NOT delete,
    /// rename or overwrite an existing term, and it may not touch the wrong-spellings list attached to one -
    /// so the worst an agent can do here is leave a stray extra word, never remove a correction the owner
    /// relies on. That is why <c>PUT /ingest/dictionary</c> (the editor's whole-document save, which can drop
    /// or empty anything) and the suggestion routes (apply writes a term AND its wrong spellings; dismiss and
    /// restore change what the owner is shown) are all still refused. The person keeps the pruning shears:
    /// anything an agent adds is removable in the Cockpit dictionary editor like any hand-added term.
    ///
    /// THE ROUTE IS HALF THE GRANT; THE HANDLER IS THE OTHER HALF. This guard can only see a method and a
    /// path, and the same path carries both a term and a wrong-spellings map. So the narrowing that a path
    /// cannot express - a session key may send <c>terms</c> and may not send <c>mistranscriptions</c> - is
    /// enforced in the handler, in <c>RecordingEndpoints</c>, which is the one place that can read a body.
    /// Neither half is sufficient alone, and both are pinned by tests.
    ///
    /// Matched by structure and by exact length, so a sibling somebody hangs off the add route later is
    /// refused until it is classified here on purpose.
    /// </summary>
    private static bool IsDictionaryAdd(string verb, string[] s)
        => verb == "POST" && s.Length == 3 && s[0] == "ingest" && s[1] == "dictionary" && s[2] == "terms";

    /// <summary>
    /// READING BACK WHAT AGENTS ADDED - <c>GET /ingest/dictionary/additions</c>, and nothing else.
    ///
    /// WHY A READ IS OPEN HERE WHEN <c>GET /ingest/dictionary</c> IS REFUSED. These are not the same kind of
    /// thing, and the difference is whose material each one holds. The glossary is the OWNER's: his
    /// hand-added terms, his wrong-spellings lists, the corrections he curates. The addition trail holds
    /// exactly one thing - what AGENTS wrote - because a person adding a term through the Cockpit is not a
    /// session and leaves no entry at all. So this route lets agents read back agents' own writes, and
    /// reaches none of the owner's material; opening the glossary would reach all of it.
    ///
    /// WHY IT IS NOT OPTIONAL. The owner's ruling asks that a bad entry can be "traced AND swept". Writing
    /// the trail satisfies only the first verb. A record no tooling can read is a file somebody has to go
    /// and find on disk, and the traceability that was traded for removing the confirmation step would have
    /// been nominal - which is the whole reason the ruling asked for it.
    ///
    /// FINDING IS NOT ACTING. There is no verb here to remove what this read finds, and there deliberately
    /// is not one: pruning stays the person's, in the Cockpit editor. This route makes a bad batch
    /// FINDABLE and stops there.
    ///
    /// Matched by exact length so a sibling hung off it later is refused until classified on purpose.
    /// </summary>
    private static bool IsDictionaryAdditionTrail(string verb, string[] s)
        => verb is "GET" or "HEAD"
           && s.Length == 3 && s[0] == "ingest" && s[1] == "dictionary" && s[2] == "additions";

    /// <summary>The read shapes of the shared skill/workflow catalogue.</summary>
    private static bool IsCatalogueRead(string[] s)
    {
        if (s.Length < 3 || s[0] != "gateway") return false;
        if (s[1] != "skills" && s[1] != "workflows") return false;

        // /gateway/{skills|workflows}/{id}
        if (s.Length == 3) return true;

        // /gateway/{skills|workflows}/{id}/{body|instructions|versions}
        if (s.Length == 4)
            return s[3] is "body" or "instructions" or "versions";

        // /gateway/{skills|workflows}/{id}/versions/{version}
        return s.Length == 5 && s[3] == "versions";
    }

    /// <summary>
    /// The write shapes of the shared skill/workflow catalogue, matched on METHOD AND PATH TOGETHER
    /// against the routes the Gateway actually maps in <c>SkillEndpoints</c> and <c>WorkflowEndpoints</c>.
    ///
    /// THIS METHOD IS WHY AN ALLOW LIST MUST BE BUILT FROM THE ROUTE TABLE, NOT FROM MEMORY. Its previous
    /// form allowed only four-segment <c>POST /gateway/{kind}/{id}/{draft|publish|clone}</c>, so the two
    /// verbs the shipped command line uses most - <c>POST /gateway/skills</c> to create and
    /// <c>PUT /gateway/skills/{id}/draft</c> to update - were refused with 403 on every call. The guard's
    /// own test did not catch it because it pinned <c>POST .../draft</c>, a route that does not exist: the
    /// test agreed with the guard about a shape neither the client nor the server ever uses, so both were
    /// consistently wrong and green. A test written from the guard tests the guard against itself.
    ///
    /// STILL REFUSED: <c>enable</c> and <c>disable</c>. Turning a fleet-wide capability off for everyone is
    /// an owner's decision, and it is the one catalogue verb that is not an agent contributing work.
    /// </summary>
    private static bool IsCatalogueWrite(string verb, string[] s)
    {
        if (s.Length < 2 || s[0] != "gateway") return false;
        if (s[1] != "skills" && s[1] != "workflows") return false;

        // POST /gateway/{skills|workflows} - create.
        if (s.Length == 2) return verb == "POST";

        // DELETE /gateway/{skills|workflows}/{id} - remove one the agent (or the fleet) no longer wants.
        if (s.Length == 3) return verb == "DELETE";

        if (s.Length == 4)
        {
            // PUT .../{id}/draft - save a draft. The client's update verb, and previously refused.
            if (s[3] == "draft") return verb == "PUT";

            // POST .../{id}/{publish|clone}.
            if (s[3] is "publish" or "clone") return verb == "POST";
        }

        return false;
    }

    /// <summary>
    /// The scheduled-job surface, matched on method and path together against <c>CronJobEndpoints</c> and
    /// <c>CronRunEndpoints</c>.
    ///
    /// Same defect as the catalogue above and found by the same inspection: the guard allowed the two GETs
    /// and nothing else, so every schedule command except <c>schedule list</c> returned 403. A schedule is
    /// configuration - it says what the product should do and when - so it falls on the allowed side of the
    /// owner's line, and running one now is no more than doing by hand what the schedule does anyway.
    /// </summary>
    private static bool IsScheduleRoute(string verb, string[] s)
    {
        if (s.Length < 2 || s[0] != "cron" || s[1] != "jobs") return false;

        // /cron/jobs - list, or create.
        if (s.Length == 2) return verb is "GET" or "HEAD" or "POST";

        // /cron/jobs/{id} - read, update, delete.
        if (s.Length == 3) return verb is "GET" or "HEAD" or "PUT" or "DELETE";

        // /cron/jobs/{id}/run - run it now. /cron/jobs/{id}/runs - its history.
        if (s.Length == 4)
        {
            if (s[3] == "run") return verb == "POST";
            if (s[3] == "runs") return verb is "GET" or "HEAD";
        }

        return false;
    }

    private static string Join(string[] segments) => string.Join('/', segments);
}
