import { useCallback, useEffect, useRef, useState } from "react";
import {
  createRule,
  deleteRule,
  draftRule,
  getRuleFirings,
  getRules,
  promoteRule,
  type RuleDraftTurn,
  type RuleFiring,
  type RuleProposal,
  type SessionRule,
} from "@devthrottle/client-core/rules/rulesClient";
import { gatewayErrorMessage, listSessions, type SessionDto } from "@devthrottle/client-core/api/client";
import { Button, ConfirmDialog, ErrorBanner, LoadingState } from "../components";
import { useDismissOnBackdrop } from "../components/useDismissOnBackdrop";

// THE RULES PAGE. A rule is a standing instruction about your sessions: it sits there costing
// nothing until one of them goes idle with something on its screen that looks like the thing you
// described, and then an agent reads that screen and your instruction together and does what you
// asked.
//
// YOU WRITE ONE BY SAYING WHAT YOU WANT, ABOUT A SESSION YOU NAME. That is the whole of the composer
// at the top of this page: choose the session it is happening on, and talk into the box. The Gateway
// reads that session's screen ITSELF, works out what it has to hold - what the screen looks like when
// the rule applies, the cheap words that make a screen worth a closer look, any check the act depends
// on, which sessions, and the two ceilings - and hands the rule back with a plain-English read-back
// BEFORE anything is stored. When guessing would make the rule do something you did not ask for, it
// asks you one question instead, and the answer goes back into the same conversation.
//
// THERE IS NO WAY TO WRITE A RULE FROM MEMORY HERE, AND THAT IS DELIBERATE (fix round D, ruling D2).
// The page used to let you, and said so; the Gateway then had no screen to check the trigger words
// against, so the words were the model's guess at what a screen says - and a rule watching for a word
// that never appears never fires while looking perfectly good in the list. Now the Gateway refuses a
// request that names no session, and this page does not offer one.
//
// THE PAGE NEVER DECIDES ANYTHING (rule 7: the client is dumb). Every verdict on this page is the
// Gateway's own words rendered verbatim - the read-back, the question, the scope and wait labels, and
// above all a REFUSAL. When a rule cannot be stored the Gateway says which check does not exist or
// which value was missing, and that sentence is what appears here. A page that flattened it into
// "could not save" would leave somebody guessing, and guessing is how a rule ends up subtly different
// from the sentence meant.
//
// TWO PEOPLE-SHAPED STEPS SIT BETWEEN A SENTENCE AND THE FIRST KEYSTROKE, and neither can be skipped
// from here because neither can be skipped on the Gateway. Drafting stores nothing. Storing always
// produces a DRY RUN rule, which watches, decides, records what it WOULD have done, and types
// nothing. Only then does a person promote it - from in front of its dry-run record.
export function RulesView() {
  const [rules, setRules] = useState<SessionRule[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      setRules(await getRules());
    } catch (e) {
      setError(gatewayErrorMessage(e));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <div className="page rules">
      <header className="ui-page-header">
        <div className="ui-page-header-text">
          <p className="rules-eyebrow">Standing instructions</p>
          <h1 className="ui-page-title">Rules</h1>
          <p className="ui-page-subtitle">
            Tell DevThrottle what to do when a session gets stuck, in ordinary words. It reads the
            session's screen, works out what to watch for, shows you exactly what it would do, and does
            nothing at all until you say so.
          </p>
        </div>
      </header>

      <div className="rules-lifecycle">
        <span className="rules-lc-step"><span className="rules-lc-who">you</span> say it</span>
        <span className="rules-lc-arrow">-&gt;</span>
        <span className="rules-lc-step">read what it would do</span>
        <span className="rules-lc-arrow">-&gt;</span>
        <span className="rules-lc-step">dry run - types nothing</span>
        <span className="rules-lc-arrow">-&gt;</span>
        <span className="rules-lc-step rules-lc-live">you make it live</span>
        <span className="rules-lc-tail">
          a rule only ever looks at a session that has stopped, and only ever at its screen
        </span>
      </div>

      <RuleComposer onStored={() => void load()} />

      {error !== null ? (
        <ErrorBanner message={error} onRetry={() => void load()} />
      ) : rules === null ? (
        <LoadingState message="Loading your rules..." />
      ) : rules.length === 0 ? (
        <p className="rules-none">
          No rules yet. Choose a session and say what you want one to do in the box above - for
          example, what should happen when a session stops on an error from the provider.
        </p>
      ) : (
        <div className="rules-list">
          {rules.map((rule) => (
            <RuleCard key={rule.id} rule={rule} onChanged={() => void load()} />
          ))}
        </div>
      )}
    </div>
  );
}

// ---- the composer: the whole point of the page --------------------------------------------------

/** The session a rule is about, as the composer holds it: the id the Gateway needs, and the words a
 *  person recognises it by. */
interface ChosenSession {
  sessionId: string;
  name: string;
  agent: string;
  machine: string;
}

function chosenFrom(session: SessionDto): ChosenSession | null {
  const sessionId = session.sessionId ?? "";
  if (sessionId.length === 0) return null;
  return {
    sessionId,
    name: session.name ?? sessionId,
    agent: session.agent ?? "",
    machine: session.machineName ?? "",
  };
}

/**
 * Say what you want, about a session you name; read what it would do; store it.
 *
 * The conversation is held HERE and sent whole on every turn, so the Gateway never has to remember
 * anything between calls: when it asks a question, the question and your answer both go back with
 * the next request, and it can see which question your answer belongs to.
 */
function RuleComposer({ onStored }: { onStored: () => void }) {
  const [said, setSaid] = useState("");
  const [session, setSession] = useState<ChosenSession | null>(null);
  const [choosing, setChoosing] = useState(false);
  // THE STAR: this rule is for every agent, not only the chosen session's. A choice the person makes
  // here and the Gateway is told; the model never chooses it (fix round D, ruling D3).
  const [allAgents, setAllAgents] = useState(false);
  const [turns, setTurns] = useState<RuleDraftTurn[]>([]);
  const [question, setQuestion] = useState<string | null>(null);
  const [proposal, setProposal] = useState<RuleProposal | null>(null);
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [stored, setStored] = useState<string | null>(null);
  const box = useRef<HTMLTextAreaElement | null>(null);

  const startOver = () => {
    setSaid("");
    setTurns([]);
    setQuestion(null);
    setProposal(null);
    setRefusal(null);
    setSession(null);
    setAllAgents(false);
  };

  const send = async () => {
    const text = said.trim();
    if (text.length === 0 || busy || session === null) return;

    // What was asked goes into the conversation BEFORE the answer, so the model reads them in the
    // order they happened rather than seeing a reply to nothing.
    const next: RuleDraftTurn[] = [...turns];
    if (question !== null) next.push({ who: "devthrottle", text: question });
    next.push({ who: "person", text });

    setBusy(true);
    setRefusal(null);
    setStored(null);
    try {
      const answer = await draftRule(next, session.sessionId, allAgents);
      setTurns(next);
      setSaid("");
      if (answer.proposal) {
        setProposal(answer.proposal);
        setQuestion(null);
      } else {
        setQuestion(answer.question ?? null);
        setProposal(null);
        box.current?.focus();
      }
    } catch (e) {
      // The Gateway's own refusal, verbatim. It says what was wrong with the rule it could not draft.
      setRefusal(gatewayErrorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  const store = async () => {
    if (proposal === null || busy) return;
    setBusy(true);
    setRefusal(null);
    try {
      // POSTED BACK UNCHANGED. The body the Gateway drafted is the body it takes, so what was read
      // and what is stored are the same document - and it carries the session, so the Gateway reads
      // that screen again before it stores anything.
      const rule = await createRule(proposal.rule);
      setStored(rule.id);
      startOver();
      onStored();
    } catch (e) {
      setRefusal(gatewayErrorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  const conversationStarted = turns.length > 0 || proposal !== null;

  return (
    <section className="rules-composer">
      <h2 className="rules-composer-title">Make a rule by saying what you want</h2>

      <div className="rules-session">
        <span className="rules-fact-label">The session it is happening on</span>
        {session === null ? (
          <span className="rules-session-none">
            None chosen yet. A rule is written against a real screen, never from memory - choose the
            session it is happening on and its screen is read for you.
          </span>
        ) : (
          <span className="rules-session-chosen">
            <span className="rules-session-name">{session.name}</span>
            {session.agent.length > 0 && <span className="rules-session-meta">{session.agent}</span>}
            {session.machine.length > 0 && <span className="rules-session-meta">{session.machine}</span>}
          </span>
        )}
        <Button variant="ghost" onClick={() => setChoosing(true)} disabled={busy || conversationStarted}>
          {session === null ? "Choose a session" : "Change"}
        </Button>
      </div>

      {session !== null && (
        <label className="rules-star">
          <input
            type="checkbox"
            checked={allAgents}
            disabled={busy || conversationStarted}
            onChange={(e) => setAllAgents(e.target.checked)}
          />
          <span>
            For every agent, not only {session.agent.length > 0 ? session.agent : "this session's agent"}.
            Without this, the rule is for {session.agent.length > 0 ? session.agent : "that agent"} sessions
            only, because the words a screen shows are that agent's.
          </span>
        </label>
      )}

      <SessionChooser
        open={choosing}
        onClose={() => setChoosing(false)}
        onChosen={(chosen) => {
          setSession(chosen);
          setChoosing(false);
        }}
      />

      {turns.length > 0 && (
        <ol className="rules-said">
          {turns.map((turn, i) => (
            <li key={i} className={turn.who === "person" ? "rules-said-you" : "rules-said-us"}>
              <span className="rules-said-who">{turn.who === "person" ? "You" : "DevThrottle"}</span>
              <span className="rules-said-text">{turn.text}</span>
            </li>
          ))}
        </ol>
      )}

      {question !== null && (
        <p className="rules-question">
          <span className="rules-question-mark" aria-hidden="true">?</span>
          {question}
        </p>
      )}

      {proposal === null ? (
        <>
          <textarea
            ref={box}
            className="rules-box"
            rows={3}
            value={said}
            placeholder={
              turns.length === 0
                ? "When the provider stops working, wait a while and then start the session back up."
                : "Your answer..."
            }
            onChange={(e) => setSaid(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) void send();
            }}
            aria-label="What you want the rule to do"
          />
          <div className="rules-composer-actions">
            <Button
              variant="primary"
              onClick={() => void send()}
              disabled={busy || said.trim().length === 0 || session === null}
            >
              {busy ? "Working it out..." : turns.length === 0 ? "Work it out" : "Answer"}
            </Button>
            {turns.length > 0 && (
              <Button variant="ghost" onClick={startOver} disabled={busy}>Start over</Button>
            )}
          </div>
        </>
      ) : (
        <div className="rules-proposal">
          <p className="rules-readback">{proposal.readBack}</p>

          <dl className="rules-detail">
            <dt>Your words</dt>
            <dd className="rules-instruction">{proposal.rule.instruction}</dd>
            <dt>What it watches for</dt>
            <dd>{proposal.rule.screenDescription}</dd>
            <dt>Words on the screen that wake it</dt>
            <dd>
              {proposal.rule.triggerWords.map((word) => (
                <span key={word} className="rules-word">{word}</span>
              ))}
            </dd>
            <dt>Which sessions</dt>
            <dd>{proposal.scopeLabel}</dd>
            <dt>Ceilings</dt>
            <dd>
              waits {proposal.waitLabel} before acting on the same session again, at most{" "}
              {proposal.rule.dailyCap} times a day
            </dd>
          </dl>

          <div className="rules-capture rules-capture-has">
            <div className="rules-capture-head">
              <span className="rules-capture-title">
                Checked against this screen of {session?.name ?? proposal.rule.sessionId}
              </span>
            </div>
            <pre className="rules-capture-screen">{proposal.exampleScreen}</pre>
            <p className="rules-checked">
              Every word above was checked against this screen, which the Gateway read itself. A rule
              that watched for something not on it would have been refused rather than offered - and
              the screen is read again before the rule is stored.
            </p>
          </div>

          <p className="rules-dryrun-note">
            Storing this does not turn it on. It goes in as a dry run: it watches, decides, and writes
            down what it WOULD have done, and types nothing. You make it live afterwards.
          </p>

          <div className="rules-composer-actions">
            <Button variant="primary" onClick={() => void store()} disabled={busy}>
              {busy ? "Storing..." : "Store it as a dry run"}
            </Button>
            <Button variant="ghost" onClick={startOver} disabled={busy}>Throw it away</Button>
          </div>
        </div>
      )}

      {refusal !== null && <p className="rules-refusal">{refusal}</p>}
      {stored !== null && (
        <p className="rules-stored">Stored as a dry run. It is in the list below, watching and typing nothing.</p>
      )}
    </section>
  );
}

/**
 * CHOOSING THE SESSION - in a dialog of its own, not inline. A form field holds ONE value and stays
 * compact; the list of sessions on a real fleet is long and variable, and it does not belong stuffed
 * into the composer. The field shows the chosen session compactly; this opens a roomy, searchable
 * list, and closes on a pick.
 */
function SessionChooser({
  open,
  onClose,
  onChosen,
}: {
  open: boolean;
  onClose: () => void;
  onChosen: (session: ChosenSession) => void;
}) {
  const [sessions, setSessions] = useState<ChosenSession[] | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [filter, setFilter] = useState("");
  const dismiss = useDismissOnBackdrop(onClose);

  useEffect(() => {
    if (!open) return;
    setSessions(null);
    setProblem(null);
    setFilter("");
    let cancelled = false;
    (async () => {
      try {
        const all = await listSessions();
        if (cancelled) return;
        setSessions(all.map(chosenFrom).filter((s): s is ChosenSession => s !== null));
      } catch (e) {
        if (!cancelled) setProblem(gatewayErrorMessage(e));
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [open, onClose]);

  if (!open) return null;

  const needle = filter.trim().toLowerCase();
  const shown = (sessions ?? []).filter(
    (s) =>
      needle.length === 0 ||
      s.name.toLowerCase().includes(needle) ||
      s.agent.toLowerCase().includes(needle) ||
      s.machine.toLowerCase().includes(needle) ||
      s.sessionId.toLowerCase().includes(needle),
  );

  return (
    <div className="ui-modal-backdrop" {...dismiss}>
      <div className="ui-confirm rules-chooser" role="dialog" aria-modal="true" aria-labelledby="rules-chooser-title">
        <h2 id="rules-chooser-title" className="ui-confirm-title">Which session is it happening on?</h2>
        <input
          className="rules-chooser-filter"
          type="search"
          placeholder="Filter by name, agent or machine"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          aria-label="Filter sessions"
        />
        {problem !== null ? (
          <p className="rules-refusal">{problem}</p>
        ) : sessions === null ? (
          <LoadingState message="Reading the roster..." />
        ) : shown.length === 0 ? (
          <p className="rules-none">
            {sessions.length === 0
              ? "No sessions are on the roster right now. A rule is written against a running session's screen."
              : "Nothing matches that."}
          </p>
        ) : (
          <ul className="rules-chooser-list">
            {shown.map((s) => (
              <li key={s.sessionId}>
                <button type="button" className="rules-chooser-row" onClick={() => onChosen(s)}>
                  <span className="rules-session-name">{s.name}</span>
                  <span className="rules-session-meta">{s.agent}</span>
                  <span className="rules-session-meta">{s.machine}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
        <div className="ui-confirm-actions">
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
        </div>
      </div>
    </div>
  );
}

// ---- one rule, and what it has done -------------------------------------------------------------

const LIVE = "live";

/** The record as it is put in front of a person before they make a rule live. */
function describeRecord(firings: RuleFiring[]): string {
  if (firings.length === 0) return "0 firings";
  const latest = firings[0];
  const count = `${firings.length} ${firings.length === 1 ? "firing" : "firings"}`;
  return `${count}, the latest on ${new Date(latest.occurredUtc).toLocaleString()} decided ${latest.decision}`;
}

/**
 * One rule as a card: what you said, what it watches for, what bounds it, and - on demand - every
 * time it has fired. A DECLINE is a firing too, and is shown exactly like an act: a rule that did
 * nothing because something broke would otherwise look identical to one that decided not to act.
 *
 * The scope and the wait are the Gateway's own words, rendered verbatim (rule 7).
 */
function RuleCard({ rule, onChanged }: { rule: SessionRule; onChanged: () => void }) {
  const [firings, setFirings] = useState<RuleFiring[] | null>(null);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  // MAKING IT LIVE: the record is loaded and shown FIRST, and the sentence sent describes what was
  // shown. Nothing is sent from a constant (fix round D, ruling D5).
  const [confirming, setConfirming] = useState<
    | { kind: "loading" }
    | { kind: "record"; firings: RuleFiring[] }
    | { kind: "unreadable"; reason: string }
    | null
  >(null);
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  const isLive = rule.state === LIVE;

  const readRecord = async (): Promise<RuleFiring[]> => {
    const read = await getRuleFirings(rule.id);
    setFirings(read);
    return read;
  };

  const showFirings = async () => {
    setOpen(!open);
    if (firings !== null || open) return;
    try {
      await readRecord();
    } catch (e) {
      setProblem(gatewayErrorMessage(e));
    }
  };

  const beginMakingLive = async () => {
    setProblem(null);
    setConfirming({ kind: "loading" });
    try {
      setConfirming({ kind: "record", firings: await readRecord() });
    } catch (e) {
      // The record could not be read, so nothing can be shown, so nothing can be agreed to.
      setConfirming({ kind: "unreadable", reason: gatewayErrorMessage(e) });
    }
  };

  const makeLive = async (shown: RuleFiring[]) => {
    setConfirming(null);
    setBusy(true);
    setProblem(null);
    try {
      await promoteRule(
        rule.id,
        `I have read this rule's dry-run record: ${describeRecord(shown)}. I am making it live.`,
      );
      onChanged();
    } catch (e) {
      setProblem(gatewayErrorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    setConfirmingDelete(false);
    setBusy(true);
    setProblem(null);
    try {
      await deleteRule(rule.id);
      onChanged();
    } catch (e) {
      setProblem(gatewayErrorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <article className={`rules-card ${isLive ? "rules-card-live" : "rules-card-dry"}`}>
      <div className="rules-card-spine" aria-hidden="true"></div>

      <div className="rules-card-body">
        <div className="rules-card-head">
          <p className="rules-instruction">{rule.instruction}</p>
          <span className={`rules-state ${isLive ? "rules-state-live" : "rules-state-dry"}`}>
            {isLive ? "Live" : "Dry run - types nothing"}
          </span>
        </div>

        <p className="rules-watching">{rule.screenDescription}</p>

        <div className="rules-facts">
          <span className="rules-fact">
            <span className="rules-fact-label">wakes on</span>
            {rule.triggerWords.map((word) => (
              <span key={word} className="rules-word">{word}</span>
            ))}
          </span>
          <span className="rules-fact">
            <span className="rules-fact-label">acts on</span>
            {rule.scopeLabel}
          </span>
          <span className="rules-fact">
            <span className="rules-fact-label">ceilings</span>
            {rule.waitLabel} apart, {rule.dailyCap} a day
          </span>
          {rule.checks.length > 0 && (
            <span className="rules-fact">
              <span className="rules-fact-label">checks</span>
              {rule.checks.join(", ")}
            </span>
          )}
          {isLive && rule.promotedBy.length > 0 && (
            <span className="rules-fact">
              <span className="rules-fact-label">made live by</span>
              {rule.promotedBy}
            </span>
          )}
          {isLive && rule.acknowledgement.length > 0 && (
            <span className="rules-fact">
              <span className="rules-fact-label">who agreed to</span>
              {rule.acknowledgement}
            </span>
          )}
        </div>

        <div className="rules-card-actions">
          <Button variant="ghost" onClick={() => void showFirings()}>
            {open ? "Hide what it has done" : "What it has done"}
          </Button>
          {!isLive && (
            <Button variant="primary" onClick={() => void beginMakingLive()} disabled={busy}>
              Make it live
            </Button>
          )}
          <Button variant="ghost" onClick={() => setConfirmingDelete(true)} disabled={busy}>Delete</Button>
        </div>

        {problem !== null && <p className="rules-refusal">{problem}</p>}

        {open && (
          firings === null ? (
            <LoadingState message="Reading the record..." />
          ) : (
            <FiringList firings={firings} />
          )
        )}
      </div>

      <ConfirmDialog
        open={confirming !== null}
        title="Make this rule live?"
        message={
          confirming === null || confirming.kind === "loading" ? (
            <LoadingState message="Reading the dry-run record..." />
          ) : confirming.kind === "unreadable" ? (
            <p className="rules-refusal">
              The dry-run record could not be read, so there is nothing to show you and nothing to
              agree to: {confirming.reason}
            </p>
          ) : (
            <div className="rules-promote">
              <p>
                While it is in dry run it types nothing. Live, it will type into your sessions on its
                own when the screen matches. This is what it has done so far in dry run:
              </p>
              <p className="rules-promote-summary">{describeRecord(confirming.firings)}</p>
              <FiringList firings={confirming.firings} />
            </div>
          )
        }
        confirmLabel="Make it live, I have read the record"
        danger={false}
        onConfirm={() => {
          if (confirming?.kind === "record") void makeLive(confirming.firings);
        }}
        onClose={() => setConfirming(null)}
      />
      <ConfirmDialog
        open={confirmingDelete}
        title="Delete this rule?"
        message="The rule goes. Everything it has already done stays on the record."
        confirmLabel="Delete"
        onConfirm={() => void remove()}
        onClose={() => setConfirmingDelete(false)}
      />
    </article>
  );
}

/** Every firing of one rule, newest first - a decline shown exactly like an act. */
function FiringList({ firings }: { firings: RuleFiring[] }) {
  if (firings.length === 0) {
    return (
      <p className="rules-none">
        It has not fired yet. Nothing has been on a screen that woke it.
      </p>
    );
  }
  return (
    <ol className="rules-firings">
      {firings.map((firing) => (
        <li key={firing.id} className={`rules-firing rules-firing-${firing.decision}`}>
          <div className="rules-firing-head">
            <span className="rules-firing-decision">{firing.decision}</span>
            <span className="rules-firing-when">{new Date(firing.occurredUtc).toLocaleString()}</span>
            <span className="rules-firing-session">{firing.sessionId}</span>
          </div>
          <p className="rules-firing-reason">{firing.reason}</p>
          {firing.understanding.length > 0 && (
            <p className="rules-firing-understood">
              <span className="rules-fact-label">it read the screen as</span>
              {firing.understanding}
            </p>
          )}
          {firing.checksRun.length > 0 && (
            <ul className="rules-firing-checks">
              {firing.checksRun.map((run, i) => (
                <li key={i}>
                  {run.name}({run.arguments}) answered {run.answer}
                </li>
              ))}
            </ul>
          )}
          {firing.typedText.length > 0 && (
            <p className="rules-firing-typed">
              <span className="rules-fact-label">typed</span>
              <code>{firing.typedText}</code>
            </p>
          )}
          <p className="rules-firing-outcome">{firing.outcome}</p>
          <p className="rules-firing-grounding">{firing.grounding}</p>
        </li>
      ))}
    </ol>
  );
}
