import { useCallback, useEffect, useRef, useState } from "react";
import {
  createRule,
  deleteRule,
  describeScope,
  describeWait,
  draftRule,
  getRuleFirings,
  getRules,
  promoteRule,
  type RuleDraftTurn,
  type RuleFiring,
  type RuleWriteBody,
  type SessionRule,
} from "@devthrottle/client-core/rules/rulesClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { Button, ConfirmDialog, ErrorBanner, LoadingState } from "../components";

// THE RULES PAGE. A rule is a standing instruction about your sessions: it sits there costing
// nothing until one of them goes idle with something on its screen that looks like the thing you
// described, and then an agent reads that screen and your instruction together and does what you
// asked.
//
// YOU WRITE ONE BY SAYING WHAT YOU WANT. That is the whole of the composer at the top of this page:
// a box you talk into. The Gateway works out what it has to hold - what the screen looks like when
// the rule applies, the cheap words that make a screen worth a closer look, any check the act
// depends on, which sessions, and the two ceilings - and hands the rule back with a plain-English
// read-back BEFORE anything is stored. When guessing would make the rule do something you did not
// ask for, it asks you one question instead, and the answer goes back into the same conversation.
//
// THE PAGE NEVER DECIDES ANYTHING (rule 7: the client is dumb). Every verdict on this page is the
// Gateway's own words rendered verbatim - the read-back, the question, and above all a REFUSAL. When
// a rule cannot be stored the Gateway says which check does not exist or which value was missing,
// and that sentence is what appears here. A page that flattened it into "could not save" would leave
// somebody guessing, and guessing is how a rule ends up subtly different from the sentence meant.
//
// TWO PEOPLE-SHAPED STEPS SIT BETWEEN A SENTENCE AND THE FIRST KEYSTROKE, and neither can be skipped
// from here because neither can be skipped on the Gateway. Drafting stores nothing. Storing always
// produces a DRY RUN rule, which watches, decides, records what it WOULD have done, and types
// nothing. Only then does a person promote it.
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
            Tell DevThrottle what to do when a session gets stuck, in ordinary words. It works out
            what to watch for, shows you exactly what it would do, and does nothing at all until you
            say so.
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
          No rules yet. Say what you want one to do in the box above - for example, what should
          happen when a session stops on an error from the provider.
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

/**
 * Say what you want; read what it would do; store it.
 *
 * The conversation is held HERE and sent whole on every turn, so the Gateway never has to remember
 * anything between calls: when it asks a question, the question and your answer both go back with
 * the next request, and it can see which question your answer belongs to.
 */
function RuleComposer({ onStored }: { onStored: () => void }) {
  const [said, setSaid] = useState("");
  // The screen this rule is being written FROM.
  //
  // IT IS ALWAYS EMPTY ON THIS PAGE TODAY, and that is a gap rather than a design. The Gateway already
  // takes a screen and checks every trigger word against it - the command line uses that path
  // (`cc-devthrottle rule add --session`) - but the page has no way to get one yet. The intended way is
  // the authoring agent going and fetching a session's terminal itself and showing it here, which is
  // not built. Until it is, a rule written on this page is written from memory, and the page says so.
  const [screen, setScreen] = useState("");
  const [capturedFrom, setCapturedFrom] = useState<string | null>(null);
  const [turns, setTurns] = useState<RuleDraftTurn[]>([]);
  const [question, setQuestion] = useState<string | null>(null);
  const [proposal, setProposal] = useState<
    { readBack: string; rule: RuleWriteBody; exampleScreen: string } | null
  >(null);
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
    setScreen("");
    setCapturedFrom(null);
  };

  const send = async () => {
    const text = said.trim();
    if (text.length === 0 || busy) return;

    // What was asked goes into the conversation BEFORE the answer, so the model reads them in the
    // order they happened rather than seeing a reply to nothing.
    const next: RuleDraftTurn[] = [...turns];
    if (question !== null) next.push({ who: "devthrottle", text: question });
    next.push({ who: "person", text });

    setBusy(true);
    setRefusal(null);
    setStored(null);
    try {
      const answer = await draftRule(next, screen);
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
      // and what is stored are the same document.
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

  return (
    <section className="rules-composer">
      <h2 className="rules-composer-title">Make a rule by saying what you want</h2>

      <ScreenCapture
        screen={screen}
        capturedFrom={capturedFrom}
        disabled={busy || proposal !== null}
        onCleared={() => {
          setScreen("");
          setCapturedFrom(null);
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
            <Button variant="primary" onClick={() => void send()} disabled={busy || said.trim().length === 0}>
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
            <dd>{describeWriteScope(proposal.rule.scope)}</dd>
            <dt>Ceilings</dt>
            <dd>
              waits {describeWait(proposal.rule.cooldownSeconds)} before acting on the same session
              again, at most {proposal.rule.dailyCap} times a day
            </dd>
          </dl>

          {proposal.exampleScreen.length > 0 && (
            <p className="rules-checked">
              Every word above was checked against the screen you captured. A rule that watched for
              something not on it would have been refused rather than offered.
            </p>
          )}

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
 * THE SCREEN THIS RULE IS ABOUT, when one was handed over.
 *
 * There is no session picker here, on purpose. Picking a session out of a list on this page is asking
 * somebody to go and find a screen they were already looking at a moment ago - the moment you want a
 * rule is while you are staring at the thing that stopped you, and that is where the button lives now
 * ("Make a rule", on the session's own action bar). This panel only shows what that button brought.
 *
 * WITHOUT A SCREEN THE RULE IS STILL WRITTEN, and the panel says so rather than hiding it. That path is
 * real and it is worse: the trigger words become the model's guess at what a screen SAYS. Measured
 * against the live model, describing the limit case from memory produced a rule watching for "hit its
 * limit" and "when it comes back" - the person's own phrasing, on no screen anywhere. It would have sat
 * in the list looking correct and never fired once.
 */
function ScreenCapture({
  screen,
  capturedFrom,
  disabled,
  onCleared,
}: {
  screen: string;
  capturedFrom: string | null;
  disabled: boolean;
  onCleared: () => void;
}) {
  if (screen.length === 0) {
    return (
      <p className="rules-capture-none">
        You are describing this from memory, which works - but the words the rule watches for will be a
        guess at what the screen says, and a rule watching for a word that never appears never fires. To
        write one against a real screen, open the session it is happening on and press "Make a rule".
      </p>
    );
  }

  return (
    <div className="rules-capture rules-capture-has">
      <div className="rules-capture-head">
        <span className="rules-capture-title">The screen this rule is about, from {capturedFrom}</span>
        <Button variant="ghost" onClick={onCleared} disabled={disabled}>Write it without a screen</Button>
      </div>
      <pre className="rules-capture-screen">{screen}</pre>
      <p className="rules-capture-note">
        The words this rule watches for will be taken from this text, and anything not on it is refused.
      </p>
    </div>
  );
}

/** The scope of a rule that has not been stored yet, which is still in the shape it was written in. */
function describeWriteScope(scope: unknown): string {
  if (typeof scope === "string") return scope === "all-sessions" ? "every session" : scope;
  if (scope !== null && typeof scope === "object") {
    const parts = Object.entries(scope as Record<string, unknown>)
      .filter(([, value]) => typeof value === "string" && value.length > 0)
      .map(([key, value]) => `${key} ${String(value)}`);
    if (parts.length > 0) return parts.join(", ");
  }
  return "every session";
}

// ---- one rule, and what it has done -------------------------------------------------------------

const LIVE = "live";

/**
 * One rule as a card: what you said, what it watches for, what bounds it, and - on demand - every
 * time it has fired. A DECLINE is a firing too, and is shown exactly like an act: a rule that did
 * nothing because something broke would otherwise look identical to one that decided not to act.
 */
function RuleCard({ rule, onChanged }: { rule: SessionRule; onChanged: () => void }) {
  const [firings, setFirings] = useState<RuleFiring[] | null>(null);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [confirmingLive, setConfirmingLive] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  const isLive = rule.state === LIVE;

  const showFirings = async () => {
    setOpen(!open);
    if (firings !== null || open) return;
    try {
      setFirings(await getRuleFirings(rule.id));
    } catch (e) {
      setProblem(gatewayErrorMessage(e));
    }
  };

  const makeLive = async () => {
    setConfirmingLive(false);
    setBusy(true);
    setProblem(null);
    try {
      await promoteRule(
        rule.id,
        "I have read this rule's dry-run record and I am making it live.",
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
            {describeScope(rule.scope)}
          </span>
          <span className="rules-fact">
            <span className="rules-fact-label">ceilings</span>
            {describeWait(rule.cooldownSeconds)} apart, {rule.dailyCap} a day
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
        </div>

        <div className="rules-card-actions">
          <Button variant="ghost" onClick={() => void showFirings()}>
            {open ? "Hide what it has done" : "What it has done"}
          </Button>
          {!isLive && (
            <Button variant="primary" onClick={() => setConfirmingLive(true)} disabled={busy}>
              Make it live
            </Button>
          )}
          <Button variant="ghost" onClick={() => setConfirmingDelete(true)} disabled={busy}>Delete</Button>
        </div>

        {problem !== null && <p className="rules-refusal">{problem}</p>}

        {open && (
          firings === null ? (
            <LoadingState message="Reading the record..." />
          ) : firings.length === 0 ? (
            <p className="rules-none">
              It has not fired yet. Nothing has been on a screen that woke it.
            </p>
          ) : (
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
          )
        )}
      </div>

      <ConfirmDialog
        open={confirmingLive}
        title="Make this rule live?"
        message={
          "While it is in dry run it types nothing. Live, it will type into your sessions on its " +
          "own when the screen matches. Read what it has done first if you have not."
        }
        confirmLabel="Make it live"
        danger={false}
        onConfirm={() => void makeLive()}
        onClose={() => setConfirmingLive(false)}
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
